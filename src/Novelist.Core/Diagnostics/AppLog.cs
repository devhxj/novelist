namespace Novelist.Core.Diagnostics;

public enum AppLogLevel
{
    Info,
    Warning,
    Error,
}

public interface IAppLogSink
{
    void Write(AppLogLevel level, string message, Exception? exception);
}

/// <summary>
/// 全局应用日志入口。默认无 sink（静默丢弃）。
/// 宿主启动时通过 <see cref="Use"/> 接入实际输出渠道，让 Release 构建里
/// 未处理的异常也留有可查的落地记录（U2：诊断不得只存在于 Debug 输出）。
/// </summary>
public static class AppLog
{
    private static IAppLogSink? _sink;

    public static void Use(IAppLogSink? sink)
    {
        _sink = sink;
    }

    public static void Info(string message)
    {
        Write(AppLogLevel.Info, message, null);
    }

    public static void Warning(string message, Exception? exception = null)
    {
        Write(AppLogLevel.Warning, message, exception);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write(AppLogLevel.Error, message, exception);
    }

    private static void Write(AppLogLevel level, string message, Exception? exception)
    {
        _sink?.Write(level, message, exception);
    }
}
