using System.Text;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class NovelImportTextParserTests
{
    [Fact]
    public void ParseDetectsUtf8BomAndSplitsChineseChapterHeaders()
    {
        var bytes = WithUtf8Bom("第一章 雨夜\n他听见门外的脚步。\n\n第二章 旧账\n灯灭了。");

        var result = NovelImportTextParser.Parse(bytes, "雨夜.txt", NovelImportKinds.Txt);

        Assert.Equal("utf-8", result.Encoding.EncodingName);
        Assert.Equal("bom:utf-8", result.Encoding.DetectionSource);
        Assert.Equal("high", result.Encoding.Confidence);
        Assert.Equal(0, result.Encoding.ReplacementCharacterCount);
        Assert.Equal(["第一章 雨夜", "第二章 旧账"], result.Chapters.Select(chapter => chapter.Title));
        Assert.Equal("他听见门外的脚步。", result.Chapters[0].Content);
    }

    [Fact]
    public void ParseDetectsValidUtf8AndMarkdownHeadings()
    {
        var text = "# Chapter 1: First Contact\r\nThe door opened.\r\n\r\n## Chapter 2 - Return\r\nThe rain stopped.";

        var result = NovelImportTextParser.Parse(Encoding.UTF8.GetBytes(text), "book.md", NovelImportKinds.Markdown);

        Assert.Equal("strict:utf-8", result.Encoding.DetectionSource);
        Assert.Equal(["Chapter 1: First Contact", "Chapter 2 - Return"], result.Chapters.Select(chapter => chapter.Title));
        Assert.Equal("The door opened.", result.Chapters[0].Content);
        Assert.Equal("The rain stopped.", result.Chapters[1].Content);
    }

    [Fact]
    public void ParseDetectsUtf16BomAndNoBomHeuristics()
    {
        var leText = "第一章 无签名\n内容一。\n第二章 继续\n内容二。";
        var beText = "Chapter 1\nBody one.\nChapter 2\nBody two.";

        var withLeBom = WithBom(Encoding.Unicode.GetBytes(leText), 0xFF, 0xFE);
        var leNoBom = Encoding.Unicode.GetBytes(leText);
        var withBeBom = WithBom(Encoding.BigEndianUnicode.GetBytes(beText), 0xFE, 0xFF);
        var beNoBom = Encoding.BigEndianUnicode.GetBytes(beText);

        Assert.Equal("bom:utf-16le", NovelImportTextParser.Parse(withLeBom, "le.txt", NovelImportKinds.Txt).Encoding.DetectionSource);
        Assert.Equal("heuristic:utf-16le", NovelImportTextParser.Parse(leNoBom, "le-no-bom.txt", NovelImportKinds.Txt).Encoding.DetectionSource);
        Assert.Equal("bom:utf-16be", NovelImportTextParser.Parse(withBeBom, "be.txt", NovelImportKinds.Txt).Encoding.DetectionSource);
        Assert.Equal("heuristic:utf-16be", NovelImportTextParser.Parse(beNoBom, "be-no-bom.txt", NovelImportKinds.Txt).Encoding.DetectionSource);
    }

    [Fact]
    public void ParseDetectsGb18030AfterRegisteringCodePageProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb18030 = Encoding.GetEncoding("GB18030");
        var bytes = gb18030.GetBytes("第一章 编码\n这是GB18030文本。\n第二章 继续\n内容。");

        var result = NovelImportTextParser.Parse(bytes, "编码.txt", NovelImportKinds.Txt);

        Assert.Equal("gb18030", result.Encoding.EncodingName);
        Assert.Equal("codepage:gb18030", result.Encoding.DetectionSource);
        Assert.Equal("medium", result.Encoding.Confidence);
        Assert.Equal(["第一章 编码", "第二章 继续"], result.Chapters.Select(chapter => chapter.Title));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "import.encoding.gb18030_provider_registered");
    }

    [Fact]
    public void ParseSupportsChineseNumeralVolumePartAndEnglishChapterHeaders()
    {
        var text = """
            第十二章 风起
            第一段。
            第2卷 旧城
            第二段。
            第三部 归途
            第三段。
            Chapter 4: Return
            Fourth body.
            """;

        var result = ParseUtf8(text);

        Assert.Equal(
            ["第十二章 风起", "第2卷 旧城", "第三部 归途", "Chapter 4: Return"],
            result.Chapters.Select(chapter => chapter.Title));
    }

    [Fact]
    public void ParseNormalizesCrOnlyNewlinesAndKeepsMixedPunctuationContent()
    {
        var text = "第一章 混合标点\r他说：“Hello, world!”\r她问：Really?\r第二章 继续\r内容仍然保留。";

        var result = ParseUtf8(text);

        Assert.Equal(["第一章 混合标点", "第二章 继续"], result.Chapters.Select(chapter => chapter.Title));
        Assert.Equal("他说：“Hello, world!”\n她问：Really?", result.Chapters[0].Content);
        Assert.Equal("内容仍然保留。", result.Chapters[1].Content);
    }

    [Fact]
    public void ParseReducesBodyLineFalsePositivesAndFallsBackToSingleChapter()
    {
        var text = """
            第一章的时候，他还不知道雨会下这么久。
            这不是章节标题，只是正文。
            Chapter 2 is mentioned inside a sentence, not a heading.
            结尾仍然属于同一章。
            """;

        var result = NovelImportTextParser.Parse(Encoding.UTF8.GetBytes(text), "单章.txt", NovelImportKinds.Txt);

        Assert.Single(result.Chapters);
        Assert.Equal("单章", result.Chapters[0].Title);
        Assert.Contains("第一章的时候", result.Chapters[0].Content, StringComparison.Ordinal);
        Assert.Empty(result.SkippedChapters);
    }

    [Fact]
    public void ParseImportsNoHeaderFileAsSingleDerivedTitle()
    {
        var result = ParseUtf8("没有章节标题。\n但这仍然是一篇完整导入文本。", "测试锚定小说.markdown", NovelImportKinds.Markdown);

        Assert.Single(result.Chapters);
        Assert.Equal("测试锚定小说", result.Chapters[0].Title);
        Assert.Equal("没有章节标题。\n但这仍然是一篇完整导入文本。", result.Chapters[0].Content);
    }

    [Fact]
    public void ParseSkipsEmptyDetectedChaptersWithoutDroppingValidOnes()
    {
        var text = """
            第一章 空章
               
            第二章 有内容
            真正的正文。
            第三章 也空
            """;

        var result = ParseUtf8(text);

        Assert.Single(result.Chapters);
        Assert.Equal("第二章 有内容", result.Chapters[0].Title);
        Assert.Equal([1, 3], result.SkippedChapters.Select(chapter => chapter.Index));
        Assert.All(result.SkippedChapters, chapter => Assert.Equal("empty_content", chapter.Reason));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "import.chapter.skipped_empty");
    }

    [Fact]
    public void ParseRejectsEmptyBinaryLowConfidenceAndInvalidBytesWithStructuredDiagnostics()
    {
        AssertFailure("import.text.empty", Encoding.UTF8.GetBytes(" \r\n\t "));
        AssertFailure("import.encoding.binary_like", [0x00, 0x01, 0x02, 0x03, 0x00, 0x04, 0x05, 0x00]);
        AssertFailure(
            "import.encoding.low_confidence",
            [0x81, 0x82, 0x83, 0x84],
            new NovelImportTextParserOptions(Gb18030EncodingFactory: () => null));
        AssertFailure("import.encoding.low_confidence", [0xFF, 0xFF, 0xFF, 0xFF]);
    }

    [Fact]
    public void ParseLargeTextFixtureWithoutUnboundedChapterGrowth()
    {
        var builder = new StringBuilder(capacity: 5_500_000);
        for (var i = 1; i <= 500; i++)
        {
            builder.Append("第").Append(i).Append("章 压测").Append(i).Append('\n');
            builder.Append("这一章用于导入解析压测，包含稳定正文。");
            builder.Append('雨', 10_000);
            builder.Append('\n');
        }

        var result = NovelImportTextParser.Parse(Encoding.UTF8.GetBytes(builder.ToString()), "large.txt", NovelImportKinds.Txt);

        Assert.Equal(500, result.Chapters.Count);
        Assert.Empty(result.SkippedChapters);
        Assert.True(result.Chapters.Sum(chapter => chapter.Content.Length) > 5_000_000);
    }

    private static NovelImportTextParseResult ParseUtf8(
        string text,
        string sourceDisplayName = "book.txt",
        string importKind = NovelImportKinds.Txt)
    {
        return NovelImportTextParser.Parse(Encoding.UTF8.GetBytes(text), sourceDisplayName, importKind);
    }

    private static void AssertFailure(
        string expectedCode,
        byte[] bytes,
        NovelImportTextParserOptions? options = null)
    {
        var exception = Assert.Throws<NovelImportTextParseException>(() =>
            NovelImportTextParser.Parse(bytes, "broken.txt", NovelImportKinds.Txt, options));
        Assert.Equal(expectedCode, exception.Code);
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    private static byte[] WithUtf8Bom(string text)
    {
        return WithBom(Encoding.UTF8.GetBytes(text), 0xEF, 0xBB, 0xBF);
    }

    private static byte[] WithBom(byte[] bytes, params byte[] bom)
    {
        var result = new byte[bom.Length + bytes.Length];
        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(bytes, 0, result, bom.Length, bytes.Length);
        return result;
    }
}
