using System.Text.RegularExpressions;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

/// <summary>
/// 细纲 beat 级语料覆盖度实现：检索通路复用 <see cref="IReferenceAnchorService.SearchMaterialsAsync"/>，
/// 与聊天写作注入保持同一条通路（轻量化聚焦方案 §3 覆盖度信号）。
/// </summary>
public sealed partial class ChapterCorpusCoverageService : IChapterCorpusCoverageService
{
    private const string PlanScope = "next";
    private const int MaxBeats = 40;
    private const int QueryMaxLength = 48;
    private const double SufficientRatio = 0.5;

    private readonly IReferenceAnchorService _referenceAnchors;
    private readonly IPlanningService _planning;

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

        var beats = SplitBeats(plan?.Content);
        if (beats.Count == 0)
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

        var anchors = await _referenceAnchors.GetAnchorsAsync(input.NovelId, cancellationToken);
        var anchorTitles = anchors
            .Where(anchor => string.Equals(anchor.Status, ReferenceAnchorBuildStates.Ready, StringComparison.Ordinal))
            .ToDictionary(anchor => anchor.AnchorId, anchor => anchor.Title);

        var coveredCount = 0;
        var beatResults = new List<ChapterCorpusBeatCoveragePayload>(beats.Count);
        foreach (var beat in beats)
        {
            var hit = await SearchBeatAsync(input.NovelId, beat, cancellationToken);
            var covered = hit is not null;
            if (covered)
            {
                coveredCount++;
            }

            beatResults.Add(new ChapterCorpusBeatCoveragePayload(
                beat,
                covered,
                hit is not null && anchorTitles.TryGetValue(hit.AnchorId, out var title) ? title : null,
                hit?.Text));
        }

        var ratio = beats.Count == 0 ? 0 : (double)coveredCount / beats.Count;
        return new ChapterCorpusCoveragePayload(
            input.NovelId,
            input.ChapterNumber,
            PlanScope,
            beatResults,
            coveredCount,
            beats.Count,
            Math.Round(ratio, 4),
            ratio >= SufficientRatio);
    }

    private async ValueTask<ReferenceMaterialPayload?> SearchBeatAsync(long novelId, string beat, CancellationToken cancellationToken)
    {
        var query = BeatQueryRegex().Replace(beat, " ").Trim();
        if (query.Length > QueryMaxLength)
        {
            query = query[..QueryMaxLength];
        }

        if (query.Length == 0)
        {
            return null;
        }

        var page = await _referenceAnchors.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novelId,
                AnchorIds: [],
                Query: query,
                MaterialTypes: [],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 1),
            cancellationToken);

        // v1 阈值：检索返回任意命中即视为该 beat 已覆盖；相关度分数阈值待真实语料校准（开放问题 3）。
        return page.Items.FirstOrDefault();
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
            .Take(MaxBeats)
            .ToArray();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex BeatQueryRegex();
}
