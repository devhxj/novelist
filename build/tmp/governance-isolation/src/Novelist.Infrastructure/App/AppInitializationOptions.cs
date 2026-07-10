namespace Novelist.Infrastructure.App;

public sealed record AppInitializationOptions
{
    public string ConfigDirectory { get; init; } = DefaultConfigDirectory();

    public string DefaultDataDirectory { get; init; } = DefaultDataDirectoryPath();

    public bool EnableLegacyMigration { get; init; }

    public string? LegacyConfigDirectory { get; init; }

    public string? LegacyDataDirectory { get; init; }

    public string UpdateCheckEndpointUrl { get; init; } = string.Empty;

    public bool UpdateChecksEnabledByDefault { get; init; }

    public int UpdateCheckTimeoutMs { get; init; } = 5000;

    private static string DefaultConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData, "novelist");
        }

        return Path.Combine(UserHomeDirectory(), ".novelist");
    }

    private static string DefaultDataDirectoryPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "novelist");
        }

        return Path.Combine(UserHomeDirectory(), "Novelist");
    }

    private static string UserHomeDirectory()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
