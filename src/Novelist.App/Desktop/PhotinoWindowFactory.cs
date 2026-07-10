using System.Drawing;
using Novelist.Core.App;
using Photino.NET;

namespace Novelist.App.Desktop;

public sealed class PhotinoWindowFactory : IPhotinoWindowFactory
{
    private static readonly Size SafeInitialWindowSize = new(1280, 840);

    public IPhotinoWindow Create(PhotinoWindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var window = new PhotinoWindow();
        window.SetBrowserControlInitParameters("--disable-gpu --disable-gpu-compositing --disable-software-rasterizer=false");
        DesktopLaunchLog.Write("Photino browser init parameters configured.");
        var temporaryFilesPath = TryCreateWebViewDataPath(settings.WebViewDataPathKey);
        if (!string.IsNullOrWhiteSpace(temporaryFilesPath))
        {
            window.SetTemporaryFilesPath(temporaryFilesPath);
            DesktopLaunchLog.Write("Photino temporary files path: " + temporaryFilesPath);
        }
        else
        {
            DesktopLaunchLog.Write("Photino temporary files path not configured; using platform default.");
        }
        var adapter = new PhotinoWindowAdapter(window);
        var runtime = DesktopBridgeComposition.CreateRuntime(adapter, settings.AppOptions);
        var bridge = runtime.Bridge;

        window
            .SetTitle(settings.Title)
            .SetChromeless(!OperatingSystem.IsMacOS())
            .SetUseOsDefaultSize(false)
            .SetSize(SafeInitialWindowSize)
            .SetUseOsDefaultLocation(false)
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler((_, message) => bridge.Post(message));
        // Photino exposes monitor geometry only after the native window is created.
        window.WindowCreatedHandler = (_, _) => RestorePlacementAfterWindowCreation(window, settings);
        window.Center();
        window.Load(settings.StartUrl);
        window.Maximized = false;

        runtime.StartAsync().AsTask().GetAwaiter().GetResult();
        return new RuntimeOwnedWindow(adapter, runtime);
    }

    private static void RestorePlacementAfterWindowCreation(
        PhotinoWindow window,
        PhotinoWindowSettings settings)
    {
        try
        {
            var workAreas = window.Monitors
                .Select(monitor => monitor.WorkArea)
                .Where(area => area.Width > 0 && area.Height > 0)
                .ToArray();
            if (workAreas.Length == 0)
            {
                DesktopLaunchLog.Write("No visible monitor work area was available; keeping the safe initial window placement.");
                return;
            }

            if (!settings.Maximized && settings.X is { } x && settings.Y is { } y &&
                PhotinoWindowPlacement.TryResolveStoredBounds(
                    new Point(x, y),
                    new Size(settings.Width, settings.Height),
                    workAreas,
                    out var storedLocation,
                    out var storedSize))
            {
                window.SetSize(storedSize);
                window.MoveTo(storedLocation, allowOutsideWorkArea: false);
                return;
            }

            var preferredLocation = settings.X is { } preferredX && settings.Y is { } preferredY
                ? new Point(preferredX, preferredY)
                : (Point?)null;
            var launchSize = PhotinoWindowPlacement.ResolveDefaultLaunchSize(
                workAreas,
                new Size(settings.Width, settings.Height),
                preferredLocation);
            var centeredLocation = PhotinoWindowPlacement.CenterInVisibleWorkArea(
                launchSize,
                workAreas,
                preferredLocation);
            window.SetSize(launchSize);
            if (centeredLocation is { } location)
            {
                window.MoveTo(location, allowOutsideWorkArea: false);
            }
            else
            {
                window.Center();
            }
        }
        catch (Exception exception)
        {
            DesktopLaunchLog.Write("Unable to restore window placement after initialization; keeping the safe initial window placement.", exception);
        }
    }

    private static string? TryCreateWebViewDataPath(string? key)
    {
        foreach (var path in CandidateWebViewDataPaths(key))
        {
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception exception)
            {
                DesktopLaunchLog.Write("Unable to create WebView2 data path: " + path, exception);
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateWebViewDataPaths(string? key)
    {
        var leaf = SanitizeWebViewDataPathKey(key);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Novelist", "WebView2", leaf);
        }

        yield return Path.Combine(Path.GetTempPath(), "Novelist", "WebView2", leaf);
    }

    private static string SanitizeWebViewDataPathKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "default";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(key.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return sanitized.Length == 0 ? "default" : sanitized;
    }

private sealed class PhotinoWindowAdapter : IPhotinoWindow
    {
        private readonly PhotinoWindow _window;

        public PhotinoWindowAdapter(PhotinoWindow window)
        {
            _window = window;
        }

        public void WaitForClose()
        {
            _window.WaitForClose();
        }

        public void SendWebMessage(string message)
        {
            _window.SendWebMessage(message);
        }

        public async ValueTask<string?> ShowSaveFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<NovelExportFileFilter> filters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var photinoFilters = filters
                .Select(filter => (filter.DisplayName, new[] { filter.Pattern }))
                .ToArray();
            var path = await _window.ShowSaveFileAsync(title, defaultPath, photinoFilters);
            cancellationToken.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        public async ValueTask<string?> ShowOpenFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<WorkspaceFileFilter> filters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var photinoFilters = filters
                .Select(filter => (filter.DisplayName, filter.Patterns.ToArray()))
                .ToArray();
            var paths = await _window.ShowOpenFileAsync(title, defaultPath, false, photinoFilters);
            cancellationToken.ThrowIfCancellationRequested();
            return paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }

        public void Minimize()
        {
            _window.Minimized = true;
        }

        public void ToggleMaximize()
        {
            _window.Maximized = !_window.Maximized;
        }

        public bool IsMaximized()
        {
            return _window.Maximized;
        }

        public PhotinoWindowBounds GetBounds()
        {
            var location = _window.Location;
            var size = _window.Size;
            return new PhotinoWindowBounds(
                location.X,
                location.Y,
                size.Width,
                size.Height,
                _window.Maximized);
        }

public void Close()
{
_window.Close();
 }
 }

 private sealed class RuntimeOwnedWindow(
 IPhotinoWindow inner,
 DesktopBridgeComposition.DesktopBridgeRuntime runtime) : IPhotinoWindow
 {
 public void WaitForClose()
 {
 try { inner.WaitForClose(); }
 finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
 }

 public void SendWebMessage(string message) => inner.SendWebMessage(message);
 public ValueTask<string?> ShowSaveFileAsync(string title,string defaultPath,IReadOnlyList<NovelExportFileFilter> filters,CancellationToken cancellationToken) => inner.ShowSaveFileAsync(title,defaultPath,filters,cancellationToken);
 public ValueTask<string?> ShowOpenFileAsync(string title,string defaultPath,IReadOnlyList<WorkspaceFileFilter> filters,CancellationToken cancellationToken) => inner.ShowOpenFileAsync(title,defaultPath,filters,cancellationToken);
 public void Minimize() => inner.Minimize();
 public void ToggleMaximize() => inner.ToggleMaximize();
 public bool IsMaximized() => inner.IsMaximized();
 public PhotinoWindowBounds GetBounds() => inner.GetBounds();
 public void Close() => inner.Close();
 }
}
