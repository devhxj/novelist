using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using Markdig;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemNovelExportService : INovelExportService
{
    private static readonly IReadOnlyDictionary<string, NovelExportFileFilter[]> ExportFilters =
        new Dictionary<string, NovelExportFileFilter[]>(StringComparer.Ordinal)
        {
            ["epub"] = [new NovelExportFileFilter("EPUB 电子书 (*.epub)", "*.epub")],
            ["markdown"] = [new NovelExportFileFilter("Markdown 文件 (*.md)", "*.md")],
            ["txt"] = [new NovelExportFileFilter("文本文件 (*.txt)", "*.txt")]
        };

    private readonly INovelService _novels;
    private readonly IChapterContentService _chapters;
    private readonly IAppSettingsService _settings;
    private readonly INovelExportDestinationPicker _destinationPicker;

    public FileSystemNovelExportService(
        INovelService novels,
        IChapterContentService chapters,
        IAppSettingsService settings,
        INovelExportDestinationPicker destinationPicker)
    {
        _novels = novels;
        _chapters = chapters;
        _settings = settings;
        _destinationPicker = destinationPicker;
    }

    public async ValueTask ExportNovelAsync(long novelId, string format, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var normalizedFormat = NormalizeFormat(format);
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        var novel = novels.SingleOrDefault(item => item.Id == novelId)
            ?? throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        var chapters = (await _chapters.GetChaptersAsync(novelId, cancellationToken))
            .OrderBy(item => item.ChapterNumber)
            .ToArray();
        if (chapters.Length == 0)
        {
            throw new ArgumentException("Novel has no chapters to export.", nameof(novelId));
        }

        var chapterContents = new List<ChapterWithContent>();
        foreach (var chapter in chapters)
        {
            chapterContents.Add(new ChapterWithContent(
                chapter,
                await _chapters.GetContentAsync(novelId, chapter.FilePath, cancellationToken)));
        }

        var settings = await _settings.GetSettingsAsync(cancellationToken);
        var (data, fileName) = normalizedFormat switch
        {
            "epub" => (BuildEpub(novel, chapterContents, settings.UserName), SafeFileName(novel.Title) + ".epub"),
            "markdown" => (Encoding.UTF8.GetBytes(BuildMarkdown(novel, chapterContents)), SafeFileName(novel.Title) + ".md"),
            "txt" => (Encoding.UTF8.GetBytes(BuildTxt(novel, chapterContents)), SafeFileName(novel.Title) + ".txt"),
            _ => throw new ArgumentException($"Unsupported export format '{format}'.", nameof(format))
        };

        var destination = await _destinationPicker.PickSaveFileAsync(
            new NovelExportDestinationRequest(fileName, normalizedFormat, ExportFilters[normalizedFormat]),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await File.WriteAllBytesAsync(destination, data, cancellationToken);
    }

    private static string BuildMarkdown(NovelPayload novel, IReadOnlyList<ChapterWithContent> chapters)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# {novel.Title}");
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(novel.Genre))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"**类型**: {novel.Genre}  ");
        }

        if (!string.IsNullOrWhiteSpace(novel.Description))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"**简介**: {novel.Description}  ");
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"**导出时间**: {DateTime.Now:yyyy-MM-dd HH:mm}  ");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("## 目录");
        builder.AppendLine();

        foreach (var chapter in chapters)
        {
            var title = ChapterTitle(chapter.Chapter);
            builder.AppendLine(CultureInfo.InvariantCulture, $"- [第{chapter.Chapter.ChapterNumber}章 {title}](#第{chapter.Chapter.ChapterNumber}章)");
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        foreach (var chapter in chapters)
        {
            var title = ChapterTitle(chapter.Chapter);
            builder.AppendLine(CultureInfo.InvariantCulture, $"## 第{chapter.Chapter.ChapterNumber}章 {title}");
            builder.AppendLine();
            builder.AppendLine(chapter.Content);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildTxt(NovelPayload novel, IReadOnlyList<ChapterWithContent> chapters)
    {
        var builder = new StringBuilder();
        builder.AppendLine(novel.Title);
        builder.AppendLine();
        foreach (var chapter in chapters)
        {
            var title = ChapterTitle(chapter.Chapter);
            builder.AppendLine(CultureInfo.InvariantCulture, $"第{chapter.Chapter.ChapterNumber}章 {title}");
            builder.AppendLine();
            builder.AppendLine(chapter.Content.Trim());
            builder.AppendLine();
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static byte[] BuildEpub(
        NovelPayload novel,
        IReadOnlyList<ChapterWithContent> chapters,
        string author)
    {
        var markdown = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        var uuid = $"urn:uuid:{Guid.NewGuid():D}";
        var chapterEntries = chapters
            .Select(chapter => new EpubChapterEntry(
                chapter.Chapter.ChapterNumber,
                $"chapters/chapter-{chapter.Chapter.ChapterNumber}.xhtml",
                $"第{chapter.Chapter.ChapterNumber}章 {ChapterTitle(chapter.Chapter)}",
                Markdown.ToHtml(chapter.Content, markdown)))
            .ToArray();

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            WriteEntry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            WriteEntry(zip, "META-INF/container.xml", ContainerXml, CompressionLevel.Optimal);
            WriteEntry(zip, "OEBPS/content.opf", BuildContentOpf(novel, author, uuid, chapterEntries), CompressionLevel.Optimal);
            WriteEntry(zip, "OEBPS/toc.ncx", BuildTocNcx(novel, uuid, chapterEntries), CompressionLevel.Optimal);
            WriteEntry(zip, "OEBPS/nav.xhtml", BuildNav(novel, chapterEntries), CompressionLevel.Optimal);
            WriteEntry(zip, "OEBPS/styles.css", EpubCss, CompressionLevel.Optimal);

            foreach (var chapter in chapterEntries)
            {
                WriteEntry(
                    zip,
                    $"OEBPS/{chapter.Href}",
                    BuildChapterXhtml(chapter),
                    CompressionLevel.Optimal);
            }
        }

        return stream.ToArray();
    }

    private static string BuildContentOpf(
        NovelPayload novel,
        string author,
        string uuid,
        IReadOnlyList<EpubChapterEntry> chapters)
    {
        var manifest = new StringBuilder();
        manifest.AppendLine("""    <item id="toc" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""");
        manifest.AppendLine("""    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""");
        manifest.AppendLine("""    <item id="css" href="styles.css" media-type="text/css"/>""");
        foreach (var chapter in chapters)
        {
            manifest.AppendLine(CultureInfo.InvariantCulture, $"""    <item id="chapter-{chapter.Number}" href="{chapter.Href}" media-type="application/xhtml+xml"/>""");
        }

        var spine = new StringBuilder();
        foreach (var chapter in chapters)
        {
            spine.AppendLine(CultureInfo.InvariantCulture, $"""    <itemref idref="chapter-{chapter.Number}"/>""");
        }

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" unique-identifier="book-id" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="book-id">{{WebUtility.HtmlEncode(uuid)}}</dc:identifier>
                <dc:title>{{WebUtility.HtmlEncode(novel.Title)}}</dc:title>
                <dc:creator>{{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(author) ? "Goink" : author)}}</dc:creator>
                <dc:language>zh-CN</dc:language>
                <dc:description>{{WebUtility.HtmlEncode(novel.Description)}}</dc:description>
              </metadata>
              <manifest>
            {{manifest.ToString().TrimEnd()}}
              </manifest>
              <spine toc="toc">
            {{spine.ToString().TrimEnd()}}
              </spine>
            </package>
            """;
    }

    private static string BuildTocNcx(
        NovelPayload novel,
        string uuid,
        IReadOnlyList<EpubChapterEntry> chapters)
    {
        var navPoints = new StringBuilder();
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            navPoints.AppendLine(CultureInfo.InvariantCulture, $$"""
                    <navPoint id="navPoint-{{i + 1}}" playOrder="{{i + 1}}">
                      <navLabel><text>{{WebUtility.HtmlEncode(chapter.Title)}}</text></navLabel>
                      <content src="{{chapter.Href}}"/>
                    </navPoint>
                """);
        }

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
              <head>
                <meta name="dtb:uid" content="{{WebUtility.HtmlEncode(uuid)}}"/>
                <meta name="dtb:depth" content="1"/>
                <meta name="dtb:totalPageCount" content="0"/>
                <meta name="dtb:maxPageNumber" content="0"/>
              </head>
              <docTitle><text>{{WebUtility.HtmlEncode(novel.Title)}}</text></docTitle>
              <navMap>
            {{navPoints.ToString().TrimEnd()}}
              </navMap>
            </ncx>
            """;
    }

    private static string BuildNav(
        NovelPayload novel,
        IReadOnlyList<EpubChapterEntry> chapters)
    {
        var links = new StringBuilder();
        foreach (var chapter in chapters)
        {
            links.AppendLine(CultureInfo.InvariantCulture, $"""      <li><a href="{chapter.Href}">{WebUtility.HtmlEncode(chapter.Title)}</a></li>""");
        }

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" lang="zh-CN">
            <head><title>{{WebUtility.HtmlEncode(novel.Title)}}</title></head>
            <body>
              <nav epub:type="toc" id="toc">
                <h1>目录</h1>
                <ol>
            {{links.ToString().TrimEnd()}}
                </ol>
              </nav>
            </body>
            </html>
            """;
    }

    private static string BuildChapterXhtml(EpubChapterEntry chapter)
    {
        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" lang="zh-CN">
            <head>
              <title>{{WebUtility.HtmlEncode(chapter.Title)}}</title>
              <link rel="stylesheet" type="text/css" href="../styles.css"/>
            </head>
            <body>
              <h1>{{WebUtility.HtmlEncode(chapter.Title)}}</h1>
            {{chapter.HtmlContent.Trim()}}
            </body>
            </html>
            """;
    }

    private static void WriteEntry(
        ZipArchive zip,
        string path,
        string content,
        CompressionLevel compressionLevel)
    {
        var entry = zip.CreateEntry(path, compressionLevel);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ChapterTitle(ChapterPayload chapter)
    {
        return string.IsNullOrWhiteSpace(chapter.Title)
            ? $"第{chapter.ChapterNumber}章"
            : chapter.Title;
    }

    private static string NormalizeFormat(string format)
    {
        var normalized = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (!ExportFilters.ContainsKey(normalized))
        {
            throw new ArgumentException($"Unsupported export format '{format}'.", nameof(format));
        }

        return normalized;
    }

    private static string SafeFileName(string title)
    {
        var safe = new string((title ?? string.Empty)
            .Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || char.IsControl(ch) ? '_' : ch)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "novel" : safe;
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private sealed record ChapterWithContent(ChapterPayload Chapter, string Content);

    private sealed record EpubChapterEntry(
        int Number,
        string Href,
        string Title,
        string HtmlContent);

    private const string ContainerXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private const string EpubCss = """
        body { font-family: "Noto Serif SC", "Source Han Serif SC", serif; line-height: 1.8; margin: 1.5em; }
        h1 { font-size: 1.6em; margin-bottom: 1em; text-align: center; }
        p { text-indent: 2em; margin: 0.5em 0; }
        """;
}
