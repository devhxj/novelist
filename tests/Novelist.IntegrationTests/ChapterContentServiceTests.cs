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

    private sealed record StaleNotification(long NovelId, string Reason);
}
