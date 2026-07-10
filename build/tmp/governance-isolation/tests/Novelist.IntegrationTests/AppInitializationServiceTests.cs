using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
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

    [Fact]
    public async Task StartupInitializationReconcilesPendingNovelImportsBeforeWorkspaceUse()
    {
        var configDirectory = Path.Combine(_root, "config");
        var dataDirectory = Path.Combine(_root, "data");
        var options = new AppInitializationOptions
        {
            ConfigDirectory = configDirectory,
            DefaultDataDirectory = dataDirectory
        };

        await new FileSystemAppInitializationService(options).InitializeAsync(dataDirectory, CancellationToken.None);

        var novelService = new FileSystemNovelService(options);
        var runService = new FileSystemNovelImportRunService(options);
        var novel = await novelService.CreateNovelAsync(
            new CreateNovelPayload("启动恢复", "partial import", ""),
            CancellationToken.None);
        var workspace = Path.Combine(dataDirectory, "novels", novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await runService.StartRunAsync(ValidStartPayload("startup-recovery"), CancellationToken.None);
        await runService.UpdateRunAsync(
            new NovelImportRunUpdate(
                "startup-recovery",
                NovelImportRunStates.WritingFiles,
                "write_chapters",
                novel.Id,
                [$"novels/{novel.Id}"],
                null,
                null,
                null,
                null),
            CancellationToken.None);

        var restarted = new FileSystemAppInitializationService(options);

        Assert.True(await restarted.IsInitializedAsync(CancellationToken.None));

        Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.False(Directory.Exists(workspace));

        var config = await restarted.GetAppConfigAsync(CancellationToken.None);
        Assert.NotNull(config.ImportRecovery);
        var recovered = Assert.Single(config.ImportRecovery.ReconciledRuns);
        Assert.Equal("startup-recovery", recovered.TaskId);
        Assert.Equal(NovelImportRunStates.CleanupCompleted, recovered.State);

        var afterReplay = await restarted.GetAppConfigAsync(CancellationToken.None);
        Assert.Same(config.ImportRecovery, afterReplay.ImportRecovery);
    }

    [Fact]
    public async Task StartupRecoveryResultIsExposedThroughGetAppConfigBridgePayload()
    {
        var configDirectory = Path.Combine(_root, "bridge-config");
        var dataDirectory = Path.Combine(_root, "bridge-data");
        var options = new AppInitializationOptions
        {
            ConfigDirectory = configDirectory,
            DefaultDataDirectory = dataDirectory
        };
        await new FileSystemAppInitializationService(options).InitializeAsync(dataDirectory, CancellationToken.None);

        var runService = new FileSystemNovelImportRunService(options);
        await runService.StartRunAsync(ValidStartPayload("startup-bridge-recovery"), CancellationToken.None);
        var service = new FileSystemAppInitializationService(options);

        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterAppInitializationHandlers(service);

        using var json = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_config",
              "method": "GetAppConfig",
              "payload": {}
            }
            """));

        var importRecovery = json.RootElement.GetProperty("result").GetProperty("import_recovery");
        Assert.Single(importRecovery.GetProperty("reconciled_runs").EnumerateArray());
        Assert.Equal("startup-bridge-recovery", importRecovery.GetProperty("reconciled_runs")[0].GetProperty("task_id").GetString());
    }

    [Fact]
    public async Task UpdatingDataDirectoryInvalidatesCachedStartupRecoveryResult()
    {
        var recovery = new CountingImportRecoveryService();
        var referenceRecovery = new CountingReferenceAnchorRecoveryService();
        var options = new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "cache-config"),
            DefaultDataDirectory = Path.Combine(_root, "cache-data-1")
        };
        var service = new FileSystemAppInitializationService(
            options,
            legacyMigration: null,
            importRecovery: recovery,
            referenceAnchorRecovery: referenceRecovery);

        await service.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var first = await service.GetAppConfigAsync(CancellationToken.None);

        await service.UpdateDataDirectoryAsync(Path.Combine(_root, "cache-data-2"), CancellationToken.None);
        var second = await service.GetAppConfigAsync(CancellationToken.None);

        Assert.Equal("startup-recovery-1", Assert.Single(first.ImportRecovery!.ReconciledRuns).TaskId);
        Assert.Equal("startup-recovery-2", Assert.Single(second.ImportRecovery!.ReconciledRuns).TaskId);
        Assert.Equal(2, recovery.CallCount);
        Assert.Equal(2, referenceRecovery.CallCount);
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

    private string CreateImportFixture(string taskId)
    {
        var fixtures = Path.Combine(_root, "fixtures");
        Directory.CreateDirectory(fixtures);
        var path = Path.Combine(fixtures, $"{taskId}.txt");
        File.WriteAllText(path, "第一章\n启动恢复测试。");
        return path;
    }

    private StartNovelImportPayload ValidStartPayload(string taskId)
    {
        var sourcePath = CreateImportFixture(taskId);
        return new StartNovelImportPayload(
            taskId,
            sourcePath,
            Path.GetFileName(sourcePath),
            NovelImportKinds.Txt,
            "启动恢复测试",
            "import startup recovery");
    }

    private sealed class CountingImportRecoveryService : INovelImportRecoveryService
    {
        public int CallCount { get; private set; }

        public ValueTask<NovelImportReconciliationResultPayload> ReconcileAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var now = DateTimeOffset.UtcNow;
            var run = new NovelImportRunPayload(
                $"startup-recovery-{CallCount}",
                NovelImportRunStates.CleanupCompleted,
                "cleanup_completed",
                "sample.txt",
                "sha256:path",
                NovelImportKinds.Txt,
                1,
                ["novels/1"],
                [],
                [],
                [],
                null,
                now,
                now,
                now);
            return ValueTask.FromResult(new NovelImportReconciliationResultPayload([run], [], [], now));
        }
    }

    private sealed class CountingReferenceAnchorRecoveryService : IReferenceAnchorProcessingRecoveryService
    {
        public int CallCount { get; private set; }

        public ValueTask ReconcileRecoverableProcessingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.CompletedTask;
        }
    }
}
