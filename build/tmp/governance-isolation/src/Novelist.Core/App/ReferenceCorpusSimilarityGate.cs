using System.Globalization;
using System.Text;

namespace Novelist.Core.App;

public static class ReferenceCorpusSimilarityGate
{
    private const int NGramSize = 4;
    private const char PunctuationPlaceholder = '#';

    public static ReferenceCorpusSimilarityPieceResult Evaluate(
        ReferenceCorpusSimilarityPiece piece,
        ReferenceCorpusSimilarityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(piece);
        ValidatePolicy(policy);

        var normalizedSource = NormalizeForComparison(piece.SourceText);
        var normalizedOutput = NormalizeForComparison(piece.OutputText);
        var sourceUnits = ToCodePoints(normalizedSource);
        var outputUnits = ToCodePoints(normalizedOutput);

        var outputGrams = BuildFourGramSet(outputUnits);
        var sourceGrams = BuildFourGramSet(sourceUnits);
        var sharedGramCount = CountSharedGrams(outputGrams, sourceGrams);
        var fourGramContainmentRatio = outputGrams.Count == 0
            ? 0
            : sharedGramCount / (double)outputGrams.Count;
        var longestCommonSubstringChars = LongestCommonSubstringLength(sourceUnits, outputUnits);
        var longestCommonSubstringRatio = outputUnits.Length == 0
            ? 0
            : longestCommonSubstringChars / (double)outputUnits.Length;

        var violations = new List<ReferenceCorpusSimilarityViolation>(capacity: 2);
        if (fourGramContainmentRatio > policy.MaxFourGramContainmentRatio)
        {
            violations.Add(new ReferenceCorpusSimilarityViolation(
                ReferenceCorpusSimilarityMetrics.FourGramContainment,
                fourGramContainmentRatio,
                policy.MaxFourGramContainmentRatio));
        }

        if (longestCommonSubstringRatio > policy.MaxLongestCommonSubstringRatio)
        {
            violations.Add(new ReferenceCorpusSimilarityViolation(
                ReferenceCorpusSimilarityMetrics.LongestCommonSubstring,
                longestCommonSubstringRatio,
                policy.MaxLongestCommonSubstringRatio));
        }

        return new ReferenceCorpusSimilarityPieceResult(
            piece.PieceId,
            piece.SourceNodeId,
            sourceUnits.Length,
            outputUnits.Length,
            outputGrams.Count,
            sharedGramCount,
            fourGramContainmentRatio,
            longestCommonSubstringChars,
            longestCommonSubstringRatio,
            violations);
    }

    public static ReferenceCorpusSimilarityGateResult EvaluateMany(
        IReadOnlyList<ReferenceCorpusSimilarityPiece> pieces,
        ReferenceCorpusSimilarityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ValidatePolicy(policy);

        var results = new List<ReferenceCorpusSimilarityPieceResult>(pieces.Count);
        foreach (var piece in pieces)
        {
            results.Add(Evaluate(piece, policy));
        }

        return new ReferenceCorpusSimilarityGateResult(results);
    }

    public static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (IsWhitespace(category))
            {
                continue;
            }

            if (IsChineseCodePoint(rune.Value) || category is UnicodeCategory.DecimalDigitNumber)
            {
                builder.Append(rune);
                continue;
            }

            if (IsPunctuation(category))
            {
                builder.Append(PunctuationPlaceholder);
            }
        }

        return builder.ToString();
    }

    private static void ValidatePolicy(ReferenceCorpusSimilarityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateRatio(policy.MaxFourGramContainmentRatio, nameof(policy.MaxFourGramContainmentRatio));
        ValidateRatio(policy.MaxLongestCommonSubstringRatio, nameof(policy.MaxLongestCommonSubstringRatio));
    }

    private static void ValidateRatio(double value, string parameterName)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Similarity thresholds must be between 0 and 1.");
        }
    }

    private static bool IsWhitespace(UnicodeCategory category)
    {
        return category is
            UnicodeCategory.SpaceSeparator or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.Control;
    }

    private static bool IsPunctuation(UnicodeCategory category)
    {
        return category is
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or
            UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or
            UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation;
    }

    private static bool IsChineseCodePoint(int value)
    {
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2A6DF
            or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F
            or >= 0x2B820 and <= 0x2CEAF
            or >= 0x2CEB0 and <= 0x2EBEF
            or >= 0x30000 and <= 0x3134F;
    }

    private static int[] ToCodePoints(string normalized)
    {
        if (normalized.Length == 0)
        {
            return [];
        }

        var codePoints = new List<int>(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            codePoints.Add(rune.Value);
        }

        return [.. codePoints];
    }

    private static HashSet<FourGram> BuildFourGramSet(IReadOnlyList<int> units)
    {
        var grams = new HashSet<FourGram>();
        if (units.Count < NGramSize)
        {
            return grams;
        }

        for (var index = 0; index <= units.Count - NGramSize; index++)
        {
            grams.Add(new FourGram(
                units[index],
                units[index + 1],
                units[index + 2],
                units[index + 3]));
        }

        return grams;
    }

    private static int CountSharedGrams(HashSet<FourGram> outputGrams, HashSet<FourGram> sourceGrams)
    {
        var count = 0;
        foreach (var gram in outputGrams)
        {
            if (sourceGrams.Contains(gram))
            {
                count++;
            }
        }

        return count;
    }

    private static int LongestCommonSubstringLength(IReadOnlyList<int> first, IReadOnlyList<int> second)
    {
        if (first.Count == 0 || second.Count == 0)
        {
            return 0;
        }

        var pattern = first.Count <= second.Count ? first : second;
        var text = ReferenceEquals(pattern, first) ? second : first;
        var states = new List<SuffixAutomatonState>(capacity: pattern.Count * 2) { new() };
        var last = 0;

        foreach (var token in pattern)
        {
            var current = states.Count;
            states.Add(new SuffixAutomatonState { Length = states[last].Length + 1 });
            var previous = last;
            while (previous >= 0 && !states[previous].Next.ContainsKey(token))
            {
                states[previous].Next[token] = current;
                previous = states[previous].Link;
            }

            if (previous == -1)
            {
                states[current].Link = 0;
            }
            else
            {
                var next = states[previous].Next[token];
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
                        Next = new Dictionary<int, int>(states[next].Next)
                    });

                    while (previous >= 0 &&
                        states[previous].Next.TryGetValue(token, out var transition) &&
                        transition == next)
                    {
                        states[previous].Next[token] = clone;
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
        foreach (var token in text)
        {
            if (states[state].Next.TryGetValue(token, out var next))
            {
                state = next;
                currentLength++;
                best = Math.Max(best, currentLength);
                continue;
            }

            while (state != 0 && !states[state].Next.ContainsKey(token))
            {
                state = states[state].Link;
            }

            if (states[state].Next.TryGetValue(token, out next))
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

    private readonly record struct FourGram(int First, int Second, int Third, int Fourth);

    private sealed class SuffixAutomatonState
    {
        public int Length { get; set; }

        public int Link { get; set; } = -1;

        public Dictionary<int, int> Next { get; set; } = [];
    }
}

public sealed record ReferenceCorpusSimilarityPiece(
    string PieceId,
    string SourceNodeId,
    string SourceText,
    string OutputText);

public sealed record ReferenceCorpusSimilarityPolicy(
    double MaxFourGramContainmentRatio,
    double MaxLongestCommonSubstringRatio)
{
    public static ReferenceCorpusSimilarityPolicy AdaptedOnlyDefault { get; } = new(
        MaxFourGramContainmentRatio: 0.35,
        MaxLongestCommonSubstringRatio: 0.30);

    public static ReferenceCorpusSimilarityPolicy VerbatimOkDefault { get; } = new(
        MaxFourGramContainmentRatio: 0.90,
        MaxLongestCommonSubstringRatio: 0.80);
}

public sealed record ReferenceCorpusSimilarityGateResult(
    IReadOnlyList<ReferenceCorpusSimilarityPieceResult> Pieces)
{
    public bool ShouldBlock => Pieces.Any(piece => piece.ShouldBlock);

    public IReadOnlyList<ReferenceCorpusSimilarityPieceResult> BlockedPieces =>
        Pieces.Where(piece => piece.ShouldBlock).ToArray();
}

public sealed record ReferenceCorpusSimilarityPieceResult(
    string PieceId,
    string SourceNodeId,
    int NormalizedSourceLength,
    int NormalizedOutputLength,
    int OutputFourGramCount,
    int SharedFourGramCount,
    double FourGramContainmentRatio,
    int LongestCommonSubstringChars,
    double LongestCommonSubstringRatio,
    IReadOnlyList<ReferenceCorpusSimilarityViolation> Violations)
{
    public bool ShouldBlock => Violations.Count > 0;
}

public sealed record ReferenceCorpusSimilarityViolation(
    string Metric,
    double Actual,
    double Threshold);

public static class ReferenceCorpusSimilarityMetrics
{
    public const string FourGramContainment = "four_gram_containment";
    public const string LongestCommonSubstring = "longest_common_substring";
}
