using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemAppInitializationService : IAppInitializationService
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;

    public FileSystemAppInitializationService(AppInitializationOptions? options = null)
    {
        _options = options ?? new AppInitializationOptions();
    }

    public async ValueTask<bool> IsInitializedAsync(CancellationToken cancellationToken)
    {
        return await LoadConfigAsync(cancellationToken) is not null;
    }

    public async ValueTask InitializeAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        await SaveConfigAsync(dataDirectory, cancellationToken);
    }

    public async ValueTask<AppConfigPayload> GetAppConfigAsync(CancellationToken cancellationToken)
    {
        var config = await LoadConfigAsync(cancellationToken);
        return config is null
            ? new AppConfigPayload(false, null)
            : new AppConfigPayload(true, config.DataDir);
    }

    public async ValueTask UpdateDataDirectoryAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        await SaveConfigAsync(dataDirectory, cancellationToken);
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

    private string ConfigPath => Path.Combine(_options.ConfigDirectory, "config.json");

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
