using System.Diagnostics;
using Novelist.Core.Bridge;

namespace Novelist.App.Desktop;

public sealed class PhotinoWebMessageBridge
{
    private readonly BridgeDispatcher _dispatcher;
    private readonly IPhotinoWindow _window;

    public PhotinoWebMessageBridge(BridgeDispatcher dispatcher, IPhotinoWindow window)
    {
        _dispatcher = dispatcher;
        _window = window;
    }

    public void Post(string message)
    {
        _ = ReceiveAsync(message)
            .AsTask()
            .ContinueWith(
                task => Debug.WriteLine(task.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    public async ValueTask ReceiveAsync(string message, CancellationToken cancellationToken = default)
    {
        var result = await _dispatcher.DispatchAsync(message, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.OutboundJson))
        {
            _window.SendWebMessage(result.OutboundJson);
        }
    }
}
