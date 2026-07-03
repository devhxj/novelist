using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class NovelServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NovelCrudPersistsAcrossServiceRecreation()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var service = new FileSystemNovelService(options, settings);

        var created = await service.CreateNovelAsync(
            new CreateNovelPayload("  长夜档案  ", "围绕一座旧城失踪案展开。", "悬疑"),
            CancellationToken.None);

        Assert.Equal(1, created.Id);
        Assert.Equal("长夜档案", created.Title);
        Assert.Equal("悬疑", created.Genre);
        Assert.True(File.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1", "goink.md")));

        var updated = await service.UpdateNovelAsync(
            created.Id,
            new UpdateNovelPayload(Title: "", Description: "", Genre: "推理"),
            CancellationToken.None);

        Assert.Equal("长夜档案", updated.Title);
        Assert.Equal("围绕一座旧城失踪案展开。", updated.Description);
        Assert.Equal("推理", updated.Genre);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);

        var reloaded = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novels = await reloaded.GetNovelsAsync(CancellationToken.None);

        var novel = Assert.Single(novels);
        Assert.Equal(created.Id, novel.Id);
        Assert.Equal("长夜档案", novel.Title);
        Assert.Equal("推理", novel.Genre);

        await reloaded.DeleteNovelAsync(created.Id, CancellationToken.None);

        Assert.Empty(await reloaded.GetNovelsAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(options.DefaultDataDirectory, "novels", "1")));
    }

    [Fact]
    public async Task NovelIdAllocationDoesNotReuseDeletedIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));

        var first = await service.CreateNovelAsync(
            new CreateNovelPayload("第一本", "", ""),
            CancellationToken.None);
        await service.DeleteNovelAsync(first.Id, CancellationToken.None);
        var second = await service.CreateNovelAsync(
            new CreateNovelPayload("第二本", "", ""),
            CancellationToken.None);

        Assert.Equal(first.Id + 1, second.Id);
    }

    [Fact]
    public async Task DeleteActiveNovelClearsLastNovelWithoutErasingOtherSettings()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var service = new FileSystemNovelService(options, settings);
        var novel = await service.CreateNovelAsync(
            new CreateNovelPayload("群星边境", "远航舰队遭遇未知信号。", "科幻"),
            CancellationToken.None);

        await settings.SetSelectedModelAsync("qwen/qwen-plus", "high", CancellationToken.None);
        await settings.SetApprovalModeAsync("auto", CancellationToken.None);
        await service.SetActiveNovelAsync(novel.Id, CancellationToken.None);
        await service.DeleteNovelAsync(novel.Id, CancellationToken.None);

        var reloadedSettings = await new FileSystemAppSettingsService(options).GetSettingsAsync(CancellationToken.None);
        Assert.Equal(0, reloadedSettings.LastNovelId);
        Assert.Equal("qwen/qwen-plus", reloadedSettings.SelectedModelKey);
        Assert.Equal("high", reloadedSettings.ReasoningEffort);
        Assert.Equal("auto", reloadedSettings.ApprovalMode);
    }

    [Fact]
    public async Task CoverSaveReadAndDeleteUseValidatedWorkspaceFile()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await service.CreateNovelAsync(
            new CreateNovelPayload("封面测试", "", ""),
            CancellationToken.None);
        var data = JpegCoverBytes();

        await service.SaveCoverAsync(novel.Id, data, CancellationToken.None);

        var cover = await service.GetCoverAsync(novel.Id, CancellationToken.None);
        Assert.NotNull(cover);
        Assert.Equal("image/jpeg", cover.ContentType);
        Assert.Equal(data.Length, cover.Length);
        Assert.Equal(Path.Combine(options.DefaultDataDirectory, "novels", "1", "cover.jpg"), cover.LocalPath);
        Assert.Equal(data, await File.ReadAllBytesAsync(cover.LocalPath));

        await service.DeleteCoverAsync(novel.Id, CancellationToken.None);
        Assert.Null(await service.GetCoverAsync(novel.Id, CancellationToken.None));

        await service.DeleteCoverAsync(novel.Id, CancellationToken.None);
    }

    [Fact]
    public async Task CreateNovelInitializesGitRepositoryWithInitialCommit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new GitVersionControlService(options);
        var service = new FileSystemNovelService(
            options,
            new FileSystemAppSettingsService(options),
            versionControl);

        var novel = await service.CreateNovelAsync(
            new CreateNovelPayload("版本库", "", ""),
            CancellationToken.None);

        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Directory.Exists(Path.Combine(workspace, ".git")) || File.Exists(Path.Combine(workspace, ".git")));
        Assert.True(File.Exists(Path.Combine(workspace, "goink.md")));
        var log = await versionControl.GetLogAsync(novel.Id, null, 10, CancellationToken.None);
        Assert.Contains(log, commit => commit.Message == "initial commit");
    }

    [Fact]
    public async Task SaveAndDeleteCoverCreateGitCommits()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var versionControl = new GitVersionControlService(options);
        var service = new FileSystemNovelService(
            options,
            new FileSystemAppSettingsService(options),
            versionControl);
        var novel = await service.CreateNovelAsync(
            new CreateNovelPayload("封面提交", "", ""),
            CancellationToken.None);

        await service.SaveCoverAsync(novel.Id, JpegCoverBytes(), CancellationToken.None);
        await service.DeleteCoverAsync(novel.Id, CancellationToken.None);

        var log = await versionControl.GetLogAsync(novel.Id, null, 10, CancellationToken.None);
        Assert.Contains(log, commit => commit.Message == "update cover");
        Assert.Contains(log, commit => commit.Message == "remove cover");
    }

    [Fact]
    public async Task SaveCoverRejectsUnsupportedImagePayloads()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await service.CreateNovelAsync(
            new CreateNovelPayload("坏封面", "", ""),
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.SaveCoverAsync(novel.Id, [0x00, 0x01, 0x02], CancellationToken.None));
    }

    [Fact]
    public async Task BridgeNovelHandlersReturnValidationErrorForInvalidPayload()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNovelHandlers(service);

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_title",
              "method": "CreateNovel",
              "payload": { "args": [{ "title": "   " }] }
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_bad_title", BridgeErrorCodes.ValidationError);
    }

    [Fact]
    public async Task BridgeNovelHandlersReturnStableErrorWhenAppIsNotInitialized()
    {
        var options = CreateOptions();
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNovelHandlers(new FileSystemNovelService(options, new FileSystemAppSettingsService(options)));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_novels",
              "method": "GetNovels",
              "payload": { "args": [] }
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_novels", BridgeErrorCodes.AppNotInitialized);
    }

    [Fact]
    public async Task BridgeNovelHandlersCreateListAndSetActiveNovel()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var service = new FileSystemNovelService(options, settings);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNovelHandlers(service);

        using var createJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_create",
              "method": "CreateNovel",
              "payload": { "args": [{ "title": "长夜档案", "description": "旧城失踪案", "genre": "悬疑" }] }
            }
            """));
        var createdId = createJson.RootElement.GetProperty("result").GetProperty("id").GetInt64();

        using var listJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_list",
              "method": "GetNovels",
              "payload": { "args": [] }
            }
            """));
        var list = listJson.RootElement.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(createdId, list[0].GetProperty("id").GetInt64());
        Assert.Equal("长夜档案", list[0].GetProperty("title").GetString());

        using var setActiveJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_set_active",
              "method": "SetActiveNovel",
              "payload": { "args": [{ "novel_id": {{createdId}} }] }
            }
            """));
        Assert.True(setActiveJson.RootElement.GetProperty("ok").GetBoolean());

        var savedSettings = await settings.GetSettingsAsync(CancellationToken.None);
        Assert.Equal(createdId, savedSettings.LastNovelId);
    }

    [Fact]
    public async Task BridgeNovelHandlersSaveAndDeleteCover()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var service = new FileSystemNovelService(options, settings);
        var novel = await service.CreateNovelAsync(
            new CreateNovelPayload("桥接封面", "", ""),
            CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNovelHandlers(service);
        var byteArrayJson = string.Join(",", JpegCoverBytes().Select(item => item.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        using var saveJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_save_cover",
              "method": "SaveCover",
              "payload": { "args": [{{novel.Id}}, [{{byteArrayJson}}]] }
            }
            """));
        Assert.True(saveJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.NotNull(await service.GetCoverAsync(novel.Id, CancellationToken.None));

        using var deleteJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_delete_cover",
              "method": "DeleteCover",
              "payload": { "args": [{{novel.Id}}] }
            }
            """));
        Assert.True(deleteJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.Null(await service.GetCoverAsync(novel.Id, CancellationToken.None));
    }

    [Fact]
    public async Task BridgeNovelHandlersRejectInvalidCoverBytesBeforeServiceCall()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNovelHandlers(service);

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_cover",
              "method": "SaveCover",
              "payload": { "args": [1, [256]] }
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_bad_cover", BridgeErrorCodes.ValidationError);
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

    private static byte[] JpegCoverBytes()
    {
        return [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];
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
}
