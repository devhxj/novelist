using System.Drawing;
using Novelist.Core.App;
using Photino.NET;

namespace Novelist.App.Desktop;

public sealed class PhotinoWindowFactory : IPhotinoWindowFactory
{
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
        var bridge = DesktopBridgeComposition.CreateBridge(adapter, settings.AppOptions);

        var hasStoredLocation = settings.X.HasValue && settings.Y.HasValue;
        var workAreas = SafeMonitorWorkAreas(window).ToArray();
        var launchSize = ResolveLaunchSize(settings, workAreas, hasStoredLocation);
        var restoreStoredLocation = hasStoredLocation && !settings.Maximized;
        window
            .SetTitle(settings.Title)
            .SetChromeless(!OperatingSystem.IsMacOS())
            .SetUseOsDefaultSize(false)
            .SetSize(launchSize)
            .SetUseOsDefaultLocation(!restoreStoredLocation)
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler((_, message) => bridge.Post(message));
        if (restoreStoredLocation)
        {
            window.MoveTo(ResolveLaunchLocation(settings, launchSize, workAreas), allowOutsideWorkArea: false);
        }
        else if (hasStoredLocation && settings.Maximized)
        {
            var centeredLocation = PhotinoWindowPlacement.CenterInVisibleWorkArea(
                launchSize,
                workAreas,
                new Point(settings.X!.Value, settings.Y!.Value));
            if (centeredLocation is { } location)
            {
                window.MoveTo(location, allowOutsideWorkArea: false);
            }
            else
            {
                window.Center();
            }
        }
        else
        {
            window.Center();
        }
        window.Load(settings.StartUrl);
        window.Maximized = false;

        return adapter;
    }

    private static Size ResolveLaunchSize(
        PhotinoWindowSettings settings,
        IReadOnlyList<Rectangle> workAreas,
        bool hasStoredLocation)
    {
        var fallback = new Size(settings.Width, settings.Height);
        if (hasStoredLocation && !settings.Maximized)
        {
            return fallback;
        }

        var preferredLocation = hasStoredLocation
            ? new Point(settings.X!.Value, settings.Y!.Value)
            : (Point?)null;
        return PhotinoWindowPlacement.ResolveDefaultLaunchSize(workAreas, fallback, preferredLocation);
    }

    private static Point ResolveLaunchLocation(
        PhotinoWindowSettings settings,
        Size launchSize,
        IReadOnlyList<Rectangle> workAreas)
    {
        var requested = new Point(settings.X!.Value, settings.Y!.Value);
        return PhotinoWindowPlacement.ClampLocationToVisibleWorkArea(
            requested,
            launchSize,
            workAreas);
    }

    private static IEnumerable<Rectangle> SafeMonitorWorkAreas(PhotinoWindow window)
    {
        try
        {
            return window.Monitors
                .Select(monitor => monitor.WorkArea)
                .Where(area => area.Width > 0 && area.Height > 0)
                .ToArray();
        }
        catch (Exception exception)
        {
            DesktopLaunchLog.Write("Unable to read monitor work areas; using stored window location without pre-clamp.", exception);
            return [];
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
}
