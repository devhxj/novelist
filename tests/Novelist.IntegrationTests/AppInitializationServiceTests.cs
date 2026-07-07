using System.Text.Json;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class AppInitializationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ServicePersistsInitializationConfigAndCreatesDataDirectory()
    {
        var configDirectory = Path.Combine(_root, "config");
        var defaultDataDirectory = Path.Combine(_root, "default-data");
        var selectedDataDirectory = Path.Combine(_root, "selected-data");
        var service = CreateService(configDirectory, defaultDataDirectory);

        Assert.False(await service.IsInitializedAsync(CancellationToken.None));

        var initialConfig = await service.GetAppConfigAsync(CancellationToken.None);
        Assert.False(initialConfig.Initialized);
        Assert.Null(initialConfig.DataDir);

        await service.InitializeAsync(selectedDataDirectory, CancellationToken.None);

        Assert.True(await service.IsInitializedAsync(CancellationToken.None));
        Assert.True(Directory.Exists(selectedDataDirectory));
        Assert.True(File.Exists(Path.Combine(configDirectory, "config.json")));

        var config = await service.GetAppConfigAsync(CancellationToken.None);
        Assert.True(config.Initialized);
        Assert.Equal(Path.GetFullPath(selectedDataDirectory), config.DataDir);
    }

    [Fact]
    public async Task AppConfigIncludesUpdateCheckProductConfigurationBeforeAndAfterInitialization()
    {
        var configDirectory = Path.Combine(_root, "config");
        var defaultDataDirectory = Path.Combine(_root, "default-data");
        var selectedDataDirectory = Path.Combine(_root, "selected-data");
        var service = new FileSystemAppInitializationService(new AppInitializationOptions
        {
            ConfigDirectory = configDirectory,
            DefaultDataDirectory = defaultDataDirectory,
            UpdateCheckEndpointUrl = "https://updates.example.test/novelist/releases.json",
            UpdateChecksEnabledByDefault = true,
            UpdateCheckTimeoutMs = 2500
        });

        var before = await service.GetAppConfigAsync(CancellationToken.None);
        Assert.False(before.Initialized);
        Assert.Null(before.DataDir);
        Assert.Equal("https://updates.example.test/novelist/releases.json", before.UpdateCheck.EndpointUrl);
        Assert.True(before.UpdateCheck.DefaultEnabled);
        Assert.Equal(2500, before.UpdateCheck.TimeoutMs);

        await service.InitializeAsync(selectedDataDirectory, CancellationToken.None);

        var after = await service.GetAppConfigAsync(CancellationToken.None);
        Assert.True(after.Initialized);
        Assert.Equal(Path.GetFullPath(selectedDataDirectory), after.DataDir);
        Assert.Equal("https://updates.example.test/novelist/releases.json", after.UpdateCheck.EndpointUrl);
        Assert.True(after.UpdateCheck.DefaultEnabled);
        Assert.Equal(2500, after.UpdateCheck.TimeoutMs);
    }

    [Fact]
    public async Task ServiceUpdatesDataDirectoryAndKeepsPlatformPayloadStable()
    {
        var configDirectory = Path.Combine(_root, "config");
        var defaultDataDirectory = Path.Combine(_root, "default-data");
        var updatedDataDirectory = Path.Combine(_root, "updated-data");
        var service = CreateService(configDirectory, defaultDataDirectory);

        var platform = await service.GetPlatformAsync(CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(platform.Os));
        Assert.Equal(Path.GetFullPath(defaultDataDirectory), platform.DefaultPath);

        await service.UpdateDataDirectoryAsync(updatedDataDirectory, CancellationToken.None);

        var config = await service.GetAppConfigAsync(CancellationToken.None);
        Assert.True(config.Initialized);
        Assert.Equal(Path.GetFullPath(updatedDataDirectory), config.DataDir);
        Assert.True(Directory.Exists(updatedDataDirectory));
    }

    [Fact]
    public async Task BridgeHandlersReturnRealInitializationState()
    {
        var service = CreateService(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "default-data"));
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterAppInitializationHandlers(service);

        using var before = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_before",
              "method": "IsInitialized",
              "payload": {}
            }
            """));
        Assert.False(before.RootElement.GetProperty("result").GetBoolean());

        await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_initialize",
              "method": "Initialize",
              "payload": { "args": ["{{JsonEncodedPath(Path.Combine(_root, "bridge-data"))}}"] }
            }
            """);

        using var after = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_after",
              "method": "GetAppConfig",
              "payload": {}
            }
            """));

        var result = after.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("initialized").GetBoolean());
        Assert.EndsWith("bridge-data", result.GetProperty("data_dir").GetString(), StringComparison.Ordinal);
        Assert.Equal("", result.GetProperty("update_check").GetProperty("endpoint_url").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static FileSystemAppInitializationService CreateService(
        string configDirectory,
        string defaultDataDirectory)
    {
        return new FileSystemAppInitializationService(new AppInitializationOptions
        {
            ConfigDirectory = configDirectory,
            DefaultDataDirectory = defaultDataDirectory
        });
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static string JsonEncodedPath(string path)
    {
        return JsonEncodedText.Encode(path).ToString();
    }
}
