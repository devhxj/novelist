using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Novelist.IntegrationTests;

public sealed class FrontendAssetTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _distPath;

    public FrontendAssetTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _distPath = CreateFrontendDistFixture();
    }

    [Fact]
    public async Task RootServesFrontendIndexWhenDistExists()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("novelist fixture", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaticAssetsAreServedFromFrontendDist()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var js = await client.GetStringAsync("/assets/app.js");

        Assert.Contains("novelist asset", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpaFallbackServesFrontendIndex()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/workspace/42");

        Assert.Contains("novelist fixture", html, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_distPath))
        {
            Directory.Delete(_distPath, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Novelist:FrontendDistPath"] = _distPath
                });
            });
        });
    }

    private static string CreateFrontendDistFixture()
    {
        var distPath = Path.Combine(Path.GetTempPath(), "novelist-frontend-" + Guid.NewGuid().ToString("N"));
        var assetsPath = Path.Combine(distPath, "assets");
        Directory.CreateDirectory(assetsPath);
        File.WriteAllText(Path.Combine(distPath, "index.html"), "<!doctype html><title>novelist fixture</title>");
        File.WriteAllText(Path.Combine(assetsPath, "app.js"), "console.log('novelist asset');");
        return distPath;
    }
}
