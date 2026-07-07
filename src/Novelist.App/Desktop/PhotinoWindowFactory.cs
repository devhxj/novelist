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

        window
            .SetTitle(settings.Title)
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(settings.Width, settings.Height))
            .Center()
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler((_, message) => bridge.Post(message))
            .Load(settings.StartUrl);

        return adapter;
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

        public void Close()
        {
            _window.Close();
        }
    }
}
