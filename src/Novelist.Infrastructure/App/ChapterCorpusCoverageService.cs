using System.Text.RegularExpressions;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

/// <summary>
/// 细纲 beat 级语料覆盖度实现：批量检索通路复用 <see cref="IReferenceAnchorService.SearchMaterialsBatchAsync"/>，
/// 与聊天写作注入同源（同一检索实现、同一 Ready 过滤，轻量化聚焦方案 §3 覆盖度信号）。
/// 单次计算只做一次材料全量读取；结果按细纲内容短 TTL 缓存，材料变化由手动刷新兜底。
/// </summary>
public sealed partial class ChapterCorpusCoverageService : IChapterCorpusCoverageService
{
    private const string PlanScope = "next";
    private const int MaxBeats = 40;
    private const int QueryMaxLength = 48;
    private const double SufficientRatio = 0.5;
    private const int BeatTextMaxLength = 200;
    private const int PreviewMaxLength = 200;
    private const int TitleMaxLength = 200;
    private const int MaxSourceBooks = 5;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    private readonly IReferenceAnchorService _referenceAnchors;
    private readonly IPlanningService _planning;
    private readonly object _cacheLock = new();
    private CacheEntry? _cache;

    public ChapterCorpusCoverageService(
        IReferenceAnchorService referenceAnchors,
        IPlanningService planning)
    {
        _referenceAnchors = referenceAnchors ?? throw new ArgumentNullException(nameof(referenceAnchors));
        _planning = planning ?? throw new ArgumentNullException(nameof(planning));
    }

    public async ValueTask<ChapterCorpusCoveragePayload> ComputeCoverageAsync(
        GetChapterCorpusCoveragePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.NovelId <= 0)
        {
            throw new ArgumentException("Novel id must be positive.", nameof(input));
        }

        var plans = await _planning.GetChapterPlansAsync(input.NovelId, cancellationToken);
        var plan = plans.FirstOrDefault(candidate => string.Equals(candidate.Scope, PlanScope, StringComparison.Ordinal));
        var planContent = plan?.Content ?? string.Empty;

        if (input.Refresh != true)
        {
            var cached = ReadCache(input.NovelId, planContent);
            if (cached is not null)
            {
                return cached;
            }
        }

        var computed = await ComputeCoreAsync(input, planContent, cancellationToken);
        WriteCache(input.NovelId, planContent, computed);
        return computed;
    }

    private async ValueTask<ChapterCorpusCoveragePayload> ComputeCoreAsync(
        GetChapterCorpusCoveragePayload input,
        string planContent,
        CancellationToken cancellationToken)
    {
        var allBeats = SplitBeats(planContent);
        var truncated = allBeats.Count > MaxBeats;
        var beats = allBeats.Take(MaxBeats).ToArray();
        if (beats.Length == 0)
        {
            return new ChapterCorpusCoveragePayload(
                input.NovelId,
                input.ChapterNumber,
                PlanScope,
                [],
                CoveredCount: 0,
                TotalCount: 0,
                CoverageRatio: 0,
                Sufficient: false);
        }

        // 与注入同口径：ReadyOnly 让"已覆盖"只统计会被实际注入的语料。
        // 空 query 的 beat 与原实现一致：直接判未覆盖，不进入检索。
        var queries = beats.Select(BuildQuery).ToArray();
        var batchInputs = new List<SearchReferenceMaterialsPayload>(beats.Length);
        var batchBeatIndexes = new List<int>(beats.Length);
        for (var index = 0; index < beats.Length; index++)
        {
            if (queries[index].Length == 0)
            {
                continue;
            }

            batchInputs.Add(new SearchReferenceMaterialsPayload(
                input.NovelId,
                AnchorIds: [],
                Query: queries[index],
                MaterialTypes: [],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 1,
                ReadyOnly: true));
            batchBeatIndexes.Add(index);
        }

        var pages = batchInputs.Count == 0
            ? []
            : await _referenceAnchors.SearchMaterialsBatchAsync(batchInputs, cancellationToken);
        var hits = new ReferenceMaterialPayload?[beats.Length];
        for (var batchIndex = 0; batchIndex < batchBeatIndexes.Count; batchIndex++)
        {
            hits[batchBeatIndexes[batchIndex]] = pages[batchIndex].Items.FirstOrDefault();
        }

        var anchorTitles = await BuildReadyAnchorTitlesAsync(input.NovelId, cancellationToken);
        var sourceBooks = anchorTitles.Values
            .OrderBy(title => title, StringComparer.Ordinal)
            .Take(MaxSourceBooks)
            .ToArray();

        var coveredCount = 0;
        var beatResults = new List<ChapterCorpusBeatCoveragePayload>(beats.Length);
        for (var index = 0; index < beats.Length; index++)
        {
            var hit = hits[index];
            var covered = hit is not null;
            if (covered)
            {
                coveredCount++;
            }

            beatResults.Add(new ChapterCorpusBeatCoveragePayload(
                Bound(beats[index], BeatTextMaxLength),
                covered,
                hit is not null && anchorTitles.TryGetValue(hit.AnchorId, out var title) ? Bound(title, TitleMaxLength) : null,
                hit is null ? null : Bound(hit.Text, PreviewMaxLength),
                hit is null ? null : MaterialHitScore(hit)));
        }

        var ratio = beats.Length == 0 ? 0 : (double)coveredCount / beats.Length;
        return new ChapterCorpusCoveragePayload(
            input.NovelId,
            input.ChapterNumber,
            PlanScope,
            beatResults,
            coveredCount,
            beats.Length,
            Math.Round(ratio, 4),
            ratio >= SufficientRatio,
            truncated,
            sourceBooks);
    }

    private async ValueTask<IReadOnlyDictionary<long, string>> BuildReadyAnchorTitlesAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        var anchors = await _referenceAnchors.GetAnchorsAsync(novelId, cancellationToken);
        return anchors
            .Where(anchor => string.Equals(anchor.Status, ReferenceAnchorBuildStates.Ready, StringComparison.Ordinal))
            .ToDictionary(anchor => anchor.AnchorId, anchor => anchor.Title);
    }

    internal static string Bound(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    // v1 观测字段：命中综合分（ScoreComponents 各分量之和），供后续相关度阈值校准（开放问题 3）。
    private static double? MaterialHitScore(ReferenceMaterialPayload hit)
    {
        var components = hit.ScoreComponents;
        return components is null || components.Count == 0 ? null : components.Values.Sum();
    }

    private static string BuildQuery(string beat)
    {
        var query = BeatQueryRegex().Replace(beat, " ").Trim();
        // v1 阈值：检索返回任意命中即视为该 beat 已覆盖；相关度分数阈值待真实语料校准（开放问题 3）。
        return query.Length > QueryMaxLength ? query[..QueryMaxLength] : query;
    }

    internal static IReadOnlyList<string> SplitBeats(string? planContent)
    {
        if (string.IsNullOrWhiteSpace(planContent))
        {
            return [];
        }

        return planContent
            .Split('\n')
            .Select(line => line.Trim().TrimStart('-', '*', '+', ' ', '·').Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private ChapterCorpusCoveragePayload? ReadCache(long novelId, string planContent)
    {
        lock (_cacheLock)
        {
            var cache = _cache;
            if (cache is null || cache.NovelId != novelId || !string.Equals(cache.PlanContent, planContent, StringComparison.Ordinal))
            {
                return null;
            }

            return UtcNow() - cache.CreatedAt <= CacheLifetime ? cache.Payload : null;
        }
    }

    private void WriteCache(long novelId, string planContent, ChapterCorpusCoveragePayload payload)
    {
        lock (_cacheLock)
        {
            _cache = new CacheEntry(novelId, planContent, payload, UtcNow());
        }
    }

    private static DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;

    private sealed record CacheEntry(long NovelId, string PlanContent, ChapterCorpusCoveragePayload Payload, DateTimeOffset CreatedAt);

    [GeneratedRegex(@"\s+")]
    private static partial Regex BeatQueryRegex();
}
