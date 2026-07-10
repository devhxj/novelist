using System.Drawing;
using Novelist.App.Desktop;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class PhotinoDesktopHostTests
{
    [Fact]
    public void LaunchSettingsUseStubUrlByDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "novelist-desktop-default-" + Guid.NewGuid().ToString("N"));
        var options = new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(root, "config"),
            DefaultDataDirectory = Path.Combine(root, "data"),
            EnableLegacyMigration = false
        };

        try
        {
            var settings = PhotinoLaunchMode.CreateSettings([PhotinoLaunchMode.DesktopFlag], appOptions: options);

            Assert.Equal("novelist", settings.Title);
            Assert.Null(settings.X);
            Assert.Null(settings.Y);
            Assert.Equal(1280, settings.Width);
            Assert.Equal(840, settings.Height);
            Assert.False(settings.Maximized);
            Assert.Equal("about:blank", settings.StartUrl);
            Assert.Same(options, settings.AppOptions);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
    public async Task LaunchSettingsUsePersistedWindowBoundsButNeverRestoreMaximized()
    {
        var root = Path.Combine(Path.GetTempPath(), "novelist-desktop-window-" + Guid.NewGuid().ToString("N"));
        var options = new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(root, "config"),
            DefaultDataDirectory = Path.Combine(root, "data"),
            EnableLegacyMigration = false
        };

        try
        {
            await new FileSystemAppInitializationService(options)
                .InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
            await new FileSystemAppSettingsService(options)
                .SaveWindowSettingsAsync(
                    new SaveWindowSettingsPayload(
                        X: 160,
                        Y: 120,
                        Width: 1440,
                        Height: 900,
                        Maximized: true),
                    CancellationToken.None);

            var settings = PhotinoLaunchMode.CreateSettings(
                [PhotinoLaunchMode.DesktopFlag],
                appOptions: options);

            Assert.Equal(160, settings.X);
            Assert.Equal(120, settings.Y);
            Assert.Equal(1440, settings.Width);
            Assert.Equal(900, settings.Height);
            Assert.False(settings.Maximized);
            Assert.Same(options, settings.AppOptions);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WindowPlacementClampsOffscreenLocationToNearestWorkArea()
    {
        var location = PhotinoWindowPlacement.ClampLocationToVisibleWorkArea(
            new Point(5000, -200),
            new Size(1200, 900),
            [new Rectangle(0, 0, 1920, 1040)]);

        Assert.Equal(new Point(720, 0), location);
    }

    [Fact]
    public void WindowPlacementKeepsLocationOnContainingMonitor()
    {
        var location = PhotinoWindowPlacement.ClampLocationToVisibleWorkArea(
            new Point(2050, 100),
            new Size(1000, 800),
            [
                new Rectangle(0, 0, 1920, 1040),
                new Rectangle(1920, 0, 1280, 984)
            ]);

        Assert.Equal(new Point(2050, 100), location);
    }

    [Fact]
    public void WindowPlacementUsesEightyPercentOfWorkAreaForDefaultLaunchSize()
    {
        var size = PhotinoWindowPlacement.ResolveDefaultLaunchSize(
            [new Rectangle(0, 0, 1920, 1040)],
            new Size(1280, 840));

        Assert.Equal(new Size(1536, 832), size);
    }

    [Fact]
    public void WindowPlacementUsesPreferredMonitorForDefaultLaunchSize()
    {
        var size = PhotinoWindowPlacement.ResolveDefaultLaunchSize(
            [
                new Rectangle(0, 0, 1920, 1040),
                new Rectangle(1920, 0, 1280, 984)
            ],
            new Size(1280, 840),
            new Point(2100, 100));

        Assert.Equal(new Size(1024, 787), size);
    }

    [Fact]
    public void WindowPlacementKeepsDefaultLaunchSizeWithinSmallWorkArea()
    {
        var size = PhotinoWindowPlacement.ResolveDefaultLaunchSize(
            [new Rectangle(0, 0, 760, 560)],
            new Size(1280, 840));

        Assert.Equal(new Size(760, 560), size);
    }

    [Fact]
    public void WindowPlacementCentersLaunchSizeOnPreferredMonitor()
    {
        var location = PhotinoWindowPlacement.CenterInVisibleWorkArea(
            new Size(1024, 787),
            [
                new Rectangle(0, 0, 1920, 1040),
                new Rectangle(1920, 0, 1280, 984)
            ],
            new Point(2100, 100));

        Assert.Equal(new Point(2048, 98), location);
    }

    [Fact]
    public void WindowPlacementRestoresSavedBoundsWithinNearestVisibleWorkArea()
    {
        var restored = PhotinoWindowPlacement.TryResolveStoredBounds(
            new Point(5000, -200),
            new Size(2500, 1600),
            [new Rectangle(0, 0, 1920, 1040)],
            out var location,
            out var size);

        Assert.True(restored);
        Assert.Equal(new Point(0, 0), location);
        Assert.Equal(new Size(1920, 1040), size);
    }

    [Fact]
    public void WindowPlacementDoesNotResolveSavedBoundsWithoutAVisibleWorkArea()
    {
        var restored = PhotinoWindowPlacement.TryResolveStoredBounds(
            new Point(5000, -200),
            new Size(2500, 1600),
            [],
            out _,
            out _);

        Assert.False(restored);
    }

    [Fact]
    public void StartupFailurePageProvidesActionsWithoutExposingExceptionDetails()
    {
        var page = DesktopStartupFailurePresenter.CreatePage();

        Assert.Contains("Novelist", page, StringComparison.Ordinal);
        Assert.Contains("npm --prefix frontend run build", page, StringComparison.Ordinal);
        Assert.Contains("desktop.log", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", page, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupFailureIsPresentedInsteadOfBeingRethrown()
    {
        var expected = new InvalidOperationException("startup failed");
        Exception? presented = null;

        DesktopApplicationEntryPoint.Run(
            [],
            _ => throw expected,
            exception => presented = exception,
            static (_, _) => { });

        Assert.Same(expected, presented);
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
        var settings = new PhotinoWindowSettings("novelist", null, null, 1280, 840, "about:blank");

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

        public PhotinoWindowBounds GetBounds()
        {
            return new PhotinoWindowBounds(160, 120, 1280, 840, Maximized);
        }

        public void Close()
        {
            Closed = true;
        }
    }
}
