using Novelist.Core.Bridge;

namespace Novelist.App.Desktop;

public sealed class PhotinoBridgeRuntimeHost : IBridgeRuntimeHost
{
    private readonly IPhotinoWindow _window;
    private readonly IExternalUrlOpener _externalUrlOpener;

    public PhotinoBridgeRuntimeHost(IPhotinoWindow window, IExternalUrlOpener externalUrlOpener)
    {
        _window = window;
        _externalUrlOpener = externalUrlOpener;
    }

    public ValueTask MinimizeWindowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _window.Minimize();
        return ValueTask.CompletedTask;
    }

    public ValueTask ToggleMaximizeWindowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _window.ToggleMaximize();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsWindowMaximizedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_window.IsMaximized());
    }

    public ValueTask QuitApplicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _window.Close();
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenExternalAsync(Uri url, CancellationToken cancellationToken)
    {
        return _externalUrlOpener.OpenAsync(url, cancellationToken);
    }
}
