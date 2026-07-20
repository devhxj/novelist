using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceAnchorBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceAnchorHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceAnchorService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("RegisterReferenceMaterializationSource", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeAnchor(
                await service.RegisterMaterializationSourceAsync(
                    ReadObjectArg<CreateReferenceAnchorPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetReferenceAnchors", async (context, cancellationToken) =>
            (await service.GetAnchorsAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                cancellationToken))
            .Select(ReferencePayloadSanitizer.SanitizeAnchor)
            .ToArray());

        dispatcher.Register("DeleteReferenceAnchor", async (context, cancellationToken) =>
        {
            await service.DeleteAnchorAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "anchorId"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteReferenceAnchors", async (context, cancellationToken) =>
        {
            await service.DeleteAnchorsAsync(
                ReadObjectArg<DeleteReferenceAnchorsPayload>(context.Payload, 0, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("UpdateReferenceAnchorMetadata", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeAnchor(
                await service.UpdateAnchorMetadataAsync(
                    ReadObjectArg<UpdateReferenceAnchorMetadataPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

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

    private static BridgeValidationException Invalid(string argumentName, string message) =>
        new(
            $"Invalid argument '{argumentName}'.",
            new Dictionary<string, string> { [argumentName] = message });
}
