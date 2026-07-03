using System.Text.Json;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SettingsDefaultValuesAreCreatedUnderInitializedDataDirectory()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemAppSettingsService(options);

        var settings = await service.GetSettingsAsync(CancellationToken.None);

        Assert.Equal(1, settings.Id);
        Assert.Equal(0, settings.LastNovelId);
        Assert.Equal("", settings.SelectedModelKey);
        Assert.Equal("", settings.ReasoningEffort);
        Assert.Equal("manual", settings.ApprovalMode);
        Assert.Equal(360, settings.ChatPanelWidth);
        Assert.Equal("", settings.LastSessionId);
        Assert.Equal("", settings.UserName);
        Assert.True(File.Exists(Path.Combine(options.DefaultDataDirectory, "app_settings.json")));
    }

    [Fact]
    public async Task SettingsMutationsPersistAcrossServiceRecreation()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemAppSettingsService(options);

        await service.SetSelectedModelAsync("deepseek/deepseek-v4-pro", "high", CancellationToken.None);
        await service.SetChatPanelWidthAsync(420, CancellationToken.None);
        await service.SetLastSessionAsync("sess_42_demo", CancellationToken.None);
        await service.SetApprovalModeAsync("auto", CancellationToken.None);
        await service.SaveUserNameAsync("Demo Writer", CancellationToken.None);

        var reloaded = new FileSystemAppSettingsService(options);
        var settings = await reloaded.GetSettingsAsync(CancellationToken.None);

        Assert.Equal("deepseek/deepseek-v4-pro", settings.SelectedModelKey);
        Assert.Equal("high", settings.ReasoningEffort);
        Assert.Equal(420, settings.ChatPanelWidth);
        Assert.Equal("sess_42_demo", settings.LastSessionId);
        Assert.Equal("auto", settings.ApprovalMode);
        Assert.Equal("Demo Writer", settings.UserName);
    }

    [Fact]
    public async Task SaveAvatarWritesToUserDirectory()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemAppSettingsService(options);

        await service.SaveAvatarAsync([1, 2, 3, 4], CancellationToken.None);

        Assert.Equal(
            [1, 2, 3, 4],
            await File.ReadAllBytesAsync(Path.Combine(options.DefaultDataDirectory, "user", "avatar.jpg")));
    }

    [Fact]
    public async Task BridgeSettingsHandlersReturnValidationErrorForInvalidPayload()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterAppSettingsHandlers(new FileSystemAppSettingsService(options));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_width",
              "method": "SetChatPanelWidth",
              "payload": { "args": [12000] }
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_bad_width", BridgeErrorCodes.ValidationError);
    }

    [Fact]
    public async Task BridgeSettingsHandlersReturnStableErrorWhenAppIsNotInitialized()
    {
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterAppSettingsHandlers(new FileSystemAppSettingsService(CreateOptions()));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_settings",
              "method": "GetSettings",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_settings", BridgeErrorCodes.AppNotInitialized);
    }

    [Fact]
    public async Task BridgeSettingsHandlersPersistRepresentativeSettings()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterAppSettingsHandlers(new FileSystemAppSettingsService(options));

        await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_model",
              "method": "SetSelectedModel",
              "payload": { "args": ["qwen/qwen-plus", "max"] }
            }
            """);
        await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_mode",
              "method": "SetApprovalMode",
              "payload": { "args": ["auto"] }
            }
            """);

        using var json = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get",
              "method": "GetSettings",
              "payload": {}
            }
            """));

        var settings = json.RootElement.GetProperty("result");
        Assert.Equal("qwen/qwen-plus", settings.GetProperty("selected_model_key").GetString());
        Assert.Equal("max", settings.GetProperty("reasoning_effort").GetString());
        Assert.Equal("auto", settings.GetProperty("approval_mode").GetString());
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
