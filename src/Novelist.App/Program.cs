using Novelist.App.Desktop;
using Novelist.Core.Diagnostics;

public partial class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 先接日志 sink：后续所有 AppLog 输出（含桥接层未处理异常）在 Release 构建里也要落地。
        AppLog.Use(new DesktopAppLogSink());

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
