using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceStyleDeterministicBaselineExtractorTests
{
    [Fact]
    public void BuildComputesRhythmDialogueParagraphAndHookFeatures()
    {
        var samples = new[]
        {
            Sample("s1", ReferenceMaterialTypes.Sentence, "dialogue", "spoken", "unknown", "dialogue_exchange", "她说：“先别开门。”"),
            Sample("s2", ReferenceMaterialTypes.Sentence, "interiority", "reflective", "close", "interiority", "他心里明白，雨声已经压住了脚步。"),
            Sample("s3", ReferenceMaterialTypes.Sentence, "environment", "neutral", "unknown", "sensory_detail", "冷风穿过门缝，灯光一寸寸暗下去。"),
            Sample("s4", ReferenceMaterialTypes.Sentence, "narration", "uncertain", "unknown", "plain", "门外忽然安静下来？"),
            Sample("p1", ReferenceMaterialTypes.Passage, "dialogue", "spoken", "unknown", "dialogue_exchange", "她说：“先别开门。”\n他没有回答。"),
            Sample("p2", ReferenceMaterialTypes.Passage, "environment", "neutral", "unknown", "sensory_detail", "冷风穿过门缝，灯光一寸寸暗下去。")
        };

        var baseline = ReferenceStyleDeterministicBaselineExtractor.Build(10, samples);

        Assert.Equal(6, Numeric(baseline, "material_count"));
        Assert.Equal(4, Numeric(baseline, "sentence_count"));
        Assert.Equal(2, Numeric(baseline, "paragraph_count"));
        Assert.Equal(0.25, Numeric(baseline, "dialogue_ratio"));
        Assert.Equal(0.25, Numeric(baseline, "interiority_ratio"));
        Assert.Equal(0.5, Numeric(baseline, "sensory_ratio"));
        Assert.Equal(0.25, Numeric(baseline, "hook_marker_ratio"));
        Assert.True(Numeric(baseline, "punctuation_per_100_chars") > 0);

        var paragraphDistribution = baseline.Features.DistributionFeatures.Single(feature => feature.FeatureKey == "paragraph_length_distribution");
        Assert.Equal(1.0, Math.Round(paragraphDistribution.Buckets.Sum(bucket => bucket.Weight), 4));
        Assert.Contains(paragraphDistribution.Buckets, bucket => bucket.Label == "short" && bucket.Weight > 0);

        var punctuationDistribution = baseline.Features.DistributionFeatures.Single(feature => feature.FeatureKey == "punctuation_rhythm_distribution");
        Assert.Contains(punctuationDistribution.Buckets, bucket => bucket.Label == "question" && bucket.Weight > 0);
        Assert.Contains(baseline.EvidenceSpans, evidence => evidence.FeatureKey == "dialogue_ratio" && evidence.Label == "dialogue_exchange");
        Assert.Contains(baseline.EvidenceSpans, evidence => evidence.FeatureKey == "hook_marker_ratio" && evidence.Label == "hook_marker");
    }

    [Fact]
    public void BuildProducesStableFeatureValuesForSameSamples()
    {
        var samples = new[]
        {
            Sample("s1", ReferenceMaterialTypes.Sentence, "dialogue", "spoken", "unknown", "dialogue_exchange", "她说：“先别开门。”"),
            Sample("s2", ReferenceMaterialTypes.Sentence, "interiority", "reflective", "close", "interiority", "他心里明白，雨声已经压住了脚步。"),
            Sample("p1", ReferenceMaterialTypes.Passage, "environment", "neutral", "unknown", "sensory_detail", "冷风穿过门缝，灯光一寸寸暗下去。")
        };

        var first = ReferenceStyleDeterministicBaselineExtractor.Build(10, samples);
        var second = ReferenceStyleDeterministicBaselineExtractor.Build(11, samples);

        Assert.Equal(first.AggregateConfidence, second.AggregateConfidence);
        Assert.Equal(FeatureSignature(first), FeatureSignature(second));
        Assert.NotEqual(
            first.EvidenceSpans.Select(evidence => evidence.EvidenceId).Order(StringComparer.Ordinal),
            second.EvidenceSpans.Select(evidence => evidence.EvidenceId).Order(StringComparer.Ordinal));
    }

    private static double Numeric(ReferenceStyleBaseline baseline, string featureKey)
    {
        return baseline.Features.NumericFeatures.Single(feature => feature.FeatureKey == featureKey).Value;
    }

    private static IReadOnlyList<string> FeatureSignature(ReferenceStyleBaseline baseline)
    {
        var numeric = baseline.Features.NumericFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => $"n:{feature.FeatureKey}:{feature.Value}:{feature.Unit}:{feature.Confidence}");
        var distributions = baseline.Features.DistributionFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => "d:" + feature.FeatureKey + ":" + string.Join(
                ";",
                feature.Buckets.Select(bucket => $"{bucket.Label}:{bucket.Min}:{bucket.Max}:{bucket.Weight}")));
        var categories = baseline.Features.CategoricalFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => $"c:{feature.FeatureKey}:{feature.Label}:{feature.Weight}:{feature.Confidence}");
        return numeric.Concat(distributions).Concat(categories).ToArray();
    }

    private static ReferenceStyleMaterialSample Sample(
        string id,
        string materialType,
        string functionTag,
        string emotionTag,
        string povTag,
        string techniqueTag,
        string text)
    {
        return new ReferenceStyleMaterialSample(
            "material-" + id,
            7,
            "segment-" + id,
            materialType,
            functionTag,
            emotionTag,
            "scene",
            povTag,
            techniqueTag,
            0.8,
            0.7,
            0.6,
            text,
            "hash-" + id,
            0,
            text.Length,
            "text-hash-" + id);
    }
}
