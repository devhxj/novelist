using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceSourceLeakAuditor
{
    private const int NGramSize = 4;
    private const int MinCandidateChars = 12;
    private const int SourceSpanMinChars = 18;

    public static ReferenceSourceLeakAuditResult Analyze(
        string sourceText,
        string candidateText,
        string rewriteLevel,
        string? imitationIntensity = null)
    {
        if (rewriteLevel is ReferenceRewriteLevels.L0 or ReferenceRewriteLevels.L1)
        {
            return ReferenceSourceLeakAuditResult.Empty;
        }

        var source = Normalize(sourceText);
        var candidate = Normalize(candidateText);
        if (source.Length < MinCandidateChars ||
            candidate.Length < MinCandidateChars ||
            candidate.Length < NGramSize ||
            source.Length < NGramSize)
        {
            return ReferenceSourceLeakAuditResult.Empty;
        }

        var thresholds = ThresholdsForIntensity(imitationIntensity);
        var sourceNgrams = BuildNGramSet(source);
        var candidateNgramCount = candidate.Length - NGramSize + 1;
        var matchedNgrams = 0;
        var currentRun = 0;
        var longestRun = 0;
        var covered = new bool[candidate.Length];

        for (var index = 0; index <= candidate.Length - NGramSize; index++)
        {
            if (sourceNgrams.Contains(candidate.Substring(index, NGramSize)))
            {
                matchedNgrams++;
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
                for (var cover = index; cover < index + NGramSize; cover++)
                {
                    covered[cover] = true;
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        var ngramOverlap = matchedNgrams / (double)candidateNgramCount;
        var coverage = covered.Count(value => value) / (double)candidate.Length;
        var longestSourceSpanChars = longestRun == 0 ? 0 : longestRun + NGramSize - 1;
        var longestSourceSpanRatio = longestSourceSpanChars / (double)candidate.Length;
        var findings = new List<string>();

        if (longestSourceSpanChars >= thresholds.SourceSpanMinChars &&
            longestSourceSpanRatio >= thresholds.SourceSpanRatio)
        {
            findings.Add(
                $"{thresholds.Label}source-span concentration {longestSourceSpanRatio:P0}: longest shared span is {longestSourceSpanChars} normalized chars.");
        }

        if (ngramOverlap >= thresholds.NGramOverlapRatio &&
            coverage >= thresholds.CoverageRatio)
        {
            findings.Add(
                $"{thresholds.Label}n-gram overlap {ngramOverlap:P0}: {coverage:P0} of candidate chars are covered by source n-grams.");
        }

        if (coverage >= thresholds.HighCoverageRatio && findings.Count == 0)
        {
            findings.Add($"{thresholds.Label}source coverage {coverage:P0}: candidate is mostly covered by source phrasing.");
        }

        return findings.Count == 0
            ? new ReferenceSourceLeakAuditResult(
                ngramOverlap,
                coverage,
                longestSourceSpanRatio,
                longestSourceSpanChars,
                [],
                ShouldFail: false)
            : new ReferenceSourceLeakAuditResult(
                Math.Round(ngramOverlap, 4),
                Math.Round(coverage, 4),
                Math.Round(longestSourceSpanRatio, 4),
                longestSourceSpanChars,
                findings,
                ShouldFail: true);
    }

    private static SourceLeakThresholds ThresholdsForIntensity(string? imitationIntensity)
    {
        return string.Equals(imitationIntensity, ReferenceStyleImitationIntensities.Strong, StringComparison.Ordinal)
            ? SourceLeakThresholds.Strong
            : SourceLeakThresholds.Default;
    }

    private static HashSet<string> BuildNGramSet(string text)
    {
        var ngrams = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index <= text.Length - NGramSize; index++)
        {
            ngrams.Add(text.Substring(index, NGramSize));
        }

        return ngrams;
    }

    private static string Normalize(string value)
    {
        return new string((value ?? string.Empty)
            .Where(ch => !char.IsWhiteSpace(ch))
            .ToArray());
    }

    private sealed record SourceLeakThresholds(
        string Label,
        int SourceSpanMinChars,
        double SourceSpanRatio,
        double NGramOverlapRatio,
        double CoverageRatio,
        double HighCoverageRatio)
    {
        public static SourceLeakThresholds Default { get; } = new(
            string.Empty,
            ReferenceSourceLeakAuditor.SourceSpanMinChars,
            SourceSpanRatio: 0.45,
            NGramOverlapRatio: 0.55,
            CoverageRatio: 0.55,
            HighCoverageRatio: 0.82);

        public static SourceLeakThresholds Strong { get; } = new(
            "strong style ",
            SourceSpanMinChars: 12,
            SourceSpanRatio: 0.35,
            NGramOverlapRatio: 0.30,
            CoverageRatio: 0.60,
            HighCoverageRatio: 0.70);
    }
}

internal sealed record ReferenceSourceLeakAuditResult(
    double NGramOverlapRatio,
    double CandidateSourceCoverageRatio,
    double LongestSourceSpanRatio,
    int LongestSourceSpanChars,
    IReadOnlyList<string> Findings,
    bool ShouldFail)
{
    public static ReferenceSourceLeakAuditResult Empty { get; } = new(
        0,
        0,
        0,
        0,
        [],
        ShouldFail: false);
}
