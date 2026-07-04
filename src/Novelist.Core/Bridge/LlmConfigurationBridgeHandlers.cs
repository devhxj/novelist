using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class LlmConfigurationBridgeHandlers
{
    public static BridgeDispatcher RegisterLlmConfigurationHandlers(
        this BridgeDispatcher dispatcher,
        ILlmConfigurationService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetLLMConfig", async (_, cancellationToken) =>
            await service.GetConfigAsync(cancellationToken));

        dispatcher.Register("SaveLLMConfig", async (context, cancellationToken) =>
        {
            await service.SaveConfigAsync(
                ReadObjectArg<LlmConfigViewPayload>(context.Payload, 0, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("GetModels", async (_, cancellationToken) =>
            await service.GetModelsAsync(cancellationToken));

        dispatcher.Register("DiscoverModels", async (context, cancellationToken) =>
            await service.DiscoverModelsAsync(
                ReadStringArg(context.Payload, 0, "baseUrl"),
                ReadStringArg(context.Payload, 1, "apiKey"),
                cancellationToken));

        dispatcher.Register("TestConnection", async (context, cancellationToken) =>
        {
            await service.TestConnectionAsync(
                ReadObjectArg<TestConnectionPayload>(context.Payload, 0, "input"),
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

    private static string ReadStringArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(argumentName, "Value must be a non-empty string.");
        }

        return value.GetString()!;
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
