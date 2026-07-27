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
        await AssertOkAsync(dispatcher, "EnqueueReferenceMaterialization", new EnqueueReferenceMaterializationPayload(42, 99, "profile-1"));
        await AssertOkAsync(dispatcher, "RunReferenceMaterializationChapter", new RunReferenceMaterializationChapterPayload(42, 99, "run-1", 2));
        await AssertOkAsync(dispatcher, "GetReferenceMaterializationStatus", new GetReferenceMaterializationStatusPayload(42, 99, "run-1"));
        await AssertOkAsync(dispatcher, "ListReferenceMaterializationChapterProgress", new ListReferenceMaterializationChapterProgressPayload(42, 99, "run-1", 1, 20));
        await AssertOkAsync(dispatcher, "ListReferenceMaterializationChapterMaterials", new ListReferenceMaterializationChapterMaterialsPayload(42, 99, "run-1", 2, 1, 20));

        Assert.Equal(
            [
                "analyze:42:99",
                "preview:42:99:第{number}章 {title}",
                "confirm:42:99:profile-1",
                "enqueue:42:99:profile-1",
                "chapter:42:99:run-1:2",
                "status:42:99:run-1",
                "progress:42:99:run-1:1:20",
                "materials:42:99:run-1:2:1:20"
            ],
            service.Calls);
    }

    [Fact]
    public async Task MaterializationProgressExposesOnlyCanonicalCounts()
    {
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterializationHandlers(
            new RecordingMaterializationService());
        var result = await dispatcher.DispatchAsync(Request(
            "ListReferenceMaterializationChapterProgress",
            new ListReferenceMaterializationChapterProgressPayload(42, 99, "run-1", 1, 20)));
        using var json = JsonDocument.Parse(result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));
        var progress = json.RootElement.GetProperty("result").GetProperty("items")[0];

        Assert.True(progress.TryGetProperty("material_count", out _));
        Assert.True(progress.TryGetProperty("vector_count", out _));
        Assert.True(progress.TryGetProperty("model_call_count", out _));
        Assert.False(progress.TryGetProperty("candidate_count", out _));
        Assert.False(progress.TryGetProperty("accepted_count", out _));
        Assert.False(progress.TryGetProperty("review_count", out _));
        Assert.False(progress.TryGetProperty("current_stage", out _));
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

    [Fact]
    public async Task BlueprintPreviewHandlerRoutesTransientGeneration()
    {
        var service = new RecordingBlueprintPreviewService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterializationBlueprintPreviewHandlers(service);

        await AssertOkAsync(
            dispatcher,
            "GenerateReferenceMaterializationBlueprintPreview",
            new GenerateReferenceMaterializationBlueprintPreviewPayload(42, [99, 101], "安排冲突升级", 2));
        Assert.Equal(["generate:42:99,101:安排冲突升级:2"], service.Calls);
    }

    [Fact]
    public async Task BlueprintPreviewHandlerPreservesCompleteMaterialText()
    {
        var materialText = "D:\\fiction\\chapter.txt\n\n" + new string('x', 5_000);
        var service = new RecordingBlueprintPreviewService { MaterialText = materialText };
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterializationBlueprintPreviewHandlers(service);

        var result = await dispatcher.DispatchAsync(Request(
            "GenerateReferenceMaterializationBlueprintPreview",
            new GenerateReferenceMaterializationBlueprintPreviewPayload(42, [99], "安排冲突升级")));
        using var json = JsonDocument.Parse(
            result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));
        var returnedText = json.RootElement
            .GetProperty("result")
            .GetProperty("candidates")[0]
            .GetProperty("beats")[0]
            .GetProperty("materials")[0]
            .GetProperty("text")
            .GetString();

        Assert.Equal(materialText, returnedText);
    }

    [Fact]
    public async Task BlueprintPreviewHandlersReturnTheStableMaterialReadyErrorCode()
    {
        var service = new RecordingBlueprintPreviewService
        {
            GenerateException = new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.BlueprintMaterialNotReady,
                "Reference source has no active material-ready generation.")
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterializationBlueprintPreviewHandlers(service);

        var result = await dispatcher.DispatchAsync(Request(
            "GenerateReferenceMaterializationBlueprintPreview",
            new GenerateReferenceMaterializationBlueprintPreviewPayload(42, [99], "安排冲突升级")));
        using var json = JsonDocument.Parse(result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ReferenceMaterializationErrorCodes.BlueprintMaterialNotReady,
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
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

    private static ReferenceMaterialMetadataPayload ArchiveMetadata(
        string sourceKind,
        string reuseHint,
        IReadOnlyList<string>? narrativeFunctions = null) =>
        new(
            new ReferenceMaterialSourceSpanPayload(1, 1), sourceKind, [], null, null, null, [], null, [], null,
            null, null, null, narrativeFunctions ?? [], [], [], [], reuseHint);

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
            Calls.Add($"enqueue:{input.NovelId}:{input.AnchorId}:{input.SplitProfileId}");
            if (EnqueueException is not null)
            {
                throw EnqueueException;
            }
            return ValueTask.FromResult(CreateStatus(input.AnchorId));
        }

        public ValueTask<ReferenceMaterializationStatusPayload> RunMaterializationChapterAsync(
            RunReferenceMaterializationChapterPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"chapter:{input.NovelId}:{input.AnchorId}:{input.RunId}:{input.ChapterIndex}");
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
                [new ReferenceMaterializationChapterProgressPayload(
                    1,
                    ReferenceMaterializationChapterStates.Pending,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null)],
                1,
                input.Page,
                input.Size,
                1));
        }

        public ValueTask<PageResultPayload<ReferenceMaterialListItemPayload>> ListMaterializationChapterMaterialsAsync(
            ListReferenceMaterializationChapterMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"materials:{input.NovelId}:{input.AnchorId}:{input.RunId}:{input.ChapterIndex}:{input.Page}:{input.Size}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialListItemPayload>(
                [new ReferenceMaterialListItemPayload(
                    "material-1",
                    "generation-1",
                    input.AnchorId,
                    input.ChapterIndex,
                    0,
                    "完整材料。\n\n第二段。",
                    ArchiveMetadata("对话", "完整说明。"),
                    "hash-1")],
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
                2,
                0,
                1,
                0,
                0,
                0,
                new ReferenceMaterializationModelIdentityPayload("provider", "model"),
                new ReferenceMaterializationModelIdentityPayload("embedding", "embedding-model", 3),
                null,
                null,
                DateTimeOffset.UtcNow,
                null,
                false);
        }
    }

    private sealed class RecordingBlueprintPreviewService : IReferenceMaterializationBlueprintPreviewService
    {
        public List<string> Calls { get; } = [];

        public Exception? GenerateException { get; init; }

        public string MaterialText { get; init; } = "A pressure point.";

        public ValueTask<ReferenceMaterializationBlueprintPreviewPayload> GenerateAsync(
            GenerateReferenceMaterializationBlueprintPreviewPayload input,
            CancellationToken cancellationToken)
        {
            Calls.Add($"generate:{input.NovelId}:{string.Join(',', input.AnchorIds)}:{input.Goal}:{input.RequestedCount}");
            if (GenerateException is not null)
            {
                throw GenerateException;
            }

            return ValueTask.FromResult(CreatePreview());
        }

        private ReferenceMaterializationBlueprintPreviewPayload CreatePreview() => new(
            "安排冲突升级",
            [new ReferenceMaterializationBlueprintPreviewSourcePayload(99, "generation-1", 2)],
            [new ReferenceMaterializationBlueprintPreviewCandidatePayload(
                "blueprint-1",
                "pressure_chain",
                [new ReferenceMaterializationBlueprintPreviewBeatPayload(
                    "beat-1",
                    0,
                    "Establish pressure.",
                    "conflict",
                    [new ReferenceMaterializationBlueprintPreviewMaterialLinkPayload(
                        "material-1",
                        99,
                        "generation-1",
                        MaterialText,
                        ArchiveMetadata("叙述", "Reusable pressure.", ["冲突升级"]),
                        0.2,
                        "Semantic match.")])])]);
    }
}
