using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemAppInitializationService : IAppInitializationService
{
    private const int DefaultUpdateCheckTimeoutMs = 5000;
    private const int MinUpdateCheckTimeoutMs = 500;
    private const int MaxUpdateCheckTimeoutMs = 30000;

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly ILegacyDataMigrationService? _legacyMigration;
    private readonly INovelImportRecoveryService _importRecovery;
    private readonly IReferenceAnchorProcessingRecoveryService? _referenceAnchorRecovery;
    private readonly SemaphoreSlim _startupRecoveryMutex = new(1, 1);
    private NovelImportReconciliationResultPayload? _lastImportRecoveryResult;
    private bool _referenceAnchorRecoveryCompleted;

    public FileSystemAppInitializationService(
        AppInitializationOptions? options = null,
        ILegacyDataMigrationService? legacyMigration = null,
        INovelImportRecoveryService? importRecovery = null,
        IReferenceAnchorProcessingRecoveryService? referenceAnchorRecovery = null)
    {
        _options = options ?? new AppInitializationOptions();
        _legacyMigration = legacyMigration ??
            (_options.EnableLegacyMigration ? new LegacyDataMigrationService(_options) : null);
        _importRecovery = importRecovery ?? new FileSystemNovelImportRecoveryService(_options);
        _referenceAnchorRecovery = referenceAnchorRecovery;
    }

    public async ValueTask<bool> IsInitializedAsync(CancellationToken cancellationToken)
    {
        if (await LoadConfigAsync(cancellationToken) is null)
        {
            return false;
        }

        await ReconcileStartupRecoveryAsync(cancellationToken);
        return true;
    }

    public async ValueTask InitializeAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        var hadConfig = File.Exists(ConfigPath);
        await SaveConfigAsync(dataDirectory, cancellationToken);
        _lastImportRecoveryResult = null;
        _referenceAnchorRecoveryCompleted = false;
        try
        {
            if (_legacyMigration is not null)
            {
                await _legacyMigration.MigrateAsync(NormalizePath(dataDirectory), cancellationToken);
            }

            await ReconcileStartupRecoveryAsync(cancellationToken);
        }
        catch
        {
            if (!hadConfig)
            {
                TryDeleteConfig();
            }

            throw;
        }
    }

    public async ValueTask<AppConfigPayload> GetAppConfigAsync(CancellationToken cancellationToken)
    {
        var config = await LoadConfigAsync(cancellationToken);
        var importRecovery = config is null
            ? null
            : await ReconcileStartupRecoveryAsync(cancellationToken);
        return config is null
            ? new AppConfigPayload(false, null, CreateUpdateCheckConfiguration(), null)
            : new AppConfigPayload(true, config.DataDir, CreateUpdateCheckConfiguration(), importRecovery);
    }

    public async ValueTask UpdateDataDirectoryAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        await SaveConfigAsync(dataDirectory, cancellationToken);
        _lastImportRecoveryResult = null;
        _referenceAnchorRecoveryCompleted = false;
        await ReconcileStartupRecoveryAsync(cancellationToken);
    }

    public ValueTask<PlatformPayload> GetPlatformAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PlatformPayload(GetPlatformName(), NormalizePath(_options.DefaultDataDirectory)));
    }

    private async ValueTask<AppPointerConfig?> LoadConfigAsync(CancellationToken cancellationToken)
    {
        var path = ConfigPath;
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<AppPointerConfig>(
            stream,
            ConfigJsonOptions,
            cancellationToken);

        if (config is null || string.IsNullOrWhiteSpace(config.DataDir))
        {
            throw new InvalidOperationException("App initialization config is empty or malformed.");
        }

        var normalized = NormalizePath(config.DataDir);
        Directory.CreateDirectory(normalized);
        return new AppPointerConfig(normalized);
    }

    private async ValueTask SaveConfigAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Data directory path is required.", nameof(dataDirectory));
        }

        var normalizedDataDirectory = NormalizePath(dataDirectory);
        Directory.CreateDirectory(normalizedDataDirectory);
        Directory.CreateDirectory(_options.ConfigDirectory);

        var config = new AppPointerConfig(normalizedDataDirectory);
        await using var stream = File.Create(ConfigPath);
        await JsonSerializer.SerializeAsync(stream, config, ConfigJsonOptions, cancellationToken);
    }

    private async ValueTask<NovelImportReconciliationResultPayload> ReconcileStartupRecoveryAsync(
        CancellationToken cancellationToken)
    {
        await _startupRecoveryMutex.WaitAsync(cancellationToken);
        try
        {
            if (_lastImportRecoveryResult is null)
            {
                _lastImportRecoveryResult = await _importRecovery.ReconcileAsync(cancellationToken);
            }

            if (!_referenceAnchorRecoveryCompleted && _referenceAnchorRecovery is not null)
            {
                await _referenceAnchorRecovery.ReconcileRecoverableProcessingAsync(cancellationToken);
                _referenceAnchorRecoveryCompleted = true;
            }

            return _lastImportRecoveryResult;
        }
        finally
        {
            _startupRecoveryMutex.Release();
        }
    }

    private string ConfigPath => Path.Combine(_options.ConfigDirectory, "config.json");

    private UpdateCheckConfigurationPayload CreateUpdateCheckConfiguration()
    {
        var endpointUrl = (_options.UpdateCheckEndpointUrl ?? string.Empty).Trim();
        var timeoutMs = _options.UpdateCheckTimeoutMs;
        if (timeoutMs <= 0)
        {
            timeoutMs = DefaultUpdateCheckTimeoutMs;
        }

        timeoutMs = Math.Clamp(timeoutMs, MinUpdateCheckTimeoutMs, MaxUpdateCheckTimeoutMs);
        return new UpdateCheckConfigurationPayload(
            endpointUrl,
            DefaultEnabled: _options.UpdateChecksEnabledByDefault && endpointUrl.Length > 0,
            TimeoutMs: timeoutMs);
    }

    private void TryDeleteConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                File.Delete(ConfigPath);
            }
        }
        catch
        {
            // Preserve the migration failure; the pointer can be repaired by reinitializing.
        }
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(ExpandTilde(path));
    }

    private static string ExpandTilde(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return path;
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "darwin";
        }

        return "linux";
    }

    private sealed record AppPointerConfig(
        [property: JsonPropertyName("data_dir")] string DataDir);
}
