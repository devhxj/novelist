using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationChapterSplitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

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
    public async Task AnalyzeAutoSplitRejectsModelEvidenceThatDoesNotPointToAValidatedHeading()
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

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.AnalyzeChapterSplitAsync(
                new AnalyzeReferenceChapterSplitPayload(novel.Id, anchor.AnchorId),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.ChapterSplitOutputInvalid, exception.ErrorCode);
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
