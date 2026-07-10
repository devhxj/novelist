using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceSourceLeakAuditorTests
{
    [Fact]
    public void AnalyzeAllowsExactL0ReuseForCompatibility()
    {
        var result = ReferenceSourceLeakAuditor.Analyze(
            "他在门口停了很久。",
            "他在门口停了很久。",
            ReferenceRewriteLevels.L0);

        Assert.False(result.ShouldFail);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void AnalyzeFlagsLongContiguousSourcePhraseInNonL0Candidate()
    {
        var result = ReferenceSourceLeakAuditor.Analyze(
            "雨声压低了整条街的呼吸，林岚在门口停住，指节慢慢发紧。",
            "雨声压低了整条街的呼吸，林岚在门口停住，指节慢慢发紧，然后他把钥匙放下。",
            ReferenceRewriteLevels.L2);

        Assert.True(result.ShouldFail);
        Assert.True(result.LongestSourceSpanRatio >= 0.5);
        Assert.Contains(result.Findings, finding => finding.Contains("source-span", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeFlagsExactCopiedPhraseEvenWhenCandidateCoverageIsLow()
    {
        const string copiedPhrase = "雨声压低了整条街的呼吸，林岚在门口停住";
        var result = ReferenceSourceLeakAuditor.Analyze(
            "她避开灯光，雨声压低了整条街的呼吸，林岚在门口停住，直到钥匙碰到掌心。",
            "她先整理桌上的旧照片，把所有线索按时间放回抽屉，又绕到窗边确认楼下没有人影。雨声压低了整条街的呼吸，林岚在门口停住。随后她才把话题转回那封信，没有提任何人的名字。",
            ReferenceRewriteLevels.L2);

        Assert.True(result.ShouldFail);
        Assert.True(result.LongestExactPhraseChars >= copiedPhrase.Length);
        Assert.Contains(result.Findings, finding => finding.Contains("exact phrase", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Findings, finding => finding.Contains(copiedPhrase, StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeFlagsHighNgramOverlapAfterSmallConnectorEdits()
    {
        var result = ReferenceSourceLeakAuditor.Analyze(
            "雨声压低了整条街的呼吸，林岚在门口停住，指节慢慢发紧。",
            "雨声压低整条街的呼吸，林岚却在门口停住，指节慢慢发紧。",
            ReferenceRewriteLevels.L2);

        Assert.True(result.ShouldFail);
        Assert.True(result.NGramOverlapRatio >= 0.5);
        Assert.Contains(result.Findings, finding => finding.Contains("n-gram", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeFlagsHighCandidateSourceSimilarityWithoutLeakingText()
    {
        const string copiedShape = "雨声压低了整条街的呼吸，林岚在门口停住，指尖慢慢发紧";
        var result = ReferenceSourceLeakAuditor.Analyze(
            "雨声压低了整条街的呼吸，林岚在门口停住，指尖慢慢发紧，仍把钥匙压回掌心，灯影从窗边退开，杯沿留着一圈冷水。",
            "雨声放低了整片街的呼吸，林岚于门前停住，指尖缓缓发紧，仍将钥匙压回掌中，灯影从窗侧退开，杯沿留下一圈凉水。",
            ReferenceRewriteLevels.L2);

        Assert.True(result.ShouldFail);
        Assert.True(result.CandidateSourceSimilarityRatio >= 0.72);
        Assert.True(result.LongestExactPhraseChars < copiedShape.Length);
        Assert.Contains(result.Findings, finding => finding.Contains("candidate/source similarity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Findings, finding => finding.Contains(copiedShape, StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeUsesStricterThresholdsForStrongImitation()
    {
        const string source = "雨声压低了整条街的呼吸，林岚在门口停住，指节慢慢发紧，心里一紧。";
        const string candidate = "雨声压低了街的呼吸，林岚却在门口停了一下，指节发紧，心里仍然发沉。";

        var defaultResult = ReferenceSourceLeakAuditor.Analyze(
            source,
            candidate,
            ReferenceRewriteLevels.L2);
        var strongResult = ReferenceSourceLeakAuditor.Analyze(
            source,
            candidate,
            ReferenceRewriteLevels.L2,
            ReferenceStyleImitationIntensities.Strong);

        Assert.False(defaultResult.ShouldFail);
        Assert.True(strongResult.ShouldFail);
        Assert.True(strongResult.CandidateSourceCoverageRatio >= 0.6);
        Assert.Contains(strongResult.Findings, finding => finding.Contains("strong", StringComparison.OrdinalIgnoreCase));
    }
}
