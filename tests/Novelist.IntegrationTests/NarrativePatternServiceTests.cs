using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class NarrativePatternServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NarrativePatternRunsPersistProgressTraceAndSkillMetadataAcrossServiceRecreation()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novel = await CreateNovelAsync(options);
        var service = new FileSystemNarrativePatternExtractionService(options, new FileSystemNovelService(options));

        var started = await service.StartExtractionAsync(
            new StartNarrativePatternExtractionPayload(
                TaskId: "pattern-task-1",
                NovelId: novel.Id,
                ChapterRanges:
                [
                    new ChapterRangePayload(1, 3),
                    new ChapterRangePayload(5, 5)
                ],
                ProviderName: "fake-provider",
                ModelId: "fake-model",
                ReasoningEffort: "low",
                SkillName: "雨夜悬疑结构"),
            CancellationToken.None);

        Assert.Equal("pattern-task-1", started.TaskId);
        Assert.Equal(novel.Id, started.NovelId);
        Assert.Equal("running", started.Status);
        Assert.Equal("queued", started.Stage);
        Assert.Equal(0, started.ProgressCompleted);
        Assert.Equal(4, started.ProgressTotal);
        Assert.Equal([new ChapterRangePayload(1, 3), new ChapterRangePayload(5, 5)], started.ChapterRanges);
        Assert.Equal("雨夜悬疑结构", started.SkillName);
        Assert.Equal("", started.SkillPreview);
        Assert.Null(started.CompletedAt);

        var progress = await service.UpdateRunAsync(
            new NarrativePatternRunUpdate(
                TaskId: "pattern-task-1",
                Status: "running",
                Stage: "chapter_summary",
                ProgressCompleted: 2,
                ProgressTotal: 4,
                SkillPreview: null,
                Diagnostics: []),
            CancellationToken.None);
        Assert.Equal("chapter_summary", progress.Stage);
        Assert.Equal(2, progress.ProgressCompleted);

        var traceEntry = new NarrativePatternTraceEntryPayload(
            TraceId: "trace-1",
            Stage: "chapter_summary",
            InputHash: "sha256:input-1",
            OutputHash: "sha256:output-1",
            Diagnostics:
            [
                Diagnostic(
                    code: "pattern.summary.cached",
                    message: "章节摘要已复用。",
                    operation: "NarrativePatternExtraction",
                    taskId: "pattern-task-1")
            ],
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"));
        await service.AppendTraceAsync(
            new NarrativePatternTraceAppend("pattern-task-1", traceEntry),
            CancellationToken.None);

        var completed = await service.CompleteRunAsync(
            new NarrativePatternRunCompletion(
                TaskId: "pattern-task-1",
                Stage: "skill_preview",
                SkillPreview: "## Narrative Pattern\n\n- 先压低信息量，再用场景证据推进反转。",
                Diagnostics:
                [
                    Diagnostic(
                        code: "pattern.skill.preview_ready",
                        message: "叙事模式技能预览已生成。",
                        operation: "NarrativePatternExtraction",
                        taskId: "pattern-task-1")
                ]),
            CancellationToken.None);

        Assert.Equal("completed", completed.Status);
        Assert.Equal("skill_preview", completed.Stage);
        Assert.Equal(4, completed.ProgressCompleted);
        Assert.Equal(4, completed.ProgressTotal);
        Assert.Contains("Narrative Pattern", completed.SkillPreview);
        Assert.NotNull(completed.CompletedAt);

        var reloaded = new FileSystemNarrativePatternExtractionService(options, new FileSystemNovelService(options));
        var persisted = await reloaded.GetRunAsync(
            new GetNarrativePatternRunPayload("pattern-task-1"),
            CancellationToken.None);
        var trace = await reloaded.GetTraceAsync(
            new GetNarrativePatternRunPayload("pattern-task-1"),
            CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal("completed", persisted.Status);
        Assert.Equal("skill_preview", persisted.Stage);
        Assert.Equal("雨夜悬疑结构", persisted.SkillName);
        Assert.Contains("先压低信息量", persisted.SkillPreview);
        Assert.Single(persisted.Diagnostics);
        Assert.NotNull(trace);
        Assert.Equal("pattern-task-1", trace.TaskId);
        var persistedTrace = Assert.Single(trace.Entries);
        Assert.Equal(traceEntry.TraceId, persistedTrace.TraceId);
        Assert.Equal(traceEntry.Stage, persistedTrace.Stage);
        Assert.Equal(traceEntry.InputHash, persistedTrace.InputHash);
        Assert.Equal(traceEntry.OutputHash, persistedTrace.OutputHash);
        Assert.Equal(traceEntry.CreatedAt, persistedTrace.CreatedAt);
        Assert.Contains(persistedTrace.Diagnostics, diagnostic => diagnostic.Code == "pattern.summary.cached");
    }

    [Fact]
    public async Task NarrativePatternCancellationAndFailureStoreTerminalDiagnostics()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novel = await CreateNovelAsync(options);
        var service = new FileSystemNarrativePatternExtractionService(options, new FileSystemNovelService(options));

        await service.StartExtractionAsync(
            ValidStartPayload("pattern-cancel-1", novel.Id),
            CancellationToken.None);

        var cancelled = await service.CancelExtractionAsync(
            new CancelNarrativePatternExtractionPayload("pattern-cancel-1", "用户取消"),
            CancellationToken.None);

        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal("cancelled", cancelled.Stage);
        Assert.NotNull(cancelled.CompletedAt);
        Assert.Contains(cancelled.Diagnostics, diagnostic => diagnostic.Code == "pattern.cancelled");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.UpdateRunAsync(
                new NarrativePatternRunUpdate("pattern-cancel-1", "running", "chapter_summary", 1, 3, null, []),
                CancellationToken.None));

        await service.StartExtractionAsync(
            ValidStartPayload("pattern-fail-1", novel.Id),
            CancellationToken.None);
        var failed = await service.FailRunAsync(
            new NarrativePatternRunFailure(
                TaskId: "pattern-fail-1",
                Stage: "model_json_validation",
                Error: Diagnostic(
                    code: "pattern.invalid_tool_json",
                    message: "模型返回的叙事模式 JSON 不可用。",
                    operation: "NarrativePatternExtraction",
                    taskId: "pattern-fail-1")),
            CancellationToken.None);

        Assert.Equal("failed", failed.Status);
        Assert.Equal("model_json_validation", failed.Stage);
        Assert.NotNull(failed.CompletedAt);
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Code == "pattern.invalid_tool_json");
    }

    [Fact]
    public async Task NarrativePatternValidationRejectsUnsafePayloads()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novel = await CreateNovelAsync(options);
        var service = new FileSystemNarrativePatternExtractionService(options, new FileSystemNovelService(options));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(ValidStartPayload("", novel.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(ValidStartPayload("pattern-missing-novel", novel.Id + 100), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(
                ValidStartPayload("pattern-empty-ranges", novel.Id) with { ChapterRanges = [] },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(
                ValidStartPayload("pattern-backward-range", novel.Id) with { ChapterRanges = [new ChapterRangePayload(3, 2)] },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(
                ValidStartPayload("pattern-overlap-range", novel.Id) with
                {
                    ChapterRanges =
                    [
                        new ChapterRangePayload(1, 3),
                        new ChapterRangePayload(3, 4)
                    ]
                },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(
                ValidStartPayload("pattern-empty-provider", novel.Id) with { ProviderName = "" },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(
                ValidStartPayload("pattern-empty-model", novel.Id) with { ModelId = "" },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(
                ValidStartPayload("pattern-empty-skill", novel.Id) with { SkillName = "" },
                CancellationToken.None));
    }

    [Fact]
    public async Task BridgeNarrativePatternHandlersPersistAndValidatePayloads()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novel = await CreateNovelAsync(options);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNarrativePatternHandlers(
                new FileSystemNarrativePatternExtractionService(options, new FileSystemNovelService(options)));

        using var startJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_start_pattern",
              "method": "StartNarrativePatternExtraction",
              "payload": {
                "args": [{
                  "task_id": "pattern-bridge-1",
                  "novel_id": {{novel.Id}},
                  "chapter_ranges": [{ "start_chapter": 1, "end_chapter": 2 }],
                  "provider_name": "fake-provider",
                  "model_id": "fake-model",
                  "reasoning_effort": "low",
                  "skill_name": "桥接叙事模式"
                }]
              }
            }
            """));
        var started = startJson.RootElement.GetProperty("result");
        Assert.Equal("running", started.GetProperty("status").GetString());
        Assert.Equal(2, started.GetProperty("progress_total").GetInt32());

        using var traceJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get_pattern_trace",
              "method": "GetNarrativePatternTrace",
              "payload": { "args": [{ "task_id": "pattern-bridge-1" }] }
            }
            """));
        Assert.Empty(traceJson.RootElement.GetProperty("result").GetProperty("entries").EnumerateArray());

        using var cancelJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_cancel_pattern",
              "method": "CancelNarrativePatternExtraction",
              "payload": { "args": [{ "task_id": "pattern-bridge-1", "reason": "用户取消" }] }
            }
            """));
        Assert.Equal("cancelled", cancelJson.RootElement.GetProperty("result").GetProperty("status").GetString());

        using var getJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get_pattern",
              "method": "GetNarrativePatternRun",
              "payload": { "args": [{ "task_id": "pattern-bridge-1" }] }
            }
            """));
        Assert.Equal("cancelled", getJson.RootElement.GetProperty("result").GetProperty("status").GetString());

        using var invalidJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bad_pattern",
              "method": "StartNarrativePatternExtraction",
              "payload": {
                "args": [{
                  "task_id": "",
                  "novel_id": {{novel.Id}},
                  "chapter_ranges": [{ "start_chapter": 1, "end_chapter": 2 }],
                  "provider_name": "fake-provider",
                  "model_id": "fake-model",
                  "reasoning_effort": "low",
                  "skill_name": "bad"
                }]
              }
            }
            """));
        AssertBridgeError(invalidJson.RootElement, "req_bad_pattern", BridgeErrorCodes.ValidationError);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private async ValueTask<NovelPayload> CreateNovelAsync(AppInitializationOptions options)
    {
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        return await novels.CreateNovelAsync(new CreateNovelPayload("雨城档案", "测试作品", "悬疑"), CancellationToken.None);
    }

    private static StartNarrativePatternExtractionPayload ValidStartPayload(string taskId, long novelId)
    {
        return new StartNarrativePatternExtractionPayload(
            TaskId: taskId,
            NovelId: novelId,
            ChapterRanges: [new ChapterRangePayload(1, 3)],
            ProviderName: "fake-provider",
            ModelId: "fake-model",
            ReasoningEffort: "low",
            SkillName: "雨夜悬疑结构");
    }

    private static CopyableDiagnosticPayload Diagnostic(
        string code,
        string message,
        string operation,
        string taskId)
    {
        return new CopyableDiagnosticPayload(
            Code: code,
            Message: message,
            Detail: "",
            Operation: operation,
            TaskId: taskId,
            RunId: null,
            BridgeMethod: null,
            Timestamp: DateTimeOffset.Parse("2026-07-07T00:00:00Z"));
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static void AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
    }
}
