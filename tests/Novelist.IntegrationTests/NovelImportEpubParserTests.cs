using System.IO.Compression;
using System.Text;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class NovelImportEpubParserTests
{
    [Fact]
    public void ParseReadsContainerMetadataSpineOrderAndReadableHtmlText()
    {
        var epub = BuildEpub(new Dictionary<string, string>
        {
            ["META-INF/container.xml"] = Container("OEBPS/content.opf"),
            ["OEBPS/content.opf"] = Opf(
                "Anchored Book",
                [
                    ("c1", "chapters/one.xhtml"),
                    ("c2", "chapters/two.xhtml")
                ],
                ["c2", "c1"]),
            ["OEBPS/chapters/one.xhtml"] = Xhtml("Chapter One", "<p>First paragraph.</p><p>Second paragraph.</p>"),
            ["OEBPS/chapters/two.xhtml"] = Xhtml(
                "Chapter Two",
                "<head><style>.x{}</style><script>bad()</script></head><body><h1>Chapter Two</h1><p>Visible text.</p><ul><li>List item</li></ul></body>",
                includeBody: false)
        });

        var result = NovelImportEpubParser.Parse(epub, "book.epub");

        Assert.Equal("Anchored Book", result.Title);
        Assert.Equal(["Chapter Two", "Chapter One"], result.Chapters.Select(chapter => chapter.Title));
        Assert.Equal("Chapter Two\nVisible text.\nList item", result.Chapters[0].Content);
        Assert.Equal("Chapter One\nFirst paragraph.\nSecond paragraph.", result.Chapters[1].Content);
        Assert.Empty(result.SkippedChapters);
    }

    [Fact]
    public void ParseResolvesNestedOpfUrlEscapedHrefAndCaseMismatchFallbackWithinZip()
    {
        var epub = BuildEpub(new Dictionary<string, string>
        {
            ["META-INF/container.xml"] = Container("OPS/package.opf"),
            ["OPS/package.opf"] = Opf(
                "Escaped",
                [("chapter", "../Text/Chapter%201.xhtml")],
                ["chapter"]),
            ["text/chapter 1.xhtml"] = Xhtml("Escaped One", "<p>Case fallback text.</p>")
        });

        var result = NovelImportEpubParser.Parse(epub, "escaped.epub");

        Assert.Single(result.Chapters);
        Assert.Equal("Escaped One", result.Chapters[0].Title);
        Assert.Equal("Escaped One\nCase fallback text.", result.Chapters[0].Content);
    }

    [Fact]
    public void ParseSkipsMissingEmptyAndInvalidSpineItemsWhenOtherChaptersAreReadable()
    {
        var epub = BuildEpub(new Dictionary<string, string>
        {
            ["META-INF/container.xml"] = Container("content.opf"),
            ["content.opf"] = Opf(
                "Partial",
                [
                    ("missing", "missing.xhtml"),
                    ("empty", "empty.xhtml"),
                    ("invalid", "invalid.xhtml"),
                    ("valid", "valid.xhtml")
                ],
                ["not-in-manifest", "missing", "empty", "invalid", "valid"]),
            ["empty.xhtml"] = RawXhtml("<body><p>   </p></body>"),
            ["invalid.xhtml"] = "<html><body><p>broken",
            ["valid.xhtml"] = Xhtml("Valid", "<p>Readable.</p>")
        });

        var result = NovelImportEpubParser.Parse(epub, "partial.epub");

        Assert.Single(result.Chapters);
        Assert.Equal("Valid", result.Chapters[0].Title);
        Assert.Equal(
            ["missing_manifest_item", "missing_file", "empty_content", "invalid_html"],
            result.SkippedChapters.Select(chapter => chapter.Reason));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "import.epub.chapter_skipped");
    }

    [Fact]
    public void ParseFailsWhenContainerIsInvalidOrNoReadableChaptersExist()
    {
        AssertFailure(
            "import.epub.invalid_container",
            BuildEpub(new Dictionary<string, string> { ["META-INF/container.xml"] = "<container>" }));

        AssertFailure(
            "import.epub.no_readable_chapters",
            BuildEpub(new Dictionary<string, string>
            {
                ["META-INF/container.xml"] = Container("content.opf"),
                ["content.opf"] = Opf("Empty", [("empty", "empty.xhtml")], ["empty"]),
                ["empty.xhtml"] = RawXhtml("<body><p> </p></body>")
            }));
    }

    [Fact]
    public void ParseRejectsZipSlipAndAbsoluteInternalPaths()
    {
        AssertFailure(
            "import.epub.unsafe_path",
            BuildEpub(new Dictionary<string, string>
            {
                ["META-INF/container.xml"] = Container("../content.opf")
            }));

        AssertFailure(
            "import.epub.unsafe_path",
            BuildEpub(new Dictionary<string, string>
            {
                ["META-INF/container.xml"] = Container("content.opf"),
                ["content.opf"] = Opf("Unsafe", [("evil", "/absolute.xhtml")], ["evil"])
            }));
    }

    private static void AssertFailure(string expectedCode, byte[] epub)
    {
        var exception = Assert.Throws<NovelImportEpubParseException>(() =>
            NovelImportEpubParser.Parse(epub, "broken.epub"));
        Assert.Equal(expectedCode, exception.Code);
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    private static byte[] BuildEpub(IReadOnlyDictionary<string, string> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Key);
                using var stream = zipEntry.Open();
                var bytes = Encoding.UTF8.GetBytes(entry.Value);
                stream.Write(bytes);
            }
        }

        return output.ToArray();
    }

    private static string Container(string opfPath)
    {
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="{{opfPath}}" media-type="application/oebps-package+xml" />
              </rootfiles>
            </container>
            """;
    }

    private static string Opf(
        string title,
        IReadOnlyList<(string Id, string Href)> manifest,
        IReadOnlyList<string> spine)
    {
        var manifestXml = string.Join(
            "\n",
            manifest.Select(item => $"""<item id="{item.Id}" href="{item.Href}" media-type="application/xhtml+xml" />"""));
        var spineXml = string.Join("\n", spine.Select(id => $"""<itemref idref="{id}" />"""));
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>{{title}}</dc:title>
              </metadata>
              <manifest>
                {{manifestXml}}
              </manifest>
              <spine>
                {{spineXml}}
              </spine>
            </package>
            """;
    }

    private static string Xhtml(string title, string bodyMarkup, bool includeBody = true)
    {
        var body = includeBody ? $"<body><h1>{title}</h1>{bodyMarkup}</body>" : bodyMarkup;
        return RawXhtml(body);
    }

    private static string RawXhtml(string body)
    {
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              {{body}}
            </html>
            """;
    }
}
