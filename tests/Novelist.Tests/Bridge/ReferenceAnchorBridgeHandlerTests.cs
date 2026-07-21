using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceAnchorBridgeHandlerTests
{
    [Fact]
    public async Task RoutesTheSourceCrudSurfaceAndRedactsLocalPaths()
    {
        var service = new RecordingReferenceAnchorService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        var registered = await DispatchOkAsync(
            dispatcher,
            "RegisterReferenceMaterializationSource",
            new RegisterReferenceMaterializationSourcePayload(
                42,
                "Reference",
                "Author",
                @"D:\private\reference.md",
                "markdown",
                "user_provided"));
        await DispatchOkAsync(dispatcher, "GetReferenceAnchors", 42L);
        await DispatchOkAsync(dispatcher, "DeleteReferenceAnchor", 42L, 7L);
        await DispatchOkAsync(
            dispatcher,
            "DeleteReferenceAnchors",
            new DeleteReferenceAnchorsPayload(42, [7, 8]));
        await DispatchOkAsync(
            dispatcher,
            "UpdateReferenceAnchorMetadata",
            new UpdateReferenceAnchorMetadataPayload(
                42,
                7,
                "Updated",
                "Author",
                "user_provided",
                ReferenceCorpusVisibilities.Private,
                ReferenceSourceTrustLevels.UserVerified,
                ["dialogue"]));

        Assert.Equal(string.Empty, registered.GetProperty("source_path").GetString());
        Assert.Equal(
            ["register", "get:42", "delete:42:7", "delete-many:42:7,8", "update:42:7"],
            service.Calls);
    }

    private static async ValueTask<JsonElement> DispatchOkAsync(
        BridgeDispatcher dispatcher,
        string method,
        params object?[] args)
    {
        var request = JsonSerializer.Serialize(new
        {
            kind = BridgeMessageKinds.Request,
            id = "request-1",
            method,
            payload = new { args }
        }, BridgeJson.SerializerOptions);
        var result = await dispatcher.DispatchAsync(request);
        using var json = JsonDocument.Parse(
            result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean(), json.RootElement.GetRawText());
        return json.RootElement.GetProperty("result").Clone();
    }

    private sealed class RecordingReferenceAnchorService : IReferenceAnchorService
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ReferenceAnchorPayload> RegisterMaterializationSourceAsync(
            RegisterReferenceMaterializationSourcePayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add("register");
            return ValueTask.FromResult(Anchor(input.Title, input.SourcePath));
        }

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
            long novelId,
            CancellationToken cancellationToken)
        {
            Calls.Add($"get:{novelId}");
            return ValueTask.FromResult<IReadOnlyList<ReferenceAnchorPayload>>(
                [Anchor("Reference", @"D:\private\reference.md")]);
        }

        public ValueTask DeleteAnchorAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken)
        {
            Calls.Add($"delete:{novelId}:{anchorId}");
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAnchorsAsync(
            DeleteReferenceAnchorsPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"delete-many:{input.NovelId}:{string.Join(',', input.AnchorIds)}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
            UpdateReferenceAnchorMetadataPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"update:{input.NovelId}:{input.AnchorId}");
            return ValueTask.FromResult(Anchor(input.Title, @"D:\private\reference.md"));
        }

        private static ReferenceAnchorPayload Anchor(string title, string sourcePath) => new(
            7,
            42,
            title,
            "Author",
            sourcePath,
            "markdown",
            "user_provided",
            "source-hash",
            "whole-chapter-v1",
            ReferenceAnchorBuildStates.PendingSplit,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
