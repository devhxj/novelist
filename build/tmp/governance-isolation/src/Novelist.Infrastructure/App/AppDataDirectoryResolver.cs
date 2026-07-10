using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal static class AppDataDirectoryResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<string> ResolveAsync(
        AppInitializationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configPath = Path.Combine(options.ConfigDirectory, "config.json");
        if (!File.Exists(configPath))
        {
            throw new AppNotInitializedException();
        }

        await using var stream = File.OpenRead(configPath);
        var config = await JsonSerializer.DeserializeAsync<AppPointerConfig>(stream, JsonOptions, cancellationToken);
        if (config is null || string.IsNullOrWhiteSpace(config.DataDir))
        {
            throw new InvalidOperationException("Application initialization config is empty or malformed.");
        }

        var dataDirectory = Path.GetFullPath(config.DataDir);
        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }

    private sealed record AppPointerConfig(
        [property: JsonPropertyName("data_dir")] string DataDir);
}
