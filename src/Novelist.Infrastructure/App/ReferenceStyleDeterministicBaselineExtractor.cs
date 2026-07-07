using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceStyleDeterministicBaselineExtractor
{
    private static readonly string[] DialogueMarkers = ["“", "”", "「", "」", "『", "』", "\"", "说：", "道：", "问：", "答："];
    private static readonly string[] SensoryMarkers = ["雨", "风", "雪", "光", "声", "呼吸", "气味", "冷", "热", "疼", "黑", "亮"];
    private static readonly string[] InteriorityMarkers = ["心", "想", "觉得", "明白", "知道", "意识到", "记得", "忘了"];
    private static readonly string[] ActionMarkers = ["走", "停", "看", "拿", "推", "转", "站", "坐", "伸", "退", "进", "出"];
    private static readonly string[] TransitionMarkers = ["后来", "然后", "这时", "与此同时", "片刻", "很快", "直到"];
    private static readonly string[] HookMarkers = ["？", "?", "！", "!", "却", "忽然", "只剩", "再也", "门外", "身后"];

    public static ReferenceStyleBaseline Build(
        long profileId,
        IReadOnlyList<ReferenceStyleMaterialSample> materials)
    {
        if (materials.Count == 0)
        {
            throw new ArgumentException("At least one material sample is required.", nameof(materials));
        }

        var sentenceRows = materials
            .Where(material => string.Equals(material.MaterialType, ReferenceMaterialTypes.Sentence, StringComparison.Ordinal))
            .ToArray();
        var passageRows = materials
            .Where(material => string.Equals(material.MaterialType, ReferenceMaterialTypes.Passage, StringComparison.Ordinal))
            .ToArray();
        var primaryRows = sentenceRows.Length > 0 ? sentenceRows : materials;
        var textLength = Math.Max(1, primaryRows.Sum(row => row.Text.Length));
        var evidence = new List<ReferenceStyleEvidenceSpanPayload>();

        AddEvidence(evidence, profileId, "dialogue_ratio", "dialogue_exchange", primaryRows.Where(IsDialogueMaterial), maxCount: 5);
        AddEvidence(evidence, profileId, "interiority_ratio", "interiority", primaryRows.Where(IsInteriorityMaterial), maxCount: 5);
        AddEvidence(evidence, profileId, "sensory_ratio", "sensory_detail", primaryRows.Where(IsSensoryMaterial), maxCount: 5);
        AddEvidence(evidence, profileId, "action_afterbeat_ratio", "afterbeat", primaryRows.Where(IsAfterbeatMaterial), maxCount: 5);
        AddEvidence(evidence, profileId, "transition_ratio", "transition", primaryRows.Where(IsTransitionMaterial), maxCount: 5);
        AddEvidence(evidence, profileId, "hook_marker_ratio", "hook_marker", primaryRows.Where(IsHookMaterial), maxCount: 5);

        var dominantFunction = TopLabel(materials.Select(material => material.FunctionTag));
        var dominantEmotion = TopLabel(materials.Select(material => material.EmotionTag));
        var dominantPov = TopLabel(materials.Select(material => material.PovTag));
        var dominantTechnique = TopLabel(materials.Select(material => material.TechniqueTag));
        AddEvidence(evidence, profileId, "dominant_function", dominantFunction, materials.Where(material => IsSame(material.FunctionTag, dominantFunction)), maxCount: 5);
        AddEvidence(evidence, profileId, "dominant_emotion", dominantEmotion, materials.Where(material => IsSame(material.EmotionTag, dominantEmotion)), maxCount: 5);
        AddEvidence(evidence, profileId, "dominant_pov", dominantPov, materials.Where(material => IsSame(material.PovTag, dominantPov)), maxCount: 5);
        AddEvidence(evidence, profileId, "dominant_technique", dominantTechnique, materials.Where(material => IsSame(material.TechniqueTag, dominantTechnique)), maxCount: 5);
        AddEvidence(evidence, profileId, "average_sentence_chars", "length_sample", RepresentativeRows(sentenceRows), maxCount: 3);
        AddEvidence(evidence, profileId, "average_paragraph_chars", "length_sample", RepresentativeRows(passageRows), maxCount: 3);

        var evidenceByFeature = evidence
            .GroupBy(item => item.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var sentenceLengths = sentenceRows.Select(row => row.Text.Length).ToArray();
        var passageLengths = passageRows.Select(row => row.Text.Length).ToArray();
        var punctuationCount = primaryRows.Sum(row => CountPunctuation(row.Text));
        var confidence = ComputeAggregateConfidence(materials.Count, evidence.Count);

        var features = new ReferenceStyleFeatureVectorPayload(
            NumericFeatures:
            [
                Numeric("material_count", materials.Count, "count", confidence, []),
                Numeric("sentence_count", sentenceRows.Length, "count", confidence, []),
                Numeric("paragraph_count", passageRows.Length, "count", confidence, []),
                Numeric("average_sentence_chars", Average(sentenceLengths), "chars", confidence, EvidenceIds(evidenceByFeature, "average_sentence_chars")),
                Numeric("median_sentence_chars", Median(sentenceLengths), "chars", confidence, EvidenceIds(evidenceByFeature, "average_sentence_chars")),
                Numeric("average_paragraph_chars", Average(passageLengths), "chars", confidence, EvidenceIds(evidenceByFeature, "average_paragraph_chars")),
                Numeric("dialogue_ratio", Ratio(primaryRows.Count(IsDialogueMaterial), primaryRows.Count), "ratio", confidence, EvidenceIds(evidenceByFeature, "dialogue_ratio")),
                Numeric("interiority_ratio", Ratio(primaryRows.Count(IsInteriorityMaterial), primaryRows.Count), "ratio", confidence, EvidenceIds(evidenceByFeature, "interiority_ratio")),
                Numeric("sensory_ratio", Ratio(primaryRows.Count(IsSensoryMaterial), primaryRows.Count), "ratio", confidence, EvidenceIds(evidenceByFeature, "sensory_ratio")),
                Numeric("action_afterbeat_ratio", Ratio(primaryRows.Count(IsAfterbeatMaterial), primaryRows.Count), "ratio", confidence, EvidenceIds(evidenceByFeature, "action_afterbeat_ratio")),
                Numeric("transition_ratio", Ratio(primaryRows.Count(IsTransitionMaterial), primaryRows.Count), "ratio", confidence, EvidenceIds(evidenceByFeature, "transition_ratio")),
                Numeric("hook_marker_ratio", Ratio(primaryRows.Count(IsHookMaterial), primaryRows.Count), "ratio", confidence, EvidenceIds(evidenceByFeature, "hook_marker_ratio")),
                Numeric("punctuation_per_100_chars", Math.Round(punctuationCount * 100.0 / textLength, 4), "per_100_chars", confidence, [])
            ],
            DistributionFeatures:
            [
                Distribution("sentence_length_distribution", "chars", BuildLengthBuckets(sentenceLengths, 20, 60, 120), confidence, EvidenceIds(evidenceByFeature, "average_sentence_chars")),
                Distribution("paragraph_length_distribution", "chars", BuildLengthBuckets(passageLengths, 80, 200, 500), confidence, EvidenceIds(evidenceByFeature, "average_paragraph_chars")),
                Distribution("punctuation_rhythm_distribution", "ratio", BuildPunctuationBuckets(primaryRows), confidence, [])
            ],
            CategoricalFeatures:
            [
                Category("dominant_function", dominantFunction, LabelWeight(materials.Select(material => material.FunctionTag), dominantFunction), confidence, EvidenceIds(evidenceByFeature, "dominant_function")),
                Category("dominant_emotion", dominantEmotion, LabelWeight(materials.Select(material => material.EmotionTag), dominantEmotion), confidence, EvidenceIds(evidenceByFeature, "dominant_emotion")),
                Category("dominant_pov", dominantPov, LabelWeight(materials.Select(material => material.PovTag), dominantPov), confidence, EvidenceIds(evidenceByFeature, "dominant_pov")),
                Category("dominant_technique", dominantTechnique, LabelWeight(materials.Select(material => material.TechniqueTag), dominantTechnique), confidence, EvidenceIds(evidenceByFeature, "dominant_technique"))
            ]);

        return new ReferenceStyleBaseline(
            features,
            evidence,
            confidence,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["material_count"] = materials.Count.ToString(CultureInfo.InvariantCulture),
                ["sentence_count"] = sentenceRows.Length.ToString(CultureInfo.InvariantCulture),
                ["passage_count"] = passageRows.Length.ToString(CultureInfo.InvariantCulture),
                ["analyzer"] = ReferenceStyleAnalyzerSources.DeterministicBaseline
            });
    }

    private static ReferenceStyleNumericFeaturePayload Numeric(
        string key,
        double value,
        string unit,
        double confidence,
        IReadOnlyList<string> evidenceIds)
    {
        return new ReferenceStyleNumericFeaturePayload(
            key,
            Math.Round(value, 4),
            unit,
            Math.Round(confidence, 4),
            evidenceIds);
    }

    private static ReferenceStyleDistributionFeaturePayload Distribution(
        string key,
        string unit,
        IReadOnlyList<ReferenceStyleDistributionBucketPayload> buckets,
        double confidence,
        IReadOnlyList<string> evidenceIds)
    {
        return new ReferenceStyleDistributionFeaturePayload(
            key,
            unit,
            buckets,
            Math.Round(confidence, 4),
            evidenceIds);
    }

    private static ReferenceStyleCategoricalFeaturePayload Category(
        string key,
        string label,
        double weight,
        double confidence,
        IReadOnlyList<string> evidenceIds)
    {
        return new ReferenceStyleCategoricalFeaturePayload(
            key,
            label,
            Math.Round(weight, 4),
            Math.Round(confidence, 4),
            evidenceIds);
    }

    private static IReadOnlyList<ReferenceStyleDistributionBucketPayload> BuildLengthBuckets(
        IReadOnlyList<int> lengths,
        int shortMax,
        int mediumMax,
        int longMax)
    {
        if (lengths.Count == 0)
        {
            return
            [
                new ReferenceStyleDistributionBucketPayload("short", 0, shortMax, 0),
                new ReferenceStyleDistributionBucketPayload("medium", shortMax + 1, mediumMax, 0),
                new ReferenceStyleDistributionBucketPayload("long", mediumMax + 1, longMax, 0),
                new ReferenceStyleDistributionBucketPayload("very_long", longMax + 1, 1000000, 0)
            ];
        }

        return
        [
            new ReferenceStyleDistributionBucketPayload("short", 0, shortMax, Ratio(lengths.Count(value => value <= shortMax), lengths.Count)),
            new ReferenceStyleDistributionBucketPayload("medium", shortMax + 1, mediumMax, Ratio(lengths.Count(value => value > shortMax && value <= mediumMax), lengths.Count)),
            new ReferenceStyleDistributionBucketPayload("long", mediumMax + 1, longMax, Ratio(lengths.Count(value => value > mediumMax && value <= longMax), lengths.Count)),
            new ReferenceStyleDistributionBucketPayload("very_long", longMax + 1, 1000000, Ratio(lengths.Count(value => value > longMax), lengths.Count))
        ];
    }

    private static IReadOnlyList<ReferenceStyleDistributionBucketPayload> BuildPunctuationBuckets(
        IReadOnlyList<ReferenceStyleMaterialSample> materials)
    {
        var punctuationCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["comma"] = materials.Sum(material => CountAny(material.Text, '，', ',')),
            ["period"] = materials.Sum(material => CountAny(material.Text, '。', '.')),
            ["question"] = materials.Sum(material => CountAny(material.Text, '？', '?')),
            ["exclamation"] = materials.Sum(material => CountAny(material.Text, '！', '!')),
            ["semicolon"] = materials.Sum(material => CountAny(material.Text, '；', ';')),
            ["ellipsis"] = materials.Sum(material => material.Text.Split("……", StringSplitOptions.None).Length - 1)
        };
        var total = Math.Max(1, punctuationCounts.Values.Sum());
        return punctuationCounts
            .Select(pair => new ReferenceStyleDistributionBucketPayload(pair.Key, 0, 1, Ratio(pair.Value, total)))
            .ToArray();
    }

    private static IReadOnlyList<ReferenceStyleMaterialSample> RepresentativeRows(IReadOnlyList<ReferenceStyleMaterialSample> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ordered = rows.OrderBy(row => row.Text.Length).ToArray();
        return
        [
            ordered[0],
            ordered[ordered.Length / 2],
            ordered[^1]
        ];
    }

    private static void AddEvidence(
        List<ReferenceStyleEvidenceSpanPayload> evidence,
        long profileId,
        string featureKey,
        string label,
        IEnumerable<ReferenceStyleMaterialSample> candidates,
        int maxCount)
    {
        foreach (var material in candidates
            .Where(material => !string.IsNullOrWhiteSpace(material.MaterialId))
            .GroupBy(material => material.MaterialId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(maxCount))
        {
            evidence.Add(new ReferenceStyleEvidenceSpanPayload(
                BuildEvidenceId(profileId, featureKey, label, material),
                profileId,
                material.AnchorId,
                material.SourceSegmentId,
                material.MaterialId,
                featureKey,
                label,
                material.StartOffset,
                material.EndOffset,
                material.TextHash,
                MaterialConfidence(material),
                ReferenceStyleAnalyzerSources.DeterministicBaseline));
        }
    }

    private static string BuildEvidenceId(
        long profileId,
        string featureKey,
        string label,
        ReferenceStyleMaterialSample material)
    {
        var hash = HashText(string.Join(
            "|",
            profileId.ToString(CultureInfo.InvariantCulture),
            featureKey,
            label,
            material.MaterialId,
            material.SourceSegmentId,
            material.TextHash,
            material.StartOffset.ToString(CultureInfo.InvariantCulture),
            material.EndOffset.ToString(CultureInfo.InvariantCulture)));
        return "style-evidence-" + hash[..16];
    }

    private static double ComputeAggregateConfidence(int materialCount, int evidenceCount)
    {
        var materialScore = Math.Min(0.25, materialCount / 80.0);
        var evidenceScore = Math.Min(0.20, evidenceCount / 60.0);
        return Math.Round(0.55 + materialScore + evidenceScore, 4);
    }

    private static double MaterialConfidence(ReferenceStyleMaterialSample material)
    {
        return Math.Round(Math.Clamp(
            (material.FunctionConfidence + material.EmotionConfidence + material.PovConfidence) / 3.0,
            0.1,
            1.0),
            4);
    }

    private static bool IsDialogueMaterial(ReferenceStyleMaterialSample material)
    {
        return IsSame(material.FunctionTag, "dialogue") ||
            IsSame(material.TechniqueTag, "dialogue_exchange") ||
            ContainsAny(material.Text, DialogueMarkers);
    }

    private static bool IsInteriorityMaterial(ReferenceStyleMaterialSample material)
    {
        return IsSame(material.FunctionTag, "interiority") ||
            IsSame(material.TechniqueTag, "interiority") ||
            ContainsAny(material.Text, InteriorityMarkers);
    }

    private static bool IsSensoryMaterial(ReferenceStyleMaterialSample material)
    {
        return IsSame(material.FunctionTag, "environment") ||
            IsSame(material.TechniqueTag, "sensory_detail") ||
            ContainsAny(material.Text, SensoryMarkers);
    }

    private static bool IsAfterbeatMaterial(ReferenceStyleMaterialSample material)
    {
        return IsSame(material.TechniqueTag, "afterbeat") ||
            (IsSame(material.FunctionTag, "action") && ContainsAny(material.Text, ActionMarkers));
    }

    private static bool IsTransitionMaterial(ReferenceStyleMaterialSample material)
    {
        return IsSame(material.FunctionTag, "transition") ||
            IsSame(material.TechniqueTag, "transition") ||
            ContainsAny(material.Text, TransitionMarkers);
    }

    private static bool IsHookMaterial(ReferenceStyleMaterialSample material)
    {
        return ContainsAny(material.Text, HookMarkers);
    }

    private static string TopLabel(IEnumerable<string> labels)
    {
        return labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .GroupBy(label => label, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "unknown";
    }

    private static double LabelWeight(IEnumerable<string> labels, string selected)
    {
        var materialized = labels.Where(label => !string.IsNullOrWhiteSpace(label)).ToArray();
        return materialized.Length == 0
            ? 0
            : Ratio(materialized.Count(label => IsSame(label, selected)), materialized.Length);
    }

    private static double Average(IReadOnlyList<int> values)
    {
        return values.Count == 0 ? 0 : values.Average();
    }

    private static double Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2.0
            : ordered[middle];
    }

    private static double Ratio(int count, int total)
    {
        return total <= 0 ? 0 : Math.Round(count / (double)total, 4);
    }

    private static int CountPunctuation(string text)
    {
        return text.Count(value => value is '，' or ',' or '。' or '.' or '？' or '?' or '！' or '!' or '；' or ';' or '：' or ':');
    }

    private static int CountAny(string text, char first, char second)
    {
        return text.Count(value => value == first || value == second);
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> markers)
    {
        return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsSame(string value, string expected)
    {
        return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> EvidenceIds(
        IReadOnlyDictionary<string, string[]> evidenceByFeature,
        string featureKey)
    {
        return evidenceByFeature.TryGetValue(featureKey, out var evidenceIds) ? evidenceIds : [];
    }

    private static string HashText(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

internal sealed record ReferenceStyleMaterialSample(
    string MaterialId,
    long AnchorId,
    string SourceSegmentId,
    string MaterialType,
    string FunctionTag,
    string EmotionTag,
    string SceneTag,
    string PovTag,
    string TechniqueTag,
    double FunctionConfidence,
    double EmotionConfidence,
    double PovConfidence,
    string Text,
    string SourceHash,
    int StartOffset,
    int EndOffset,
    string TextHash);

internal sealed record ReferenceStyleBaseline(
    ReferenceStyleFeatureVectorPayload Features,
    IReadOnlyList<ReferenceStyleEvidenceSpanPayload> EvidenceSpans,
    double AggregateConfidence,
    IReadOnlyDictionary<string, string> Diagnostics);
