using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.App.Desktop;

public static class PhotinoLaunchMode
{
    public const string DesktopFlag = "--desktop";
    public const string StartUrlPrefix = "--start-url=";
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 840;

    public static bool HasStartUrlOverride(IEnumerable<string> args)
    {
        return args.Any(arg => arg.StartsWith(StartUrlPrefix, StringComparison.OrdinalIgnoreCase));
    }

    public static PhotinoWindowSettings CreateSettings(
        IEnumerable<string> args,
        string? defaultStartUrl = null,
        string? webViewDataPathKey = null,
        AppInitializationOptions? appOptions = null)
    {
        var argList = args as IReadOnlyCollection<string> ?? args.ToArray();
        var startUrl = argList
            .FirstOrDefault(arg => arg.StartsWith(StartUrlPrefix, StringComparison.OrdinalIgnoreCase))?
            .Substring(StartUrlPrefix.Length);
        var options = appOptions ?? DesktopAppConfiguration.CreateAppInitializationOptions(argList);
        var windowSettings = TryLoadPersistedWindowSettings(options);

        return new PhotinoWindowSettings(
            Title: "novelist",
            X: windowSettings.X,
            Y: windowSettings.Y,
            Width: windowSettings.Width,
            Height: windowSettings.Height,
            StartUrl: string.IsNullOrWhiteSpace(startUrl) ? defaultStartUrl ?? "about:blank" : startUrl,
            Maximized: windowSettings.Maximized,
            WebViewDataPathKey: string.IsNullOrWhiteSpace(startUrl) ? webViewDataPathKey : null,
            AppOptions: options);
    }

    private static RestoredWindowSettings TryLoadPersistedWindowSettings(AppInitializationOptions options)
    {
        try
        {
            var settings = new FileSystemAppSettingsService(options)
                .GetWindowSettingsAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return new RestoredWindowSettings(settings.X, settings.Y, settings.Width, settings.Height, Maximized: false);
        }
        catch (AppNotInitializedException)
        {
            return new RestoredWindowSettings(null, null, DefaultWidth, DefaultHeight, Maximized: false);
        }
        catch (Exception exception)
        {
            DesktopLaunchLog.Write("Window settings not restored; using safe defaults.", exception);
            return new RestoredWindowSettings(null, null, DefaultWidth, DefaultHeight, Maximized: false);
        }
    }

    private sealed record RestoredWindowSettings(int? X, int? Y, int Width, int Height, bool Maximized);
}
