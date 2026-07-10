using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ChatSessionBridgeHandlers
{
    public static BridgeDispatcher RegisterChatSessionHandlers(
        this BridgeDispatcher dispatcher,
        IChatSessionService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetSessions", async (context, cancellationToken) =>
            await service.GetSessionsAsync(
                ReadObjectArg<GetSessionsPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetSession", async (context, cancellationToken) =>
            await service.GetSessionAsync(
                ReadStringArg(context.Payload, 0, "sessionId", allowEmpty: false),
                cancellationToken));

        dispatcher.Register("GetSessionMessages", async (context, cancellationToken) =>
            await service.GetSessionMessagesAsync(
                ReadStringArg(context.Payload, 0, "sessionId", allowEmpty: false),
                cancellationToken));

        dispatcher.Register("Chat", async (context, cancellationToken) =>
            await service.ChatAsync(
                ReadObjectArg<ChatInputPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("CompressContext", async (context, cancellationToken) =>
            await service.CompressContextAsync(
                ReadObjectArg<CompressInputPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("CancelChat", async (context, cancellationToken) =>
        {
            await service.CancelChatAsync(
                ReadStringArg(context.Payload, 0, "sessionId", allowEmpty: true),
                cancellationToken);
            return null;
        });

        return dispatcher;
    }

    private static T ReadObjectArg<T>(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(argumentName, "Value must be an object.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value.GetRawText(), BridgeJson.SerializerOptions)
                ?? throw Invalid(argumentName, "Value must not be null.");
        }
        catch (JsonException)
        {
            throw Invalid(argumentName, "Value must match the expected object shape.");
        }
    }

    private static string ReadStringArg(
        JsonElement? payload,
        int index,
        string argumentName,
        bool allowEmpty)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(argumentName, "Value must be a string.");
        }

        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw Invalid(argumentName, "Value must be a non-empty string.");
        }

        return text;
    }

    private static JsonElement ReadArg(JsonElement? payload, int index, string argumentName)
    {
        if (payload is null ||
            payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("args", out var args) ||
            args.ValueKind != JsonValueKind.Array ||
            args.GetArrayLength() <= index)
        {
            throw Invalid(argumentName, $"Argument at index {index} is required.");
        }

        return args[index];
    }

    private static BridgeValidationException Invalid(string argumentName, string message)
    {
        return new BridgeValidationException(
            $"Invalid argument '{argumentName}'.",
            new Dictionary<string, string> { [argumentName] = message });
    }
}
