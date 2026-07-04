using Novelist.App.Desktop;

public partial class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            DesktopLaunchLog.Write(
                "Unhandled exception",
                eventArgs.ExceptionObject as Exception);
        };

        try
        {
            DesktopLaunchLog.Write("Launching desktop mode");
            var desktopApplication = new PhotinoDesktopApplication(new PhotinoWindowFactory());
            desktopApplication.Run(args);
            DesktopLaunchLog.Write("Desktop mode exited normally");
        }
        catch (Exception exception)
        {
            DesktopLaunchLog.Write("Fatal startup exception", exception);
            throw;
        }
    }
}
