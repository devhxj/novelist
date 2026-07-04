namespace Novelist.App.Desktop;

internal static class DesktopLaunchLog
{
    private static readonly object Lock = new();

    public static void Write(string message, Exception? exception = null)
    {
        foreach (var root in CandidateLogRoots())
        {
            try
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "desktop.log");
                var line = $"{DateTimeOffset.Now:O} {message}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                lock (Lock)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }

                return;
            }
            catch
            {
                // Try the next location.
            }
        }
    }

    private static IEnumerable<string> CandidateLogRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Novelist", "logs");
        }

        yield return Path.Combine(Path.GetTempPath(), "Novelist", "logs");
    }
}
