using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class NovelImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TxtImportCreatesNovelChaptersRagStaleStateAndSingleImportCommit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveGitAuthorSettingsAsync(
            new SaveGitAuthorSettingsPayload("Import Author", "import-author@example.com"),
            CancellationToken.None);
        var versionControl = new GitVersionControlService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var notifier = new RecordingRagIndexRefreshNotifier();
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: versionControl,
            ragRefreshNotifier: notifier);
        var source = WriteFixture(
            "雨城档案.txt",
            """
            第一章 雨夜
            林岚听见门外的脚步。

            第二章 旧账
            她翻出那封旧信。
            """);

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-txt-1",
                source,
                "雨城档案.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "雨城档案",
                CommitMessage: "import novel from txt"),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.Completed, run.State);
        Assert.Equal("done", run.Stage);
        Assert.NotNull(run.CreatedNovelId);
        Assert.Equal(["novels/1"], run.CreatedFileRoots);
        Assert.Empty(run.Warnings);

        var novel = Assert.Single(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.Equal("雨城档案", novel.Title);
        Assert.Contains("Imported from 雨城档案.txt", novel.Description, StringComparison.Ordinal);

        var chapters = new FileSystemChapterContentService(options, novelService, versionControl: new NoOpVersionControlService());
        var importedChapters = await chapters.GetChaptersAsync(novel.Id, CancellationToken.None);
        Assert.Equal(["第一章 雨夜", "第二章 旧账"], importedChapters.Select(chapter => chapter.Title));
        Assert.All(importedChapters, chapter => Assert.True(chapter.WordCount > 0));
        Assert.Equal("林岚听见门外的脚步。", await chapters.GetContentAsync(novel.Id, "chapters/001.md", CancellationToken.None));
        Assert.Equal("她翻出那封旧信。", await chapters.GetContentAsync(novel.Id, "chapters/002.md", CancellationToken.None));

        Assert.Equal(2, notifier.Notifications.Count);
        Assert.All(notifier.Notifications, item => Assert.Equal(novel.Id, item.NovelId));

        var log = await versionControl.GetLogAsync(novel.Id, null, 20, CancellationToken.None);
        Assert.Contains(log, commit => commit.Message == "import novel from txt");
        Assert.DoesNotContain(log, commit => commit.Message.StartsWith("create chapter ", StringComparison.Ordinal));
        Assert.DoesNotContain(log, commit => commit.Message.StartsWith("update chapters/", StringComparison.Ordinal));
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString());
        Assert.True(File.Exists(Path.Combine(workspace, "novelist.md")));
        Assert.Equal("Import Author", await ReadGitConfigAsync(workspace, "user.name"));
        Assert.Equal("import-author@example.com", await ReadGitConfigAsync(workspace, "user.email"));
    }

    [Fact]
    public async Task ImportEmitsProgressEventsWithCurrentChapterWithoutSourcePathExposure()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var events = new RecordingBridgeEventSink();
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: versionControl,
            eventSink: events);
        var source = WriteFixture(
            "进度事件.txt",
            """
            第一章 起雾
            第一段正文。

            第二章 回声
            第二段正文。
            """);

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-progress-1",
                source,
                "进度事件.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "进度事件",
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.Completed, run.State);
        var payloads = events.Events
            .Where(item => item.Name == "novel_import:progress")
            .Select(item => Assert.IsType<NovelImportProgressPayload>(item.Payload))
            .ToArray();

        Assert.NotEmpty(payloads);
        Assert.All(payloads, payload =>
        {
            Assert.Equal("import-progress-1", payload.TaskId);
            Assert.InRange(payload.ProgressCompleted, 0, payload.ProgressTotal);
            Assert.DoesNotContain(source, payload.Message, StringComparison.Ordinal);
        });
        Assert.Contains(payloads, payload => payload.State == NovelImportRunStates.Parsing && payload.Stage == "parse_source");
        Assert.Contains(payloads, payload => payload.State == NovelImportRunStates.CreatingNovel && payload.Stage == "create_novel");
        Assert.Contains(payloads, payload => payload.State == NovelImportRunStates.WritingFiles && payload.Stage == "write_chapters");
        Assert.Contains(payloads, payload =>
            payload.State == NovelImportRunStates.WritingFiles &&
            payload.Stage == "write_chapter" &&
            payload.CurrentChapterIndex == 1 &&
            payload.CurrentChapterTitle == "第一章 起雾");
        Assert.Contains(payloads, payload =>
            payload.State == NovelImportRunStates.WritingFiles &&
            payload.Stage == "write_chapter" &&
            payload.CurrentChapterIndex == 2 &&
            payload.CurrentChapterTitle == "第二章 回声");
        Assert.Contains(payloads, payload => payload.State == NovelImportRunStates.Indexing && payload.Stage == "indexing");
        Assert.Contains(payloads, payload => payload.State == NovelImportRunStates.GitCommit && payload.Stage == "git_commit");
        var final = payloads.Last();
        Assert.Equal(NovelImportRunStates.Completed, final.State);
        Assert.Equal("done", final.Stage);
        Assert.Equal(final.ProgressTotal, final.ProgressCompleted);
        Assert.Equal(run.CreatedNovelId, final.CreatedNovelId);
    }

    [Fact]
    public async Task EpubImportUsesMetadataTitleAndPersistsSkippedChapterDiagnostics()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: versionControl);
        var source = WriteBinaryFixture("锚定.epub", BuildEpub(new Dictionary<string, string>
        {
            ["META-INF/container.xml"] = Container("OEBPS/content.opf"),
            ["OEBPS/content.opf"] = Opf(
                "EPUB 元数据标题",
                [("empty", "empty.xhtml"), ("valid", "valid.xhtml")],
                ["empty", "valid"]),
            ["OEBPS/empty.xhtml"] = RawXhtml("<body><p> </p></body>"),
            ["OEBPS/valid.xhtml"] = RawXhtml("<body><h1>第一章 EPUB</h1><p>章节正文。</p></body>")
        }));

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-epub-1",
                source,
                "锚定.epub",
                NovelImportKinds.Epub,
                RequestedTitle: null,
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.Completed, run.State);
        Assert.NotNull(run.CreatedNovelId);
        Assert.Single(run.SkippedChapters);
        Assert.Equal("empty_content", run.SkippedChapters[0].Reason);

        var novel = Assert.Single(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.Equal("EPUB 元数据标题", novel.Title);

        var chapters = new FileSystemChapterContentService(options, novelService, versionControl: versionControl);
        var chapter = Assert.Single(await chapters.GetChaptersAsync(novel.Id, CancellationToken.None));
        Assert.Equal("第一章 EPUB", chapter.Title);
        Assert.Equal("第一章 EPUB\n章节正文。", await chapters.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
    }

    [Fact]
    public async Task MarkdownImportWithoutHeadersCreatesSingleChapterAndNoReferenceOrStyleSideEffects()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: versionControl);
        var source = WriteFixture("single.markdown", "没有章节标题。\n但这仍然是一篇完整文本。");

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-md-1",
                source,
                "single.markdown",
                NovelImportKinds.Markdown,
                RequestedTitle: "Markdown 导入",
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.Completed, run.State);
        var novel = Assert.Single(await novelService.GetNovelsAsync(CancellationToken.None));
        var chapters = new FileSystemChapterContentService(options, novelService, versionControl: versionControl);
        var chapter = Assert.Single(await chapters.GetChaptersAsync(novel.Id, CancellationToken.None));
        Assert.Equal("single", chapter.Title);
        Assert.Equal("没有章节标题。\n但这仍然是一篇完整文本。", await chapters.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));

        var styleSamples = new FileSystemStyleSampleService(options, novelService);
        var samples = await styleSamples.SearchSamplesAsync(
            new SearchStyleSamplesPayload(NovelId: novel.Id, IncludeGlobal: true, Query: "", Tags: [], Page: 1, Size: 20),
            CancellationToken.None);
        Assert.Empty(samples.Items);

        var referenceAnchors = new SqliteReferenceAnchorService(options, novelService);
        Assert.Empty(await referenceAnchors.GetAnchorsAsync(novel.Id, CancellationToken.None));

        var styleProfiles = new SqliteReferenceStyleProfileService(options, novelService);
        Assert.Empty(await styleProfiles.GetStyleProfilesAsync(
            new GetReferenceStyleProfilesPayload(novel.Id, IncludeArchived: true),
            CancellationToken.None));
    }

    [Fact]
    public async Task ParseFailureDoesNotCreateNovelOrWorkspace()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: versionControl);
        var source = WriteFixture("empty.txt", " \r\n\t ");

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-parse-fail-1",
                source,
                "empty.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "Empty",
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.Failed, run.State);
        Assert.Equal("import.text.empty", run.Error?.Code);
        Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1")));
    }

    [Fact]
    public async Task GitInitializationFailureDoesNotCreateNovelOrWorkspace()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(
            options,
            settings,
            new ThrowingEnsureRepositoryVersionControlService());
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: new NoOpVersionControlService());
        var source = WriteFixture("git-init-fail.txt", "第一章 初始化失败\n正文不应留下。");

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-git-init-fail-1",
                source,
                "git-init-fail.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "Git Init Failure",
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.Failed, run.State);
        Assert.Equal("import.failed", run.Error?.Code);
        Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1")));
    }

    [Fact]
    public async Task IndexRefreshFailureReturnsWarningWithoutDeletingImportedData()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: versionControl,
            ragRefreshNotifier: new ThrowingRagIndexRefreshNotifier());
        var source = WriteFixture("index-warning.txt", "第一章 索引\n正文保留。");

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-index-warning-1",
                source,
                "index-warning.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "Index Warning",
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.CompletedWithWarning, run.State);
        Assert.Contains(run.Warnings, warning => warning.Code == "index.refresh_failed");
        var novel = Assert.Single(await novelService.GetNovelsAsync(CancellationToken.None));
        var chapters = new FileSystemChapterContentService(options, novelService, versionControl: versionControl);
        var chapter = Assert.Single(await chapters.GetChaptersAsync(novel.Id, CancellationToken.None));
        Assert.Equal("正文保留。", await chapters.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
    }

    [Fact]
    public async Task ImportFailureAfterChapterWriteCleansCreatedNovelRowsFilesAndRunState()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var realChapters = new FileSystemChapterContentService(options, novelService, versionControl: versionControl);
        var failingChapters = new ThrowAfterFirstSaveChapterContentService(realChapters);
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            chapterContentService: failingChapters,
            versionControl: versionControl);
        var source = WriteFixture(
            "失败导入.txt",
            """
            第一章 会写入
            已经写入的正文。
            第二章 不应留下
            第二段。
            """);

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-fail-1",
                source,
                "失败导入.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "失败导入",
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.CleanupCompleted, run.State);
        Assert.NotNull(run.Error);
        Assert.Equal("import.write_failed", run.Error.Code);
        Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1")));

        var persisted = await importService.GetRunAsync(new GetNovelImportRunPayload("import-fail-1"), CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(NovelImportRunStates.CleanupCompleted, persisted.State);
    }

    [Fact]
    public async Task ImportFailureAfterChapterMetadataCreationCleansCreatedNovelRowsAndFiles()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var realChapters = new FileSystemChapterContentService(options, novelService, versionControl: versionControl);
        var failingChapters = new ThrowAfterFirstCreateChapterContentService(realChapters);
        var notifier = new RecordingRagIndexRefreshNotifier();
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            chapterContentService: failingChapters,
            versionControl: versionControl,
            ragRefreshNotifier: notifier);
        var source = WriteFixture("元数据失败.txt", "第一章 会创建元数据\n正文不应进入完成态。");

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-metadata-fail-1",
                source,
                "元数据失败.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "元数据失败",
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.CleanupCompleted, run.State);
        Assert.NotNull(run.Error);
        Assert.Equal("import.write_failed", run.Error.Code);
        Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1")));
        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task CancellationAfterChapterWriteCleansCreatedNovelRowsAndFiles()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var realChapters = new FileSystemChapterContentService(options, novelService, versionControl: versionControl);
        var cancellingChapters = new CancelAfterFirstSaveChapterContentService(realChapters);
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            chapterContentService: cancellingChapters,
            versionControl: versionControl);
        var source = WriteFixture("取消导入.txt", "第一章 会取消\n已写入后取消。");

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-cancel-1",
                source,
                "取消导入.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "取消导入",
                CommitMessage: null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.CleanupCompleted, run.State);
        Assert.Equal("import.cancelled", run.Error?.Code);
        Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1")));
    }

    [Fact]
    public async Task CancelRunCancelsActiveImportAndCleansCreatedNovelRowsAndFiles()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var realChapters = new FileSystemChapterContentService(options, novelService, versionControl: versionControl);
        var blockingChapters = new BlockingAfterFirstSaveChapterContentService(realChapters);
        var notifier = new RecordingRagIndexRefreshNotifier();
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            chapterContentService: blockingChapters,
            versionControl: versionControl,
            ragRefreshNotifier: notifier);
        var source = WriteFixture("主动取消.txt", "第一章 等待取消\n已写入后等待取消。");
        var importTask = importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-active-cancel-1",
                source,
                "主动取消.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "主动取消",
                CommitMessage: null),
            CancellationToken.None).AsTask();

        await blockingChapters.FirstSaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            var cancelled = await importService.CancelRunAsync(
                new CancelNovelImportPayload("import-active-cancel-1", "user requested cancellation"),
                CancellationToken.None);

            Assert.Equal(NovelImportRunStates.CleanupCompleted, cancelled.State);
            Assert.Equal("import.cancelled", cancelled.Error?.Code);
            Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1")));
            Assert.Empty(notifier.Notifications);

            var final = await importTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(cancelled.State, final.State);
        }
        finally
        {
            blockingChapters.Release();
            if (!importTask.IsCompleted)
            {
                try
                {
                    await importTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // The failing pre-fix path can leave the background import faulted after the test releases it.
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentImportsCreateIndependentNovelsAndRuns()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new NoOpVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: versionControl);
        var firstSource = WriteFixture("并发一.txt", "第一章 A\n第一本。");
        var secondSource = WriteFixture("并发二.txt", "第一章 B\n第二本。");

        var first = importService.StartRunAsync(
            new StartNovelImportPayload("import-concurrent-1", firstSource, "并发一.txt", NovelImportKinds.Txt, "并发一", null),
            CancellationToken.None).AsTask();
        var second = importService.StartRunAsync(
            new StartNovelImportPayload("import-concurrent-2", secondSource, "并发二.txt", NovelImportKinds.Txt, "并发二", null),
            CancellationToken.None).AsTask();

        var runs = await Task.WhenAll(first, second);

        Assert.All(runs, run => Assert.Equal(NovelImportRunStates.Completed, run.State));
        Assert.Equal(2, runs.Select(run => run.CreatedNovelId).Distinct().Count());
        var novels = await novelService.GetNovelsAsync(CancellationToken.None);
        Assert.Equal(["并发二", "并发一"], novels.Select(novel => novel.Title));
        var chapters = new FileSystemChapterContentService(options, novelService, versionControl: versionControl);
        foreach (var novel in novels)
        {
            var chapter = Assert.Single(await chapters.GetChaptersAsync(novel.Id, CancellationToken.None));
            Assert.StartsWith("第一章", chapter.Title, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GitCommitFailurePreservesImportedDataAndReturnsWarning()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var writingVersionControl = new NoOpVersionControlService();
        var finalVersionControl = new ThrowingCommitVersionControlService();
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings, writingVersionControl);
        var importService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: finalVersionControl);
        var source = WriteFixture("git-warning.txt", "第一章 保留\n正文仍应保留。");

        var run = await importService.StartRunAsync(
            new StartNovelImportPayload(
                "import-git-warning-1",
                source,
                "git-warning.txt",
                NovelImportKinds.Txt,
                RequestedTitle: "Git Warning",
                CommitMessage: "import with failing git"),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.CompletedWithWarning, run.State);
        Assert.Single(run.Warnings);
        Assert.Equal("git.commit_failed", run.Warnings[0].Code);

        var novel = Assert.Single(await novelService.GetNovelsAsync(CancellationToken.None));
        var chapters = new FileSystemChapterContentService(options, novelService, versionControl: writingVersionControl);
        var chapter = Assert.Single(await chapters.GetChaptersAsync(novel.Id, CancellationToken.None));
        Assert.Equal("正文仍应保留。", await chapters.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
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
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private string WriteFixture(string fileName, string content)
    {
        return WriteBinaryFixture(fileName, Encoding.UTF8.GetBytes(content));
    }

    private string WriteBinaryFixture(string fileName, byte[] bytes)
    {
        var directory = Path.Combine(_root, "fixtures");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
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
                stream.Write(Encoding.UTF8.GetBytes(entry.Value));
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

    private static string RawXhtml(string body)
    {
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              {{body}}
            </html>
            """;
    }

    private static async Task<string> ReadGitConfigAsync(string workspace, string key)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add(key);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Trim();
    }

    private sealed class RecordingRagIndexRefreshNotifier : IRagIndexRefreshNotifier
    {
        public List<StaleNotification> Notifications { get; } = [];

        public ValueTask MarkNovelIndexStaleAsync(long novelId, string reason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Notifications.Add(new StaleNotification(novelId, reason));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingBridgeEventSink : IBridgeEventSink
    {
        public List<BridgeEvent> Events { get; } = [];

        public ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(new BridgeEvent(name, payload));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record BridgeEvent(string Name, object? Payload);

    private sealed class ThrowingRagIndexRefreshNotifier : IRagIndexRefreshNotifier
    {
        public ValueTask MarkNovelIndexStaleAsync(long novelId, string reason, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("simulated index refresh failure");
        }
    }

    private class NoOpVersionControlService : IVersionControlService
    {
        public virtual ValueTask EnsureRepositoryAsync(long novelId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public virtual ValueTask<VersionControlCommitResult> CommitIfChangedAsync(
            long novelId,
            string message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new VersionControlCommitResult(false, string.Empty));
        }

        public ValueTask<IReadOnlyList<VersionControlCommitInfo>> GetLogAsync(
            long novelId,
            string? relativePath,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<VersionControlCommitInfo>>([]);
        }

        public ValueTask<PageResultPayload<GitCommitSummaryPayload>> GetCommitSummariesAsync(
            GetGitCommitsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = input.Page <= 0 ? 1 : input.Page;
            var size = input.Size <= 0 ? 20 : input.Size;
            return ValueTask.FromResult(new PageResultPayload<GitCommitSummaryPayload>([], 0, page, size, 0));
        }

        public ValueTask<IReadOnlyList<GitCommitFilePayload>> GetCommitFilesAsync(
            GetGitCommitFilesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<GitCommitFilePayload>>([]);
        }

        public ValueTask<GitFileDiffPayload> GetFileDiffAsync(
            GetGitFileDiffPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new VersionControlException("No-op version control does not expose Git diffs.");
        }
    }

    private sealed class ThrowingCommitVersionControlService : NoOpVersionControlService
    {
        public override ValueTask<VersionControlCommitResult> CommitIfChangedAsync(
            long novelId,
            string message,
            CancellationToken cancellationToken)
        {
            throw new VersionControlException("simulated git failure");
        }
    }

    private sealed class ThrowingEnsureRepositoryVersionControlService : NoOpVersionControlService
    {
        public override ValueTask EnsureRepositoryAsync(long novelId, CancellationToken cancellationToken)
        {
            throw new VersionControlException("simulated git init failure");
        }
    }

    private sealed class ThrowAfterFirstSaveChapterContentService : IChapterContentService
    {
        private readonly IChapterContentService _inner;
        private int _saveCount;

        public ThrowAfterFirstSaveChapterContentService(IChapterContentService inner)
        {
            _inner = inner;
        }

        public ValueTask<IReadOnlyList<ChapterPayload>> GetChaptersAsync(long novelId, CancellationToken cancellationToken) =>
            _inner.GetChaptersAsync(novelId, cancellationToken);

        public ValueTask<int> GetMaxChapterNumberAsync(long novelId, CancellationToken cancellationToken) =>
            _inner.GetMaxChapterNumberAsync(novelId, cancellationToken);

        public ValueTask<ChapterPayload> CreateChapterAsync(CreateChapterPayload input, CancellationToken cancellationToken) =>
            _inner.CreateChapterAsync(input, cancellationToken);

        public ValueTask UpdateChapterTitleAsync(long novelId, int chapterNumber, string title, CancellationToken cancellationToken) =>
            _inner.UpdateChapterTitleAsync(novelId, chapterNumber, title, cancellationToken);

        public ValueTask<string> GetContentAsync(long novelId, string path, CancellationToken cancellationToken) =>
            _inner.GetContentAsync(novelId, path, cancellationToken);

        public async ValueTask SaveContentAsync(SaveContentPayload input, CancellationToken cancellationToken)
        {
            await _inner.SaveContentAsync(input, cancellationToken);
            _saveCount++;
            if (_saveCount == 1)
            {
                throw new IOException("simulated write failure after partial content persistence");
            }
        }
    }

    private sealed class ThrowAfterFirstCreateChapterContentService : IChapterContentService
    {
        private readonly IChapterContentService _inner;
        private int _createCount;

        public ThrowAfterFirstCreateChapterContentService(IChapterContentService inner)
        {
            _inner = inner;
        }

        public ValueTask<IReadOnlyList<ChapterPayload>> GetChaptersAsync(long novelId, CancellationToken cancellationToken) =>
            _inner.GetChaptersAsync(novelId, cancellationToken);

        public ValueTask<int> GetMaxChapterNumberAsync(long novelId, CancellationToken cancellationToken) =>
            _inner.GetMaxChapterNumberAsync(novelId, cancellationToken);

        public async ValueTask<ChapterPayload> CreateChapterAsync(CreateChapterPayload input, CancellationToken cancellationToken)
        {
            var chapter = await _inner.CreateChapterAsync(input, cancellationToken);
            _createCount++;
            if (_createCount == 1)
            {
                throw new IOException("simulated metadata failure after chapter row creation");
            }

            return chapter;
        }

        public ValueTask UpdateChapterTitleAsync(long novelId, int chapterNumber, string title, CancellationToken cancellationToken) =>
            _inner.UpdateChapterTitleAsync(novelId, chapterNumber, title, cancellationToken);

        public ValueTask<string> GetContentAsync(long novelId, string path, CancellationToken cancellationToken) =>
            _inner.GetContentAsync(novelId, path, cancellationToken);

        public ValueTask SaveContentAsync(SaveContentPayload input, CancellationToken cancellationToken) =>
            _inner.SaveContentAsync(input, cancellationToken);
    }

    private sealed class CancelAfterFirstSaveChapterContentService : IChapterContentService
    {
        private readonly IChapterContentService _inner;
        private int _saveCount;

        public CancelAfterFirstSaveChapterContentService(IChapterContentService inner)
        {
            _inner = inner;
        }

        public ValueTask<IReadOnlyList<ChapterPayload>> GetChaptersAsync(long novelId, CancellationToken cancellationToken) =>
            _inner.GetChaptersAsync(novelId, cancellationToken);

        public ValueTask<int> GetMaxChapterNumberAsync(long novelId, CancellationToken cancellationToken) =>
            _inner.GetMaxChapterNumberAsync(novelId, cancellationToken);

        public ValueTask<ChapterPayload> CreateChapterAsync(CreateChapterPayload input, CancellationToken cancellationToken) =>
            _inner.CreateChapterAsync(input, cancellationToken);

        public ValueTask UpdateChapterTitleAsync(long novelId, int chapterNumber, string title, CancellationToken cancellationToken) =>
            _inner.UpdateChapterTitleAsync(novelId, chapterNumber, title, cancellationToken);

        public ValueTask<string> GetContentAsync(long novelId, string path, CancellationToken cancellationToken) =>
            _inner.GetContentAsync(novelId, path, cancellationToken);

        public async ValueTask SaveContentAsync(SaveContentPayload input, CancellationToken cancellationToken)
        {
            await _inner.SaveContentAsync(input, cancellationToken);
            _saveCount++;
            if (_saveCount == 1)
            {
                throw new OperationCanceledException("simulated import cancellation");
            }
        }
    }

    private sealed class BlockingAfterFirstSaveChapterContentService : IChapterContentService
    {
        private readonly IChapterContentService _inner;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingAfterFirstSaveChapterContentService(IChapterContentService inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource FirstSaveCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<ChapterPayload>> GetChaptersAsync(long novelId, CancellationToken cancellationToken) =>
            _inner.GetChaptersAsync(novelId, cancellationToken);

        public ValueTask<int> GetMaxChapterNumberAsync(long novelId, CancellationToken cancellationToken) =>
            _inner.GetMaxChapterNumberAsync(novelId, cancellationToken);

        public ValueTask<ChapterPayload> CreateChapterAsync(CreateChapterPayload input, CancellationToken cancellationToken) =>
            _inner.CreateChapterAsync(input, cancellationToken);

        public ValueTask UpdateChapterTitleAsync(long novelId, int chapterNumber, string title, CancellationToken cancellationToken) =>
            _inner.UpdateChapterTitleAsync(novelId, chapterNumber, title, cancellationToken);

        public ValueTask<string> GetContentAsync(long novelId, string path, CancellationToken cancellationToken) =>
            _inner.GetContentAsync(novelId, path, cancellationToken);

        public async ValueTask SaveContentAsync(SaveContentPayload input, CancellationToken cancellationToken)
        {
            await _inner.SaveContentAsync(input, cancellationToken);
            FirstSaveCompleted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed record StaleNotification(long NovelId, string Reason);
}
