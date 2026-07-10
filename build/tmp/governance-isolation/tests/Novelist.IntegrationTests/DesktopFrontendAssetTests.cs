using Novelist.App.Desktop;

namespace Novelist.IntegrationTests;

public sealed class DesktopFrontendAssetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-frontend-assets-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryResolveUsesConfiguredDistPathWhenIndexExists()
    {
        var distPath = CreateDistFixture(Path.Combine(_root, "configured-dist"));

        var asset = DesktopFrontendAssets.TryResolve([$"--frontend-dist={distPath}"]);

        Assert.NotNull(asset);
        Assert.Equal(Path.GetFullPath(distPath), asset.DistPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(distPath), "index.html"), asset.IndexPath);
        Assert.Equal(asset.IndexPath, asset.StartUrl);
        AssertCacheKey(asset.CacheKey);
    }

    [Fact]
    public void TryResolveReturnsNullWhenConfiguredDistHasNoIndex()
    {
        var distPath = Path.Combine(_root, "missing-index");
        Directory.CreateDirectory(distPath);

        var asset = DesktopFrontendAssets.TryResolve([$"--frontend-dist={distPath}"]);

        Assert.Null(asset);
    }

    [Fact]
    public void TryResolveDiscoversFrontendDistFromParentDirectories()
    {
        var repoRoot = Path.Combine(_root, "repo");
        var distPath = CreateDistFixture(Path.Combine(repoRoot, "frontend", "dist"));
        var nestedPath = Path.Combine(repoRoot, "src", "Novelist.App", "bin");
        Directory.CreateDirectory(nestedPath);

        var asset = DesktopFrontendAssets.TryResolve(contentRootPath: nestedPath);

        Assert.NotNull(asset);
        Assert.Equal(Path.GetFullPath(distPath), asset.DistPath);
    }

    [Fact]
    public void TryResolveChangesCacheKeyWhenIndexContentChanges()
    {
        var distPath = CreateDistFixture(Path.Combine(_root, "versioned-dist"));
        var first = DesktopFrontendAssets.TryResolve([$"--frontend-dist={distPath}"]);

        File.WriteAllText(Path.Combine(distPath, "index.html"), "<!doctype html><title>changed fixture</title>");
        var second = DesktopFrontendAssets.TryResolve([$"--frontend-dist={distPath}"]);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.IndexPath, first.StartUrl);
        Assert.Equal(second.IndexPath, second.StartUrl);
        AssertCacheKey(first.CacheKey);
        AssertCacheKey(second.CacheKey);
        Assert.NotEqual(first.CacheKey, second.CacheKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string CreateDistFixture(string distPath)
    {
        Directory.CreateDirectory(distPath);
        File.WriteAllText(Path.Combine(distPath, "index.html"), "<!doctype html><title>novelist fixture</title>");
        return distPath;
    }

    private static void AssertCacheKey(string cacheKey)
    {
        Assert.Matches(@"^frontend-[0-9a-f]{16}$", cacheKey);
    }
}
