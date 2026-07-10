namespace Novelist.App.Desktop;

internal static class DesktopApplicationEntryPoint
{
    public static void Run(
        string[] args,
        Action<string[]> runDesktopApplication,
        Action<Exception> presentFatalStartupError,
        Action<string, Exception?>? writeLog = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runDesktopApplication);
        ArgumentNullException.ThrowIfNull(presentFatalStartupError);
        writeLog ??= DesktopLaunchLog.Write;

        try
        {
            writeLog("Launching desktop mode", null);
            runDesktopApplication(args);
            writeLog("Desktop mode exited normally", null);
        }
        catch (Exception exception)
        {
            writeLog("Fatal startup exception", exception);
            presentFatalStartupError(exception);
        }
    }
}
