using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class NovelImportEpubParser
{
    private const int MaxDiagnosticDetailLength = 1_000;
    private const int MaxDerivedTitleLength = 120;

    public static NovelImportEpubParseResult Parse(
        byte[] sourceBytes,
        string sourceDisplayName,
        NovelImportEpubParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        options ??= new NovelImportEpubParserOptions();
        ValidateOptions(options);

        if (sourceBytes.Length == 0)
        {
            throw BuildFailure("import.epub.empty", "EPUB 文件为空。", "Source file has zero bytes.");
        }

        if (sourceBytes.Length > options.MaxCompressedBytes)
        {
            throw BuildFailure(
                "import.epub.too_large",
                "EPUB 文件超过大小限制。",
                $"observed_bytes={sourceBytes.Length}; limit_bytes={options.MaxCompressedBytes}");
        }

        using var stream = new MemoryStream(sourceBytes, writable: false);
        using var archive = OpenArchive(stream);
        var diagnostics = new List<NovelImportDiagnosticPayload>();

        var containerEntry = FindEntry(archive, "META-INF/container.xml")
            ?? throw BuildFailure(
                "import.epub.invalid_container",
                "EPUB 缺少 container.xml。",
                "Missing META-INF/container.xml.");
        var container = ParseXml(
            ReadEntryText(containerEntry, options.MaxMetadataEntryBytes),
            "import.epub.invalid_container",
            "EPUB container.xml 无法解析。");

        var opfPathRaw = container
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "rootfile", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("full-path")
            ?.Value;
        if (string.IsNullOrWhiteSpace(opfPathRaw))
        {
            throw BuildFailure(
                "import.epub.invalid_container",
                "EPUB container.xml 缺少 OPF 路径。",
                "The first rootfile full-path attribute is missing.");
        }

        var opfPath = NormalizeZipPath(opfPathRaw);
        var opfEntry = FindEntry(archive, opfPath)
            ?? throw BuildFailure(
                "import.epub.invalid_opf",
                "EPUB OPF 文件不存在。",
                $"opf_path={opfPath}");
        var opf = ParseXml(
            ReadEntryText(opfEntry, options.MaxMetadataEntryBytes),
            "import.epub.invalid_opf",
            "EPUB OPF 文件无法解析。");

        var title = NormalizeInlineText(opf
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase))
            ?.Value);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = DeriveTitle(sourceDisplayName);
        }

        var manifest = opf
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase))
            .Select(element => new ManifestItem(
                Id: element.Attribute("id")?.Value ?? string.Empty,
                Href: element.Attribute("href")?.Value ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var spine = opf
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "itemref", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("idref")?.Value ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        if (spine.Count == 0)
        {
            throw BuildFailure(
                "import.epub.invalid_opf",
                "EPUB OPF 文件缺少 spine。",
                "No readable spine itemrefs were found.");
        }

        var opfDirectory = GetZipDirectory(opfPath);
        var chapters = new List<NovelImportParsedChapter>();
        var skipped = new List<NovelImportSkippedChapterPayload>();
        for (var index = 0; index < spine.Count; index++)
        {
            var idref = spine[index];
            if (!manifest.TryGetValue(idref, out var item))
            {
                Skip(skipped, diagnostics, index + 1, idref, "missing_manifest_item");
                continue;
            }

            var chapterPath = NormalizeZipPath(item.Href, opfDirectory);
            var chapterEntry = FindEntry(archive, chapterPath);
            if (chapterEntry is null)
            {
                Skip(skipped, diagnostics, index + 1, item.Href, "missing_file");
                continue;
            }

            string chapterText;
            try
            {
                chapterText = ReadEntryText(chapterEntry, options.MaxChapterEntryBytes);
            }
            catch (NovelImportEpubParseException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Skip(skipped, diagnostics, index + 1, item.Href, "unreadable_file", ex.Message);
                continue;
            }

            EpubHtmlText extracted;
            try
            {
                extracted = ExtractHtmlText(chapterText, chapterPath);
            }
            catch (XmlException ex)
            {
                Skip(skipped, diagnostics, index + 1, item.Href, "invalid_html", ex.Message);
                continue;
            }
            catch (NovelImportEpubParseException ex) when (ex.Code == "import.epub.invalid_html")
            {
                Skip(skipped, diagnostics, index + 1, item.Href, "invalid_html", ex.Message);
                continue;
            }

            if (string.IsNullOrWhiteSpace(extracted.Content))
            {
                Skip(skipped, diagnostics, index + 1, extracted.Title ?? item.Href, "empty_content");
                continue;
            }

            chapters.Add(new NovelImportParsedChapter(
                Index: chapters.Count + 1,
                Title: string.IsNullOrWhiteSpace(extracted.Title) ? DeriveTitle(chapterPath) : extracted.Title,
                Content: extracted.Content,
                StartLine: 1,
                EndLine: extracted.Content.Count(ch => ch == '\n') + 1));
        }

        if (chapters.Count == 0)
        {
            throw BuildFailure(
                "import.epub.no_readable_chapters",
                "EPUB 中没有可导入的有效章节。",
                $"spine_count={spine.Count}; skipped={skipped.Count}",
                diagnostics,
                skipped);
        }

        return new NovelImportEpubParseResult(title, chapters, skipped, diagnostics);
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw BuildFailure(
                "import.epub.invalid_zip",
                "EPUB 压缩包无法读取。",
                Truncate(ex.Message));
        }
    }

    private static XDocument ParseXml(string xml, string code, string message)
    {
        try
        {
            using var textReader = new StringReader(xml);
            using var reader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw BuildFailure(code, message, Truncate(ex.Message));
        }
    }

    private static string ReadEntryText(ZipArchiveEntry entry, long maxBytes)
    {
        if (entry.Length > maxBytes)
        {
            throw BuildFailure(
                "import.epub.entry_too_large",
                "EPUB 内部文件超过大小限制。",
                $"entry={entry.FullName}; observed_bytes={entry.Length}; limit_bytes={maxBytes}");
        }

        using var entryStream = entry.Open();
        using var memory = new MemoryStream(capacity: checked((int)Math.Min(entry.Length, maxBytes)));
        entryStream.CopyTo(memory);
        if (memory.Length > maxBytes)
        {
            throw BuildFailure(
                "import.epub.entry_too_large",
                "EPUB 内部文件超过大小限制。",
                $"entry={entry.FullName}; observed_bytes={memory.Length}; limit_bytes={maxBytes}");
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static EpubHtmlText ExtractHtmlText(string html, string chapterPath)
    {
        var document = ParseXml(html, "import.epub.invalid_html", "EPUB 章节 XHTML 无法解析。");
        var body = document
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "body", StringComparison.OrdinalIgnoreCase))
            ?? document.Root
            ?? throw new XmlException("HTML document has no root element.");
        var title = document
            .Descendants()
            .FirstOrDefault(IsHeadingElement);
        var lines = new List<string>();
        var current = new StringBuilder();
        VisitNode(body, lines, current);
        FlushLine(lines, current);

        return new EpubHtmlText(
            Title: NormalizeInlineText(title?.Value) ?? DeriveTitle(chapterPath),
            Content: string.Join('\n', lines.Select(NormalizeInlineText).Where(line => !string.IsNullOrWhiteSpace(line))));
    }

    private static void VisitNode(XNode node, List<string> lines, StringBuilder current)
    {
        if (node is XText text)
        {
            AppendText(current, text.Value);
            return;
        }

        if (node is not XElement element)
        {
            return;
        }

        var localName = element.Name.LocalName.ToLowerInvariant();
        if (localName is "head" or "script" or "style" or "title" or "meta" or "link")
        {
            return;
        }

        if (localName == "br")
        {
            FlushLine(lines, current);
            return;
        }

        var block = IsBlockElement(localName);
        if (block)
        {
            FlushLine(lines, current);
        }

        foreach (var child in element.Nodes())
        {
            VisitNode(child, lines, current);
        }

        if (block)
        {
            FlushLine(lines, current);
        }
    }

    private static void AppendText(StringBuilder current, string value)
    {
        var normalized = NormalizeInlineText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (current.Length > 0 && !char.IsWhiteSpace(current[^1]) && !StartsWithClosingPunctuation(normalized))
        {
            current.Append(' ');
        }

        current.Append(normalized);
    }

    private static void FlushLine(List<string> lines, StringBuilder current)
    {
        var line = NormalizeInlineText(current.ToString());
        current.Clear();
        if (!string.IsNullOrWhiteSpace(line))
        {
            lines.Add(line);
        }
    }

    private static bool IsHeadingElement(XElement element)
    {
        return element.Name.LocalName.ToLowerInvariant() is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
    }

    private static bool IsBlockElement(string localName)
    {
        return localName is "body" or "section" or "article" or "div" or "p" or "blockquote" or "pre"
            or "ul" or "ol" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
    }

    private static bool StartsWithClosingPunctuation(string value)
    {
        return value[0] is ',' or '.' or ':' or ';' or '!' or '?' or ')' or ']' or '}' or '，' or '。' or '：' or '；' or '！' or '？' or '）' or '】' or '」' or '”';
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string normalizedPath)
    {
        var exact = archive.GetEntry(normalizedPath);
        if (exact is not null)
        {
            return exact;
        }

        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeZipEntryName(entry.FullName), normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeZipPath(string rawPath, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw BuildFailure("import.epub.unsafe_path", "EPUB 内部路径不安全。", "Path is empty.");
        }

        var decoded = Uri.UnescapeDataString(rawPath).Replace('\\', '/');
        if (decoded.Contains('\0') ||
            decoded.StartsWith("/", StringComparison.Ordinal) ||
            decoded.StartsWith("//", StringComparison.Ordinal) ||
            (decoded.Length >= 2 && char.IsAsciiLetter(decoded[0]) && decoded[1] == ':'))
        {
            throw BuildFailure("import.epub.unsafe_path", "EPUB 内部路径不安全。", $"path={rawPath}");
        }

        var combined = string.IsNullOrWhiteSpace(baseDirectory)
            ? decoded
            : $"{baseDirectory.TrimEnd('/')}/{decoded}";
        var segments = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw BuildFailure("import.epub.unsafe_path", "EPUB 内部路径不安全。", $"path={rawPath}");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw BuildFailure("import.epub.unsafe_path", "EPUB 内部路径不安全。", $"path={rawPath}");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeZipEntryName(string entryName)
    {
        return entryName.Replace('\\', '/').TrimStart('/');
    }

    private static string GetZipDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string DeriveTitle(string sourceDisplayName)
    {
        var fileName = Path.GetFileName(sourceDisplayName.Replace('\\', '/'));
        var title = Path.GetFileNameWithoutExtension(fileName);
        title = NormalizeInlineText(title) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Untitled";
        }

        return title.Length <= MaxDerivedTitleLength ? title : title[..MaxDerivedTitleLength].Trim();
    }

    private static string? NormalizeInlineText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void Skip(
        List<NovelImportSkippedChapterPayload> skipped,
        List<NovelImportDiagnosticPayload> diagnostics,
        int index,
        string title,
        string reason,
        string detail = "")
    {
        var safeTitle = NormalizeInlineText(title) ?? $"spine-{index}";
        skipped.Add(new NovelImportSkippedChapterPayload(index, safeTitle, reason));
        diagnostics.Add(Diagnostic(
            "import.epub.chapter_skipped",
            "已跳过不可导入的 EPUB 章节。",
            $"index={index}; title={safeTitle}; reason={reason}; detail={Truncate(detail)}",
            "warning"));
    }

    private static NovelImportEpubParseException BuildFailure(
        string code,
        string message,
        string detail,
        IReadOnlyList<NovelImportDiagnosticPayload>? diagnostics = null,
        IReadOnlyList<NovelImportSkippedChapterPayload>? skippedChapters = null)
    {
        var allDiagnostics = new List<NovelImportDiagnosticPayload>(diagnostics ?? []);
        if (allDiagnostics.All(diagnostic => !string.Equals(diagnostic.Code, code, StringComparison.Ordinal)))
        {
            allDiagnostics.Add(Diagnostic(code, message, detail, "error"));
        }

        return new NovelImportEpubParseException(code, message, allDiagnostics, skippedChapters ?? []);
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

    private static void ValidateOptions(NovelImportEpubParserOptions options)
    {
        if (options.MaxCompressedBytes <= 0 ||
            options.MaxMetadataEntryBytes <= 0 ||
            options.MaxChapterEntryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Parser limits must be positive.");
        }
    }

    private sealed record ManifestItem(string Id, string Href);

    private sealed record EpubHtmlText(string Title, string Content);
}

internal sealed record NovelImportEpubParserOptions(
    long MaxCompressedBytes = 104_857_600,
    long MaxMetadataEntryBytes = 1_048_576,
    long MaxChapterEntryBytes = 10_485_760);

internal sealed record NovelImportEpubParseResult(
    string Title,
    IReadOnlyList<NovelImportParsedChapter> Chapters,
    IReadOnlyList<NovelImportSkippedChapterPayload> SkippedChapters,
    IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics);

internal sealed class NovelImportEpubParseException : Exception
{
    public NovelImportEpubParseException(
        string code,
        string message,
        IReadOnlyList<NovelImportDiagnosticPayload> diagnostics,
        IReadOnlyList<NovelImportSkippedChapterPayload> skippedChapters)
        : base(message)
    {
        Code = code;
        Diagnostics = diagnostics;
        SkippedChapters = skippedChapters;
    }

    public string Code { get; }

    public IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics { get; }

    public IReadOnlyList<NovelImportSkippedChapterPayload> SkippedChapters { get; }
}
