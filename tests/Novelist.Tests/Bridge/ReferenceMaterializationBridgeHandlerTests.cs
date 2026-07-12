using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceMaterializationBridgeHandlerTests
{
    [Fact]
    public async Task ChapterSplitHandlersRouteAllProductActionsToTheMaterializationService()
    {
        var service = new RecordingMaterializationService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterializationHandlers(service);

        await AssertOkAsync(dispatcher, "AnalyzeReferenceChapterSplit", new AnalyzeReferenceChapterSplitPayload(42, 99));
        await AssertOkAsync(dispatcher, "PreviewReferenceChapterSplit", new PreviewReferenceChapterSplitPayload(42, 99, "第{number}章 {title}"));
        await AssertOkAsync(dispatcher, "ConfirmReferenceChapterSplit", new ConfirmReferenceChapterSplitPayload(42, 99, "profile-1"));

        Assert.Equal(
            [
                "analyze:42:99",
                "preview:42:99:第{number}章 {title}",
                "confirm:42:99:profile-1"
            ],
            service.Calls);
    }

    [Fact]
    public async Task ChapterSplitHandlersRejectMissingObjectArguments()
    {
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterializationHandlers(new RecordingMaterializationService());
        var result = await dispatcher.DispatchAsync(Request("AnalyzeReferenceChapterSplit", 42L));
        using var json = JsonDocument.Parse(result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(BridgeErrorCodes.ValidationError, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task AssertOkAsync(BridgeDispatcher dispatcher, string method, params object?[] args)
    {
        var result = await dispatcher.DispatchAsync(Request(method, args));
        using var json = JsonDocument.Parse(result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
    }

    private static string Request(string method, params object?[] args)
    {
        return JsonSerializer.Serialize(new
        {
            kind = "request",
            id = $"req_{method}",
            method,
            payload = new { args }
        }, BridgeJson.SerializerOptions);
    }

    private sealed class RecordingMaterializationService : IReferenceMaterializationService
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ReferenceChapterSplitProfilePayload> AnalyzeChapterSplitAsync(
            AnalyzeReferenceChapterSplitPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"analyze:{input.NovelId}:{input.AnchorId}");
            return ValueTask.FromResult(CreateProfile(input.AnchorId));
        }

        public ValueTask<ReferenceChapterSplitProfilePayload> PreviewChapterSplitAsync(
            PreviewReferenceChapterSplitPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"preview:{input.NovelId}:{input.AnchorId}:{input.DelimiterTemplate}");
            return ValueTask.FromResult(CreateProfile(input.AnchorId));
        }

        public ValueTask<ReferenceChapterSplitProfilePayload> ConfirmChapterSplitAsync(
            ConfirmReferenceChapterSplitPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"confirm:{input.NovelId}:{input.AnchorId}:{input.SplitProfileId}");
            return ValueTask.FromResult(CreateProfile(input.AnchorId) with
            {
                Status = ReferenceChapterSplitProfileStates.Confirmed
            });
        }

        private static ReferenceChapterSplitProfilePayload CreateProfile(long anchorId)
        {
            return new ReferenceChapterSplitProfilePayload(
                "profile-1",
                anchorId,
                "source-hash",
                ReferenceChapterSplitModes.Auto,
                "markdown_heading",
                "# {title}",
                100,
                ReferenceChapterSplitProfileStates.Validated,
                2,
                [new ReferenceChapterSplitBoundaryPayload(1, "第一章", 0, 6, 30, "chapter-hash")],
                "provider",
                "model",
                0.9);
        }
    }
}
