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
    public async Task UpdatingDataDirectoryCopiesDataBeforeRepointingAndWritesManifest()
    {
        var configDirectory = Path.Combine(_root, "config");
        var sourceData = Path.Combine(_root, "source-data");
        var targetData = Path.Combine(_root, "target-data");
        var service = CreateService(configDirectory, sourceData);
        await service.InitializeAsync(sourceData, CancellationToken.None);

        // 在源目录造出有意义的用户数据：一部小说 + 设置 + 章节正文。
        var novelWorkspace = Path.Combine(sourceData, "novels", "1");
        Directory.CreateDirectory(Path.Combine(novelWorkspace, "chapters"));
        await File.WriteAllTextAsync(Path.Combine(novelWorkspace, "chapters", "001.md"), "第一章正文");
        await File.WriteAllTextAsync(Path.Combine(sourceData, "app_settings.json"), "{\"last_novel_id\":1}");
        var sourceSnapshotBefore = Directory
            .EnumerateFiles(sourceData, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, File.ReadAllText(path)))
            .ToArray();

        var result = await service.UpdateDataDirectoryAsync(targetData, CancellationToken.None);

        // 复制完成：目标目录包含全部数据，清单为 completed。
        Assert.True(result.CopiedFiles >= sourceSnapshotBefore.Length);
        Assert.True(File.Exists(Path.Combine(targetData, "novels", "1", "chapters", "001.md")));
        Assert.True(File.Exists(Path.Combine(targetData, "app_settings.json")));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(targetData), DataDirectoryRelocationService.ManifestFileName),
            result.ManifestPath);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.Equal("completed", ReadManifestStatus(result.ManifestPath));

        // 指针切换到新目录。
        var config = await service.GetAppConfigAsync(CancellationToken.None);
        Assert.Equal(Path.GetFullPath(targetData), config.DataDir);

        // source 逐文件未动（copy-first 不变量）。
        var sourceSnapshotAfter = Directory
            .EnumerateFiles(sourceData, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, File.ReadAllText(path)))
            .ToArray();
        Assert.Equal(sourceSnapshotBefore, sourceSnapshotAfter);
    }

    [Fact]
    public async Task UpdatingDataDirectoryRejectsNestedTargetAndKeepsConfigUnchanged()
    {
        var configDirectory = Path.Combine(_root, "config");
        var sourceData = Path.Combine(_root, "source-data");
        var service = CreateService(configDirectory, sourceData);
        await service.InitializeAsync(sourceData, CancellationToken.None);
        var configBefore = await File.ReadAllBytesAsync(Path.Combine(configDirectory, "config.json"), CancellationToken.None);

        var nestedTarget = Path.Combine(sourceData, "inside-target");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateDataDirectoryAsync(nestedTarget, CancellationToken.None).AsTask());

        // 指针未动；路径校验在任何文件系统写入之前完成，目标连目录都不产生。
        Assert.Equal(configBefore, await File.ReadAllBytesAsync(Path.Combine(configDirectory, "config.json"), CancellationToken.None));
        var config = await service.GetAppConfigAsync(CancellationToken.None);
        Assert.Equal(Path.GetFullPath(sourceData), config.DataDir);
        Assert.False(Directory.Exists(nestedTarget));
    }

    [Fact]
    public async Task UpdatingDataDirectorySkipsConflictingTargetFilesWithWarnings()
    {
        var configDirectory = Path.Combine(_root, "config");
        var sourceData = Path.Combine(_root, "source-data");
        var targetData = Path.Combine(_root, "target-data");
        var service = CreateService(configDirectory, sourceData);
        await service.InitializeAsync(sourceData, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(sourceData, "app_settings.json"), "{\"last_novel_id\":1}");

        // 目标目录已有内容不同的同名文件：跳过并计入 warning，不覆盖。
        Directory.CreateDirectory(targetData);
        await File.WriteAllTextAsync(Path.Combine(targetData, "app_settings.json"), "{\"last_novel_id\":9}");

        var result = await service.UpdateDataDirectoryAsync(targetData, CancellationToken.None);

        Assert.True(result.SkippedFiles >= 1);
        Assert.Equal(1, result.Warnings);
        Assert.Equal("{\"last_novel_id\":9}", await File.ReadAllTextAsync(Path.Combine(targetData, "app_settings.json")));
        Assert.Equal("completed_with_warnings", ReadManifestStatus(result.ManifestPath));
    }

    [Fact]
    public async Task RelocationRetryOverwritesStaleFilesFromFailedPriorAttempt()
    {
        var source = Path.Combine(_root, "retry-source");
        var target = Path.Combine(_root, "retry-target");
        Directory.CreateDirectory(Path.Combine(source, "novels"));
        await File.WriteAllTextAsync(Path.Combine(source, "novels", "index.json"), "{\"v\":2}");

        // 模拟上次失败尝试：目标里留下 failed 清单 + 一份过期的部分复制产物（源此后又改过）。
        Directory.CreateDirectory(Path.Combine(target, "novels"));
        await File.WriteAllTextAsync(
            Path.Combine(target, DataDirectoryRelocationService.ManifestFileName),
            "{\"status\":\"failed\",\"started_at\":\"2026-01-01T00:00:00Z\"}");
        await File.WriteAllTextAsync(Path.Combine(target, "novels", "index.json"), "{\"v\":1}");

        var result = await new DataDirectoryRelocationService().RelocateAsync(source, target, CancellationToken.None);

        // R2：重试以源为权威覆盖上次的部分产物，而不是跳过陈旧文件。
        Assert.Equal("{\"v\":2}", await File.ReadAllTextAsync(Path.Combine(target, "novels", "index.json")));
        Assert.Equal(0, result.WarningCount);
        Assert.Equal("completed", ReadManifestStatus(result.ManifestPath));
    }

    [Fact]
    public async Task RelocationReportsProgressWithMonotonicCountsAndFinalTotal()
    {
        var source = Path.Combine(_root, "progress-source");
        var target = Path.Combine(_root, "progress-target");
        Directory.CreateDirectory(Path.Combine(source, "novels", "1", "chapters"));
        for (var i = 1; i <= 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, "novels", "1", "chapters", $"{i:000}.md"), $"第{i}章正文 {i}");
        }

        var reports = new List<DataDirectoryRelocationProgress>();
        // Progress<T> 会把回调投递到同步上下文（异步时序），这里用同步实现保证确定性。
        var progress = new SynchronousProgress<DataDirectoryRelocationProgress>(reports.Add);
        var result = await new DataDirectoryRelocationService().RelocateAsync(
            source,
            target,
            CancellationToken.None,
            progress);

        // 残余 2：进度上报单调递增，末次报告为最终复制数（= 全部文件）。
        Assert.Equal(5, result.CopiedFiles);
        Assert.NotEmpty(reports);
        Assert.True(reports.SequenceEqual(reports.OrderBy(report => report.CopiedFiles).ToArray()), "progress counts must be monotonic");
        var last = reports[^1];
        Assert.Equal(result.CopiedFiles, last.CopiedFiles);
        Assert.Equal(5, last.TotalFiles);
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public SynchronousProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }

    private static string ReadManifestStatus(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        return document.RootElement.GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task UpdatingDataDirectoryInvalidatesCachedStartupRecoveryResult()    {
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
