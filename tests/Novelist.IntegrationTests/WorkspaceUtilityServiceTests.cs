using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class WorkspaceUtilityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SkillsListDeduplicatesByNovelUserBuiltinPriorityAndDrivesSlashCommands()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);

        Directory.CreateDirectory(Path.Combine(options.DefaultDataDirectory, "skills"));
        await File.WriteAllTextAsync(
            Path.Combine(options.DefaultDataDirectory, "skills", "review.md"),
            SkillDocument("review", "用户级审稿", "manual"),
            CancellationToken.None);

        Directory.CreateDirectory(Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "skills"));
        await File.WriteAllTextAsync(
            Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "skills", "next.md"),
            SkillDocument("next", "小说级下一章", "always"),
            CancellationToken.None);

        var service = new FileSystemSkillCatalogService(options, novelService);
        var skills = await service.ListSkillsAsync(new ListSkillsPayload(novel.Id), CancellationToken.None);

        Assert.Equal("novel", skills.Single(item => item.Name == "next").Source);
        Assert.Equal("user", skills.Single(item => item.Name == "review").Source);
        Assert.Contains(skills, item => item.Source == "builtin" && item.Name == "collect");

        var slash = await service.ListSlashCommandsAsync(new ListSlashCommandsPayload(novel.Id), CancellationToken.None);
        Assert.Contains(slash, item => item.Name == "next" && item.Type == "always");
        Assert.Contains(slash, item => item.Name == "review" && item.Type == "manual");

        await service.DeleteSkillAsync(new DeleteSkillPayload(novel.Id, "review", "user"), CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(options.DefaultDataDirectory, "skills", "review.md")));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.DeleteSkillAsync(new DeleteSkillPayload(novel.Id, "collect", "builtin"), CancellationToken.None));
    }

    [Fact]
    public async Task ContentServiceReadsVirtualSkillPathsAndKeepsBuiltinReadOnly()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("群星边境", "", ""), CancellationToken.None);
        var content = new FileSystemChapterContentService(options, novelService);

        Directory.CreateDirectory(Path.Combine(options.DefaultDataDirectory, "skills"));
        await File.WriteAllTextAsync(
            Path.Combine(options.DefaultDataDirectory, "skills", "voice.md"),
            SkillDocument("voice", "用户级声线", "auto"),
            CancellationToken.None);

        var userSkill = await content.GetContentAsync(novel.Id, "~/.goink/skills/voice.md", CancellationToken.None);
        Assert.Contains("用户级声线", userSkill);

        var builtinSkill = await content.GetContentAsync(novel.Id, "/builtin/skills/next.md", CancellationToken.None);
        Assert.Contains("name: next", builtinSkill);

        await Assert.ThrowsAsync<InvalidContentPathException>(async () =>
            await content.SaveContentAsync(
                new SaveContentPayload(novel.Id, "/builtin/skills/next.md", "bad"),
                CancellationToken.None));
    }

    [Fact]
    public async Task ExtractStyleSupportsResponsesEndpointConfiguration()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "output_text": "---\nname: extracted-style\ndescription: responses 风格\ncategory: 测试\nmode: auto\nauthor: ai\nversion: 1\n---\n# extracted-style\n\n保持短句。"
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        var llm = new FileSystemLlmConfigurationService(options, new HttpClient(handler));
        await llm.SaveConfigAsync(
            new LlmConfigViewPayload([
                new ProviderViewPayload(
                    "custom-responses",
                    "Custom Responses",
                    "https://api.example.com/v1/responses",
                    "responses",
                    "",
                    "sk-secret",
                    "",
                    "",
                    0.2,
                    "custom",
                    [],
                    [new ModelInfoPayload("model-a", "Model A", 128_000, 4096, false, [], false)])
            ]),
            CancellationToken.None);
        var service = new FileSystemSkillCatalogService(
            options,
            novelService,
            llm,
            new HttpClient(handler));

        var result = await service.ExtractStyleAsync(
            new ExtractStylePayload(novel.Id, "这是用于提取风格的样本文本。", "custom-responses", "model-a", ""),
            CancellationToken.None);

        Assert.Equal("extracted-style", result.Name);
        Assert.Equal("responses 风格", result.Description);
        Assert.Equal("skills/extracted-style.md", result.FilePath);
        Assert.Contains("保持短句", result.RawContent, StringComparison.Ordinal);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.example.com/v1/responses", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Equal("model-a", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("messages", out _));
        Assert.Equal("system", body.RootElement.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal(4096, body.RootElement.GetProperty("max_output_tokens").GetInt32());
    }

    [Fact]
    public async Task SearchAllReturnsEntityChapterAndContentMatchesWithoutVectorIndex()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var chapterService = new FileSystemChapterContentService(options, novelService);
        var world = new FileSystemWorldEntityService(options, novelService);
        var planning = new FileSystemPlanningService(options, novelService);

        await world.CreateCharacterAsync(novel.Id, new CreateCharacterPayload("林岚", "旧城记者", "", "[]"), CancellationToken.None);
        await world.CreateLocationAsync(novel.Id, new CreateLocationPayload("旧城门", "城门", "线索聚集地", "{}", null, "[]"), CancellationToken.None);
        await planning.CreateTimelineEntryAsync(
            novel.Id,
            new CreateTimelineEntryPayload("foreshadowing", "旧城门暗号", "林岚在门边发现暗号", TargetChapter: 1),
            CancellationToken.None);
        await planning.CreateStoryArcAsync(
            novel.Id,
            new CreateStoryArcPayload("旧城门追踪线", "main", "围绕旧城门展开"),
            CancellationToken.None);

        var chapter = await chapterService.CreateChapterAsync(new CreateChapterPayload(novel.Id, "旧城门来信"), CancellationToken.None);
        await chapterService.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "林岚站在旧城门下，读完了那封信。"),
            CancellationToken.None);

        var search = new FileSystemWorkspaceSearchService(options, novelService, chapterService, world, planning);
        var results = await search.SearchAllAsync(novel.Id, "旧城门", CancellationToken.None);

        Assert.Contains(results, item => item.Type == "location" && item.PanelId == "locations");
        Assert.Contains(results, item => item.Type == "timeline" && item.PanelId == "timeline");
        Assert.Contains(results, item => item.Type == "storyarc" && item.PanelId == "storyarcs");
        Assert.Contains(results, item => item.Type == "chapter" && item.FilePath == "chapters/001.md");

        var contentMatch = Assert.Single(results, item => item.Type == "content");
        Assert.Equal("旧城门", contentMatch.MatchHit);
        Assert.Equal(4, contentMatch.MatchPosition);
        Assert.Equal(1, contentMatch.Relevance);

        Assert.Empty(await search.SearchAllAsync(novel.Id, "   ", CancellationToken.None));
        await search.RebuildNovelIndexAsync(novel.Id, CancellationToken.None);
    }

    [Fact]
    public async Task SearchAllKeepsExactResultsWhenSemanticSearchFails()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var chapterService = new FileSystemChapterContentService(options, novelService);
        var world = new FileSystemWorldEntityService(options, novelService);
        var planning = new FileSystemPlanningService(options, novelService);
        var chapter = await chapterService.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        await chapterService.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "林岚发现暗号"),
            CancellationToken.None);

        var search = new FileSystemWorkspaceSearchService(
            options,
            novelService,
            chapterService,
            world,
            planning,
            new RecordingRagIndexService(),
            new ThrowingSemanticSearchService());

        var results = await search.SearchAllAsync(novel.Id, "暗号", CancellationToken.None);

        var contentMatch = Assert.Single(results, item => item.Type == "content");
        Assert.Equal("暗号", contentMatch.MatchHit);
        Assert.DoesNotContain(results, item => item.Type == "rag");
    }

    [Fact]
    public async Task SearchAllPropagatesSemanticSearchCancellation()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var chapterService = new FileSystemChapterContentService(options, novelService);
        var world = new FileSystemWorldEntityService(options, novelService);
        var planning = new FileSystemPlanningService(options, novelService);
        var search = new FileSystemWorkspaceSearchService(
            options,
            novelService,
            chapterService,
            world,
            planning,
            new RecordingRagIndexService(),
            new CancelingSemanticSearchService());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await search.SearchAllAsync(novel.Id, "暗号", CancellationToken.None));
    }

    [Fact]
    public async Task SearchAllAddsSemanticChunkHitsWhenRagIndexIsReady()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("星图档案", "", ""), CancellationToken.None);
        var chapterService = new FileSystemChapterContentService(options, novelService);
        var world = new FileSystemWorldEntityService(options, novelService);
        var planning = new FileSystemPlanningService(options, novelService);
        var chapter = await chapterService.CreateChapterAsync(new CreateChapterPayload(novel.Id, "观测记录"), CancellationToken.None);
        await chapterService.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "第一段只是背景。\n\n第二段记录了隐藏航线和星图坐标。"),
            CancellationToken.None);

        var vec = new RecordingSqliteVecProvider();
        var embeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var rag = new SqliteRagIndexService(
            options,
            novelService,
            chapterService,
            new StaticEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "custom",
                "https://api.example.com/v1/embeddings",
                "sk-secret",
                "embed-v1",
                3,
                null)),
            embeddings,
            vec,
            vec);

        await rag.RebuildNovelAsync(novel.Id, CancellationToken.None);
        var indexedVectors = Assert.Single(vec.Provisions).Vectors;
        var semanticRowId = indexedVectors
            .Single(item => item.ChunkId.StartsWith($"{novel.Id}:1:1:", StringComparison.Ordinal))
            .RowId;
        vec.SearchRecords.Add(new SqliteVecSearchRecord(semanticRowId, 0.08));

        var search = new FileSystemWorkspaceSearchService(
            options,
            novelService,
            chapterService,
            world,
            planning,
            rag,
            rag);

        var results = await search.SearchAllAsync(novel.Id, "星图线索", CancellationToken.None);

        var semantic = Assert.Single(results, item => item.Type == "rag");
        Assert.Equal("观测记录", semantic.Title);
        Assert.Equal("chapters/001.md", semantic.FilePath);
        Assert.Equal("chapters", semantic.PanelId);
        Assert.Equal("语义匹配", semantic.Subtitle);
        Assert.Contains("隐藏航线", semantic.MatchPrefix, StringComparison.Ordinal);
        Assert.Equal(0.92, semantic.Relevance);
        Assert.Equal(["星图线索"], embeddings.Requests.Last());
    }

    [Fact]
    public async Task ExportNovelWritesMarkdownTxtAndValidEpubToSelectedDestination()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveUserNameAsync("测试作者", CancellationToken.None);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "旧城谜案", "悬疑"), CancellationToken.None);
        var chapterService = new FileSystemChapterContentService(options, novelService);
        var chapter = await chapterService.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        await chapterService.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "第一段\n\n**重点**"),
            CancellationToken.None);

        var picker = new RecordingExportDestinationPicker(Path.Combine(_root, "exports"));
        var service = new FileSystemNovelExportService(novelService, chapterService, settings, picker);

        await service.ExportNovelAsync(novel.Id, "markdown", CancellationToken.None);
        var markdown = await File.ReadAllTextAsync(picker.LastSavedPath!, CancellationToken.None);
        Assert.Contains("# 长夜档案", markdown);
        Assert.Contains("## 第1章 雾中来信", markdown);

        await service.ExportNovelAsync(novel.Id, "txt", CancellationToken.None);
        var txt = await File.ReadAllTextAsync(picker.LastSavedPath!, CancellationToken.None);
        Assert.Contains("第1章 雾中来信", txt);

        await service.ExportNovelAsync(novel.Id, "epub", CancellationToken.None);
        await using var epubStream = File.OpenRead(picker.LastSavedPath!);
        using var zip = new ZipArchive(epubStream, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("mimetype"));
        Assert.NotNull(zip.GetEntry("META-INF/container.xml"));
        Assert.NotNull(zip.GetEntry("OEBPS/content.opf"));
        Assert.Contains(zip.Entries, entry => entry.FullName.StartsWith("OEBPS/chapters/chapter-1", StringComparison.Ordinal));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ExportNovelAsync(novel.Id, "pdf", CancellationToken.None));
    }

    [Fact]
    public async Task ChapterSavesRecordWritingDeltasAndStatsAggregatePositiveActivity()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero));
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var writing = new FileSystemWritingStatisticsService(options, novelService, clock);
        var chapterService = new FileSystemChapterContentService(options, novelService, writing);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var chapter = await chapterService.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);

        await chapterService.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "你好 world"),
            CancellationToken.None);
        clock.UtcNow = new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero);
        await chapterService.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "你好 world plus"),
            CancellationToken.None);
        await chapterService.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "你好"),
            CancellationToken.None);

        var activity = await writing.GetWritingActivityAsync(12, CancellationToken.None);
        Assert.Equal(
            [new DailyActivityPayload("2026-07-01", 3), new DailyActivityPayload("2026-07-02", 1)],
            activity);

        var stats = await writing.GetWritingStatsAsync(CancellationToken.None);
        Assert.Equal(4, stats.TotalWords);
        Assert.Equal(2, stats.TotalDaysActive);
        Assert.Equal(2, stats.CurrentStreak);
        Assert.Equal(2, stats.LongestStreak);
        Assert.Equal(1, stats.TotalNovels);
        Assert.Equal(1, stats.TotalChapters);
    }

    [Fact]
    public async Task BridgeWorkspaceUtilityHandlersDispatchRepresentativeMethods()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var writing = new FileSystemWritingStatisticsService(options, novelService);
        var chapterService = new FileSystemChapterContentService(options, novelService, writing);
        var world = new FileSystemWorldEntityService(options, novelService);
        var planning = new FileSystemPlanningService(options, novelService);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var chapter = await chapterService.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        await chapterService.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, "林岚发现暗号"), CancellationToken.None);

        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterWorkspaceUtilityHandlers(
                new FileSystemSkillCatalogService(options, novelService),
                new FileSystemWorkspaceSearchService(options, novelService, chapterService, world, planning),
                new FileSystemNovelExportService(novelService, chapterService, settings, new RecordingExportDestinationPicker(Path.Combine(_root, "bridge-exports"))),
                writing);

        using var skills = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_skills",
              "method": "ListSkills",
              "payload": { "args": [{ "novel_id": {{novel.Id}} }] }
            }
            """));
        Assert.True(skills.RootElement.GetProperty("result").GetArrayLength() > 0);

        using var search = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_search",
              "method": "SearchAll",
              "payload": { "args": [{{novel.Id}}, "暗号"] }
            }
            """));
        Assert.Equal("content", search.RootElement.GetProperty("result")[0].GetProperty("type").GetString());

        using var stats = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_stats",
              "method": "GetWritingStats",
              "payload": {}
            }
            """));
        Assert.Equal(6, stats.RootElement.GetProperty("result").GetProperty("total_words").GetInt32());

        using var rebuild = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_rebuild",
              "method": "RebuildNovelIndex",
              "payload": { "args": [{{novel.Id}}] }
            }
            """));
        Assert.True(rebuild.RootElement.GetProperty("ok").GetBoolean());
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

    private static string SkillDocument(string name, string description, string mode)
    {
        return $$"""
            ---
            name: {{name}}
            description: {{description}}
            category: 测试
            mode: {{mode}}
            author: test
            version: 1
            ---
            # {{name}}
            """;
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private sealed class RecordingExportDestinationPicker : INovelExportDestinationPicker
    {
        private readonly string _directory;

        public RecordingExportDestinationPicker(string directory)
        {
            _directory = directory;
        }

        public string? LastSavedPath { get; private set; }

        public ValueTask<string?> PickSaveFileAsync(
            NovelExportDestinationRequest request,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_directory);
            LastSavedPath = Path.Combine(_directory, request.DefaultFileName);
            return ValueTask.FromResult<string?>(LastSavedPath);
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }

    private sealed class ThrowingSemanticSearchService : IRagSemanticSearchService
    {
        public ValueTask<IReadOnlyList<RagSearchHitPayload>> SearchAsync(
            long novelId,
            string query,
            int topK,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("semantic search is unavailable");
        }
    }

    private sealed class CancelingSemanticSearchService : IRagSemanticSearchService
    {
        public ValueTask<IReadOnlyList<RagSearchHitPayload>> SearchAsync(
            long novelId,
            string query,
            int topK,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class RecordingRagIndexService : IRagIndexService
    {
        public ValueTask<RagIndexStatePayload?> GetIndexStateAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RagIndexStatePayload?>(null);
        }

        public ValueTask<IReadOnlyList<RagChunkPayload>> GetIndexedChunksAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<RagChunkPayload>>([]);
        }

        public ValueTask<RagIndexStatePayload> RebuildNovelAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new RagIndexStatePayload(
                novelId,
                string.Empty,
                string.Empty,
                0,
                "paragraph-v1",
                "ready",
                0,
                string.Empty,
                string.Empty,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class StaticEmbeddingConfigurationService : IEmbeddingConfigurationService
    {
        private readonly EmbeddingRequestOptions? _options;

        public StaticEmbeddingConfigurationService(EmbeddingRequestOptions? options)
        {
            _options = options;
        }

        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_options);
        }
    }

    private sealed class DeterministicEmbeddingClient : IEmbeddingClient
    {
        private readonly int _dimensions;

        public DeterministicEmbeddingClient(int dimensions)
        {
            _dimensions = dimensions;
        }

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(inputs.ToArray());
            var items = inputs
                .Select((input, index) => new EmbeddingItemResult(
                    index,
                    Enumerable.Range(0, _dimensions)
                        .Select(offset => (float)(input.Length + offset))
                        .ToArray()))
                .ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                _dimensions,
                items,
                new EmbeddingUsage(0, inputs.Sum(input => input.Length))));
        }
    }

    private sealed class RecordingSqliteVecProvider : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
    {
        public List<SqliteVecProvisionRequest> Provisions { get; } = [];

        public List<SqliteVecSearchRecord> SearchRecords { get; } = [];

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Provisions.Add(request);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
            string databasePath,
            SqliteVecSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<SqliteVecSearchRecord>>(
                SearchRecords.Take(request.TopK).ToArray());
        }
    }
}
