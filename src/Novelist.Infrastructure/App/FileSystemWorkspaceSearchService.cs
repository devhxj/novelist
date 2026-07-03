using System.Globalization;
using System.Text;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemWorkspaceSearchService : IWorkspaceSearchService
{
    private const int EntityLimit = 5;
    private const int ContentLimit = 10;
    private const int RagTopK = 8;
    private const int ContextRadius = 15;

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IChapterContentService _chapters;
    private readonly IWorldEntityService _world;
    private readonly IPlanningService _planning;
    private readonly IRagIndexService _ragIndex;
    private readonly IRagSemanticSearchService? _semanticSearch;

    public FileSystemWorkspaceSearchService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IChapterContentService? chapters = null,
        IWorldEntityService? world = null,
        IPlanningService? planning = null,
        IRagIndexService? ragIndex = null,
        IRagSemanticSearchService? semanticSearch = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _chapters = chapters ?? new FileSystemChapterContentService(_options, _novels);
        _world = world ?? new FileSystemWorldEntityService(_options, _novels);
        _planning = planning ?? new FileSystemPlanningService(_options, _novels);
        _ragIndex = ragIndex ?? new SqliteRagIndexService(_options, _novels, _chapters);
        _semanticSearch = semanticSearch ?? _ragIndex as IRagSemanticSearchService;
    }

    public async ValueTask<IReadOnlyList<SearchResultPayload>> SearchAllAsync(
        long novelId,
        string query,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length == 0)
        {
            return [];
        }

        await EnsureNovelExistsAsync(novelId, cancellationToken);

        var results = new List<SearchResultPayload>();
        results.AddRange(await SearchEntitiesAsync(novelId, normalizedQuery, cancellationToken));
        results.AddRange(await SearchContentAsync(novelId, normalizedQuery, cancellationToken));
        results.AddRange(await SearchSemanticAsync(novelId, normalizedQuery, cancellationToken));
        return results;
    }

    public async ValueTask RebuildNovelIndexAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        await _ragIndex.RebuildNovelAsync(novelId, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<SearchResultPayload>> SearchEntitiesAsync(
        long novelId,
        string query,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResultPayload>();

        results.AddRange((await _world.GetCharactersAsync(novelId, cancellationToken))
            .Where(item => Matches(query, item.Name, item.Description, item.Personality, item.Abilities))
            .Take(EntityLimit)
            .Select(item => Result("character", item.Id, item.Name, string.Empty, 0, string.Empty, "characters")));

        results.AddRange((await _world.GetLocationsAsync(novelId, cancellationToken))
            .Where(item => Matches(query, item.Name, item.Description, item.LocationType, item.Tags, item.DetailJson))
            .Take(EntityLimit)
            .Select(item => Result("location", item.Id, item.Name, item.LocationType, 0, string.Empty, "locations")));

        results.AddRange((await _planning.GetTimelineEntriesAsync(novelId, 0, 0, cancellationToken))
            .Where(item => Matches(query, item.Title, item.Content, item.Category, item.DetailJson))
            .Take(EntityLimit)
            .Select(item => Result("timeline", item.Id, item.Title, TimelineSubtitle(item.Category), item.TargetChapter, string.Empty, "timeline")));

        results.AddRange((await _planning.GetStoryArcsAsync(novelId, cancellationToken))
            .Where(item => Matches(query, item.Name, item.Description, item.ArcType, item.Status))
            .Take(EntityLimit)
            .Select(item => Result("storyarc", item.Id, item.Name, item.ArcType, 0, string.Empty, "storyarcs")));

        results.AddRange((await _chapters.GetChaptersAsync(novelId, cancellationToken))
            .Where(item => Matches(query, item.Title, item.Summary))
            .Take(EntityLimit)
            .Select(item => Result("chapter", item.Id, item.Title, "标题匹配", item.ChapterNumber, item.FilePath, "chapters")));

        return results;
    }

    private async ValueTask<IReadOnlyList<SearchResultPayload>> SearchContentAsync(
        long novelId,
        string query,
        CancellationToken cancellationToken)
    {
        var chapters = await _chapters.GetChaptersAsync(novelId, cancellationToken);
        var results = new List<SearchResultPayload>();

        foreach (var chapter in chapters.OrderBy(item => item.ChapterNumber))
        {
            var content = await _chapters.GetContentAsync(novelId, chapter.FilePath, cancellationToken);
            var searchFrom = 0;
            while (results.Count < ContentLimit)
            {
                var index = content.IndexOf(query, searchFrom, StringComparison.Ordinal);
                if (index < 0)
                {
                    break;
                }

                var runePosition = RuneCountBefore(content, index);
                var (prefix, hit, suffix) = BuildContext(content, runePosition, query);
                results.Add(new SearchResultPayload(
                    "content",
                    0,
                    chapter.Title,
                    string.Empty,
                    chapter.ChapterNumber,
                    chapter.FilePath,
                    prefix,
                    hit,
                    suffix,
                    runePosition,
                    query.EnumerateRunes().Count(),
                    1,
                    "chapters"));

                searchFrom = index + query.Length;
            }

            if (results.Count >= ContentLimit)
            {
                break;
            }
        }

        return results;
    }

    private async ValueTask<IReadOnlyList<SearchResultPayload>> SearchSemanticAsync(
        long novelId,
        string query,
        CancellationToken cancellationToken)
    {
        if (_semanticSearch is null)
        {
            return [];
        }

        IReadOnlyList<RagSearchHitPayload> hits;
        try
        {
            hits = await _semanticSearch.SearchAsync(novelId, query, RagTopK, cancellationToken);
        }
        catch
        {
            return [];
        }

        return hits
            .Where(hit => hit.Relevance >= 0.3)
            .OrderByDescending(hit => hit.Relevance)
            .ThenBy(hit => hit.ChapterNumber)
            .ThenBy(hit => hit.ChunkIndex)
            .Take(RagTopK)
            .Select(hit => new SearchResultPayload(
                "rag",
                0,
                hit.Title,
                "语义匹配",
                hit.ChapterNumber,
                hit.FilePath,
                SemanticPreview(hit.Content),
                string.Empty,
                string.Empty,
                hit.StartPosition,
                hit.Content.EnumerateRunes().Count(),
                Math.Round(hit.Relevance, 4),
                "chapters"))
            .ToArray();
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private static SearchResultPayload Result(
        string type,
        long id,
        string title,
        string subtitle,
        int chapterNumber,
        string filePath,
        string panelId)
    {
        return new SearchResultPayload(
            type,
            id,
            title,
            subtitle,
            chapterNumber,
            filePath,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0,
            0,
            panelId);
    }

    private static bool Matches(string query, params string[] values)
    {
        return values.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string TimelineSubtitle(string category)
    {
        return category switch
        {
            "foreshadowing" => "伏笔",
            "user_directive" => "用户指令",
            _ => category
        };
    }

    private static int RuneCountBefore(string content, int utf16Index)
    {
        return content[..utf16Index].EnumerateRunes().Count();
    }

    private static (string Prefix, string Hit, string Suffix) BuildContext(
        string content,
        int matchRunePosition,
        string query)
    {
        var runes = content.EnumerateRunes().ToArray();
        var queryLength = query.EnumerateRunes().Count();
        var start = Math.Max(0, matchRunePosition - ContextRadius);
        var end = Math.Min(runes.Length, matchRunePosition + queryLength + ContextRadius);

        var prefix = start > 0 ? "..." : string.Empty;
        prefix += RunesToString(runes, start, matchRunePosition - start);
        var hit = RunesToString(runes, matchRunePosition, queryLength);
        var suffix = RunesToString(runes, matchRunePosition + queryLength, end - (matchRunePosition + queryLength));
        if (end < runes.Length)
        {
            suffix += "...";
        }

        return (prefix, hit, suffix);
    }

    private static string SemanticPreview(string content)
    {
        var runes = content.EnumerateRunes().ToArray();
        var max = Math.Min(200, runes.Length);
        var preview = RunesToString(runes, 0, max);
        return max < runes.Length ? preview + "..." : preview;
    }

    private static string RunesToString(Rune[] runes, int start, int count)
    {
        var builder = new StringBuilder();
        for (var i = start; i < start + count && i < runes.Length; i++)
        {
            builder.Append(runes[i]);
        }

        return builder.ToString();
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }
}
