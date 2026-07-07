using Novelist.App.Desktop;
using Novelist.Core.App;

namespace Novelist.IntegrationTests;

public sealed class PhotinoDesktopHostTests
{
    [Fact]
    public void LaunchSettingsUseStubUrlByDefault()
    {
        var settings = PhotinoLaunchMode.CreateSettings([PhotinoLaunchMode.DesktopFlag]);

        Assert.Equal("novelist", settings.Title);
        Assert.Equal(1280, settings.Width);
        Assert.Equal(840, settings.Height);
        Assert.Equal("about:blank", settings.StartUrl);
        Assert.NotNull(settings.AppOptions);
        Assert.True(settings.AppOptions.EnableLegacyMigration);
        Assert.Equal("", settings.AppOptions.UpdateCheckEndpointUrl);
        Assert.False(settings.AppOptions.UpdateChecksEnabledByDefault);
    }

    [Fact]
    public void LaunchSettingsUseProvidedDefaultUrl()
    {
        var settings = PhotinoLaunchMode.CreateSettings(
            [PhotinoLaunchMode.DesktopFlag],
            @"C:\novelist\frontend\dist\index.html");

        Assert.Equal(@"C:\novelist\frontend\dist\index.html", settings.StartUrl);
    }

    [Fact]
    public void StartUrlArgumentOverridesDefaultUrl()
    {
        var settings = PhotinoLaunchMode.CreateSettings(
            [PhotinoLaunchMode.DesktopFlag, "--start-url=http://localhost:5173/"],
            @"C:\novelist\frontend\dist\index.html",
            "frontend-cache-key");

        Assert.Equal("http://localhost:5173/", settings.StartUrl);
        Assert.Null(settings.WebViewDataPathKey);
    }

    [Fact]
    public void LaunchSettingsCarryUpdateCheckProductConfigurationFromArguments()
    {
        var settings = PhotinoLaunchMode.CreateSettings(
            [
                PhotinoLaunchMode.DesktopFlag,
                "--Novelist:UpdateCheckEndpointUrl=https://updates.example.test/novelist/releases.json",
                "--Novelist:UpdateChecksEnabledByDefault=true",
                "--Novelist:UpdateCheckTimeoutMs=2500"
            ]);

        Assert.NotNull(settings.AppOptions);
        Assert.Equal("https://updates.example.test/novelist/releases.json", settings.AppOptions.UpdateCheckEndpointUrl);
        Assert.True(settings.AppOptions.UpdateChecksEnabledByDefault);
        Assert.Equal(2500, settings.AppOptions.UpdateCheckTimeoutMs);
    }

    [Fact]
    public void HasStartUrlOverrideDetectsViteDebugUrl()
    {
        Assert.False(PhotinoLaunchMode.HasStartUrlOverride([PhotinoLaunchMode.DesktopFlag]));
        Assert.True(PhotinoLaunchMode.HasStartUrlOverride(["--start-url=http://localhost:5173/"]));
    }

    [Fact]
    public void DesktopHostCreatesWindowAndWaitsForClose()
    {
        var factory = new CapturingWindowFactory();
        var host = new PhotinoDesktopHost(factory);
        var settings = new PhotinoWindowSettings("novelist", 1280, 840, "about:blank");

        host.Run(settings);

        Assert.Same(settings, factory.Settings);
        Assert.True(factory.Window.WaitForCloseCalled);
    }

    [Fact]
    public async Task DesktopApplicationLoadsResolvedFrontendIndexWithoutLoopbackHost()
    {
        var distPath = CreateDistFixture();
        try
        {
            var factory = new CapturingWindowFactory();
            var app = new PhotinoDesktopApplication(factory);

            await app.RunAsync([PhotinoLaunchMode.DesktopFlag, $"{DesktopFrontendAssets.FrontendDistPrefix}{distPath}"]);

            Assert.NotNull(factory.Settings);
            Assert.Equal(Path.Combine(distPath, "index.html"), factory.Settings.StartUrl);
            AssertCacheKey(factory.Settings.WebViewDataPathKey);
            Assert.True(factory.Window.WaitForCloseCalled);
        }
        finally
        {
            if (Directory.Exists(distPath))
            {
                Directory.Delete(distPath, recursive: true);
            }
        }
    }

    [Fact]
    public void DesktopApplicationFailsClearlyWhenFrontendAssetsAreMissing()
    {
        var missingDistPath = Path.Combine(Path.GetTempPath(), "novelist-missing-dist-" + Guid.NewGuid().ToString("N"));
        var factory = new CapturingWindowFactory();
        var app = new PhotinoDesktopApplication(factory);

        var error = Assert.Throws<InvalidOperationException>(() =>
            app.Run([PhotinoLaunchMode.DesktopFlag, $"{DesktopFrontendAssets.FrontendDistPrefix}{missingDistPath}"]));

        Assert.Contains("Frontend assets were not found", error.Message, StringComparison.Ordinal);
        Assert.Null(factory.Settings);
    }

    private static string CreateDistFixture()
    {
        var distPath = Path.Combine(Path.GetTempPath(), "novelist-desktop-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(distPath);
        File.WriteAllText(Path.Combine(distPath, "index.html"), "<!doctype html><title>novelist fixture</title>");
        return distPath;
    }

    private static void AssertCacheKey(string? cacheKey)
    {
        Assert.NotNull(cacheKey);
        Assert.Matches(@"^frontend-[0-9a-f]{16}$", cacheKey);
    }

    private sealed class CapturingWindowFactory : IPhotinoWindowFactory
    {
        public CapturingWindow Window { get; } = new();

        public PhotinoWindowSettings? Settings { get; private set; }

        public IPhotinoWindow Create(PhotinoWindowSettings settings)
        {
            Settings = settings;
            return Window;
        }
    }

    private sealed class CapturingWindow : IPhotinoWindow
    {
        public bool WaitForCloseCalled { get; private set; }

        public List<string> SentMessages { get; } = [];

        public bool Minimized { get; private set; }

        public bool Maximized { get; private set; }

        public bool Closed { get; private set; }

        public void WaitForClose()
        {
            WaitForCloseCalled = true;
        }

        public void SendWebMessage(string message)
        {
            SentMessages.Add(message);
        }

        public ValueTask<string?> ShowSaveFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<NovelExportFileFilter> filters,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string?> ShowOpenFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<WorkspaceFileFilter> filters,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public void Minimize()
        {
            Minimized = true;
        }

        public void ToggleMaximize()
        {
            Maximized = !Maximized;
        }

        public bool IsMaximized()
        {
            return Maximized;
        }

        public void Close()
        {
            Closed = true;
        }
    }
}
