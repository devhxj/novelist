using System.Globalization;
using System.Reflection;
using Novelist.Infrastructure.App;

namespace Novelist.App.Desktop;

public static class DesktopAppConfiguration
{
    private const string UpdateCheckEndpointUrlKey = "Novelist:UpdateCheckEndpointUrl";
    private const string UpdateChecksEnabledByDefaultKey = "Novelist:UpdateChecksEnabledByDefault";
    private const string UpdateCheckTimeoutMsKey = "Novelist:UpdateCheckTimeoutMs";

    public static AppInitializationOptions CreateAppInitializationOptions(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var argList = args as IReadOnlyCollection<string> ?? args.ToArray();
        var endpointUrl = ReadValue(argList, UpdateCheckEndpointUrlKey) ??
            ReadAssemblyMetadata(UpdateCheckEndpointUrlKey) ??
            string.Empty;
        return new AppInitializationOptions
        {
            EnableLegacyMigration = true,
            UpdateCheckEndpointUrl = endpointUrl.Trim(),
            UpdateChecksEnabledByDefault = ReadBool(argList, UpdateChecksEnabledByDefaultKey) ??
                ParseBool(ReadAssemblyMetadata(UpdateChecksEnabledByDefaultKey), UpdateChecksEnabledByDefaultKey) ??
                false,
            UpdateCheckTimeoutMs = ReadInt(argList, UpdateCheckTimeoutMsKey) ??
                ParseInt(ReadAssemblyMetadata(UpdateCheckTimeoutMsKey), UpdateCheckTimeoutMsKey) ??
                5000
        };
    }

    private static string? ReadValue(IEnumerable<string> args, string key)
    {
        var shortPrefix = key + "=";
        var longPrefix = "--" + shortPrefix;
        foreach (var arg in args)
        {
            if (arg.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[shortPrefix.Length..];
            }

            if (arg.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[longPrefix.Length..];
            }
        }

        return null;
    }

    private static bool? ReadBool(IEnumerable<string> args, string key)
    {
        return ParseBool(ReadValue(args, key), key);
    }

    private static int? ReadInt(IEnumerable<string> args, string key)
    {
        return ParseInt(ReadValue(args, key), key);
    }

    private static string? ReadAssemblyMetadata(string key)
    {
        return typeof(DesktopAppConfiguration)
            .Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static bool? ParseBool(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be 'true' or 'false'.");
    }

    private static int? ParseInt(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be an integer.");
    }
}
