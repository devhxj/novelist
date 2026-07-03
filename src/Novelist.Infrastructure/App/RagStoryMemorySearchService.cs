using System.Globalization;
using System.Text;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class RagStoryMemorySearchService : IStoryMemorySearchService
{
    private const int DefaultTopK = 5;
    private const int MaxTopK = 20;
    private const int MaxFetchK = 40;
    private const double DefaultMinRelevance = 0.5;
    private static readonly HashSet<string> AllowedChunkTypes = new(StringComparer.Ordinal)
    {
        "summary",
        "chapter_brief",
        "content"
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IChapterContentService _chapters;
    private readonly IRagIndexService _ragIndex;
    private readonly IRagSemanticSearchService _semanticSearch;

    public RagStoryMemorySearchService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IChapterContentService? chapters = null,
        IRagIndexService? ragIndex = null,
        IRagSemanticSearchService? semanticSearch = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _chapters = chapters ?? new FileSystemChapterContentService(_options, _novels);
        var defaultRag = ragIndex ?? new SqliteRagIndexService(_options, _novels, _chapters);
        _ragIndex = defaultRag;
        _semanticSearch = semanticSearch ?? defaultRag as IRagSemanticSearchService
            ?? throw new ArgumentException("RAG semantic search service is required.", nameof(semanticSearch));
    }

    public async ValueTask<SearchStoryMemoryResultPayload> SearchAsync(
        SearchStoryMemoryPayload input,
        CancellationToken cancellationToken)
    {
        var request = NormalizeInput(input);
        await EnsureNovelExistsAsync(request.NovelId, cancellationToken);
        await EnsureReadyIndexAsync(request.NovelId, cancellationToken);

        var fetchK = Math.Min(request.TopK * 2, MaxFetchK);
        var hits = await _semanticSearch.SearchAsync(
            request.NovelId,
            request.Query,
            fetchK,
            cancellationToken);

        var chapterFilter = request.ChapterNumbers.Count == 0
            ? null
            : request.ChapterNumbers.ToHashSet();
        var chunkTypeFilter = request.ChunkTypes.Count == 0
            ? null
            : request.ChunkTypes.ToHashSet(StringComparer.Ordinal);

        var filtered = hits
            .Where(hit => hit.Relevance >= request.MinRelevance)
            .Where(hit => chapterFilter is null || chapterFilter.Contains(hit.ChapterNumber))
            .Where(hit => chunkTypeFilter is null || chunkTypeFilter.Contains(hit.ChunkType))
            .OrderByDescending(hit => hit.Relevance)
            .ThenBy(hit => hit.ChapterNumber)
            .ThenBy(hit => hit.ChunkIndex)
            .Take(request.TopK)
            .ToArray();

        if (filtered.Length == 0)
        {
            return new SearchStoryMemoryResultPayload(
                request.Query,
                Total: 0,
                Message: "未找到相关记忆，可以尝试更换查询词或降低相关度阈值",
                MaxRelevance: string.Empty,
                Content: string.Empty,
                Results: []);
        }

        var chapterTitles = await LoadChapterTitlesAsync(request.NovelId, cancellationToken);
        var results = filtered
            .Select(hit => new StoryMemoryHitPayload(
                hit.ChunkId,
                hit.ChapterNumber,
                ResolveChapterTitle(chapterTitles, hit),
                hit.ChunkType,
                Math.Round(hit.Relevance, 4),
                hit.Content))
            .ToArray();

        var maxRelevance = filtered.Max(hit => hit.Relevance);
        return new SearchStoryMemoryResultPayload(
            request.Query,
            results.Length,
            Message: string.Empty,
            MaxRelevance: maxRelevance.ToString("0.00", CultureInfo.InvariantCulture),
            Content: FormatMarkdown(request.Query, results, maxRelevance),
            Results: results);
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private async ValueTask EnsureReadyIndexAsync(long novelId, CancellationToken cancellationToken)
    {
        var state = await _ragIndex.GetIndexStateAsync(novelId, cancellationToken);
        if (state is null)
        {
            throw RagUnavailable("语义索引尚未建立，请先重建小说索引。", "missing");
        }

        if (state.Status == "ready")
        {
            return;
        }

        var message = state.Status switch
        {
            "missing_config" => "Embeddings 配置缺失，请先在设置中配置 Embeddings API。",
            "stale" => "语义索引已过期，请先重建小说索引。",
            "failed" when !string.IsNullOrWhiteSpace(state.LastError) => $"语义索引不可用: {state.LastError}",
            "failed" => "语义索引不可用，请重建索引后重试。",
            "disabled" => "语义索引服务未启用。",
            _ => $"语义索引状态不是 ready: {state.Status}"
        };
        throw RagUnavailable(message, state.Status);
    }

    private static BridgeRequestException RagUnavailable(string message, string status)
    {
        return new BridgeRequestException(
            BridgeErrorCodes.RagUnavailable,
            message,
            details: new Dictionary<string, string> { ["status"] = status });
    }

    private async ValueTask<Dictionary<int, string>> LoadChapterTitlesAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        var chapters = await _chapters.GetChaptersAsync(novelId, cancellationToken);
        return chapters.ToDictionary(
            chapter => chapter.ChapterNumber,
            chapter => chapter.Title);
    }

    private static string ResolveChapterTitle(
        IReadOnlyDictionary<int, string> chapterTitles,
        RagSearchHitPayload hit)
    {
        return chapterTitles.TryGetValue(hit.ChapterNumber, out var title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : string.IsNullOrWhiteSpace(hit.Title)
                ? "未知章节"
                : hit.Title;
    }

    private static string FormatMarkdown(
        string query,
        IReadOnlyList<StoryMemoryHitPayload> results,
        double maxRelevance)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 语义搜索结果");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"**查询：** {query}  ");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"**结果数：** {results.Count}");

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"### {index + 1}. ");
            if (result.ChapterNumber > 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $"第{result.ChapterNumber}章 {result.ChapterTitle}");
            }
            else
            {
                builder.Append("未知章节");
            }

            builder.Append(CultureInfo.InvariantCulture, $" — {ChunkTypeLabel(result.ChunkType)}（相关度：{result.Relevance:0.00}）");
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(result.Content);
        }

        builder.AppendLine();
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"> 最高相关度：{maxRelevance:0.00} | 查询：{query}");
        return builder.ToString();
    }

    private static SearchStoryMemoryPayload NormalizeInput(SearchStoryMemoryPayload input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input.NovelId), input.NovelId, "Novel id must be positive.");
        }

        var query = (input.Query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query must be a non-empty string.", nameof(input.Query));
        }

        var topK = input.TopK == 0 ? DefaultTopK : input.TopK;
        if (topK is < 1 or > MaxTopK)
        {
            throw new ArgumentOutOfRangeException(nameof(input.TopK), input.TopK, $"TopK must be between 1 and {MaxTopK}.");
        }

        var minRelevance = input.MinRelevance == 0 ? DefaultMinRelevance : input.MinRelevance;
        if (double.IsNaN(minRelevance) || double.IsInfinity(minRelevance) || minRelevance is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input.MinRelevance), input.MinRelevance, "Min relevance must be between 0 and 1.");
        }

        var chapterNumbers = (input.ChapterNumbers ?? [])
            .Where(number => number > 0)
            .Distinct()
            .Order()
            .ToArray();
        var chunkTypes = (input.ChunkTypes ?? [])
            .Select(type => (type ?? string.Empty).Trim())
            .Where(type => type.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var chunkType in chunkTypes)
        {
            if (!AllowedChunkTypes.Contains(chunkType))
            {
                throw new ArgumentException("Chunk type must be summary, chapter_brief, or content.", nameof(input.ChunkTypes));
            }
        }

        return input with
        {
            Query = query,
            TopK = topK,
            MinRelevance = minRelevance,
            ChapterNumbers = chapterNumbers,
            ChunkTypes = chunkTypes
        };
    }

    private static string ChunkTypeLabel(string chunkType)
    {
        return chunkType switch
        {
            "summary" => "章节摘要",
            "chapter_brief" => "章节概要",
            "content" => "正文内容",
            _ => chunkType
        };
    }
}
