using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceSourceLeakAuditor
{
    private const int NGramSize = 4;
    private const int MinCandidateChars = 12;
    private const int SourceSpanMinChars = 18;
    private const int MaxEditSimilarityChars = 2_000;

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
        var longestExactPhraseChars = LongestCommonSubstringLength(source, candidate);
        var longestExactPhraseRatio = longestExactPhraseChars / (double)candidate.Length;
        var candidateSourceSimilarity = CandidateSourceSimilarityRatio(source, candidate);
        var lengthRatio = Math.Min(source.Length, candidate.Length) / (double)Math.Max(source.Length, candidate.Length);
        var findings = new List<string>();

        if (longestExactPhraseChars >= thresholds.ExactPhraseMinChars)
        {
            findings.Add(
                $"{thresholds.Label}exact phrase reuse: longest exact shared phrase is {longestExactPhraseChars} normalized chars ({longestExactPhraseRatio:P0} of candidate).");
        }

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

        if (candidateSourceSimilarity >= thresholds.CandidateSourceSimilarityRatio &&
            lengthRatio >= thresholds.CandidateSourceSimilarityMinLengthRatio)
        {
            findings.Add(
                $"{thresholds.Label}candidate/source similarity {candidateSourceSimilarity:P0}: normalized edit similarity exceeds threshold {thresholds.CandidateSourceSimilarityRatio:P0}.");
        }

        return findings.Count == 0
            ? new ReferenceSourceLeakAuditResult(
                ngramOverlap,
                coverage,
                longestSourceSpanRatio,
                longestSourceSpanChars,
                longestExactPhraseRatio,
                longestExactPhraseChars,
                Math.Round(candidateSourceSimilarity, 4),
                [],
                ShouldFail: false)
            : new ReferenceSourceLeakAuditResult(
                Math.Round(ngramOverlap, 4),
                Math.Round(coverage, 4),
                Math.Round(longestSourceSpanRatio, 4),
                longestSourceSpanChars,
                Math.Round(longestExactPhraseRatio, 4),
                longestExactPhraseChars,
                Math.Round(candidateSourceSimilarity, 4),
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

    private static int LongestCommonSubstringLength(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return 0;
        }

        var pattern = first.Length <= second.Length ? first : second;
        var text = ReferenceEquals(pattern, first) ? second : first;
        var states = new List<SuffixAutomatonState>(capacity: pattern.Length * 2) { new() };
        var last = 0;

        foreach (var ch in pattern)
        {
            var current = states.Count;
            states.Add(new SuffixAutomatonState { Length = states[last].Length + 1 });
            var previous = last;
            while (previous >= 0 && !states[previous].Next.ContainsKey(ch))
            {
                states[previous].Next[ch] = current;
                previous = states[previous].Link;
            }

            if (previous == -1)
            {
                states[current].Link = 0;
            }
            else
            {
                var next = states[previous].Next[ch];
                if (states[previous].Length + 1 == states[next].Length)
                {
                    states[current].Link = next;
                }
                else
                {
                    var clone = states.Count;
                    states.Add(new SuffixAutomatonState
                    {
                        Length = states[previous].Length + 1,
                        Link = states[next].Link,
                        Next = new Dictionary<char, int>(states[next].Next)
                    });

                    while (previous >= 0 &&
                        states[previous].Next.TryGetValue(ch, out var transition) &&
                        transition == next)
                    {
                        states[previous].Next[ch] = clone;
                        previous = states[previous].Link;
                    }

                    states[next].Link = clone;
                    states[current].Link = clone;
                }
            }

            last = current;
        }

        var state = 0;
        var currentLength = 0;
        var best = 0;
        foreach (var ch in text)
        {
            if (states[state].Next.TryGetValue(ch, out var next))
            {
                state = next;
                currentLength++;
                best = Math.Max(best, currentLength);
                continue;
            }

            while (state != 0 && !states[state].Next.ContainsKey(ch))
            {
                state = states[state].Link;
            }

            if (states[state].Next.TryGetValue(ch, out next))
            {
                currentLength = states[state].Length + 1;
                state = next;
                best = Math.Max(best, currentLength);
            }
            else
            {
                state = 0;
                currentLength = 0;
            }
        }

        return best;
    }

    private static double CandidateSourceSimilarityRatio(string source, string candidate)
    {
        if (source.Length == 0 && candidate.Length == 0)
        {
            return 1;
        }

        if (source.Length == 0 || candidate.Length == 0)
        {
            return 0;
        }

        if (Math.Max(source.Length, candidate.Length) > MaxEditSimilarityChars)
        {
            return 0;
        }

        var distance = LevenshteinDistance(source, candidate);
        return 1 - distance / (double)Math.Max(source.Length, candidate.Length);
    }

    private static int LevenshteinDistance(string first, string second)
    {
        if (first.Length < second.Length)
        {
            (first, second) = (second, first);
        }

        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (var index = 0; index <= second.Length; index++)
        {
            previous[index] = index;
        }

        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            current[0] = firstIndex;
            for (var secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                var cost = first[firstIndex - 1] == second[secondIndex - 1] ? 0 : 1;
                current[secondIndex] = Math.Min(
                    Math.Min(current[secondIndex - 1] + 1, previous[secondIndex] + 1),
                    previous[secondIndex - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[second.Length];
    }

    private static string Normalize(string value)
    {
        return new string((value ?? string.Empty)
            .Where(ch => !char.IsWhiteSpace(ch))
            .ToArray());
    }

    private sealed record SourceLeakThresholds(
        string Label,
        int ExactPhraseMinChars,
        int SourceSpanMinChars,
        double SourceSpanRatio,
        double NGramOverlapRatio,
        double CoverageRatio,
        double HighCoverageRatio,
        double CandidateSourceSimilarityRatio,
        double CandidateSourceSimilarityMinLengthRatio)
    {
        public static SourceLeakThresholds Default { get; } = new(
            string.Empty,
            ExactPhraseMinChars: ReferenceSourceLeakAuditor.SourceSpanMinChars,
            ReferenceSourceLeakAuditor.SourceSpanMinChars,
            SourceSpanRatio: 0.45,
            NGramOverlapRatio: 0.55,
            CoverageRatio: 0.55,
            HighCoverageRatio: 0.82,
            CandidateSourceSimilarityRatio: 0.72,
            CandidateSourceSimilarityMinLengthRatio: 0.55);

        public static SourceLeakThresholds Strong { get; } = new(
            "strong style ",
            ExactPhraseMinChars: 12,
            SourceSpanMinChars: 12,
            SourceSpanRatio: 0.35,
            NGramOverlapRatio: 0.30,
            CoverageRatio: 0.60,
            HighCoverageRatio: 0.70,
            CandidateSourceSimilarityRatio: 0.62,
            CandidateSourceSimilarityMinLengthRatio: 0.55);
    }

    private sealed class SuffixAutomatonState
    {
        public int Length { get; set; }

        public int Link { get; set; } = -1;

        public Dictionary<char, int> Next { get; set; } = [];
    }
}

internal sealed record ReferenceSourceLeakAuditResult(
    double NGramOverlapRatio,
    double CandidateSourceCoverageRatio,
    double LongestSourceSpanRatio,
    int LongestSourceSpanChars,
    double LongestExactPhraseRatio,
    int LongestExactPhraseChars,
    double CandidateSourceSimilarityRatio,
    IReadOnlyList<string> Findings,
    bool ShouldFail)
{
    public static ReferenceSourceLeakAuditResult Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        [],
        ShouldFail: false);
}
