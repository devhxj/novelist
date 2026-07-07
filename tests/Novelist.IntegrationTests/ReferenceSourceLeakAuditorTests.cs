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
