using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class WorldEntityBridgeHandlers
{
    public static BridgeDispatcher RegisterWorldEntityHandlers(
        this BridgeDispatcher dispatcher,
        IWorldEntityService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetCharacters", async (context, cancellationToken) =>
            await service.GetCharactersAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("GetCharacterRelations", async (context, cancellationToken) =>
            await service.GetCharacterRelationsAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("CreateCharacter", async (context, cancellationToken) =>
            await service.CreateCharacterAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadObjectArg<CreateCharacterPayload>(context.Payload, 1, "input"),
                cancellationToken));

        dispatcher.Register("UpdateCharacter", async (context, cancellationToken) =>
        {
            await service.UpdateCharacterAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "characterId"),
                ReadObjectArg<UpdateCharacterPayload>(context.Payload, 2, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteCharacter", async (context, cancellationToken) =>
        {
            await service.DeleteCharacterAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "characterId"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("GetLocations", async (context, cancellationToken) =>
            await service.GetLocationsAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("GetLocationRelations", async (context, cancellationToken) =>
            await service.GetLocationRelationsAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("CreateLocation", async (context, cancellationToken) =>
            await service.CreateLocationAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadObjectArg<CreateLocationPayload>(context.Payload, 1, "input"),
                cancellationToken));

        dispatcher.Register("UpdateLocation", async (context, cancellationToken) =>
        {
            await service.UpdateLocationAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "locationId"),
                ReadObjectArg<UpdateLocationPayload>(context.Payload, 2, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteLocation", async (context, cancellationToken) =>
        {
            await service.DeleteLocationAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "locationId"),
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

    private static long ReadLongArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw Invalid(argumentName, "Value must be an integer.");
        }

        return number;
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
