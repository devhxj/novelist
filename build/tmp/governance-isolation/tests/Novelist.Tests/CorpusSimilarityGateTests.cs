using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class CorpusSimilarityGateTests
{
    [Fact]
    public void EvaluateNormalizesWhitespaceAndPunctuationBeforeMeasuring()
    {
        var result = ReferenceCorpusSimilarityGate.Evaluate(
            new ReferenceCorpusSimilarityPiece(
                PieceId: "piece-1",
                SourceNodeId: "node-1",
                SourceText: "他 在门口，停住。12",
                OutputText: "他在门口。停住！12"),
            new ReferenceCorpusSimilarityPolicy(
                MaxFourGramContainmentRatio: 1,
                MaxLongestCommonSubstringRatio: 1));

        Assert.False(result.ShouldBlock);
        Assert.Equal("他在门口#停住#12", ReferenceCorpusSimilarityGate.NormalizeForComparison("他 在门口，停住。12"));
        Assert.Equal("他在门口#停住#12", ReferenceCorpusSimilarityGate.NormalizeForComparison("他在门口。停住！12"));
        Assert.Equal(1, result.FourGramContainmentRatio, precision: 6);
        Assert.Equal(1, result.LongestCommonSubstringRatio, precision: 6);
    }

    [Fact]
    public void EvaluateManyBlocksAtPieceLevelInsteadOfDilutingAcrossAWholeChapter()
    {
        var result = ReferenceCorpusSimilarityGate.EvaluateMany(
            [
                new ReferenceCorpusSimilarityPiece(
                    PieceId: "copied-piece",
                    SourceNodeId: "node-copied",
                    SourceText: "雨声压低长街呼吸他在门口停住",
                    OutputText: "雨声压低长街呼吸他在门口停住"),
                new ReferenceCorpusSimilarityPiece(
                    PieceId: "rewritten-piece",
                    SourceNodeId: "node-rewritten",
                    SourceText: "窗外灯火摇了一下",
                    OutputText: "远处钟声落下之后新的线索才被拿到桌面上")
            ],
            new ReferenceCorpusSimilarityPolicy(
                MaxFourGramContainmentRatio: 0.8,
                MaxLongestCommonSubstringRatio: 0.8));

        Assert.True(result.ShouldBlock);
        Assert.Single(result.BlockedPieces);
        Assert.Equal("copied-piece", result.BlockedPieces[0].PieceId);
    }

    [Fact]
    public void EvaluateReportsFourGramContainmentAgainstOutputGramSet()
    {
        var result = ReferenceCorpusSimilarityGate.Evaluate(
            new ReferenceCorpusSimilarityPiece(
                PieceId: "piece-1",
                SourceNodeId: "node-1",
                SourceText: "甲乙丙丁戊己庚辛壬癸",
                OutputText: "甲乙丙丁戊己子丑"),
            new ReferenceCorpusSimilarityPolicy(
                MaxFourGramContainmentRatio: 0.5,
                MaxLongestCommonSubstringRatio: 1));

        Assert.True(result.ShouldBlock);
        Assert.Equal(0.6, result.FourGramContainmentRatio, precision: 6);
        Assert.Contains(
            result.Violations,
            violation => violation.Metric == ReferenceCorpusSimilarityMetrics.FourGramContainment);
    }

    [Fact]
    public void EvaluateBlocksWhenLongestCommonSubstringRatioExceedsEvenIfContainmentIsAllowed()
    {
        var result = ReferenceCorpusSimilarityGate.Evaluate(
            new ReferenceCorpusSimilarityPiece(
                PieceId: "piece-1",
                SourceNodeId: "node-1",
                SourceText: "风压着屋檐他没有抬头只把钥匙扣进掌心",
                OutputText: "新的线索放到桌上风压着屋檐他没有抬头只把钥匙扣进掌心众人终于安静下来"),
            new ReferenceCorpusSimilarityPolicy(
                MaxFourGramContainmentRatio: 1,
                MaxLongestCommonSubstringRatio: 0.45));

        Assert.True(result.ShouldBlock);
        Assert.True(result.LongestCommonSubstringRatio > 0.45);
        Assert.Contains(
            result.Violations,
            violation => violation.Metric == ReferenceCorpusSimilarityMetrics.LongestCommonSubstring);
    }

    [Fact]
    public void EvaluateTreatsProperNameReplacementAsRealRewriteVolume()
    {
        var exact = ReferenceCorpusSimilarityGate.Evaluate(
            new ReferenceCorpusSimilarityPiece(
                PieceId: "exact",
                SourceNodeId: "node-1",
                SourceText: "林岚在门口停住指节慢慢发紧",
                OutputText: "林岚在门口停住指节慢慢发紧"),
            new ReferenceCorpusSimilarityPolicy(1, 1));
        var renamed = ReferenceCorpusSimilarityGate.Evaluate(
            new ReferenceCorpusSimilarityPiece(
                PieceId: "renamed",
                SourceNodeId: "node-1",
                SourceText: "林岚在门口停住指节慢慢发紧",
                OutputText: "周野在门口停住指节慢慢发紧"),
            new ReferenceCorpusSimilarityPolicy(1, 1));

        Assert.Equal(1, exact.FourGramContainmentRatio, precision: 6);
        Assert.Equal(1, exact.LongestCommonSubstringRatio, precision: 6);
        Assert.True(renamed.FourGramContainmentRatio < exact.FourGramContainmentRatio);
        Assert.True(renamed.LongestCommonSubstringRatio < exact.LongestCommonSubstringRatio);
    }

    [Fact]
    public void EvaluateAllowsMetricsEqualToPolicyLimit()
    {
        var result = ReferenceCorpusSimilarityGate.Evaluate(
            new ReferenceCorpusSimilarityPiece(
                PieceId: "piece-1",
                SourceNodeId: "node-1",
                SourceText: "甲乙丙丁戊己庚辛壬癸",
                OutputText: "甲乙丙丁戊己子丑"),
            new ReferenceCorpusSimilarityPolicy(
                MaxFourGramContainmentRatio: 0.6,
                MaxLongestCommonSubstringRatio: 0.75));

        Assert.False(result.ShouldBlock);
        Assert.Empty(result.Violations);
    }
}
