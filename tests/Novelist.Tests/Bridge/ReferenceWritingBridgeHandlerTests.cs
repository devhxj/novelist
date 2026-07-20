using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceWritingBridgeHandlerTests
{
    [Fact]
    public async Task RoutesTheFourMaterialWritingActionsAndPreservesDraftText()
    {
        var draftText = "First line.\n\n" + new string('x', 5_000);
        var service = new RecordingReferenceWritingService(draftText);
        var dispatcher = new BridgeDispatcher().RegisterReferenceWritingHandlers(service);

        await AssertOkAsync(
            dispatcher,
            "GenerateReferenceBlueprints",
            new GenerateReferenceBlueprintsPayload(7, 3, "chapter-7-3", "Escalate conflict.", 2));
        await AssertOkAsync(
            dispatcher,
            "GetReferenceWritingSession",
            new GetReferenceWritingSessionPayload(7, 3, "chapter-7-3"));
        await AssertOkAsync(
            dispatcher,
            "SelectReferenceBlueprint",
            new SelectReferenceBlueprintPayload(7, 3, "chapter-7-3", "blueprint-1"));
        var draftResult = await dispatcher.DispatchAsync(Request(
            "GenerateReferenceDraftCandidates",
            new GenerateReferenceDraftCandidatesPayload(
                7,
                3,
                "chapter-7-3",
                "blueprint-1",
                string.Empty,
                0,
                new Dictionary<string, string>(),
                2)));
        using var json = JsonDocument.Parse(
            draftResult.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            draftText,
            json.RootElement
                .GetProperty("result")
                .GetProperty("candidates")[0]
                .GetProperty("text")
                .GetString());
        Assert.Equal(
            ["generate", "get", "select", "draft"],
            service.Calls);
    }

    [Fact]
    public async Task ReturnsStableWritingErrorsWithoutAnEmptySuccess()
    {
        var service = new RecordingReferenceWritingService("unused")
        {
            Exception = new ReferenceWritingException(
                ReferenceWritingErrorCodes.BlueprintStale,
                "The selected blueprint is stale.")
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceWritingHandlers(service);

        var result = await dispatcher.DispatchAsync(Request(
            "GetReferenceWritingSession",
            new GetReferenceWritingSessionPayload(7, 3, "chapter-7-3")));
        using var json = JsonDocument.Parse(
            result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ReferenceWritingErrorCodes.BlueprintStale,
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task AssertOkAsync(
        BridgeDispatcher dispatcher,
        string method,
        object input)
    {
        var result = await dispatcher.DispatchAsync(Request(method, input));
        using var json = JsonDocument.Parse(
            result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
    }

    private static string Request(string method, object input) => JsonSerializer.Serialize(new
    {
        kind = BridgeMessageKinds.Request,
        id = "request-1",
        method,
        payload = new { args = new[] { input } }
    }, BridgeJson.SerializerOptions);

    private sealed class RecordingReferenceWritingService(string draftText) : IReferenceWritingService
    {
        public List<string> Calls { get; } = [];

        public Exception? Exception { get; init; }

        public ValueTask<ReferenceWritingSessionPayload> GenerateBlueprintsAsync(
            GenerateReferenceBlueprintsPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add("generate");
            ThrowIfNeeded();
            return ValueTask.FromResult(Session());
        }

        public ValueTask<ReferenceWritingSessionPayload?> GetSessionAsync(
            GetReferenceWritingSessionPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add("get");
            ThrowIfNeeded();
            return ValueTask.FromResult<ReferenceWritingSessionPayload?>(Session());
        }

        public ValueTask<ReferenceWritingSessionPayload> SelectBlueprintAsync(
            SelectReferenceBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add("select");
            ThrowIfNeeded();
            return ValueTask.FromResult(Session() with { SelectedBlueprintId = input.BlueprintId });
        }

        public ValueTask<ReferenceWritingDraftCandidatesPayload> GenerateDraftCandidatesAsync(
            GenerateReferenceDraftCandidatesPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add("draft");
            ThrowIfNeeded();
            return ValueTask.FromResult(new ReferenceWritingDraftCandidatesPayload(
                input.SessionId,
                input.BlueprintId,
                [new ReferenceWritingDraftCandidatePayload(
                    "draft-1",
                    input.BlueprintId,
                    draftText,
                    draftText,
                    [new ReferenceWritingDraftSourcePayload(
                        "beat-1",
                        "material-1",
                        "generation-1",
                        11,
                        1,
                        "hash",
                        "authorized",
                        "verbatim_ok")],
                    new ReferenceWritingDraftAuditPayload(true, []))]));
        }

        private void ThrowIfNeeded()
        {
            if (Exception is not null)
            {
                throw Exception;
            }
        }

        private static ReferenceWritingSessionPayload Session() => new(
            "chapter-7-3",
            7,
            3,
            "Escalate conflict.",
            [new ReferenceWritingBlueprintPayload(
                "blueprint-1",
                "progressive",
                [new ReferenceWritingBlueprintBeatPayload(
                    "beat-1",
                    0,
                    "Raise pressure.",
                    "dialogue",
                    [new ReferenceMaterialIdentityPayload("material-1", "generation-1")])])],
            string.Empty,
            DateTimeOffset.UtcNow);
    }
}
