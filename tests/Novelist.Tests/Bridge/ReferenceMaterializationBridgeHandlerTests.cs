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
        await AssertOkAsync(dispatcher, "EnqueueReferenceMaterialization", new EnqueueReferenceMaterializationPayload(42, 99, "profile-1", 10));
        await AssertOkAsync(dispatcher, "GetReferenceMaterializationStatus", new GetReferenceMaterializationStatusPayload(42, 99, "run-1"));
        await AssertOkAsync(dispatcher, "ListReferenceMaterializationChapterProgress", new ListReferenceMaterializationChapterProgressPayload(42, 99, "run-1", 1, 20));
        await AssertOkAsync(dispatcher, "ListActiveReferenceMaterializationMaterials", new ListActiveReferenceMaterializationMaterialsPayload(42, 99, 1, 20, "真相"));

        Assert.Equal(
            [
                "analyze:42:99",
                "preview:42:99:第{number}章 {title}",
                "confirm:42:99:profile-1",
                "enqueue:42:99:profile-1:10",
                "status:42:99:run-1",
                "progress:42:99:run-1:1:20",
                "materials:42:99:1:20:真相"
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

    [Fact]
    public async Task EnqueueReturnsTheStableMaterializationErrorCodeFromModelPreflight()
    {
        var service = new RecordingMaterializationService
        {
            EnqueueException = new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                "Embedding health check failed.")
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterializationHandlers(service);
        var result = await dispatcher.DispatchAsync(Request(
            "EnqueueReferenceMaterialization",
            new EnqueueReferenceMaterializationPayload(42, 99, "profile-1")));
        using var json = JsonDocument.Parse(result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed, error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
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
        public Exception? EnqueueException { get; init; }

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

        public ValueTask<ReferenceMaterializationStatusPayload> EnqueueMaterializationAsync(
            EnqueueReferenceMaterializationPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"enqueue:{input.NovelId}:{input.AnchorId}:{input.SplitProfileId}:{input.ChapterBatchSize}");
            if (EnqueueException is not null)
            {
                throw EnqueueException;
            }
            return ValueTask.FromResult(CreateStatus(input.AnchorId));
        }

        public ValueTask<ReferenceMaterializationStatusPayload?> GetMaterializationStatusAsync(
            GetReferenceMaterializationStatusPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"status:{input.NovelId}:{input.AnchorId}:{input.RunId}");
            return ValueTask.FromResult<ReferenceMaterializationStatusPayload?>(CreateStatus(input.AnchorId));
        }

        public ValueTask<PageResultPayload<ReferenceMaterializationChapterProgressPayload>> ListMaterializationChapterProgressAsync(
            ListReferenceMaterializationChapterProgressPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"progress:{input.NovelId}:{input.AnchorId}:{input.RunId}:{input.Page}:{input.Size}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterializationChapterProgressPayload>(
                [new ReferenceMaterializationChapterProgressPayload(1, 0, "pending", "pending", 0, 0, 0, 0, 0, 0, 0, null, null, null, null, 0)],
                1,
                input.Page,
                input.Size,
                1));
        }

        public ValueTask<PageResultPayload<ReferenceMaterializationMaterialPayload>> ListActiveMaterialsAsync(
            ListActiveReferenceMaterializationMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"materials:{input.NovelId}:{input.AnchorId}:{input.Page}:{input.Size}:{input.Query}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterializationMaterialPayload>(
                [new ReferenceMaterializationMaterialPayload(
                    "material-1",
                    input.AnchorId,
                    "generation-1",
                    "passage",
                    "她说出了真相。",
                    0.9,
                    0.8,
                    new ReferenceMaterializationMaterialTagsPayload(["reveal"], [], ["close_third"], ["subtext"]),
                    ["complete_exchange"])],
                1,
                input.Page,
                input.Size,
                1));
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

        private static ReferenceMaterializationStatusPayload CreateStatus(long anchorId)
        {
            return new ReferenceMaterializationStatusPayload(
                "run-1",
                anchorId,
                "profile-1",
                "generation-1",
                ReferenceMaterializationRunStates.Queued,
                5,
                2,
                0,
                1,
                0,
                0,
                1,
                2,
                0,
                0,
                0,
                0,
                0,
                new ReferenceMaterializationModelIdentityPayload("provider", "model"),
                new ReferenceMaterializationModelIdentityPayload("embedding", "embedding-model", 3),
                null,
                null,
                DateTimeOffset.UtcNow,
                null,
                false,
                "start_processing");
        }
    }
}
