using Novelist.Contracts.Bridge;

namespace Novelist.Core.Bridge;

public interface IBridgeEventSink
{
    ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken);
}

public sealed class NullBridgeEventSink : IBridgeEventSink
{
    public ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
