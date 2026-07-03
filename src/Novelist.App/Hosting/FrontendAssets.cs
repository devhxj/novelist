using Microsoft.Extensions.FileProviders;

namespace Novelist.App.Hosting;

public sealed class FrontendAssets
{
    private const string FrontendDistPathKey = "Novelist:FrontendDistPath";

    private FrontendAssets(string distPath)
    {
        DistPath = distPath;
        FileProvider = new PhysicalFileProvider(distPath);
    }

    public string DistPath { get; }

    public IFileProvider FileProvider { get; }

    public static FrontendAssets? TryResolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredPath = configuration[FrontendDistPathKey];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath);
            return HasIndex(fullPath) ? new FrontendAssets(fullPath) : null;
        }

        var directory = new DirectoryInfo(environment.ContentRootPath);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "frontend", "dist");
            if (HasIndex(candidate))
            {
                return new FrontendAssets(candidate);
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool HasIndex(string distPath)
    {
        return File.Exists(Path.Combine(distPath, "index.html"));
    }
}
