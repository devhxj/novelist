using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

[Collection(WebApplicationFactoryCollection.Name)]
public sealed class FrontendAssetTests : IDisposable
{
    private readonly string _distPath;
    private readonly List<string> _temporaryRoots = [];

    public FrontendAssetTests()
    {
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

    [Fact]
    public async Task CoverRouteServesValidatedNovelCover()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await service.CreateNovelAsync(
            new CreateNovelPayload("封面路由", "", ""),
            CancellationToken.None);
        await service.SaveCoverAsync(novel.Id, JpegCoverBytes(), CancellationToken.None);

        using var factory = CreateFactory(options);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/covers/{novel.Id}?v=1");

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(JpegCoverBytes(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task CoverRouteReturnsNotFoundWhenCoverIsMissing()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await service.CreateNovelAsync(
            new CreateNovelPayload("无封面", "", ""),
            CancellationToken.None);

        using var factory = CreateFactory(options);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/covers/{novel.Id}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_distPath))
        {
            Directory.Delete(_distPath, recursive: true);
        }

        foreach (var root in _temporaryRoots)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Novelist:FrontendDistPath"] = _distPath
                });
            });
        });
    }

    private WebApplicationFactory<Program> CreateFactory(AppInitializationOptions options)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Novelist:FrontendDistPath"] = _distPath,
                    ["Novelist:ConfigDirectory"] = options.ConfigDirectory,
                    ["Novelist:DefaultDataDirectory"] = options.DefaultDataDirectory
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

    private AppInitializationOptions CreateOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "novelist-cover-route-" + Guid.NewGuid().ToString("N"));
        _temporaryRoots.Add(root);
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(root, "config"),
            DefaultDataDirectory = Path.Combine(root, "data")
        };
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static byte[] JpegCoverBytes()
    {
        return [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];
    }
}
