using Novelist.App.Desktop;
using Novelist.Core.App;

namespace Novelist.IntegrationTests;

public sealed class PhotinoDesktopHostTests
{
    [Fact]
    public void LaunchModeIsExplicit()
    {
        Assert.False(PhotinoLaunchMode.ShouldLaunchDesktop(Array.Empty<string>()));
        Assert.True(PhotinoLaunchMode.ShouldLaunchDesktop([PhotinoLaunchMode.DesktopFlag]));
    }

    [Fact]
    public void LaunchSettingsUseStubUrlByDefault()
    {
        var settings = PhotinoLaunchMode.CreateSettings([PhotinoLaunchMode.DesktopFlag]);

        Assert.Equal("novelist", settings.Title);
        Assert.Equal(1280, settings.Width);
        Assert.Equal(840, settings.Height);
        Assert.Equal("about:blank", settings.StartUrl);
    }

    [Fact]
    public void LaunchSettingsUseProvidedDefaultUrl()
    {
        var settings = PhotinoLaunchMode.CreateSettings(
            [PhotinoLaunchMode.DesktopFlag],
            "http://127.0.0.1:54321/");

        Assert.Equal("http://127.0.0.1:54321/", settings.StartUrl);
    }

    [Fact]
    public void StartUrlArgumentOverridesDefaultUrl()
    {
        var settings = PhotinoLaunchMode.CreateSettings(
            [PhotinoLaunchMode.DesktopFlag, "--start-url=http://localhost:5173/"],
            "http://127.0.0.1:54321/");

        Assert.Equal("http://localhost:5173/", settings.StartUrl);
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
    public async Task DesktopApplicationStartsLoopbackHostAndPassesUrlToWindow()
    {
        var factory = new CapturingWindowFactory();
        var app = new PhotinoDesktopApplication(factory);

        await app.RunAsync([PhotinoLaunchMode.DesktopFlag]);

        Assert.NotNull(factory.Settings);
        Assert.StartsWith("http://127.0.0.1:", factory.Settings.StartUrl, StringComparison.Ordinal);
        Assert.EndsWith("/", factory.Settings.StartUrl, StringComparison.Ordinal);
        Assert.True(factory.Window.WaitForCloseCalled);
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
