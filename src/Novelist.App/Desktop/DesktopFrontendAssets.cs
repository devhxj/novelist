namespace Novelist.App.Desktop;

public sealed record DesktopFrontendAsset(
    string DistPath,
    string IndexPath,
    string StartUrl);

public static class DesktopFrontendAssets
{
    public const string FrontendDistPrefix = "--frontend-dist=";
    private const string AppConfigurationDistPrefix = "Novelist:FrontendDistPath=";
    private const string AppConfigurationDistLongPrefix = "--Novelist:FrontendDistPath=";

    public static DesktopFrontendAsset? TryResolve(
        IEnumerable<string>? args = null,
        string? contentRootPath = null)
    {
        var configuredPath = ReadConfiguredDistPath(args);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath);
            return HasIndex(fullPath) ? Create(fullPath) : null;
        }

        foreach (var root in CandidateRoots(contentRootPath))
        {
            var asset = TryResolveFromRoot(root);
            if (asset is not null)
            {
                return asset;
            }
        }

        return null;
    }

    private static DesktopFrontendAsset? TryResolveFromRoot(string root)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "frontend", "dist");
            if (HasIndex(candidate))
            {
                return Create(candidate);
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots(string? contentRootPath)
    {
        var roots = new[]
        {
            contentRootPath,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(root);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static string? ReadConfiguredDistPath(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (var arg in args)
        {
            if (arg.StartsWith(FrontendDistPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[FrontendDistPrefix.Length..];
            }

            if (arg.StartsWith(AppConfigurationDistPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[AppConfigurationDistPrefix.Length..];
            }

            if (arg.StartsWith(AppConfigurationDistLongPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[AppConfigurationDistLongPrefix.Length..];
            }
        }

        return null;
    }

    private static DesktopFrontendAsset Create(string distPath)
    {
        var fullDistPath = Path.GetFullPath(distPath);
        var indexPath = Path.Combine(fullDistPath, "index.html");
        return new DesktopFrontendAsset(
            fullDistPath,
            indexPath,
            indexPath);
    }

    private static bool HasIndex(string distPath)
    {
        return File.Exists(Path.Combine(distPath, "index.html"));
    }
}
