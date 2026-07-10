using System.Text.Json;
using Novelist.Contracts.App;
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
        Assert.Equal("", settings.GitAuthorName);
        Assert.Equal("", settings.GitAuthorEmail);
        Assert.False(settings.UpdateCheckEnabled);
        Assert.Equal("", settings.UpdateCheckEndpointUrl);
        Assert.Equal("", settings.UpdateCheckDismissedVersion);
        Assert.Null(settings.UpdateCheckLastCheckedAt);
        Assert.Equal(280, settings.SidebarWidth);
        Assert.Equal(320, settings.MetadataPanelWidth);
        Assert.Null(settings.WindowX);
        Assert.Null(settings.WindowY);
        Assert.Equal(1280, settings.WindowWidth);
        Assert.Equal(840, settings.WindowHeight);
        Assert.False(settings.WindowMaximized);
        Assert.True(File.Exists(Path.Combine(options.DefaultDataDirectory, "app_settings.json")));
    }

    [Fact]
    public async Task Phase15SettingsDefaultsUseProductUpdateConfigurationWhenAvailable()
    {
        var options = CreateOptions() with
        {
            UpdateCheckEndpointUrl = "https://updates.example.test/novelist/releases.json",
            UpdateChecksEnabledByDefault = true
        };
        await InitializeAsync(options);
        var service = new FileSystemAppSettingsService(options);

        var settings = await service.GetUpdateCheckSettingsAsync(CancellationToken.None);

        Assert.True(settings.Enabled);
        Assert.Equal("https://updates.example.test/novelist/releases.json", settings.EndpointUrl);
        Assert.Equal("", settings.DismissedVersion);
        Assert.Null(settings.LastCheckedAt);
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
        await service.SaveGitAuthorSettingsAsync(
            new SaveGitAuthorSettingsPayload("Demo Author", "demo@example.com"),
            CancellationToken.None);
        await service.SaveUpdateCheckSettingsAsync(
            new SaveUpdateCheckSettingsPayload(
                Enabled: true,
                EndpointUrl: "https://updates.example.test/releases.json",
                DismissedVersion: "1.2.0"),
            CancellationToken.None);
        await service.SetUpdateCheckLastCheckedAtAsync(
            DateTimeOffset.Parse("2026-07-07T12:00:00Z"),
            CancellationToken.None);
        await service.SaveLayoutSettingsAsync(
            new SaveLayoutSettingsPayload(
                SidebarWidth: 300,
                ChatPanelWidth: 420,
                MetadataPanelWidth: 360),
            CancellationToken.None);
        await service.SaveWindowSettingsAsync(
            new SaveWindowSettingsPayload(
                X: 50,
                Y: 60,
                Width: 1440,
                Height: 900,
                Maximized: true),
            CancellationToken.None);

        var reloaded = new FileSystemAppSettingsService(options);
        var settings = await reloaded.GetSettingsAsync(CancellationToken.None);

        Assert.Equal("deepseek/deepseek-v4-pro", settings.SelectedModelKey);
        Assert.Equal("high", settings.ReasoningEffort);
        Assert.Equal(420, settings.ChatPanelWidth);
        Assert.Equal("sess_42_demo", settings.LastSessionId);
        Assert.Equal("auto", settings.ApprovalMode);
        Assert.Equal("Demo Writer", settings.UserName);
        Assert.Equal("Demo Author", settings.GitAuthorName);
        Assert.Equal("demo@example.com", settings.GitAuthorEmail);
        Assert.True(settings.UpdateCheckEnabled);
        Assert.Equal("https://updates.example.test/releases.json", settings.UpdateCheckEndpointUrl);
        Assert.Equal("1.2.0", settings.UpdateCheckDismissedVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T12:00:00Z"), settings.UpdateCheckLastCheckedAt);
        Assert.Equal(300, settings.SidebarWidth);
        Assert.Equal(360, settings.MetadataPanelWidth);
        Assert.Equal(50, settings.WindowX);
        Assert.Equal(60, settings.WindowY);
        Assert.Equal(1440, settings.WindowWidth);
        Assert.Equal(900, settings.WindowHeight);
        Assert.True(settings.WindowMaximized);

        var author = await reloaded.GetGitAuthorSettingsAsync(CancellationToken.None);
        Assert.Equal("Demo Author", author.Name);
        Assert.Equal("demo@example.com", author.Email);
        Assert.Equal("app", author.Scope);

        var update = await reloaded.GetUpdateCheckSettingsAsync(CancellationToken.None);
        Assert.True(update.Enabled);
        Assert.Equal("https://updates.example.test/releases.json", update.EndpointUrl);
        Assert.Equal("1.2.0", update.DismissedVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T12:00:00Z"), update.LastCheckedAt);

        var layout = await reloaded.GetLayoutSettingsAsync(CancellationToken.None);
        Assert.Equal(300, layout.SidebarWidth);
        Assert.Equal(420, layout.ChatPanelWidth);
        Assert.Equal(360, layout.MetadataPanelWidth);

        var window = await reloaded.GetWindowSettingsAsync(CancellationToken.None);
        Assert.Equal(50, window.X);
        Assert.Equal(60, window.Y);
        Assert.Equal(1440, window.Width);
        Assert.Equal(900, window.Height);
        Assert.True(window.Maximized);
    }

    [Fact]
    public async Task InvalidStoredPhase15SettingsAreNormalizedOnLoad()
    {
        var options = CreateOptions() with
        {
            UpdateCheckEndpointUrl = "https://updates.example.test/default.json",
            UpdateChecksEnabledByDefault = true
        };
        await InitializeAsync(options);
        await File.WriteAllTextAsync(
            Path.Combine(options.DefaultDataDirectory, "app_settings.json"),
            """
            {
              "ID": 99,
              "last_novel_id": 0,
              "selected_model_key": "bad-model-key",
              "reasoning_effort": "ok",
              "approval_mode": "robot",
              "chat_panel_width": 12,
              "last_session_id": "session one",
              "user_name": "Writer",
              "git_author_name": "Bad\u0001Name",
              "git_author_email": "not an email",
              "update_check_enabled": true,
              "update_check_endpoint_url": "file:///tmp/release.json",
              "update_check_dismissed_version": "bad\u0001version",
              "sidebar_width": 10,
              "metadata_panel_width": 5000,
              "window_x": 9999999,
              "window_y": -9999999,
              "window_width": 120,
              "window_height": 100,
              "window_maximized": true
            }
            """);
        var service = new FileSystemAppSettingsService(options);

        var settings = await service.GetSettingsAsync(CancellationToken.None);

        Assert.Equal(1, settings.Id);
        Assert.Equal("", settings.SelectedModelKey);
        Assert.Equal("manual", settings.ApprovalMode);
        Assert.Equal(360, settings.ChatPanelWidth);
        Assert.Equal("", settings.LastSessionId);
        Assert.Equal("", settings.GitAuthorName);
        Assert.Equal("", settings.GitAuthorEmail);
        Assert.True(settings.UpdateCheckEnabled);
        Assert.Equal("https://updates.example.test/default.json", settings.UpdateCheckEndpointUrl);
        Assert.Equal("", settings.UpdateCheckDismissedVersion);
        Assert.Equal(280, settings.SidebarWidth);
        Assert.Equal(320, settings.MetadataPanelWidth);
        Assert.Null(settings.WindowX);
        Assert.Null(settings.WindowY);
        Assert.Equal(1280, settings.WindowWidth);
        Assert.Equal(840, settings.WindowHeight);
        Assert.True(settings.WindowMaximized);
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
        await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_git_author",
              "method": "SaveGitAuthorSettings",
              "payload": { "args": [{ "name": "Bridge Author", "email": "bridge@example.com" }] }
            }
            """);
        await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_update_settings",
              "method": "SaveUpdateCheckSettings",
              "payload": { "args": [{ "enabled": true, "endpoint_url": "https://updates.example.test/releases.json", "dismissed_version": "1.1.0" }] }
            }
            """);
        await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_layout",
              "method": "SaveLayoutSettings",
              "payload": { "args": [{ "sidebar_width": 304, "chat_panel_width": 430, "metadata_panel_width": 372 }] }
            }
            """);
        await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_window",
              "method": "SaveWindowSettings",
              "payload": { "args": [{ "x": 100, "y": 120, "width": 1500, "height": 920, "maximized": false }] }
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
        Assert.Equal("Bridge Author", settings.GetProperty("git_author_name").GetString());
        Assert.Equal("bridge@example.com", settings.GetProperty("git_author_email").GetString());
        Assert.Equal(304, settings.GetProperty("sidebar_width").GetInt32());
        Assert.Equal(430, settings.GetProperty("chat_panel_width").GetInt32());
        Assert.Equal(372, settings.GetProperty("metadata_panel_width").GetInt32());

        using var authorJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get_author",
              "method": "GetGitAuthorSettings",
              "payload": {}
            }
            """));
        var author = authorJson.RootElement.GetProperty("result");
        Assert.Equal("Bridge Author", author.GetProperty("name").GetString());
        Assert.Equal("bridge@example.com", author.GetProperty("email").GetString());
        Assert.Equal("app", author.GetProperty("scope").GetString());

        using var updateJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get_update",
              "method": "GetUpdateCheckSettings",
              "payload": {}
            }
            """));
        var update = updateJson.RootElement.GetProperty("result");
        Assert.True(update.GetProperty("enabled").GetBoolean());
        Assert.Equal("https://updates.example.test/releases.json", update.GetProperty("endpoint_url").GetString());
        Assert.Equal("1.1.0", update.GetProperty("dismissed_version").GetString());

        using var windowJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get_window",
              "method": "GetWindowSettings",
              "payload": {}
            }
            """));
        var window = windowJson.RootElement.GetProperty("result");
        Assert.Equal(100, window.GetProperty("x").GetInt32());
        Assert.Equal(120, window.GetProperty("y").GetInt32());
        Assert.Equal(1500, window.GetProperty("width").GetInt32());
        Assert.Equal(920, window.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task BridgePhase15SettingsHandlersReturnValidationErrorsForInvalidPayloads()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterAppSettingsHandlers(new FileSystemAppSettingsService(options));

        using var authorJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_author",
              "method": "SaveGitAuthorSettings",
              "payload": { "args": [{ "name": "Bridge Author", "email": "not an email" }] }
            }
            """));
        AssertBridgeError(authorJson.RootElement, "req_bad_author", BridgeErrorCodes.ValidationError);

        using var nullAuthorJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_null_author",
              "method": "SaveGitAuthorSettings",
              "payload": { "args": [{ "name": null, "email": "bridge@example.com" }] }
            }
            """));
        AssertBridgeError(nullAuthorJson.RootElement, "req_null_author", BridgeErrorCodes.ValidationError);

        using var nullUpdateJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_null_update",
              "method": "SaveUpdateCheckSettings",
              "payload": { "args": [{ "enabled": true, "endpoint_url": null, "dismissed_version": "" }] }
            }
            """));
        AssertBridgeError(nullUpdateJson.RootElement, "req_null_update", BridgeErrorCodes.ValidationError);

        using var windowJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_window",
              "method": "SaveWindowSettings",
              "payload": { "args": [{ "x": 0, "y": 0, "width": 200, "height": 120, "maximized": false }] }
            }
            """));
        AssertBridgeError(windowJson.RootElement, "req_bad_window", BridgeErrorCodes.ValidationError);
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
