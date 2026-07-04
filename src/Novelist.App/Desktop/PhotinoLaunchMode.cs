namespace Novelist.App.Desktop;

public static class PhotinoLaunchMode
{
    public const string DesktopFlag = "--desktop";
    public const string ServerFlag = "--server";
    public const string StartUrlPrefix = "--start-url=";

    public static bool ShouldLaunchDesktop(IEnumerable<string> args)
    {
        return !args.Any(arg => string.Equals(arg, ServerFlag, StringComparison.OrdinalIgnoreCase));
    }

    public static PhotinoWindowSettings CreateSettings(IEnumerable<string> args, string? defaultStartUrl = null)
    {
        var startUrl = args
            .FirstOrDefault(arg => arg.StartsWith(StartUrlPrefix, StringComparison.OrdinalIgnoreCase))?
            .Substring(StartUrlPrefix.Length);

        return new PhotinoWindowSettings(
            Title: "novelist",
            Width: 1280,
            Height: 840,
            StartUrl: string.IsNullOrWhiteSpace(startUrl) ? defaultStartUrl ?? "about:blank" : startUrl);
    }
}
