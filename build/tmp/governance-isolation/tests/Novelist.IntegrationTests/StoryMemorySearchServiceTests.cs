using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;
using System.Text.Json;

namespace Novelist.IntegrationTests;

public sealed class StoryMemorySearchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SearchStoryMemoryFormatsFilteredRagContexts()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, "林岚发现暗号"), CancellationToken.None);

        var semantic = new RecordingSemanticSearchService([
            new RagSearchHitPayload("c1", novel.Id, 1, "content", 0, 0, "林岚在旧城门发现暗号。", chapter.FilePath, chapter.Title, 0.08, 0.92),
            new RagSearchHitPayload("c2", novel.Id, 1, "summary", 1, 0, "摘要不应被 content 过滤命中。", chapter.FilePath, chapter.Title, 0.04, 0.96),
            new RagSearchHitPayload("c3", novel.Id, 2, "content", 0, 0, "其他章节。", "chapters/002.md", "第二章", 0.2, 0.8)
        ]);
        var service = new RagStoryMemorySearchService(
            options,
            novels,
            chapters,
            new ReadyRagIndexService(novel.Id),
            semantic);

        var result = await service.SearchAsync(
            new SearchStoryMemoryPayload(
                novel.Id,
                "旧城门暗号",
                TopK: 1,
                MinRelevance: 0.5,
                ChapterNumbers: [1],
                ChunkTypes: ["content"]),
            CancellationToken.None);

        Assert.Equal("旧城门暗号", result.Query);
        Assert.Equal(1, result.Total);
        Assert.Equal("0.92", result.MaxRelevance);
        Assert.Contains("## 语义搜索结果", result.Content, StringComparison.Ordinal);
        Assert.Contains("第1章 雾中来信", result.Content, StringComparison.Ordinal);
        Assert.Contains("正文内容（相关度：0.92）", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("摘要不应", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, semantic.LastTopK);
    }

    [Fact]
    public async Task SearchStoryMemoryReturnsStableEmptyResultAfterRelevanceFiltering()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);

        var service = new RagStoryMemorySearchService(
            options,
            novels,
            chapters,
            new ReadyRagIndexService(novel.Id),
            new RecordingSemanticSearchService([
                new RagSearchHitPayload("c1", novel.Id, 1, "content", 0, 0, "低相关内容", "chapters/001.md", "雾中来信", 0.7, 0.3)
            ]));

        var result = await service.SearchAsync(
            new SearchStoryMemoryPayload(novel.Id, "暗号", 5, 0.8, [], []),
            CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Contains("未找到相关记忆", result.Message, StringComparison.Ordinal);
        Assert.Equal("", result.Content);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task SearchStoryMemoryThrowsStableUnavailableErrorWhenIndexIsNotReady()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);

        var service = new RagStoryMemorySearchService(
            options,
            novels,
            chapters,
            new ReadyRagIndexService(novel.Id, status: "stale"),
            new RecordingSemanticSearchService([]));

        var error = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await service.SearchAsync(
                new SearchStoryMemoryPayload(novel.Id, "暗号", 5, 0.5, [], []),
                CancellationToken.None));

        Assert.Equal(BridgeErrorCodes.RagUnavailable, error.Code);
        Assert.Contains("已过期", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeWorkspaceHandlersDispatchSearchStoryMemory()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        var chapters = new FileSystemChapterContentService(options, novels);
        var world = new FileSystemWorldEntityService(options, novels);
        var planning = new FileSystemPlanningService(options, novels);
        var writing = new FileSystemWritingStatisticsService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);

        var memory = new RagStoryMemorySearchService(
            options,
            novels,
            chapters,
            new ReadyRagIndexService(novel.Id),
            new RecordingSemanticSearchService([
                new RagSearchHitPayload("c1", novel.Id, 1, "content", 0, 0, "林岚发现暗号", chapter.FilePath, chapter.Title, 0.1, 0.9)
            ]));
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterWorkspaceUtilityHandlers(
                new FileSystemSkillCatalogService(options, novels),
                new FileSystemWorkspaceSearchService(options, novels, chapters, world, planning),
                new FileSystemNovelExportService(novels, chapters, settings, new NullExportDestinationPicker()),
                writing,
                memory);

        var result = await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_memory",
              "method": "SearchStoryMemory",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "query": "暗号",
                    "top_k": 5,
                    "min_relevance": 0.5,
                    "chapter_numbers": [],
                    "chunk_types": []
                  }
                ]
              }
            }
            """);

        Assert.Contains("\"ok\":true", result.OutboundJson, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(result.OutboundJson!);
        Assert.Contains(
            "林岚发现暗号",
            json.RootElement.GetProperty("result").GetProperty("content").GetString(),
            StringComparison.Ordinal);
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

    private sealed class ReadyRagIndexService : IRagIndexService
    {
        private readonly long _novelId;
        private readonly string _status;

        public ReadyRagIndexService(long novelId, string status = "ready")
        {
            _novelId = novelId;
            _status = status;
        }

        public ValueTask<RagIndexStatePayload?> GetIndexStateAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RagIndexStatePayload?>(new RagIndexStatePayload(
                _novelId,
                "custom",
                "embed-v1",
                3,
                "paragraph-v1",
                _status,
                1,
                "vec_novel_1_3",
                string.Empty,
                DateTimeOffset.UtcNow));
        }

        public ValueTask<IReadOnlyList<RagChunkPayload>> GetIndexedChunksAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<RagChunkPayload>>([]);
        }

        public ValueTask<RagIndexStatePayload> RebuildNovelAsync(long novelId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingSemanticSearchService : IRagSemanticSearchService
    {
        private readonly IReadOnlyList<RagSearchHitPayload> _hits;

        public RecordingSemanticSearchService(IReadOnlyList<RagSearchHitPayload> hits)
        {
            _hits = hits;
        }

        public int LastTopK { get; private set; }

        public ValueTask<IReadOnlyList<RagSearchHitPayload>> SearchAsync(
            long novelId,
            string query,
            int topK,
            CancellationToken cancellationToken)
        {
            LastTopK = topK;
            return ValueTask.FromResult(_hits);
        }
    }

    private sealed class NullExportDestinationPicker : INovelExportDestinationPicker
    {
        public ValueTask<string?> PickSaveFileAsync(
            NovelExportDestinationRequest request,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }
}
