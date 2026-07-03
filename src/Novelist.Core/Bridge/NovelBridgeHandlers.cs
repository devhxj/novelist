using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class NovelBridgeHandlers
{
    public static BridgeDispatcher RegisterNovelHandlers(
        this BridgeDispatcher dispatcher,
        INovelService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetNovels", async (_, cancellationToken) =>
            await service.GetNovelsAsync(cancellationToken));

        dispatcher.Register("CreateNovel", async (context, cancellationToken) =>
            await service.CreateNovelAsync(ReadObjectArg<CreateNovelPayload>(context.Payload, 0, "input"), cancellationToken));

        dispatcher.Register("UpdateNovel", async (context, cancellationToken) =>
            await service.UpdateNovelAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadObjectArg<UpdateNovelPayload>(context.Payload, 1, "input"),
                cancellationToken));

        dispatcher.Register("DeleteNovel", async (context, cancellationToken) =>
        {
            await service.DeleteNovelAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken);
            return null;
        });

        dispatcher.Register("SetActiveNovel", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<SetActiveNovelPayload>(context.Payload, 0, "input");
            await service.SetActiveNovelAsync(input.NovelId, cancellationToken);
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
