using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class PreferenceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreferenceCrudPersistsAndSplitsGlobalFromNovelPreferences()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var firstNovel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var secondNovel = await novelService.CreateNovelAsync(new CreateNovelPayload("群星边境", "", ""), CancellationToken.None);
        var service = new FileSystemPreferenceService(options, novelService);

        var global = await service.CreatePreferenceAsync(
            firstNovel.Id,
            new CreatePreferencePayload(IsGlobal: true, Category: "风格", Content: "保持冷峻克制。"),
            CancellationToken.None);
        var firstOnly = await service.CreatePreferenceAsync(
            firstNovel.Id,
            new CreatePreferencePayload(IsGlobal: false, Category: "角色", Content: "主角不主动解释。"),
            CancellationToken.None);
        await service.CreatePreferenceAsync(
            secondNovel.Id,
            new CreatePreferencePayload(IsGlobal: false, Category: "世界观", Content: "边境星域低通信。"),
            CancellationToken.None);

        var updated = await service.UpdatePreferenceAsync(
            firstOnly.Id,
            new UpdatePreferencePayload(Category: "", Content: "主角只在行动中暴露动机。", IsGlobal: null),
            CancellationToken.None);
        Assert.Equal("角色", updated.Category);
        Assert.Equal("主角只在行动中暴露动机。", updated.Content);

        var reloaded = new FileSystemPreferenceService(options, novelService);
        var firstPreferences = await reloaded.GetPreferencesAsync(firstNovel.Id, CancellationToken.None);
        Assert.Equal([global.Id], firstPreferences.Global.Select(item => item.Id));
        Assert.Equal([firstOnly.Id], firstPreferences.Novel.Select(item => item.Id));

        var secondPreferences = await reloaded.GetPreferencesAsync(secondNovel.Id, CancellationToken.None);
        Assert.Equal([global.Id], secondPreferences.Global.Select(item => item.Id));
        Assert.Single(secondPreferences.Novel);

        await reloaded.DeletePreferenceAsync(global.Id, CancellationToken.None);
        Assert.Empty((await reloaded.GetPreferencesAsync(firstNovel.Id, CancellationToken.None)).Global);
    }

    [Fact]
    public async Task BridgePreferenceHandlersCreateListUpdateAndDelete()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterPreferenceHandlers(new FileSystemPreferenceService(options, novelService));

        using var createJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_create_pref",
              "method": "CreatePreference",
              "payload": { "args": [{{novel.Id}}, { "is_global": false, "category": "风格", "content": "少用解释性旁白。" }] }
            }
            """));
        var preferenceId = createJson.RootElement.GetProperty("result").GetProperty("id").GetInt64();

        using var updateJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_update_pref",
              "method": "UpdatePreference",
              "payload": { "args": [{{preferenceId}}, { "category": "叙事", "content": "避免连续总结。" }] }
            }
            """));
        Assert.Equal("叙事", updateJson.RootElement.GetProperty("result").GetProperty("category").GetString());

        using var listJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_preferences",
              "method": "GetPreferences",
              "payload": { "args": [{{novel.Id}}] }
            }
            """));
        var result = listJson.RootElement.GetProperty("result");
        Assert.Empty(result.GetProperty("global").EnumerateArray());
        Assert.Equal(preferenceId, result.GetProperty("novel")[0].GetProperty("id").GetInt64());

        using var deleteJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_delete_pref",
              "method": "DeletePreference",
              "payload": { "args": [{{preferenceId}}] }
            }
            """));
        Assert.True(deleteJson.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BridgePreferenceHandlersReturnStableErrors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterPreferenceHandlers(new FileSystemPreferenceService(options, novelService));

        using var invalidContent = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bad_pref",
              "method": "CreatePreference",
              "payload": { "args": [{{novel.Id}}, { "is_global": false, "category": "风格", "content": "   " }] }
            }
            """));
        AssertBridgeError(invalidContent.RootElement, "req_bad_pref", BridgeErrorCodes.ValidationError);
    }

    [Fact]
    public async Task BridgePreferenceHandlersReturnStableErrorWhenAppIsNotInitialized()
    {
        var options = CreateOptions();
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterPreferenceHandlers(new FileSystemPreferenceService(
                options,
                new FileSystemNovelService(options, new FileSystemAppSettingsService(options))));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_preferences",
              "method": "GetPreferences",
              "payload": { "args": [1] }
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_preferences", BridgeErrorCodes.AppNotInitialized);
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
}
