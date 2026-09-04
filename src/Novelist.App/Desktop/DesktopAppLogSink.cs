using Novelist.Core.Diagnostics;

namespace Novelist.App.Desktop;

/// <summary>
/// 把 <see cref="AppLog"/> 写入桌面的 desktop.log。
/// Debug.WriteLine 在 Release 构建中被编译掉，这里保证发布版本同样有日志。
/// </summary>
internal sealed class DesktopAppLogSink : IAppLogSink
{
    public void Write(AppLogLevel level, string message, Exception? exception)
    {
        DesktopLaunchLog.Write($"[{level.ToString().ToUpperInvariant()}] {message}", exception);
    }
}
