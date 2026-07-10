using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class StyleTextStatistics
{
    public const string SchemaVersion = "style_sample_stats_v2";

    private static readonly string[] InteriorityMarkers =
    [
        "想", "心里", "意识到", "明白", "知道", "觉得", "以为", "记得", "念头", "犹豫", "不该"
    ];

    private static readonly string[] SensoryMarkers =
    [
        "雨", "风", "声", "光", "灯", "冷", "热", "潮", "气味", "味", "疼", "痛", "滑", "针", "铁锈", "窗"
    ];

    public static StyleSampleStatsPayload Analyze(string? content)
    {
        var text = content ?? string.Empty;
        var characterCount = CountTextCharacters(text);
        if (characterCount == 0)
        {
            return new StyleSampleStatsPayload(
                SchemaVersion: SchemaVersion,
                CharacterCount: 0,
                WordCount: 0,
                SentenceCount: 0,
                SentenceLengthDistribution: [],
                AverageSentenceChars: 0,
                SentenceLengthStdDev: 0,
                PunctuationPer100Chars: 0,
                QuoteDensity: 0,
                ParagraphCount: 0,
                AverageParagraphChars: 0,
                DialogueRatio: 0,
                InteriorityRatio: 0,
                SensoryRatio: 0);
        }

        var sentences = SplitSentences(text);
        var sentenceLengths = sentences
            .Select(CountTextCharacters)
            .Where(length => length > 0)
            .ToArray();
        var paragraphs = SplitParagraphs(text);
        var paragraphLengths = paragraphs
            .Select(CountTextCharacters)
            .Where(length => length > 0)
            .ToArray();
        var punctuationCount = text.Count(char.IsPunctuation);
        var quoteCount = text.Count(IsQuoteMarker);

        return new StyleSampleStatsPayload(
            SchemaVersion: SchemaVersion,
            CharacterCount: characterCount,
            WordCount: CountWords(text),
            SentenceCount: sentenceLengths.Length,
            SentenceLengthDistribution: sentenceLengths,
            AverageSentenceChars: Average(sentenceLengths),
            SentenceLengthStdDev: StandardDeviation(sentenceLengths),
            PunctuationPer100Chars: Round(punctuationCount / (double)characterCount * 100),
            QuoteDensity: Round(quoteCount / (double)characterCount * 100),
            ParagraphCount: paragraphLengths.Length,
            AverageParagraphChars: Average(paragraphLengths),
            DialogueRatio: Ratio(CountDialogueCharacters(text), characterCount),
            InteriorityRatio: Ratio(CountMarkerSentenceCharacters(sentences, InteriorityMarkers), characterCount),
            SensoryRatio: Ratio(CountMarkerSentenceCharacters(sentences, SensoryMarkers), characterCount));
    }

    private static int CountTextCharacters(string value)
    {
        return value.Count(ch => !char.IsWhiteSpace(ch));
    }

    private static int CountWords(string value)
    {
        var count = 0;
        var inLatinRun = false;

        foreach (var ch in value)
        {
            if (IsCjkWordCharacter(ch))
            {
                count++;
                inLatinRun = false;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (!inLatinRun)
                {
                    count++;
                    inLatinRun = true;
                }

                continue;
            }

            inLatinRun = false;
        }

        return count;
    }

    private static IReadOnlyList<string> SplitSentences(string content)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (!IsSentenceTerminator(content[i]))
            {
                continue;
            }

            var end = i + 1;
            while (end < content.Length && IsClosingQuote(content[end]))
            {
                end++;
            }

            AddSentence(content[start..end]);
            start = end;
            i = end - 1;
        }

        if (start < content.Length)
        {
            AddSentence(content[start..]);
        }

        return sentences;

        void AddSentence(string sentence)
        {
            var normalized = sentence.Trim();
            if (normalized.Length > 0)
            {
                sentences.Add(normalized);
            }
        }
    }

    private static IReadOnlyList<string> SplitParagraphs(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsSentenceTerminator(char ch)
    {
        return ch is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '.' or '\n';
    }

    private static bool IsClosingQuote(char ch)
    {
        return ch is '”' or '」' or '』' or '"' or '\'' or ')' or '）';
    }

    private static bool IsQuoteMarker(char ch)
    {
        return ch is '“' or '”' or '「' or '」' or '『' or '』' or '"' or '\'';
    }

    private static bool IsCjkWordCharacter(char ch)
    {
        return ch is >= '\u3400' and <= '\u9fff' ||
            ch is >= '\uf900' and <= '\ufaff' ||
            ch is >= '\u3040' and <= '\u30ff' ||
            ch is >= '\uac00' and <= '\ud7af';
    }

    private static int CountDialogueCharacters(string content)
    {
        var count = 0;
        var inDialogue = false;
        foreach (var ch in content)
        {
            if (ch is '“' or '「' or '『')
            {
                inDialogue = true;
                continue;
            }

            if (ch is '”' or '」' or '』')
            {
                inDialogue = false;
                continue;
            }

            if (inDialogue && !char.IsWhiteSpace(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountMarkerSentenceCharacters(IReadOnlyList<string> sentences, IReadOnlyList<string> markers)
    {
        return sentences
            .Where(sentence => markers.Any(marker => sentence.Contains(marker, StringComparison.Ordinal)))
            .Sum(CountTextCharacters);
    }

    private static double Average(IReadOnlyList<int> values)
    {
        return values.Count == 0 ? 0 : Round(values.Average());
    }

    private static double StandardDeviation(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var average = values.Average();
        var variance = values.Sum(value => Math.Pow(value - average, 2)) / values.Count;
        return Round(Math.Sqrt(variance));
    }

    private static double Ratio(int numerator, int denominator)
    {
        return denominator == 0 ? 0 : Round(Math.Min(1, Math.Max(0, numerator / (double)denominator)));
    }

    private static double Round(double value)
    {
        return Math.Round(value, 4, MidpointRounding.AwayFromZero);
    }
}
