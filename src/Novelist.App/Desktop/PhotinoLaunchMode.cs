namespace Novelist.App.Desktop;

public static class PhotinoLaunchMode
{
    public const string DesktopFlag = "--desktop";
    public const string StartUrlPrefix = "--start-url=";

    public static bool HasStartUrlOverride(IEnumerable<string> args)
    {
        return args.Any(arg => arg.StartsWith(StartUrlPrefix, StringComparison.OrdinalIgnoreCase));
    }

    public static PhotinoWindowSettings CreateSettings(
        IEnumerable<string> args,
        string? defaultStartUrl = null,
        string? webViewDataPathKey = null)
    {
        var argList = args as IReadOnlyCollection<string> ?? args.ToArray();
        var startUrl = argList
            .FirstOrDefault(arg => arg.StartsWith(StartUrlPrefix, StringComparison.OrdinalIgnoreCase))?
            .Substring(StartUrlPrefix.Length);

        return new PhotinoWindowSettings(
            Title: "novelist",
            Width: 1280,
            Height: 840,
            StartUrl: string.IsNullOrWhiteSpace(startUrl) ? defaultStartUrl ?? "about:blank" : startUrl,
            WebViewDataPathKey: string.IsNullOrWhiteSpace(startUrl) ? webViewDataPathKey : null,
            AppOptions: DesktopAppConfiguration.CreateAppInitializationOptions(argList));
    }
}
