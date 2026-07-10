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

        DesktopApplicationEntryPoint.Run(
            args,
            static applicationArgs => new PhotinoDesktopApplication(new PhotinoWindowFactory()).Run(applicationArgs),
            DesktopStartupFailurePresenter.Show);
    }
}
