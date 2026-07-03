namespace Novelist.Core.Bridge;

public interface IBridgeRuntimeHost
{
    ValueTask MinimizeWindowAsync(CancellationToken cancellationToken);

    ValueTask ToggleMaximizeWindowAsync(CancellationToken cancellationToken);

    ValueTask<bool> IsWindowMaximizedAsync(CancellationToken cancellationToken);

    ValueTask QuitApplicationAsync(CancellationToken cancellationToken);

    ValueTask OpenExternalAsync(Uri url, CancellationToken cancellationToken);
}
