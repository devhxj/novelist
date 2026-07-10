using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class NovelImportTextParser
{
    private const int MaxChapterHeaderLineLength = 120;
    private const int MaxDerivedTitleLength = 120;
    private const int MaxDiagnosticDetailLength = 1_000;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(false, false, true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, false, true);

    private static readonly Regex ChineseHeaderRegex = new(
        @"^第\s*(?<number>[0-9０-９零〇一二三四五六七八九十百千万两]+)\s*(?<unit>章节|章|节|回|卷|部)(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnglishHeaderRegex = new(
        @"^Chapter\s+(?<number>[0-9]+|[ivxlcdm]+)(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static NovelImportTextParseResult Parse(
        byte[] sourceBytes,
        string sourceDisplayName,
        string importKind,
        NovelImportTextParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        options ??= new NovelImportTextParserOptions();
        ValidateOptions(options);

        if (sourceBytes.Length > options.MaxInputBytes)
        {
            throw BuildFailure(
                "import.text.too_large",
                "导入文本超过大小限制。",
                $"observed_bytes={sourceBytes.Length}; limit_bytes={options.MaxInputBytes}");
        }

        var decoded = Decode(sourceBytes, options);
        var normalizedText = NormalizeNewlines(decoded.Text).Trim();
        if (normalizedText.Length == 0)
        {
            throw BuildFailure(
                "import.text.empty",
                "导入文本为空。",
                "Decoded text contains no readable content.",
                decoded.Encoding,
                decoded.Diagnostics);
        }

        if (normalizedText.Length > options.MaxTextCharacters)
        {
            throw BuildFailure(
                "import.text.too_large",
                "导入文本超过字符限制。",
                $"observed_chars={normalizedText.Length}; limit_chars={options.MaxTextCharacters}",
                decoded.Encoding,
                decoded.Diagnostics);
        }

        var lines = normalizedText.Split('\n');
        if (lines.Length > options.MaxLineCount)
        {
            throw BuildFailure(
                "import.text.too_many_lines",
                "导入文本行数过多。",
                $"observed_lines={lines.Length}; limit_lines={options.MaxLineCount}",
                decoded.Encoding,
                decoded.Diagnostics);
        }

        var headers = FindChapterHeaders(lines);
        if (headers.Count > options.MaxChapterCount)
        {
            throw BuildFailure(
                "import.chapter.too_many_headers",
                "检测到的章节数量过多。",
                $"observed_headers={headers.Count}; limit_headers={options.MaxChapterCount}",
                decoded.Encoding,
                decoded.Diagnostics);
        }

        var diagnostics = new List<NovelImportDiagnosticPayload>(decoded.Diagnostics);
        IReadOnlyList<NovelImportParsedChapter> chapters;
        IReadOnlyList<NovelImportSkippedChapterPayload> skippedChapters;
        if (headers.Count == 0)
        {
            chapters =
            [
                new NovelImportParsedChapter(
                    Index: 1,
                    Title: DeriveTitle(sourceDisplayName),
                    Content: normalizedText,
                    StartLine: 1,
                    EndLine: lines.Length)
            ];
            skippedChapters = [];
        }
        else
        {
            (chapters, skippedChapters) = SplitChapters(lines, headers, diagnostics);
        }

        if (chapters.Count == 0)
        {
            throw BuildFailure(
                "import.chapter.no_readable_chapters",
                "未找到可导入的有效章节。",
                $"detected_headers={headers.Count}; skipped={skippedChapters.Count}",
                decoded.Encoding,
                diagnostics,
                skippedChapters);
        }

        return new NovelImportTextParseResult(
            Encoding: decoded.Encoding,
            Chapters: chapters,
            SkippedChapters: skippedChapters,
            Diagnostics: diagnostics);
    }

    private static DecodedText Decode(byte[] sourceBytes, NovelImportTextParserOptions options)
    {
        if (sourceBytes.Length == 0)
        {
            throw BuildFailure("import.text.empty", "导入文本为空。", "Source file has zero bytes.");
        }

        if (StartsWith(sourceBytes, 0xEF, 0xBB, 0xBF))
        {
            return DecodeStrict(sourceBytes.AsSpan(3), StrictUtf8, "utf-8", "bom:utf-8", "high", []);
        }

        if (StartsWith(sourceBytes, 0xFF, 0xFE))
        {
            return DecodeStrict(sourceBytes.AsSpan(2), StrictUtf16Le, "utf-16le", "bom:utf-16le", "high", []);
        }

        if (StartsWith(sourceBytes, 0xFE, 0xFF))
        {
            return DecodeStrict(sourceBytes.AsSpan(2), StrictUtf16Be, "utf-16be", "bom:utf-16be", "high", []);
        }

        var utf8 = TryDecodeStrict(sourceBytes, StrictUtf8);
        if (utf8 is not null && !LooksBinaryLikeText(utf8))
        {
            return BuildDecodedText(utf8, "utf-8", "strict:utf-8", "high", false, []);
        }

        var utf16 = TryDecodeUtf16Heuristic(sourceBytes);
        if (utf16 is not null)
        {
            return utf16;
        }

        if (LooksBinaryLikeBytes(sourceBytes))
        {
            throw BuildFailure(
                "import.encoding.binary_like",
                "导入文件看起来像二进制内容。",
                "Binary-like byte distribution or control characters were detected.");
        }

        var diagnostics = new List<NovelImportDiagnosticPayload>();
        var gb18030 = ResolveGb18030Encoding(options, diagnostics);
        if (gb18030 is null)
        {
            throw BuildFailure(
                "import.encoding.low_confidence",
                "无法可靠识别文本编码。",
                "GB18030 code-page provider is unavailable and strict UTF-8/UTF-16 detection did not pass.",
                diagnostics: diagnostics);
        }

        var gb18030Text = TryDecodeStrict(sourceBytes, gb18030);
        if (gb18030Text is not null && LooksPlausibleDecodedText(gb18030Text, requireNaturalText: true))
        {
            return BuildDecodedText(gb18030Text, "gb18030", "codepage:gb18030", "medium", false, diagnostics);
        }

        throw BuildFailure(
            "import.encoding.low_confidence",
            "无法可靠识别文本编码。",
            "Strict UTF-8, UTF-16 heuristics, and GB18030 decoding did not produce safe readable text.",
            diagnostics: diagnostics);
    }

    private static DecodedText DecodeStrict(
        ReadOnlySpan<byte> bytes,
        Encoding encoding,
        string encodingName,
        string detectionSource,
        string confidence,
        IReadOnlyList<NovelImportDiagnosticPayload> priorDiagnostics)
    {
        try
        {
            var text = encoding.GetString(bytes);
            if (LooksBinaryLikeText(text))
            {
                throw BuildFailure(
                    "import.encoding.binary_like",
                    "导入文件看起来像二进制内容。",
                    $"encoding={encodingName}; detection_source={detectionSource}",
                    diagnostics: priorDiagnostics);
            }

            return BuildDecodedText(text, encodingName, detectionSource, confidence, false, priorDiagnostics);
        }
        catch (DecoderFallbackException ex)
        {
            throw BuildFailure(
                "import.encoding.low_confidence",
                "无法可靠识别文本编码。",
                $"encoding={encodingName}; detection_source={detectionSource}; {Truncate(ex.Message)}",
                diagnostics: priorDiagnostics);
        }
    }

    private static string? TryDecodeStrict(byte[] bytes, Encoding encoding)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static DecodedText? TryDecodeUtf16Heuristic(byte[] bytes)
    {
        if (bytes.Length < 16 || bytes.Length % 2 != 0)
        {
            return null;
        }

        var nulCount = bytes.Count(value => value == 0);
        if (nulCount < 2)
        {
            return null;
        }

        var leText = TryDecodeStrict(bytes, StrictUtf16Le);
        var beText = TryDecodeStrict(bytes, StrictUtf16Be);
        var leScore = leText is null ? double.NegativeInfinity : ScoreDecodedText(leText);
        var beScore = beText is null ? double.NegativeInfinity : ScoreDecodedText(beText);

        if (double.IsNegativeInfinity(leScore) && double.IsNegativeInfinity(beScore))
        {
            return null;
        }

        var useLe = leScore >= beScore;
        var bestText = useLe ? leText : beText;
        var bestScore = useLe ? leScore : beScore;
        var otherScore = useLe ? beScore : leScore;
        if (bestText is null ||
            LooksBinaryLikeText(bestText) ||
            !LooksPlausibleDecodedText(bestText, requireNaturalText: true) ||
            bestScore < otherScore + 2)
        {
            return null;
        }

        return BuildDecodedText(
            bestText,
            useLe ? "utf-16le" : "utf-16be",
            useLe ? "heuristic:utf-16le" : "heuristic:utf-16be",
            "high",
            false,
            []);
    }

    private static Encoding? ResolveGb18030Encoding(
        NovelImportTextParserOptions options,
        List<NovelImportDiagnosticPayload> diagnostics)
    {
        if (options.Gb18030EncodingFactory is not null)
        {
            try
            {
                return options.Gb18030EncodingFactory();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                diagnostics.Add(Diagnostic(
                    "import.encoding.gb18030_unavailable",
                    "GB18030 编码提供程序不可用。",
                    Truncate(ex.Message),
                    "warning"));
                return null;
            }
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            diagnostics.Add(Diagnostic(
                "import.encoding.gb18030_provider_registered",
                "已注册 GB18030 编码提供程序。",
                "provider=System.Text.Encoding.CodePages",
                "info"));
            return Encoding.GetEncoding(
                "GB18030",
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            diagnostics.Add(Diagnostic(
                "import.encoding.gb18030_unavailable",
                "GB18030 编码提供程序不可用。",
                Truncate(ex.Message),
                "warning"));
            return null;
        }
    }

    private static DecodedText BuildDecodedText(
        string text,
        string encodingName,
        string detectionSource,
        string confidence,
        bool binaryLike,
        IReadOnlyList<NovelImportDiagnosticPayload> priorDiagnostics)
    {
        var diagnostics = new List<NovelImportDiagnosticPayload>(priorDiagnostics)
        {
            Diagnostic(
                "import.encoding.detected",
                "已识别导入文本编码。",
                $"encoding={encodingName}; confidence={confidence}; source={detectionSource}",
                "info")
        };
        var encoding = new NovelImportEncodingDiagnosticPayload(
            EncodingName: encodingName,
            Confidence: confidence,
            DetectionSource: detectionSource,
            ReplacementCharacterCount: CountReplacementCharacters(text),
            BinaryLike: binaryLike,
            Diagnostics: diagnostics);
        return new DecodedText(text, encoding, diagnostics);
    }

    private static (IReadOnlyList<NovelImportParsedChapter> Chapters, IReadOnlyList<NovelImportSkippedChapterPayload> Skipped) SplitChapters(
        IReadOnlyList<string> lines,
        IReadOnlyList<ChapterHeader> headers,
        List<NovelImportDiagnosticPayload> diagnostics)
    {
        var chapters = new List<NovelImportParsedChapter>();
        var skipped = new List<NovelImportSkippedChapterPayload>();
        for (var index = 0; index < headers.Count; index++)
        {
            var header = headers[index];
            var nextLine = index + 1 < headers.Count ? headers[index + 1].LineIndex : lines.Count;
            var content = ExtractContent(lines, header.LineIndex + 1, nextLine);
            if (string.IsNullOrWhiteSpace(content))
            {
                skipped.Add(new NovelImportSkippedChapterPayload(
                    Index: index + 1,
                    Title: header.Title,
                    Reason: "empty_content"));
                diagnostics.Add(Diagnostic(
                    "import.chapter.skipped_empty",
                    "已跳过空章节。",
                    $"index={index + 1}; title={header.Title}",
                    "warning"));
                continue;
            }

            chapters.Add(new NovelImportParsedChapter(
                Index: chapters.Count + 1,
                Title: header.Title,
                Content: content,
                StartLine: header.LineIndex + 1,
                EndLine: nextLine));
        }

        return (chapters, skipped);
    }

    private static List<ChapterHeader> FindChapterHeaders(IReadOnlyList<string> lines)
    {
        var headers = new List<ChapterHeader>();
        for (var index = 0; index < lines.Count; index++)
        {
            var header = TryParseChapterHeader(lines[index]);
            if (header is not null)
            {
                headers.Add(new ChapterHeader(index, header));
            }
        }

        return headers;
    }

    private static string? TryParseChapterHeader(string line)
    {
        var normalized = StripMarkdownHeading(line);
        if (normalized.Length == 0 || normalized.Length > MaxChapterHeaderLineLength)
        {
            return null;
        }

        var chinese = ChineseHeaderRegex.Match(normalized);
        if (chinese.Success && IsPlausibleHeaderRemainder(chinese.Groups["rest"].Value, english: false))
        {
            return CollapseWhitespace(normalized);
        }

        var english = EnglishHeaderRegex.Match(normalized);
        if (english.Success && IsPlausibleHeaderRemainder(english.Groups["rest"].Value, english: true))
        {
            return CollapseWhitespace(normalized);
        }

        return null;
    }

    private static string StripMarkdownHeading(string line)
    {
        var trimmed = line.Trim();
        var index = 0;
        while (index < trimmed.Length && index < 6 && trimmed[index] == '#')
        {
            index++;
        }

        if (index > 0 && index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
        {
            return trimmed[index..].Trim();
        }

        return trimmed;
    }

    private static bool IsPlausibleHeaderRemainder(string rawRemainder, bool english)
    {
        if (rawRemainder.Length == 0)
        {
            return true;
        }

        if (rawRemainder.Length > MaxChapterHeaderLineLength)
        {
            return false;
        }

        var first = rawRemainder[0];
        var trimmed = rawRemainder.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        var separated = char.IsWhiteSpace(first) || first is ':' or '：' or '-' or '－' or '—';
        if (!separated)
        {
            if (trimmed.StartsWith("的", StringComparison.Ordinal) ||
                trimmed.StartsWith("时", StringComparison.Ordinal) ||
                trimmed.StartsWith("时候", StringComparison.Ordinal))
            {
                return false;
            }

            if (ContainsSentencePunctuation(trimmed) && trimmed.Length > 12)
            {
                return false;
            }
        }

        if (english)
        {
            var titleStart = trimmed.TrimStart(':', '：', '-', '－', '—').TrimStart();
            if (titleStart.Length > 0 && char.IsLower(titleStart[0]) && ContainsSentencePunctuation(titleStart))
            {
                return false;
            }
        }

        return trimmed.Length <= MaxChapterHeaderLineLength && !LooksLikeBodySentence(trimmed);
    }

    private static bool LooksLikeBodySentence(string text)
    {
        if (text.Length <= 32)
        {
            return false;
        }

        var punctuationCount = text.Count(IsSentencePunctuation);
        if (punctuationCount == 0)
        {
            return false;
        }

        var whitespaceCount = text.Count(char.IsWhiteSpace);
        return punctuationCount >= 1 && whitespaceCount >= 2;
    }

    private static string ExtractContent(IReadOnlyList<string> lines, int startInclusive, int endExclusive)
    {
        while (startInclusive < endExclusive && string.IsNullOrWhiteSpace(lines[startInclusive]))
        {
            startInclusive++;
        }

        while (endExclusive > startInclusive && string.IsNullOrWhiteSpace(lines[endExclusive - 1]))
        {
            endExclusive--;
        }

        return startInclusive >= endExclusive
            ? string.Empty
            : string.Join('\n', lines.Skip(startInclusive).Take(endExclusive - startInclusive)).Trim();
    }

    private static string DeriveTitle(string sourceDisplayName)
    {
        var fileName = Path.GetFileName(sourceDisplayName);
        var title = Path.GetFileNameWithoutExtension(fileName);
        title = CollapseWhitespace(title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return "未命名章节";
        }

        return title.Length <= MaxDerivedTitleLength ? title : title[..MaxDerivedTitleLength].Trim();
    }

    private static string NormalizeNewlines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        return bytes.AsSpan().StartsWith(prefix);
    }

    private static bool LooksBinaryLikeBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var nulCount = bytes.Count(value => value == 0);
        var hardControlCount = bytes.Count(value => value < 0x09 || value is > 0x0D and < 0x20);
        return nulCount >= 2 && (double)nulCount / bytes.Length > 0.05 ||
            hardControlCount >= 4 && (double)hardControlCount / bytes.Length > 0.08;
    }

    private static bool LooksBinaryLikeText(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return true;
        }

        var hardControls = text.Count(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'));
        return hardControls >= 4 && (double)hardControls / text.Length > 0.01;
    }

    private static bool LooksPlausibleDecodedText(string text, bool requireNaturalText)
    {
        if (LooksBinaryLikeText(text))
        {
            return false;
        }

        var visible = 0;
        var natural = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            visible++;
            if (IsNaturalTextCharacter(ch))
            {
                natural++;
            }
        }

        if (visible == 0)
        {
            return false;
        }

        return !requireNaturalText || natural >= Math.Min(8, Math.Max(2, visible / 20));
    }

    private static double ScoreDecodedText(string text)
    {
        if (LooksBinaryLikeText(text))
        {
            return double.NegativeInfinity;
        }

        var score = 0.0;
        foreach (var ch in text)
        {
            if (IsNaturalTextCharacter(ch))
            {
                score += 2.0;
            }
            else if (char.IsWhiteSpace(ch) || IsKnownPunctuation(ch))
            {
                score += 0.6;
            }
            else if (char.IsControl(ch))
            {
                score -= 10.0;
            }
            else
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                score += category is UnicodeCategory.OtherSymbol or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate
                    ? -4.0
                    : -0.5;
            }
        }

        foreach (var line in NormalizeNewlines(text).Split('\n').Take(200))
        {
            if (TryParseChapterHeader(line) is not null)
            {
                score += 20.0;
            }
        }

        return score;
    }

    private static bool IsNaturalTextCharacter(char ch)
    {
        return char.IsLetterOrDigit(ch) || IsCjk(ch);
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u3400' and <= '\u9fff' ||
            ch is >= '\uf900' and <= '\ufaff';
    }

    private static bool IsKnownPunctuation(char ch)
    {
        return ch is '，' or '。' or '、' or '：' or '；' or '！' or '？' or '“' or '”' or '‘' or '’' or '《' or '》'
            or ',' or '.' or ':' or ';' or '!' or '?' or '"' or '\'' or '(' or ')' or '[' or ']' or '-' or '—' or '…';
    }

    private static bool ContainsSentencePunctuation(string value)
    {
        return value.Any(IsSentencePunctuation);
    }

    private static bool IsSentencePunctuation(char ch)
    {
        return ch is '。' or '！' or '？' or '.' or '!' or '?' or ',' or '，' or ';' or '；';
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static int CountReplacementCharacters(string text)
    {
        return text.Count(ch => ch == '\uFFFD');
    }

    private static NovelImportTextParseException BuildFailure(
        string code,
        string message,
        string detail,
        NovelImportEncodingDiagnosticPayload? encoding = null,
        IReadOnlyList<NovelImportDiagnosticPayload>? diagnostics = null,
        IReadOnlyList<NovelImportSkippedChapterPayload>? skippedChapters = null)
    {
        var allDiagnostics = new List<NovelImportDiagnosticPayload>(diagnostics ?? []);
        if (allDiagnostics.All(diagnostic => !string.Equals(diagnostic.Code, code, StringComparison.Ordinal)))
        {
            allDiagnostics.Add(Diagnostic(code, message, detail, "error"));
        }

        return new NovelImportTextParseException(
            code,
            message,
            allDiagnostics,
            encoding,
            skippedChapters ?? []);
    }

    private static NovelImportDiagnosticPayload Diagnostic(
        string code,
        string message,
        string detail,
        string severity)
    {
        return new NovelImportDiagnosticPayload(code, message, Truncate(detail), severity);
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxDiagnosticDetailLength
            ? value
            : value[..MaxDiagnosticDetailLength];
    }

    private static void ValidateOptions(NovelImportTextParserOptions options)
    {
        if (options.MaxInputBytes <= 0 ||
            options.MaxTextCharacters <= 0 ||
            options.MaxLineCount <= 0 ||
            options.MaxChapterCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Parser limits must be positive.");
        }
    }

    private sealed record DecodedText(
        string Text,
        NovelImportEncodingDiagnosticPayload Encoding,
        IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics);

    private sealed record ChapterHeader(int LineIndex, string Title);
}

internal sealed record NovelImportTextParserOptions(
    Func<Encoding?>? Gb18030EncodingFactory = null,
    int MaxInputBytes = 52_428_800,
    int MaxTextCharacters = 60_000_000,
    int MaxLineCount = 2_000_000,
    int MaxChapterCount = 20_000);

internal sealed record NovelImportTextParseResult(
    NovelImportEncodingDiagnosticPayload Encoding,
    IReadOnlyList<NovelImportParsedChapter> Chapters,
    IReadOnlyList<NovelImportSkippedChapterPayload> SkippedChapters,
    IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics);

internal sealed record NovelImportParsedChapter(
    int Index,
    string Title,
    string Content,
    int StartLine,
    int EndLine);

internal sealed class NovelImportTextParseException : Exception
{
    public NovelImportTextParseException(
        string code,
        string message,
        IReadOnlyList<NovelImportDiagnosticPayload> diagnostics,
        NovelImportEncodingDiagnosticPayload? encoding,
        IReadOnlyList<NovelImportSkippedChapterPayload> skippedChapters)
        : base(message)
    {
        Code = code;
        Diagnostics = diagnostics;
        Encoding = encoding;
        SkippedChapters = skippedChapters;
    }

    public string Code { get; }

    public IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics { get; }

    public NovelImportEncodingDiagnosticPayload? Encoding { get; }

    public IReadOnlyList<NovelImportSkippedChapterPayload> SkippedChapters { get; }
}
