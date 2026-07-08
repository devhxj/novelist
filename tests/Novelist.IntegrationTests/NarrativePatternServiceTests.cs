using System.Runtime.CompilerServices;
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
    public async Task StartNarrativePatternExtractionRunsPipelineAndPersistsValidatedPreviewTraceAndProgress()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options), new NoOpVersionControlService());
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("雨城档案", "测试作品", "悬疑"), CancellationToken.None);
        var chapters = await CreateChaptersAsync(options, novelService, novel.Id, 5);
        var chat = new ScriptedNarrativePatternChat(chapters, ValidSkillMarkdown("雨夜悬疑结构"));
        var events = new RecordingBridgeEventSink();
        var service = CreateService(options, novelService, chat, events);

        var run = await service.StartExtractionAsync(
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

        Assert.Equal("completed", run.Status);
        Assert.Equal("skill_preview", run.Stage);
        Assert.Equal([new ChapterRangePayload(1, 3), new ChapterRangePayload(5, 5)], run.ChapterRanges);
        Assert.Equal([chapters[0].Id, chapters[1].Id, chapters[2].Id, chapters[4].Id], run.SelectedChapterIds);
        Assert.Contains("generated_by: narrative_pattern_extraction", run.SkillPreview, StringComparison.Ordinal);
        Assert.Contains("source_chapter_ranges: 1-3,5-5", run.SkillPreview, StringComparison.Ordinal);
        Assert.Contains("先用失踪案压低信息量", run.SkillPreview, StringComparison.Ordinal);
        Assert.Contains(run.Diagnostics, item => item.Code == "pattern.skill.preview_ready");
        Assert.DoesNotContain(chat.Requests, request =>
            request.Messages.Any(message => message.Content.Contains("UNSELECTED_CHAPTER_SECRET", StringComparison.Ordinal)));

        var persisted = await service.GetRunAsync(new GetNarrativePatternRunPayload("pattern-task-1"), CancellationToken.None);
        var trace = await service.GetTraceAsync(new GetNarrativePatternRunPayload("pattern-task-1"), CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal("completed", persisted.Status);
        Assert.NotNull(trace);
        Assert.Contains(trace.Entries, entry => entry.Stage == "boundary_detection");
        Assert.Contains(trace.Entries, entry => entry.Stage == "chapter_summary");
        Assert.Contains(trace.Entries, entry => entry.Stage == "phase_compression");
        Assert.Contains(trace.Entries, entry => entry.Stage == "skill_generation");

        Assert.Contains(events.Events, item =>
            item.Name == "narrative_pattern_extraction:progress" &&
            item.Payload is NarrativePatternProgressPayload progress &&
            progress.Stage == "phase_compression" &&
            progress.Round is > 0 &&
            progress.BatchTotal is > 0 &&
            progress.TokenEstimate is > 0);
        Assert.Contains(events.Events, item =>
            item.Payload is NarrativePatternProgressPayload progress &&
            progress.Status == "completed" &&
            progress.BoundaryCount == 2 &&
            progress.SummaryCount == 4 &&
            progress.PhaseCount > 0);

        var reloaded = CreateService(options, novelService, chat, events);
        var reloadedRun = await reloaded.GetRunAsync(new GetNarrativePatternRunPayload("pattern-task-1"), CancellationToken.None);
        Assert.NotNull(reloadedRun);
        Assert.Equal("completed", reloadedRun.Status);
        Assert.Contains("source_chapter_hashes:", reloadedRun.SkillPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidBoundaryJsonMarksRunFailedWithDiagnostic()
    {
        var (options, novelService, novel, chapters) = await CreateNovelWithChaptersAsync(4);
        var chat = new ScriptedNarrativePatternChat(chapters, ValidSkillMarkdown("不会使用"))
        {
            BoundaryJsonOverride = "{not json"
        };
        var service = CreateService(options, novelService, chat, new RecordingBridgeEventSink());

        var failed = await service.StartExtractionAsync(
            ValidStartPayload("pattern-invalid-boundary", novel.Id),
            CancellationToken.None);

        Assert.Equal("failed", failed.Status);
        Assert.Equal("model_json_validation", failed.Stage);
        Assert.Contains(failed.Diagnostics, item => item.Code == "pattern.invalid_boundary_json");
    }

    [Fact]
    public async Task InvalidSummaryContentHashMarksRunFailedBeforeSkillGeneration()
    {
        var (options, novelService, novel, chapters) = await CreateNovelWithChaptersAsync(4);
        var chat = new ScriptedNarrativePatternChat(chapters, ValidSkillMarkdown("不会使用"))
        {
            SummaryHashOverride = "sha256:stale"
        };
        var service = CreateService(options, novelService, chat, new RecordingBridgeEventSink());

        var failed = await service.StartExtractionAsync(
            ValidStartPayload("pattern-invalid-summary", novel.Id),
            CancellationToken.None);

        Assert.Equal("failed", failed.Status);
        Assert.Contains(failed.Diagnostics, item => item.Code == "pattern.stale_summary");
        Assert.DoesNotContain(chat.Requests, request => request.Messages.Any(message =>
            message.Content.Contains("生成一个可复用的 Novelist 技能 Markdown", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EmptyPhaseOutputRetriesWithinBoundedLimit()
    {
        var (options, novelService, novel, chapters) = await CreateNovelWithChaptersAsync(4);
        var chat = new ScriptedNarrativePatternChat(chapters, ValidSkillMarkdown("雨夜悬疑结构"))
        {
            EmptyPhaseResponsesBeforeSuccess = 1
        };
        var service = CreateService(options, novelService, chat, new RecordingBridgeEventSink());

        var run = await service.StartExtractionAsync(
            ValidStartPayload("pattern-phase-retry", novel.Id),
            CancellationToken.None);

        Assert.Equal("completed", run.Status);
        Assert.True(chat.PhaseCallCount >= 2);
    }

    [Fact]
    public async Task CompressionStallMarksRunFailed()
    {
        var (options, novelService, novel, chapters) = await CreateNovelWithChaptersAsync(6);
        var chat = new ScriptedNarrativePatternChat(chapters, ValidSkillMarkdown("不会使用"))
        {
            StallCompression = true
        };
        var service = CreateService(options, novelService, chat, new RecordingBridgeEventSink());

        var failed = await service.StartExtractionAsync(
            new StartNarrativePatternExtractionPayload(
                "pattern-stall",
                novel.Id,
                [new ChapterRangePayload(1, 6)],
                "fake-provider",
                "fake-model",
                "",
                "停滞结构"),
            CancellationToken.None);

        Assert.Equal("failed", failed.Status);
        Assert.Contains(failed.Diagnostics, item => item.Code == "pattern.compression_stalled");
    }

    [Fact]
    public async Task CancellationStopsModelCallAndMarksRunCancelled()
    {
        var (options, novelService, novel, chapters) = await CreateNovelWithChaptersAsync(4);
        var chat = new BlockingNarrativePatternChat(chapters);
        var service = CreateService(options, novelService, chat, new RecordingBridgeEventSink());

        var startTask = service.StartExtractionAsync(
            ValidStartPayload("pattern-cancel-1", novel.Id),
            CancellationToken.None).AsTask();
        await chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelled = await service.CancelExtractionAsync(
            new CancelNarrativePatternExtractionPayload("pattern-cancel-1", "用户取消"),
            CancellationToken.None);
        var startResult = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal("cancelled", startResult.Status);
        Assert.True(chat.CancellationObserved);
        Assert.Contains(startResult.Diagnostics, diagnostic => diagnostic.Code == "pattern.cancelled");
    }

    [Fact]
    public async Task InvalidFinalSkillMarkdownMarksRunFailed()
    {
        var (options, novelService, novel, chapters) = await CreateNovelWithChaptersAsync(4);
        var chat = new ScriptedNarrativePatternChat(
            chapters,
            """
            ---
            name: 缺字段
            description: 少字段
            mode: auto
            ---
            # 缺字段
            """);
        var service = CreateService(options, novelService, chat, new RecordingBridgeEventSink());

        var failed = await service.StartExtractionAsync(
            ValidStartPayload("pattern-invalid-skill", novel.Id),
            CancellationToken.None);

        Assert.Equal("failed", failed.Status);
        Assert.Equal("skill_validation", failed.Stage);
        Assert.Contains(failed.Diagnostics, item =>
            item.Code == "pattern.invalid_skill" &&
            item.Detail.Contains("category", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidationRejectsUnsafePayloadsWithoutCallingModel()
    {
        var (options, novelService, novel, chapters) = await CreateNovelWithChaptersAsync(4);
        var chat = new ScriptedNarrativePatternChat(chapters, ValidSkillMarkdown("不会调用"));
        var service = CreateService(options, novelService, chat, new RecordingBridgeEventSink());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(ValidStartPayload("", novel.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(ValidStartPayload("pattern-missing-novel", novel.Id + 100), CancellationToken.None));
        await Assert.ThrowsAsync<NarrativePatternValidationException>(async () =>
            await service.StartExtractionAsync(
                ValidStartPayload("pattern-backward-range", novel.Id) with { ChapterRanges = [new ChapterRangePayload(3, 2)] },
                CancellationToken.None));
        await Assert.ThrowsAsync<NarrativePatternValidationException>(async () =>
            await service.StartExtractionAsync(
                ValidStartPayload("pattern-out-of-bounds", novel.Id) with { ChapterRanges = [new ChapterRangePayload(1, 9)] },
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

        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task BridgeNarrativePatternHandlersRunPipelineAndValidatePayloads()
    {
        var (options, novelService, novel, chapters) = await CreateNovelWithChaptersAsync(4);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNarrativePatternHandlers(
                CreateService(
                    options,
                    novelService,
                    new ScriptedNarrativePatternChat(chapters, ValidSkillMarkdown("桥接叙事模式")),
                    new RecordingBridgeEventSink()));

        using var startJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_start_pattern",
              "method": "StartNarrativePatternExtraction",
              "payload": {
                "args": [{
                  "task_id": "pattern-bridge-1",
                  "novel_id": {{novel.Id}},
                  "chapter_ranges": [{ "start_chapter": 1, "end_chapter": 4 }],
                  "provider_name": "fake-provider",
                  "model_id": "fake-model",
                  "reasoning_effort": "low",
                  "skill_name": "桥接叙事模式"
                }]
              }
            }
            """));
        Assert.True(startJson.RootElement.GetProperty("ok").GetBoolean());
        var started = startJson.RootElement.GetProperty("result");
        Assert.Equal("completed", started.GetProperty("status").GetString());
        Assert.Equal(4, started.GetProperty("selected_chapter_ids").GetArrayLength());

        using var traceJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get_pattern_trace",
              "method": "GetNarrativePatternTrace",
              "payload": { "args": [{ "task_id": "pattern-bridge-1" }] }
            }
            """));
        Assert.NotEmpty(traceJson.RootElement.GetProperty("result").GetProperty("entries").EnumerateArray());

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

    [Fact]
    public void DefaultNarrativePatternServiceCanBeConstructedWithoutLiveNetwork()
    {
        var options = CreateOptions();
        var service = new FileSystemNarrativePatternExtractionService(options);

        Assert.NotNull(service);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileSystemNarrativePatternExtractionService CreateService(
        AppInitializationOptions options,
        INovelService novelService,
        IChatCompletionClient chat,
        IBridgeEventSink events)
    {
        return new FileSystemNarrativePatternExtractionService(
            options,
            novelService,
            new FileSystemChapterContentService(
                options,
                novelService,
                versionControl: new NoOpVersionControlService()),
            chat,
            new FixedLlmConfigurationService(),
            events);
    }

    private async ValueTask<(AppInitializationOptions Options, INovelService NovelService, NovelPayload Novel, IReadOnlyList<ChapterPayload> Chapters)> CreateNovelWithChaptersAsync(int chapterCount)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options), new NoOpVersionControlService());
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("雨城档案", "测试作品", "悬疑"), CancellationToken.None);
        var chapters = await CreateChaptersAsync(options, novelService, novel.Id, chapterCount);
        return (options, novelService, novel, chapters);
    }

    private async ValueTask<IReadOnlyList<ChapterPayload>> CreateChaptersAsync(
        AppInitializationOptions options,
        INovelService novelService,
        long novelId,
        int count)
    {
        var service = new FileSystemChapterContentService(
            options,
            novelService,
            versionControl: new NoOpVersionControlService());
        var chapters = new List<ChapterPayload>();
        for (var index = 1; index <= count; index++)
        {
            var chapter = await service.CreateChapterAsync(
                new CreateChapterPayload(novelId, $"第{index}章 雨夜线索"),
                CancellationToken.None);
            var marker = index == 4 ? "UNSELECTED_CHAPTER_SECRET " : "";
            await service.SaveContentAsync(
                new SaveContentPayload(novelId, chapter.FilePath, LongChapterText(index, marker)),
                CancellationToken.None);
            chapters.Add((await service.GetChaptersAsync(novelId, CancellationToken.None)).Single(item => item.Id == chapter.Id));
        }

        return chapters;
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config", Guid.NewGuid().ToString("N")),
            DefaultDataDirectory = Path.Combine(_root, "data", Guid.NewGuid().ToString("N"))
        };
    }

    private static StartNarrativePatternExtractionPayload ValidStartPayload(string taskId, long novelId)
    {
        return new StartNarrativePatternExtractionPayload(
            TaskId: taskId,
            NovelId: novelId,
            ChapterRanges: [new ChapterRangePayload(1, 4)],
            ProviderName: "fake-provider",
            ModelId: "fake-model",
            ReasoningEffort: "low",
            SkillName: "雨夜悬疑结构");
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

    private static string ValidSkillMarkdown(string name)
    {
        return $$"""
            ---
            name: {{name}}
            description: 从所选章节归纳出的叙事模式。
            category: 叙事模式
            mode: auto
            author: ai
            version: 1
            ---
            # {{name}}

            ## 使用原则
            - 先用失踪案压低信息量。
            - 让每个阶段的证据改变读者判断。
            """;
    }

    private static string LongChapterText(int chapterNumber, string marker)
    {
        return string.Join(
            "\n",
            Enumerable.Range(1, 18).Select(index =>
                $"{marker}第{chapterNumber}章段落{index}。雨声压住街口，林岚把证词和旧案线索反复对照，人物的选择不断改变失踪案的方向。"));
    }

    private sealed class ScriptedNarrativePatternChat : IChatCompletionClient
    {
        private readonly IReadOnlyList<ChapterPayload> _chapters;
        private readonly string _skillMarkdown;
        private readonly Dictionary<int, string> _hashByChapter;
        private int _phaseAttempt;

        public ScriptedNarrativePatternChat(IReadOnlyList<ChapterPayload> chapters, string skillMarkdown)
        {
            _chapters = chapters;
            _skillMarkdown = skillMarkdown;
            _hashByChapter = chapters.ToDictionary(chapter => chapter.ChapterNumber, chapter => "");
        }

        public string? BoundaryJsonOverride { get; init; }

        public string? SummaryHashOverride { get; init; }

        public int EmptyPhaseResponsesBeforeSuccess { get; init; }

        public bool StallCompression { get; init; }

        public int PhaseCallCount { get; private set; }

        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Tools is null || request.Tools.Count == 0)
            {
                await Task.Yield();
                yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, _skillMarkdown);
                yield break;
            }

            var response = ResponseFor(request);
            await Task.Yield();
            yield return new ChatCompletionStreamEvent(
                ChatCompletionStreamEventKind.ToolCall,
                ToolCall: new ChatToolCall(
                    "call-1",
                    request.Tools?.FirstOrDefault()?.Name ?? "tool",
                    response));
        }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(_skillMarkdown);
        }

        private string ResponseFor(ChatCompletionRequest request)
        {
            var toolName = request.Tools?.FirstOrDefault()?.Name;
            var prompt = request.Messages.Select(item => item.Content).LastOrDefault() ?? "";
            return toolName switch
            {
                "submit_narrative_boundaries" => BoundaryJsonOverride ?? Boundaries(prompt),
                "submit_chapter_summaries" => Summaries(prompt),
                "submit_narrative_phases" => Phases(prompt),
                _ => "{}"
            };
        }

        private string Boundaries(string prompt)
        {
            var selected = ChapterNumbersFromPrompt(prompt);
            var segments = ContinuousSegments(selected);
            var items = segments.Select(segment =>
                $$"""{ "start_chapter": {{segment.Start}}, "end_chapter": {{segment.End}}, "label": "压低信息", "function": "建立失踪压力", "evidence": "雨夜、证词和旧案并置" }""");
            return $$"""
                {
                  "schema_version": "narrative-pattern-v1",
                  "boundaries": [
                    {{string.Join(",\n", items)}}
                  ]
                }
                """;
        }

        private string Summaries(string prompt)
        {
            var numbers = ChapterNumbersFromPrompt(prompt);
            var items = numbers.Select(number =>
            {
                var hash = SummaryHashOverride ?? ReadHash(prompt, number);
                return $$"""
                    {
                      "chapter_number": {{number}},
                      "content_hash": "{{hash}}",
                      "summary": "第{{number}}章通过雨夜证词推进失踪案，并让人物关系承压。",
                      "turning_points": ["证词改变", "旧案线索靠近"]
                    }
                    """;
            });
            return $$"""
                {
                  "schema_version": "narrative-pattern-v1",
                  "summaries": [
                    {{string.Join(",\n", items)}}
                  ]
                }
                """;
        }

        private string Phases(string prompt)
        {
            PhaseCallCount++;
            if (_phaseAttempt++ < EmptyPhaseResponsesBeforeSuccess)
            {
                return """{"schema_version":"narrative-pattern-v1","phases":[]}""";
            }

            var units = UnitRangesFromPrompt(prompt);
            if (StallCompression)
            {
                var stalled = units.Select(unit =>
                    $$"""{ "start_chapter": {{unit.Start}}, "end_chapter": {{unit.End}}, "phase_name": "阶段{{unit.Start}}", "narrative_function": "保持原粒度", "guidance": "不压缩。" }""");
                return $$"""
                    {
                      "schema_version": "narrative-pattern-v1",
                      "phases": [{{string.Join(",", stalled)}}]
                    }
                    """;
            }

            var segments = ContinuousSegments(units.SelectMany(unit => Enumerable.Range(unit.Start, unit.End - unit.Start + 1)).ToArray());
            var items = segments.Select(segment =>
                $$"""{ "start_chapter": {{segment.Start}}, "end_chapter": {{segment.End}}, "phase_name": "雨夜压迫到证据反转", "narrative_function": "压低信息后重组线索", "guidance": "用证词冲突推动阶段升级。" }""");
            return $$"""
                {
                  "schema_version": "narrative-pattern-v1",
                  "phases": [
                    {{string.Join(",\n", items)}}
                  ]
                }
                """;
        }

        private static IReadOnlyList<int> ChapterNumbersFromPrompt(string prompt)
        {
            return prompt.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("## chapter_number=", StringComparison.Ordinal))
                .Select(line => int.Parse(line["## chapter_number=".Length..]))
                .ToArray();
        }

        private static IReadOnlyList<(int Start, int End)> UnitRangesFromPrompt(string prompt)
        {
            return prompt.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("## unit chapters=", StringComparison.Ordinal))
                .Select(line =>
                {
                    var value = line["## unit chapters=".Length..];
                    var parts = value.Split('-', 2);
                    return (int.Parse(parts[0]), int.Parse(parts[1]));
                })
                .ToArray();
        }

        private static IReadOnlyList<(int Start, int End)> ContinuousSegments(IReadOnlyList<int> numbers)
        {
            var ordered = numbers.Distinct().Order().ToArray();
            if (ordered.Length == 0)
            {
                return [];
            }

            var segments = new List<(int Start, int End)>();
            var start = ordered[0];
            var end = ordered[0];
            for (var index = 1; index < ordered.Length; index++)
            {
                var number = ordered[index];
                if (number == end + 1)
                {
                    end = number;
                    continue;
                }

                segments.Add((start, end));
                start = number;
                end = number;
            }

            segments.Add((start, end));
            return segments;
        }

        private static string ReadHash(string prompt, int chapterNumber)
        {
            var lines = prompt.Split('\n').Select(line => line.Trim()).ToArray();
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index] == $"## chapter_number={chapterNumber}")
                {
                    var hashLine = lines.Skip(index + 1).First(line => line.StartsWith("content_hash: ", StringComparison.Ordinal));
                    return hashLine["content_hash: ".Length..].Trim();
                }
            }

            return "sha256:missing";
        }
    }

    private sealed class BlockingNarrativePatternChat : IChatCompletionClient
    {
        private readonly ScriptedNarrativePatternChat _inner;

        public BlockingNarrativePatternChat(IReadOnlyList<ChapterPayload> chapters)
        {
            _inner = new ScriptedNarrativePatternChat(chapters, ValidSkillMarkdown("不应完成"));
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            await foreach (var item in _inner.StreamChatAsync(request, cancellationToken))
            {
                yield return item;
            }
        }

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
        {
            return _inner.GenerateTextAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingBridgeEventSink : IBridgeEventSink
    {
        public List<(string Name, object? Payload)> Events { get; } = [];

        public ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add((name, payload));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedLlmConfigurationService : ILlmConfigurationService
    {
        public ValueTask<LlmConfigViewPayload> GetConfigAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new LlmConfigViewPayload(
            [
                new ProviderViewPayload(
                    "fake-provider",
                    "Fake Provider",
                    "https://example.invalid",
                    "chat",
                    "https://example.invalid/chat/completions",
                    "fake-key",
                    "",
                    "",
                    0,
                    "custom",
                    [new ModelInfoPayload("fake-model", "Fake Model", 16_000, 4_000, false, null, false)],
                    [])
            ]));
        }

        public ValueTask SaveConfigAsync(LlmConfigViewPayload input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<AvailableModelPayload>> GetModelsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<AvailableModelPayload>>(
            [
                new AvailableModelPayload(
                    "fake-provider/fake-model",
                    "Fake Provider",
                    "Fake Model",
                    16_000,
                    4_000,
                    false,
                    [],
                    false)
            ]);
        }

        public ValueTask<IReadOnlyList<ModelInfoPayload>> DiscoverModelsAsync(
            string baseUrl,
            string apiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ModelInfoPayload>>(
            [
                new ModelInfoPayload("fake-model", "Fake Model", 16_000, 4_000, false, null, false)
            ]);
        }

        public ValueTask TestConnectionAsync(TestConnectionPayload input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpVersionControlService : IVersionControlService
    {
        public ValueTask EnsureRepositoryAsync(long novelId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<VersionControlCommitResult> CommitIfChangedAsync(
            long novelId,
            string message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new VersionControlCommitResult(false, string.Empty));
        }

        public ValueTask<IReadOnlyList<VersionControlCommitInfo>> GetLogAsync(
            long novelId,
            string? relativePath,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<VersionControlCommitInfo>>([]);
        }

        public ValueTask<PageResultPayload<GitCommitSummaryPayload>> GetCommitSummariesAsync(
            GetGitCommitsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = input.Page <= 0 ? 1 : input.Page;
            var size = input.Size <= 0 ? 20 : input.Size;
            return ValueTask.FromResult(new PageResultPayload<GitCommitSummaryPayload>([], 0, page, size, 0));
        }

        public ValueTask<IReadOnlyList<GitCommitFilePayload>> GetCommitFilesAsync(
            GetGitCommitFilesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<GitCommitFilePayload>>([]);
        }

        public ValueTask<GitFileDiffPayload> GetFileDiffAsync(
            GetGitFileDiffPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new VersionControlException("No-op version control does not expose Git diffs.");
        }
    }
}
