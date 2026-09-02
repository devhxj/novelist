using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationChapterSplitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RegisterMaterializationSourceSkipsLegacyExtractionAndIsReadyForChapterSplit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料化来源登记", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "materialization-source.txt",
            "第1章 开端\n" + new string('甲', 25_000) + "\n第2章 转折\n" + new string('乙', 25_000));
        var anchors = new SqliteReferenceAnchorService(options, novels);

        var anchor = await anchors.RegisterMaterializationSourceAsync(
            new CreateReferenceAnchorPayload(novel.Id, "材料化来源", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status);
        Assert.Equal(0, await CountAnchorRowsAsync(options, "reference_source_segments", anchor.AnchorId));
        Assert.Equal(0, await CountAnchorRowsAsync(options, "reference_materials", anchor.AnchorId));

        var materialization = new SqliteReferenceMaterializationService(
            options,
            new RecordingChapterSplitAnalyzer(ReferenceChapterSplitModelResult.Empty));
        var preview = await materialization.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, "第{number}章 {title}"),
            CancellationToken.None);

        Assert.Equal(2, preview.ChapterCount);
    }

    [Fact]
    public async Task RegisterMaterializationSourceFromContentWritesAppDataFileAndRegisters()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("拖拽导入登记", "", ""), CancellationToken.None);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var content = "第1章 开端\n" + new string('丙', 12_000) + "\n第2章 结束\n" + new string('丁', 8_000);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));

        var anchor = await anchors.RegisterMaterializationSourceFromContentAsync(
            new CreateReferenceAnchorFromContentPayload(novel.Id, "雨夜参考书", null, "拖拽的书.md", encoded),
            CancellationToken.None);

        // 内容被写入应用数据目录（服务端生成文件名），并按普通来源完成注册。
        Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status);
        Assert.True(File.Exists(anchor.SourcePath), "content registration must materialize a source file");
        Assert.EndsWith(".md", anchor.SourcePath, StringComparison.Ordinal);
        Assert.Contains("sources", anchor.SourcePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterMaterializationSourceFromContentRejectsUnsupportedExtensions()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("拖拽扩展名校验", "", ""), CancellationToken.None);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("不应被接受的文件内容"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await anchors.RegisterMaterializationSourceFromContentAsync(
                new CreateReferenceAnchorFromContentPayload(novel.Id, "非法扩展名", null, "书.pdf", encoded),
                CancellationToken.None));

        Assert.Contains(".txt or .md", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAutoSplitSendsOnlyTheFirstFiftyThousandNormalizedCharactersToTheModel()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("自动章节切分", "", ""), CancellationToken.None);
        var prefix = "# 第一章\r\n" + new string('甲', 49_990);
        var sourcePath = CreateSourceFile("auto-split.md", prefix + "\r\n# 第二章\r\n" + new string('乙', 300));
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "自动切分来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var analyzer = new RecordingChapterSplitAnalyzer(
            new ReferenceChapterSplitModelResult(
                PatternKind: "markdown_heading",
                DelimiterTemplate: "# {title}",
                Confidence: 0.91,
                EvidenceOffsets: [0]));
        var service = new SqliteReferenceMaterializationService(options, analyzer);

        var result = await service.AnalyzeChapterSplitAsync(
            new AnalyzeReferenceChapterSplitPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);

        var request = Assert.Single(analyzer.Requests);
        Assert.Equal(50_000, request.NormalizedSample.Length);
        Assert.DoesNotContain("# 第二章", request.NormalizedSample, StringComparison.Ordinal);
        Assert.Equal(50_000, result.SampleCharCount);
        Assert.Equal(2, result.ChapterCount);
        Assert.Equal(ReferenceChapterSplitProfileStates.Validated, result.Status);
    }

    [Fact]
    public async Task PreviewManualSplitValidatesTheWholeSourceAndPersistsItsBoundaries()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("手动章节切分", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "manual-split.txt",
            "第1章 开端\n\n雨声压住窗沿。\n\n第2章 转折\n\n门外响起第三次敲门。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "手动切分来源", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);
        var analyzer = new RecordingChapterSplitAnalyzer(ReferenceChapterSplitModelResult.Empty);
        var service = new SqliteReferenceMaterializationService(options, analyzer);

        var result = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, "第{number}章 {title}"),
            CancellationToken.None);

        Assert.Empty(analyzer.Requests);
        Assert.Equal(ReferenceChapterSplitModes.Manual, result.SplitMode);
        Assert.Equal(ReferenceChapterSplitProfileStates.Validated, result.Status);
        Assert.Equal(2, result.ChapterCount);
        Assert.Collection(
            result.Boundaries,
            first =>
            {
                Assert.Equal(1, first.ChapterIndex);
                Assert.Equal("开端", first.Title);
                Assert.True(first.ContentEnd > first.ContentStart);
            },
            second =>
            {
                Assert.Equal(2, second.ChapterIndex);
                Assert.Equal("转折", second.Title);
                Assert.True(second.ContentEnd > second.ContentStart);
            });
        Assert.Equal(2, await CountBoundariesAsync(options, result.SplitProfileId));
    }

    [Fact]
    public async Task PreviewManualSplitSupportsEnglishChapterTemplates()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("English chapter split", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "english-split.txt",
            "Chapter 1: First Contact\n\nThe door opened.\n\nChapter 2: Return\n\nThe rain stopped.\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "English split source", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(options, new RecordingChapterSplitAnalyzer(ReferenceChapterSplitModelResult.Empty));

        var result = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, "Chapter {number}: {title}"),
            CancellationToken.None);

        Assert.Equal(ReferenceChapterSplitProfileStates.Validated, result.Status);
        Assert.Equal(2, result.ChapterCount);
        Assert.Equal(["First Contact", "Return"], result.Boundaries.Select(boundary => boundary.Title).ToArray());
        Assert.All(result.Boundaries, boundary => Assert.True(boundary.ContentEnd > boundary.ContentStart));
    }

    [Fact]
    public async Task PreviewManualSplitExcludesLeadingTableOfContentsThatRepeatsTheBodyHeadings()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("目录章节切分", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "toc-split.txt",
            "目录\n第一辑 开端(text00002.html)\n第二辑 回声(text00003.html)\n\n版权信息\n\n第一辑 开端\n\n雨声压住窗沿。\n\n第二辑 回声\n\n门外响起第三次敲门。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "目录来源", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(options, new RecordingChapterSplitAnalyzer(ReferenceChapterSplitModelResult.Empty));

        var result = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, "第{number}辑 {title}"),
            CancellationToken.None);

        Assert.Equal(2, result.ChapterCount);
        Assert.Equal(["开端", "回声"], result.Boundaries.Select(boundary => boundary.Title).ToArray());
        Assert.True(result.Boundaries[0].HeadingStart > 30);
        Assert.All(result.Boundaries, boundary => Assert.True(boundary.ContentEnd > boundary.ContentStart));
    }

    [Fact]
    public async Task AnalyzeAutoSplitHandlesUtf8BomAndFullWidthHeadingWhitespace()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("全角空格目录", "", ""), CancellationToken.None);
        var source = """
            目录
            第一辑 开端(text00002.html)
            第二辑 回声(text00003.html)

            版权信息

            第一辑 开端

            雨声压住窗沿。

            第二辑　回声

            门外响起第三次敲门。
            """;
        var sourcePath = CreateSourceFileWithBom("full-width-heading-space.txt", source);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "全角空格来源", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);
        var firstHeadingOffset = source.IndexOf("第一辑", StringComparison.Ordinal);
        var service = new SqliteReferenceMaterializationService(
            options,
            new RecordingChapterSplitAnalyzer(new ReferenceChapterSplitModelResult(
                "chapter_template",
                "第{number}辑 {title}",
                0.95,
                [firstHeadingOffset])));

        var result = await service.AnalyzeChapterSplitAsync(
            new AnalyzeReferenceChapterSplitPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);

        Assert.Equal(2, result.ChapterCount);
        Assert.Equal(["开端", "回声"], result.Boundaries.Select(boundary => boundary.Title).ToArray());
    }

    [Fact]
    public async Task AnalyzeAutoSplitRejectsTemplatesMatchingOnlyOneMidSampleHeading()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("单标题幻觉防护", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "single-heading.md",
            "开头是没有标题的正文。\n\n雨声压住窗沿。\n\n# 突兀孤立的标题\n\n后续仍然只有这一处标题。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "单标题来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(
            options,
            new RecordingChapterSplitAnalyzer(new ReferenceChapterSplitModelResult(
                "markdown_heading",
                "# {title}",
                0.9,
                [0])));

        // 幻觉防护：模板在整个样本中只命中一个非起始标题时判定不可靠，而不是生成单章切分。
        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.AnalyzeChapterSplitAsync(
                new AnalyzeReferenceChapterSplitPayload(novel.Id, anchor.AnchorId),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.ChapterSplitOutputInvalid, exception.ErrorCode);
        Assert.Contains("one heading", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAutoSplitAcceptsSingleHeadingAnchoredAtSampleStart()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("首章超长", "", ""), CancellationToken.None);
        var longFirstChapterBody = new string('雨', 55_000);
        var sourcePath = CreateSourceFile(
            "long-first-chapter.md",
            "# 第一章 开端\n\n" + longFirstChapterBody + "\n\n# 第二章 回声\n\n门外的雨停了。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "超长首章来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(
            options,
            new RecordingChapterSplitAnalyzer(new ReferenceChapterSplitModelResult(
                "markdown_heading",
                "# {title}",
                0.9,
                [0])));

        var result = await service.AnalyzeChapterSplitAsync(
            new AnalyzeReferenceChapterSplitPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);

        // 样本内只有起点锚定的单个标题，但全文边界重建找到两章：分析应成功而非误判幻觉。
        Assert.Equal(2, result.ChapterCount);
    }

    [Fact]
    public async Task PreviewManualSplitSupportsLiteralDelimiters()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("Literal chapter split", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "literal-split.txt",
            "--- CHAPTER ---\n\nA door opens.\n\n--- CHAPTER ---\n\nThe rain returns.\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "Literal split source", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(options, new RecordingChapterSplitAnalyzer(ReferenceChapterSplitModelResult.Empty));

        var result = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, "literal:--- CHAPTER ---"),
            CancellationToken.None);

        Assert.Equal(ReferenceChapterSplitProfileStates.Validated, result.Status);
        Assert.Equal(2, result.ChapterCount);
        Assert.Equal(["第1章", "第2章"], result.Boundaries.Select(boundary => boundary.Title).ToArray());
        Assert.All(result.Boundaries, boundary => Assert.True(boundary.ContentEnd > boundary.ContentStart));
    }

    [Fact]
    public async Task PreviewManualSplitRejectsTemplatesWithNoValidFullSourceBoundaries()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("无章节边界", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("no-boundaries.txt", "没有标题的正文。\n\n仍然没有章节。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "无边界来源", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(options, new RecordingChapterSplitAnalyzer(ReferenceChapterSplitModelResult.Empty));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.PreviewChapterSplitAsync(
                new PreviewReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, "第{number}章 {title}"),
                CancellationToken.None));

        Assert.Contains("boundaries", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAutoSplitPersistsValidatedHeadingOffsetsInsteadOfModelReportedOffsets()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("切分证据校验", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("invalid-evidence.md", "# 第一章\n\n雨声压住窗沿。\n\n# 第二章\n\n门外响起第三次敲门。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "证据无效来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(
            options,
            new RecordingChapterSplitAnalyzer(new ReferenceChapterSplitModelResult(
                "markdown_heading",
                "# {title}",
                0.9,
                [1])));

        var result = await service.AnalyzeChapterSplitAsync(
            new AnalyzeReferenceChapterSplitPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);

        Assert.Equal(2, result.ChapterCount);
        var evidenceOffsets = await ReadProfileEvidenceOffsetsAsync(options, result.SplitProfileId);
        Assert.Equal(new[] { 0, 16 }, evidenceOffsets);
    }

    [Fact]
    public async Task ConfirmChapterSplitMarksTheProfileStaleWhenTheSourceHashChanged()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("章节切分失效", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "stale-split.txt",
            "第1章 开端\n\n雨声压住窗沿。\n\n第2章 转折\n\n门外响起第三次敲门。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "失效切分来源", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(options, new RecordingChapterSplitAnalyzer(ReferenceChapterSplitModelResult.Empty));
        var preview = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, "第{number}章 {title}"),
            CancellationToken.None);
        await File.WriteAllTextAsync(sourcePath, "第1章 新开端\n\n来源已经变更。\n");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ConfirmChapterSplitAsync(
                new ConfirmReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, preview.SplitProfileId),
                CancellationToken.None));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReferenceChapterSplitProfileStates.Stale, await ReadProfileStatusAsync(options, preview.SplitProfileId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data"),
            EnableLegacyMigration = false
        };
    }

    private string CreateSourceFile(string fileName, string content)
    {
        var directory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string CreateSourceFileWithBom(string fileName, string content)
    {
        var directory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static async ValueTask<int> CountBoundariesAsync(AppInitializationOptions options, string splitProfileId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_chapter_split_boundaries WHERE split_profile_id = $split_profile_id;";
        command.Parameters.AddWithValue("$split_profile_id", splitProfileId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask<string> ReadProfileStatusAsync(AppInitializationOptions options, string splitProfileId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM reference_chapter_split_profiles WHERE split_profile_id = $split_profile_id;";
        command.Parameters.AddWithValue("$split_profile_id", splitProfileId);
        return (string)(await command.ExecuteScalarAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Split profile was not persisted."));
    }

    private static async ValueTask<int[]> ReadProfileEvidenceOffsetsAsync(AppInitializationOptions options, string splitProfileId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pattern_json FROM reference_chapter_split_profiles WHERE split_profile_id = $split_profile_id;";
        command.Parameters.AddWithValue("$split_profile_id", splitProfileId);
        var json = (string)(await command.ExecuteScalarAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Split profile was not persisted."));
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("evidence_offsets")
            .EnumerateArray()
            .Select(item => item.GetInt32())
            .ToArray();
    }

    private static async ValueTask<int> CountAnchorRowsAsync(AppInitializationOptions options, string tableName, long anchorId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE anchor_id = $anchor_id;";
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(AppInitializationOptions options)
    {
        var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private sealed class RecordingChapterSplitAnalyzer : IReferenceChapterSplitAnalyzer
    {
        private readonly ReferenceChapterSplitModelResult _result;

        public RecordingChapterSplitAnalyzer(ReferenceChapterSplitModelResult result)
        {
            _result = result;
        }

        public List<ReferenceChapterSplitModelRequest> Requests { get; } = [];

        public ValueTask<ReferenceChapterSplitModelResult> AnalyzeAsync(
            ReferenceChapterSplitModelRequest input,
            CancellationToken cancellationToken)
        {
            Requests.Add(input);
            return ValueTask.FromResult(_result);
        }
    }
}
