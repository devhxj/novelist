using System.Text.Json;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.App.Desktop;

public sealed class PhotinoBridgeEventSink : IBridgeEventSink
{
    private readonly IPhotinoWindow _window;

    public PhotinoBridgeEventSink(IPhotinoWindow window)
    {
        _window = window;
    }

    public ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = JsonSerializer.Serialize(
            BridgeOutboundEvent.Create(name, payload),
            BridgeJson.SerializerOptions);
        _window.SendWebMessage(message);
        return ValueTask.CompletedTask;
    }
}
