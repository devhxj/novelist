using System.Text.Json;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public sealed class BridgeDispatcher
{
    private readonly Dictionary<string, BridgeMethodHandler> _handlers = new(StringComparer.Ordinal);

    public void Register(string method, BridgeMethodHandler handler)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Bridge method name is required.", nameof(method));
        }

        ArgumentNullException.ThrowIfNull(handler);
        _handlers[method] = handler;
    }

    public async ValueTask<BridgeDispatchResult> DispatchAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Error(null, BridgeErrorCodes.InvalidMessage, "Bridge message is required.");
        }

        using var document = TryParse(message, out var parseError);
        if (document is null)
        {
            return Error(null, BridgeErrorCodes.InvalidMessage, parseError ?? "Bridge message must be valid JSON.");
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Error(null, BridgeErrorCodes.InvalidMessage, "Bridge message must be a JSON object.");
        }

        var kind = ReadString(root, "kind");
        return kind switch
        {
            BridgeMessageKinds.Request => await DispatchRequestAsync(root, cancellationToken),
            BridgeMessageKinds.Cancel => DispatchCancel(root),
            _ => Error(ReadString(root, "id"), BridgeErrorCodes.InvalidMessage, "Bridge message kind is not supported.")
        };
    }

    private async ValueTask<BridgeDispatchResult> DispatchRequestAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var id = ReadString(root, "id");
        var method = ReadString(root, "method");

        if (string.IsNullOrWhiteSpace(id))
        {
            return Error(null, BridgeErrorCodes.InvalidMessage, "Bridge request id is required.");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            return Error(id, BridgeErrorCodes.ValidationError, "Bridge request method is required.");
        }

        if (!_handlers.TryGetValue(method, out var handler))
        {
            return Error(id, BridgeErrorCodes.MethodNotFound, $"Bridge method '{method}' is not registered.");
        }

        try
        {
            var context = new BridgeInvocationContext(
                id,
                method,
                TryReadProperty(root, "payload", out var payload) ? payload : null,
                ReadDeadline(root));
            var result = await handler(context, cancellationToken);
            return Serialize(BridgeResponse.Success(id, result));
        }
        catch (BridgeValidationException ex)
        {
            return Error(id, BridgeErrorCodes.ValidationError, ex.Message, ex.Details);
        }
        catch (BridgeRequestException ex)
        {
            return Error(id, ex.Code, ex.Message, ex.Details, ex.Retryable);
        }
        catch (AppNotInitializedException ex)
        {
            return Error(id, BridgeErrorCodes.AppNotInitialized, ex.Message);
        }
        catch (InvalidContentPathException ex)
        {
            return Error(id, BridgeErrorCodes.InvalidPath, ex.Message, ex.Details);
        }
        catch (ArgumentException ex)
        {
            return Error(id, BridgeErrorCodes.ValidationError, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error(id, BridgeErrorCodes.Cancelled, "Bridge request was cancelled.");
        }
        catch
        {
            return Error(id, BridgeErrorCodes.InternalError, "Internal bridge error.");
        }
    }

    private static BridgeDispatchResult DispatchCancel(JsonElement root)
    {
        var id = ReadString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return Error(null, BridgeErrorCodes.InvalidMessage, "Bridge cancel id is required.");
        }

        return BridgeDispatchResult.Cancel(id);
    }

    private static JsonDocument? TryParse(string message, out string? error)
    {
        try
        {
            error = null;
            return JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            error = "Bridge message must be valid JSON.";
            return null;
        }
    }

    private static TimeSpan? ReadDeadline(JsonElement root)
    {
        if (!TryReadProperty(root, "deadline_ms", out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return TryReadProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryReadProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        return root.TryGetProperty(propertyName, out value);
    }

    private static BridgeDispatchResult Error(
        string? id,
        string code,
        string message,
        object? details = null,
        bool retryable = false)
    {
        return Serialize(BridgeResponse.Failure(id, new BridgeError(code, message, details, retryable)));
    }

    private static BridgeDispatchResult Serialize(BridgeResponse response)
    {
        return BridgeDispatchResult.Outbound(JsonSerializer.Serialize(response, BridgeJson.SerializerOptions));
    }
}
