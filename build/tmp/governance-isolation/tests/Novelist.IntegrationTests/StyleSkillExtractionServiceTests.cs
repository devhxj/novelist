using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class StyleSkillExtractionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExtractStyleSkillFromSamplesBuildsValidatedPreviewAndPersistsRun()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("雨城档案", "", ""), CancellationToken.None);
        var otherNovel = await novelService.CreateNovelAsync(new CreateNovelPayload("不相关作品", "", ""), CancellationToken.None);
        var styleSamples = new FileSystemStyleSampleService(options, novelService);
        var localSample = await styleSamples.CreateSampleAsync(
            new CreateStyleSamplePayload(
                novel.Id,
                IsGlobal: false,
                "近身内心动作",
                "她把伞柄压低，指节一点点发白。雨声把迟疑都挡在门外。",
                ["内心", "雨夜"],
                new StyleSampleSourceMetadataPayload("manual", "chapter-1", "sha256:local")),
            CancellationToken.None);
        var globalSample = await styleSamples.CreateSampleAsync(
            new CreateStyleSamplePayload(
                NovelId: null,
                IsGlobal: true,
                "全局雨夜节奏",
                "短句先落下。再给一个停顿。最后让对白把真相推近。",
                ["节奏"],
                new StyleSampleSourceMetadataPayload("manual", "global", "sha256:global")),
            CancellationToken.None);
        await styleSamples.CreateSampleAsync(
            new CreateStyleSamplePayload(
                otherNovel.Id,
                IsGlobal: false,
                "不相关样本",
                "UNRELATED_NOVEL_SECRET should never appear.",
                ["禁止"],
                new StyleSampleSourceMetadataPayload("manual", "other", "sha256:other")),
            CancellationToken.None);

        var chat = new RecordingChatCompletionClient(
            """
            ---
            name: 雨夜克制
            description: 从样本抽取出的克制雨夜文风。
            category: 风格仿写
            mode: auto
            author: ai
            version: 1
            ---
            # 雨夜克制

            ## 仿写要点
            - 短句推进。
            - 用动作承载心理变化。
            """);
        var events = new RecordingBridgeEventSink();
        var service = new FileSystemStyleSkillExtractionService(
            options,
            novelService,
            styleSamples,
            chat,
            events);

        var run = await service.StartExtractionAsync(
            new StartStyleSkillExtractionPayload(
                TaskId: "style-task-1",
                NovelId: novel.Id,
                SampleIds: [localSample.SampleId, globalSample.SampleId],
                ProviderName: "fake-provider",
                ModelId: "fake-model",
                ReasoningEffort: "low",
                SkillName: "雨夜克制"),
            CancellationToken.None);

        Assert.Equal("completed", run.Status);
        Assert.Equal("skill_preview", run.Stage);
        Assert.Equal(2, run.ProgressCompleted);
        Assert.Equal(2, run.ProgressTotal);
        Assert.Equal([localSample.SampleId, globalSample.SampleId], run.SampleIds);
        Assert.Equal("雨夜克制", run.SkillName);
        Assert.Contains("source_sample_ids: 1,2", run.SkillPreview, StringComparison.Ordinal);
        Assert.Contains("source_sample_hashes: sha256:local,sha256:global", run.SkillPreview, StringComparison.Ordinal);
        Assert.Contains("## 仿写要点", run.SkillPreview, StringComparison.Ordinal);
        Assert.Contains(run.Diagnostics, item => item.Code == "style_skill.preview_ready");

        var prompt = string.Join("\n", chat.Requests.Single().Messages.Select(message => message.Content));
        Assert.Contains("近身内心动作", prompt, StringComparison.Ordinal);
        Assert.Contains("全局雨夜节奏", prompt, StringComparison.Ordinal);
        Assert.Contains("word_count", prompt, StringComparison.Ordinal);
        Assert.Contains("sentence_length_distribution", prompt, StringComparison.Ordinal);
        Assert.Contains("她把伞柄压低", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("UNRELATED_NOVEL_SECRET", prompt, StringComparison.Ordinal);

        var reloaded = new FileSystemStyleSkillExtractionService(options, novelService, styleSamples, chat, events);
        var persisted = await reloaded.GetRunAsync(
            new GetNovelImportRunPayload("style-task-1"),
            CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("completed", persisted.Status);
        Assert.Equal("skills/雨夜克制.md", persisted.SkillFilePath);

        Assert.Contains(events.Events, item =>
            item.Name == "style_skill_extraction:progress" &&
            item.Payload is StyleSkillExtractionProgressPayload progress &&
            progress.Status == "completed");
    }

    [Fact]
    public async Task ExtractionRejectsUnauthorizedSamplesBeforeModelCall()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("雨城档案", "", ""), CancellationToken.None);
        var otherNovel = await novelService.CreateNovelAsync(new CreateNovelPayload("他人作品", "", ""), CancellationToken.None);
        var styleSamples = new FileSystemStyleSampleService(options, novelService);
        var otherSample = await styleSamples.CreateSampleAsync(
            new CreateStyleSamplePayload(
                otherNovel.Id,
                IsGlobal: false,
                "越界样本",
                "这段文本不属于当前作品。",
                [],
                null),
            CancellationToken.None);
        var chat = new RecordingChatCompletionClient(ValidSkillMarkdown("不会调用"));
        var service = new FileSystemStyleSkillExtractionService(options, novelService, styleSamples, chat);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartExtractionAsync(
                new StartStyleSkillExtractionPayload(
                    TaskId: "style-unauthorized",
                    NovelId: novel.Id,
                    SampleIds: [otherSample.SampleId],
                    ProviderName: "fake-provider",
                    ModelId: "fake-model",
                    ReasoningEffort: "",
                    SkillName: "越界"),
                CancellationToken.None));

        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task InvalidSkillMarkdownMarksRunFailedWithDiagnostics()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("雨城档案", "", ""), CancellationToken.None);
        var styleSamples = new FileSystemStyleSampleService(options, novelService);
        var sample = await styleSamples.CreateSampleAsync(
            new CreateStyleSamplePayload(novel.Id, false, "样本", "雨落下来。人没有说话。", [], null),
            CancellationToken.None);
        var chat = new RecordingChatCompletionClient(
            """
            ---
            name: 缺字段
            description: 少了必要字段。
            mode: auto
            ---
            # 缺字段
            """);
        var service = new FileSystemStyleSkillExtractionService(options, novelService, styleSamples, chat);

        var failed = await service.StartExtractionAsync(
            new StartStyleSkillExtractionPayload(
                TaskId: "style-invalid",
                NovelId: novel.Id,
                SampleIds: [sample.SampleId],
                ProviderName: "fake-provider",
                ModelId: "fake-model",
                ReasoningEffort: "",
                SkillName: "缺字段"),
            CancellationToken.None);

        Assert.Equal("failed", failed.Status);
        Assert.Equal("skill_validation", failed.Stage);
        Assert.Empty(failed.SkillPreview);
        Assert.Contains(failed.Diagnostics, item =>
            item.Code == "style_skill.invalid_frontmatter" &&
            item.Detail.Contains("category", StringComparison.Ordinal) &&
            item.Detail.Contains("author", StringComparison.Ordinal) &&
            item.Detail.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractionHandlesSkillFilenameCollisionsWithoutSavingPartialOutput()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("雨城档案", "", ""), CancellationToken.None);
        var existingSkillDirectory = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "skills");
        Directory.CreateDirectory(existingSkillDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(existingSkillDirectory, "雨夜克制.md"),
            ValidSkillMarkdown("雨夜克制"),
            CancellationToken.None);

        var styleSamples = new FileSystemStyleSampleService(options, novelService);
        var sample = await styleSamples.CreateSampleAsync(
            new CreateStyleSamplePayload(novel.Id, false, "样本", "雨落下来。人没有说话。", [], null),
            CancellationToken.None);
        var chat = new RecordingChatCompletionClient(ValidSkillMarkdown("雨夜克制"));
        var service = new FileSystemStyleSkillExtractionService(options, novelService, styleSamples, chat);

        var run = await service.StartExtractionAsync(
            new StartStyleSkillExtractionPayload(
                TaskId: "style-collision",
                NovelId: novel.Id,
                SampleIds: [sample.SampleId],
                ProviderName: "fake-provider",
                ModelId: "fake-model",
                ReasoningEffort: "",
                SkillName: "雨夜克制"),
            CancellationToken.None);

        Assert.Equal("completed", run.Status);
        Assert.Equal("雨夜克制-2", run.SkillName);
        Assert.Equal("skills/雨夜克制-2.md", run.SkillFilePath);
        Assert.Contains("name: 雨夜克制-2", run.SkillPreview, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(existingSkillDirectory, "雨夜克制-2.md")));
    }

    [Fact]
    public async Task CancellationStopsModelCallAndMarksRunCancelled()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("雨城档案", "", ""), CancellationToken.None);
        var styleSamples = new FileSystemStyleSampleService(options, novelService);
        var sample = await styleSamples.CreateSampleAsync(
            new CreateStyleSamplePayload(novel.Id, false, "样本", "雨落下来。人没有说话。", [], null),
            CancellationToken.None);
        var chat = new BlockingChatCompletionClient();
        var service = new FileSystemStyleSkillExtractionService(options, novelService, styleSamples, chat);

        var startTask = service.StartExtractionAsync(
            new StartStyleSkillExtractionPayload(
                TaskId: "style-cancel",
                NovelId: novel.Id,
                SampleIds: [sample.SampleId],
                ProviderName: "fake-provider",
                ModelId: "fake-model",
                ReasoningEffort: "",
                SkillName: "取消样本"),
            CancellationToken.None).AsTask();
        await chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelled = await service.CancelExtractionAsync(
            new CancelStyleSkillExtractionPayload("style-cancel", "用户取消"),
            CancellationToken.None);
        var startResult = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal("cancelled", startResult.Status);
        Assert.True(chat.CancellationObserved);
        Assert.Empty(startResult.SkillPreview);
        Assert.Contains(startResult.Diagnostics, item => item.Code == "style_skill.cancelled");
    }

    [Fact]
    public async Task BridgeStyleSkillExtractionHandlersOverrideCompatibilityBoundary()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("雨城档案", "", ""), CancellationToken.None);
        var styleSamples = new FileSystemStyleSampleService(options, novelService);
        var sample = await styleSamples.CreateSampleAsync(
            new CreateStyleSamplePayload(novel.Id, false, "样本", "雨落下来。人没有说话。", [], null),
            CancellationToken.None);
        var extraction = new FileSystemStyleSkillExtractionService(
            options,
            novelService,
            styleSamples,
            new RecordingChatCompletionClient(ValidSkillMarkdown("桥接风格")));
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterStyleSampleHandlers(styleSamples, extraction);

        using var startJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_style_extract",
              "method": "ExtractStyleSkillFromSamples",
              "payload": {
                "args": [{
                  "task_id": "style-bridge",
                  "novel_id": {{novel.Id}},
                  "sample_ids": [{{sample.SampleId}}],
                  "provider_name": "fake-provider",
                  "model_id": "fake-model",
                  "reasoning_effort": "",
                  "skill_name": "桥接风格"
                }]
              }
            }
            """));
        var started = startJson.RootElement.GetProperty("result");
        Assert.True(startJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("completed", started.GetProperty("status").GetString());
        Assert.Equal("skills/桥接风格.md", started.GetProperty("skill_file_path").GetString());

        using var getJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_style_get",
              "method": "GetStyleSkillExtractionRun",
              "payload": { "args": [{ "task_id": "style-bridge" }] }
            }
            """));
        Assert.Equal("completed", getJson.RootElement.GetProperty("result").GetProperty("status").GetString());
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

    private static string ValidSkillMarkdown(string name)
    {
        return $$"""
            ---
            name: {{name}}
            description: 可用技能。
            category: 风格仿写
            mode: auto
            author: ai
            version: 1
            ---
            # {{name}}
            """;
    }

    private sealed class RecordingChatCompletionClient : IChatCompletionClient
    {
        private readonly string _response;

        public RecordingChatCompletionClient(string response)
        {
            _response = response;
        }

        public List<ChatCompletionRequest> Requests { get; } = [];

        public IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(_response);
        }
    }

    private sealed class BlockingChatCompletionClient : IChatCompletionClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                return ValidSkillMarkdown("不应完成");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
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
}
