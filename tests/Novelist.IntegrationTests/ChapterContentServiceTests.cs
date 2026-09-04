using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ChapterContentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ChapterLifecyclePersistsMetadataContentAndWordCounts()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(
            new CreateNovelPayload("长夜档案", "", "悬疑"),
            CancellationToken.None);
        var service = new FileSystemChapterContentService(options, novelService);

        Assert.Equal(0, await service.GetMaxChapterNumberAsync(novel.Id, CancellationToken.None));

        var first = await service.CreateChapterAsync(
            new CreateChapterPayload(novel.Id, "  雾中来信  "),
            CancellationToken.None);

        Assert.Equal(1, first.Id);
        Assert.Equal(novel.Id, first.NovelId);
        Assert.Equal(1, first.ChapterNumber);
        Assert.Equal("雾中来信", first.Title);
        Assert.Equal("chapters/001.md", first.FilePath);
        Assert.True(File.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1", "chapters", "001.md")));

        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, first.FilePath, "你好 world"),
            CancellationToken.None);
        Assert.Equal("你好 world", await service.GetContentAsync(novel.Id, first.FilePath, CancellationToken.None));

        await service.UpdateChapterTitleAsync(novel.Id, first.ChapterNumber, "旧城暗号", CancellationToken.None);

        var reloaded = new FileSystemChapterContentService(options, novelService);
        var chapters = await reloaded.GetChaptersAsync(novel.Id, CancellationToken.None);
        var chapter = Assert.Single(chapters);
        Assert.Equal("旧城暗号", chapter.Title);
        Assert.Equal(3, chapter.WordCount);
        Assert.Equal(1, await reloaded.GetMaxChapterNumberAsync(novel.Id, CancellationToken.None));

        var second = await reloaded.CreateChapterAsync(
            new CreateChapterPayload(novel.Id, "第二章"),
            CancellationToken.None);
        Assert.Equal(2, second.ChapterNumber);
        Assert.Equal("chapters/002.md", second.FilePath);
    }

    [Fact]
    public async Task SaveContentMarksRagIndexStaleAfterChapterMetadataIsPersisted()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var notifier = new RecordingRagIndexRefreshNotifier();
        var service = new FileSystemChapterContentService(options, novelService, ragRefreshNotifier: notifier);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);

        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "林岚发现暗号"),
            CancellationToken.None);

        var notification = Assert.Single(notifier.Notifications);
        Assert.Equal(novel.Id, notification.NovelId);
        Assert.Contains("chapters/001.md", notification.Reason, StringComparison.Ordinal);

        var chapters = await service.GetChaptersAsync(novel.Id, CancellationToken.None);
        Assert.Equal(6, Assert.Single(chapters).WordCount);
    }

    [Fact]
    public async Task SaveContentCreatesGitCommitForRepositoryFiles()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new GitVersionControlService(options);
        var novelService = new FileSystemNovelService(
            options,
            new FileSystemAppSettingsService(options),
            versionControl);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("版本章节", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(
            options,
            novelService,
            versionControl: versionControl);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);

        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "可追溯正文"),
            CancellationToken.None);

        var log = await versionControl.GetLogAsync(novel.Id, null, 10, CancellationToken.None);
        Assert.Contains(log, commit => commit.Message == "create chapter 001");
        Assert.Contains(log, commit => commit.Message == "update chapters/001.md");
    }

    [Fact]
    public async Task DeleteChapterSoftDeletesWithoutRenumberingOrReusingNumbers()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("删除留痕", "", ""), CancellationToken.None);
        var notifier = new RecordingRagIndexRefreshNotifier();
        var versionControl = new GitVersionControlService(options);
        var service = new FileSystemChapterContentService(
            options,
            novelService,
            ragRefreshNotifier: notifier,
            versionControl: versionControl);

        var first = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        var second = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "旧城暗号"), CancellationToken.None);
        await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "未读的名字"), CancellationToken.None);

        await service.DeleteChapterAsync(new DeleteChapterPayload(novel.Id, second.Id), CancellationToken.None);

        // 列表隐藏被删章节，剩余章号保持原样（不重排）。
        var chapters = await service.GetChaptersAsync(novel.Id, CancellationToken.None);
        Assert.Equal(2, chapters.Count);
        Assert.Equal(1, chapters[0].ChapterNumber);
        Assert.Equal(3, chapters[1].ChapterNumber);
        Assert.Equal(first.Id, chapters[0].Id);

        // 正文文件与大纲伴生文件一并移除。
        Assert.False(File.Exists(Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "chapters", "002.md")));
        Assert.False(File.Exists(Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "outlines", "002.md")));

        // 版本历史留有删除提交，正文可追溯。
        var log = await versionControl.GetLogAsync(novel.Id, null, 10, CancellationToken.None);
        Assert.Contains(log, commit => commit.Message == "delete chapter 002");

        // 索引清理：正文与大纲两条 stale 标记。
        Assert.Contains(notifier.Notifications, notification => notification.NovelId == novel.Id && notification.Reason.Contains("chapters/002.md", StringComparison.Ordinal));
        Assert.Contains(notifier.Notifications, notification => notification.Reason.Contains("outlines/002.md", StringComparison.Ordinal));

        // 新章节不复用被删章号，也永不重排。
        var fresh = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "新增章节"), CancellationToken.None);
        Assert.Equal(4, fresh.ChapterNumber);

        // 重复删除同一章必须报错。
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DeleteChapterAsync(new DeleteChapterPayload(novel.Id, second.Id), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SaveContentToDeletedChapterIsRejectedAndDoesNotResurrectTheFile()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("复活守卫", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(options, novelService);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "被删的一章"), CancellationToken.None);
        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "即将消失的正文"),
            CancellationToken.None);

        await service.DeleteChapterAsync(new DeleteChapterPayload(novel.Id, chapter.Id), CancellationToken.None);

        // 对已删除章号的保存必须被拒绝（O15）——文件不得以孤儿形态复活。
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveContentAsync(
                new SaveContentPayload(novel.Id, chapter.FilePath, "编辑器残留 tab 的自动保存"),
                CancellationToken.None).AsTask());
        Assert.False(File.Exists(Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "chapters", "001.md")));
        Assert.Empty(await service.GetChaptersAsync(novel.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SaveContentToDeletedChapterOutlineIsRejectedToo()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("大纲守卫", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(options, novelService);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "带大纲的一章"), CancellationToken.None);
        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, "outlines/001.md", "这一章的三幕结构"),
            CancellationToken.None);

        await service.DeleteChapterAsync(new DeleteChapterPayload(novel.Id, chapter.Id), CancellationToken.None);

        // R3：大纲伴生文件与正文同守卫——Agent 编辑或残留 tab 不得复活已删章节的大纲。
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveContentAsync(
                new SaveContentPayload(novel.Id, "outlines/001.md", "迟到的大纲改动"),
                CancellationToken.None).AsTask());
        Assert.False(File.Exists(Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "outlines", "001.md")));
    }

    [Fact]
    public async Task DeleteChapterToleratesWritingDeltaRecorderFailure()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("统计容错", "", ""), CancellationToken.None);
        var notifier = new RecordingRagIndexRefreshNotifier();
        var service = new FileSystemChapterContentService(
            options,
            novelService,
            writingDeltaRecorder: new ThrowingWritingDeltaRecorder(),
            ragRefreshNotifier: notifier);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "长章"), CancellationToken.None);
        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "正文内容若干字"),
            CancellationToken.None);

        // R4：统计扣减失败不得中断删除——元数据已持久化，中断会丢掉文件清理/stale/提交。
        await service.DeleteChapterAsync(new DeleteChapterPayload(novel.Id, chapter.Id), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "chapters", "001.md")));
        Assert.Contains(notifier.Notifications, notification => notification.Reason.Contains("chapters/001.md", StringComparison.Ordinal));
        Assert.Empty(await service.GetChaptersAsync(novel.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteChapterRecordsNegativeWordDeltaInWritingStatistics()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("统计扣减", "", ""), CancellationToken.None);
        var recorder = new RecordingWritingDeltaRecorder();
        var service = new FileSystemChapterContentService(options, novelService, writingDeltaRecorder: recorder);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "长章"), CancellationToken.None);
        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "六个字的正文内容"),
            CancellationToken.None);

        await service.DeleteChapterAsync(new DeleteChapterPayload(novel.Id, chapter.Id), CancellationToken.None);

        // 删除时按章节字数扣减（N6），写作速度数据不再虚高。
        var deleteDelta = Assert.Single(recorder.Deltas, delta => delta.WordDelta < 0);
        Assert.Equal(chapter.Id, deleteDelta.ChapterId);
        Assert.Equal(-8, deleteDelta.WordDelta);
    }

    [Fact]
    public async Task GetMaxChapterNumberIncludesDeletedHighWaterMark()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("高水位", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(options, novelService);
        var first = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "一"), CancellationToken.None);
        var second = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "二"), CancellationToken.None);

        await service.DeleteChapterAsync(new DeleteChapterPayload(novel.Id, second.Id), CancellationToken.None);

        // 尾章删除后历史最高章号仍是 2（O19）：时间线/卷轴的 max+1 推导与分配器一致。
        Assert.Equal(2, await service.GetMaxChapterNumberAsync(novel.Id, CancellationToken.None));
        var third = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "三"), CancellationToken.None);
        Assert.Equal(3, third.ChapterNumber);
        Assert.Equal(3, await service.GetMaxChapterNumberAsync(novel.Id, CancellationToken.None));
        // xUnit2000：期望值在前，失败信息里的 expected/actual 不再反向。
        Assert.Equal(1, first.ChapterNumber);
    }

    [Fact]
    public async Task SaveContentDoesNotFailWhenRagStaleNotificationFails()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(
            options,
            novelService,
            ragRefreshNotifier: new ThrowingRagIndexRefreshNotifier());
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);

        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "保存后仍应成功"),
            CancellationToken.None);

        Assert.Equal(
            "保存后仍应成功",
            await service.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
    }

    [Fact]
    public async Task SaveContentWithStaleBaselineHashRejectsAndKeepsDiskContent()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("并发保存", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(options, novelService);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);

        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "作者读到的版本"),
            CancellationToken.None);
        var baseline = ChapterContentBaselineHash.Compute(
            await service.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));

        // 绕过事件流的外部写入（第二窗口/外部编辑器/Agent 直写）：直接改盘上文件。
        var fullPath = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "chapters", "001.md");
        await File.WriteAllTextAsync(fullPath, "外部编辑器的新版本", CancellationToken.None);

        var error = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await service.SaveContentAsync(
                new SaveContentPayload(novel.Id, chapter.FilePath, "作者刚写的版本", baseline),
                CancellationToken.None));

        // U1：基线不匹配 → CONTENT_CONFLICT，且磁盘上的外部版本不被覆盖。
        Assert.Equal(BridgeErrorCodes.ContentConflict, error.Code);
        Assert.Equal(
            "外部编辑器的新版本",
            await service.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
    }

    [Fact]
    public async Task SaveContentWithFreshBaselineHashSucceeds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("并发保存", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(options, novelService);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);

        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "v1"),
            CancellationToken.None);
        var baseline = ChapterContentBaselineHash.Compute(
            await service.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));

        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "v2", baseline),
            CancellationToken.None);

        Assert.Equal("v2", await service.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
    }

    [Fact]
    public async Task BridgeSaveContentSurfacesBaselineConflictErrorCode()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("桥接冲突", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(options, novelService);
        var dispatcher = new BridgeDispatcher()
            .RegisterChapterContentHandlers(service);
        var chapter = await service.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        await service.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "v1"),
            CancellationToken.None);

        using var conflictJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_conflict_save",
              "method": "SaveContent",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "path": "chapters/001.md", "content": "v2", "baseline_hash": "fnv1a:00000000:2" }] }
            }
            """));

        Assert.False(conflictJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            BridgeErrorCodes.ContentConflict,
            conflictJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ContentAccessReturnsEmptyForMissingFilesAndRejectsUnsafePaths()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("群星边境", "", ""), CancellationToken.None);
        var service = new FileSystemChapterContentService(options, novelService);

        Assert.Equal("", await service.GetContentAsync(novel.Id, "outlines/001.md", CancellationToken.None));

        await Assert.ThrowsAsync<InvalidContentPathException>(async () =>
            await service.SaveContentAsync(
                new SaveContentPayload(novel.Id, "../outside.md", "bad"),
                CancellationToken.None));
    }

    [Fact]
    public async Task BridgeChapterHandlersCreateSaveReadAndList()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var chapterService = new FileSystemChapterContentService(options, novelService);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterChapterContentHandlers(chapterService);

        using var createJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_create_chapter",
              "method": "CreateChapter",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "title": "雾中来信" }] }
            }
            """));
        var filePath = createJson.RootElement.GetProperty("result").GetProperty("file_path").GetString();
        Assert.Equal("chapters/001.md", filePath);

        using var saveJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_save_content",
              "method": "SaveContent",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "path": "chapters/001.md", "content": "你好 world" }] }
            }
            """));
        Assert.True(saveJson.RootElement.GetProperty("ok").GetBoolean());

        using var contentJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_content",
              "method": "GetContent",
              "payload": { "args": [{{novel.Id}}, "chapters/001.md"] }
            }
            """));
        Assert.Equal("你好 world", contentJson.RootElement.GetProperty("result").GetString());

        using var listJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_chapters",
              "method": "GetChapters",
              "payload": { "args": [{{novel.Id}}] }
            }
            """));
        var chapters = listJson.RootElement.GetProperty("result");
        Assert.Equal(1, chapters.GetArrayLength());
        Assert.Equal(3, chapters[0].GetProperty("word_count").GetInt32());
    }

    [Fact]
    public async Task BridgeChapterHandlersReturnStableErrors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterChapterContentHandlers(new FileSystemChapterContentService(options, novelService));

        using var invalidTitle = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bad_title",
              "method": "CreateChapter",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "title": "   " }] }
            }
            """));
        AssertBridgeError(invalidTitle.RootElement, "req_bad_title", BridgeErrorCodes.ValidationError);

        using var invalidPath = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bad_path",
              "method": "SaveContent",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "path": "../outside.md", "content": "bad" }] }
            }
            """));
        AssertBridgeError(invalidPath.RootElement, "req_bad_path", BridgeErrorCodes.InvalidPath);
    }

    [Fact]
    public async Task BridgeChapterHandlersReturnStableErrorWhenAppIsNotInitialized()
    {
        var options = CreateOptions();
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterChapterContentHandlers(new FileSystemChapterContentService(
                options,
                new FileSystemNovelService(options, new FileSystemAppSettingsService(options))));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_chapters",
              "method": "GetChapters",
              "payload": { "args": [1] }
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_chapters", BridgeErrorCodes.AppNotInitialized);
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

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static void AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class RecordingRagIndexRefreshNotifier : IRagIndexRefreshNotifier
    {
        public List<StaleNotification> Notifications { get; } = [];

        public ValueTask MarkNovelIndexStaleAsync(
            long novelId,
            string reason,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Notifications.Add(new StaleNotification(novelId, reason));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingRagIndexRefreshNotifier : IRagIndexRefreshNotifier
    {
        public ValueTask MarkNovelIndexStaleAsync(
            long novelId,
            string reason,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("stale notification failed");
        }
    }

    private sealed class RecordingWritingDeltaRecorder : IWritingDeltaRecorder
    {
        public List<(long ChapterId, int WordDelta)> Deltas { get; } = [];

        public ValueTask RecordWordDeltaAsync(
            long novelId,
            long chapterId,
            int wordDelta,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deltas.Add((chapterId, wordDelta));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingWritingDeltaRecorder : IWritingDeltaRecorder
    {
        public ValueTask RecordWordDeltaAsync(
            long novelId,
            long chapterId,
            int wordDelta,
            CancellationToken cancellationToken)
        {
            // 只让"删除扣减"这条路径失败：保存时的正向记录是另一条语义（保存失败应如实上抛）。
            if (wordDelta < 0)
            {
                throw new InvalidOperationException("writing statistics store is corrupt");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record StaleNotification(long NovelId, string Reason);
}
