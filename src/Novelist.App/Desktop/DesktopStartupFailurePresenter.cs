using System.Drawing;
using Photino.NET;

namespace Novelist.App.Desktop;

internal static class DesktopStartupFailurePresenter
{
    private static readonly Size WindowSize = new(560, 360);

    public static void Show(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var window = new PhotinoWindow();
            window
                .SetTitle("Novelist 无法启动")
                .SetChromeless(false)
                .SetUseOsDefaultSize(false)
                .SetSize(WindowSize)
                .SetUseOsDefaultLocation(true)
                .SetResizable(false)
                .RegisterWebMessageReceivedHandler((_, message) =>
                {
                    if (string.Equals(message, "close", StringComparison.Ordinal))
                    {
                        window.Close();
                    }
                })
                .LoadRawString(CreatePage())
                .Center();
            window.WaitForClose();
        }
        catch (Exception presentationException)
        {
            DesktopLaunchLog.Write("Unable to show startup failure page.", presentationException);
        }
    }

    internal static string CreatePage() =>
        """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Novelist 无法启动</title>
          <style>
            :root { color: #17251d; background: #f4f7f3; font-family: "Segoe UI", "Microsoft YaHei", sans-serif; }
            body { align-items: center; display: flex; justify-content: center; margin: 0; min-height: 100vh; }
            main { box-sizing: border-box; max-width: 540px; padding: 36px 42px; width: 100%; }
            .marker { align-items: center; background: #fee2e2; border-radius: 50%; color: #b42318; display: flex; font-size: 22px; font-weight: 700; height: 42px; justify-content: center; width: 42px; }
            h1 { font-size: 24px; font-weight: 650; margin: 18px 0 10px; }
            p, li { color: #425047; font-size: 15px; line-height: 1.65; }
            ol { margin: 14px 0; padding-left: 22px; }
            code { background: #e8eee8; border-radius: 3px; color: #214c32; font-family: Consolas, monospace; font-size: 13px; padding: 2px 4px; }
            .log { border-top: 1px solid #dbe5dc; margin-top: 20px; padding-top: 16px; }
            button { background: #166534; border: 0; border-radius: 4px; color: #fff; cursor: pointer; font: inherit; margin-top: 18px; min-height: 36px; padding: 0 18px; }
            button:hover { background: #14532d; }
            button:focus-visible { outline: 3px solid #86efac; outline-offset: 2px; }
          </style>
        </head>
        <body>
          <main>
            <div class="marker" aria-hidden="true">!</div>
            <h1>Novelist 未能启动</h1>
            <p>应用没有成功打开。请重新启动；若问题仍然存在，请按下面的提示处理。</p>
            <ol>
              <li>开发环境请先运行 <code>npm --prefix frontend run build</code>，再启动桌面应用。</li>
              <li>从安装包启动时，请重新运行安装程序以修复应用文件。</li>
            </ol>
            <p class="log">诊断日志：<code>%LOCALAPPDATA%\Novelist\logs\desktop.log</code></p>
            <button type="button" id="close">关闭</button>
          </main>
          <script>
            document.getElementById('close').addEventListener('click', () => {
              if (window.external && typeof window.external.sendMessage === 'function') {
                window.external.sendMessage('close');
              }
            });
          </script>
        </body>
        </html>
        """;
}
