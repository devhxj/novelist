using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.App.Desktop;

public sealed class PhotinoWebMessageBridge
{
    private const int MaxPreStartCancellationIds = 1024;

    private readonly BridgeDispatcher _dispatcher;
    private readonly IPhotinoWindow _window;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _cancelledBeforeStart = new(StringComparer.Ordinal);

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
        var envelope = TryReadEnvelope(message);
        if (string.Equals(envelope.Kind, "cancel", StringComparison.Ordinal))
        {
            var cancelResult = await _dispatcher.DispatchAsync(message, cancellationToken);
            if (!string.IsNullOrWhiteSpace(cancelResult.CancelRequestId))
            {
                CancelPendingRequest(cancelResult.CancelRequestId);
            }

            if (!string.IsNullOrWhiteSpace(cancelResult.OutboundJson))
            {
                _window.SendWebMessage(cancelResult.OutboundJson);
            }

            return;
        }

        if (!string.Equals(envelope.Kind, "request", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(envelope.Id))
        {
            var passthroughResult = await _dispatcher.DispatchAsync(message, cancellationToken);
            if (!string.IsNullOrWhiteSpace(passthroughResult.OutboundJson))
            {
                _window.SendWebMessage(passthroughResult.OutboundJson);
            }

            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_pendingRequests.TryAdd(envelope.Id, linkedCancellation))
        {
            _window.SendWebMessage(DuplicateRequestResponse(envelope.Id));
            return;
        }

        if (_cancelledBeforeStart.TryRemove(envelope.Id, out _))
        {
            linkedCancellation.Cancel();
        }

        BridgeDispatchResult result;
        try
        {
            result = await _dispatcher.DispatchAsync(message, linkedCancellation.Token);
        }
        finally
        {
            _pendingRequests.TryRemove(envelope.Id, out _);
        }

        if (!string.IsNullOrWhiteSpace(result.OutboundJson))
        {
            _window.SendWebMessage(result.OutboundJson);
        }
    }

    private void CancelPendingRequest(string requestId)
    {
        if (_pendingRequests.TryRemove(requestId, out var pending))
        {
            pending.Cancel();
            return;
        }

        if (_cancelledBeforeStart.Count >= MaxPreStartCancellationIds)
        {
            _cancelledBeforeStart.Clear();
        }

        _cancelledBeforeStart.TryAdd(requestId, 0);
    }

    private static string DuplicateRequestResponse(string requestId)
    {
        return JsonSerializer.Serialize(
            BridgeResponse.Failure(
                requestId,
                new BridgeError(
                    BridgeErrorCodes.ValidationError,
                    "Bridge request id is already in progress.")),
            BridgeJson.SerializerOptions);
    }

    private static BridgeEnvelope TryReadEnvelope(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new BridgeEnvelope(null, null);
            }

            var kind = root.TryGetProperty("kind", out var kindElement) &&
                kindElement.ValueKind == JsonValueKind.String
                    ? kindElement.GetString()
                    : null;
            var id = root.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;
            return new BridgeEnvelope(kind, id);
        }
        catch (JsonException)
        {
            return new BridgeEnvelope(null, null);
        }
    }

    private sealed record BridgeEnvelope(string? Kind, string? Id);
}
