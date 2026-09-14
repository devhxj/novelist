namespace Novelist.Core.App;

// 结构化检索需求：代替"把章节计划截断 160 字当 query"。duty 词表与
// SqliteReferenceAnchorService 的 MatchesNarrativeDuty / MatchesProseDuty 对齐。
public sealed record CorpusNeed(
    string Query,
    IReadOnlyList<string> NarrativeDuties,
    IReadOnlyList<string> ProseDuties);

public static class CorpusNeedBuilder
{
    public const int PlanQueryMaxLength = 480;
    public const int BeatQueryMaxLength = 160;

    // 关键词 → duty 的确定性映射（可复核、可扩展）。命中即加入对应 duty 列表。
    private static readonly (string Duty, bool IsProse, string[] Keywords)[] KeywordMap =
    [
        ("interiority", false, ["内心", "心理", "独白", "思绪", "回想", "意识"]),
        ("subtext", false, ["对话", "台词", "交锋", "试探", "言外", "潜台词"]),
        ("transition", false, ["过渡", "转场", "衔接", "承接"]),
        ("causality", false, ["因果", "导致", "因为", "连锁", "后果"]),
        ("external_evidence", false, ["侧面", "证据", "旁人", "外部反应"]),
        ("source_backed_detail", true, ["环境", "天气", "雨", "风", "光线", "描写"]),
        ("sensory_anchor", true, ["声音", "气味", "触感", "温度", "感官", "听觉", "视觉"]),
        ("physical_afterbeat", true, ["动作", "打斗", "肢体", "余波"])
    ];

    public static CorpusNeed FromPlanText(string? planContent) =>
        Build(planContent, PlanQueryMaxLength);

    public static CorpusNeed FromBeat(string? beat) =>
        Build(beat, BeatQueryMaxLength);

    private static CorpusNeed Build(string? text, int maxLength)
    {
        var normalized = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length > maxLength)
        {
            normalized = normalized[..maxLength];
        }

        var narrative = new List<string>();
        var prose = new List<string>();
        foreach (var (duty, isProse, keywords) in KeywordMap)
        {
            if (!keywords.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal)))
            {
                continue;
            }

            (isProse ? prose : narrative).Add(duty);
        }

        return new CorpusNeed(normalized, narrative, prose);
    }
}
