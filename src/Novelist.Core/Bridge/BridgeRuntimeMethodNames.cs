namespace Novelist.Core.Bridge;

public static class BridgeRuntimeMethodNames
{
    public const string WindowMinimize = "runtime.window.minimize";
    public const string WindowToggleMaximize = "runtime.window.toggleMaximize";
    public const string WindowIsMaximized = "runtime.window.isMaximized";
    public const string AppQuit = "runtime.app.quit";
    public const string ShellOpenExternal = "runtime.shell.openExternal";

    public static IReadOnlyList<string> All { get; } =
    [
        WindowMinimize,
        WindowToggleMaximize,
        WindowIsMaximized,
        AppQuit,
        ShellOpenExternal
    ];
}
