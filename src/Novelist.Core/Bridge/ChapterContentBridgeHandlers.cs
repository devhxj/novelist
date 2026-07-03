using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ChapterContentBridgeHandlers
{
    public static BridgeDispatcher RegisterChapterContentHandlers(
        this BridgeDispatcher dispatcher,
        IChapterContentService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetChapters", async (context, cancellationToken) =>
            await service.GetChaptersAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("GetMaxChapterNumber", async (context, cancellationToken) =>
            await service.GetMaxChapterNumberAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("CreateChapter", async (context, cancellationToken) =>
            await service.CreateChapterAsync(
                ReadObjectArg<CreateChapterPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("UpdateChapterTitle", async (context, cancellationToken) =>
        {
            await service.UpdateChapterTitleAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadIntArg(context.Payload, 1, "chapterNumber"),
                ReadStringArg(context.Payload, 2, "title", allowEmpty: false),
                cancellationToken);
            return null;
        });

        dispatcher.Register("GetContent", async (context, cancellationToken) =>
            await service.GetContentAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadStringArg(context.Payload, 1, "path", allowEmpty: false),
                cancellationToken));

        dispatcher.Register("SaveContent", async (context, cancellationToken) =>
        {
            await service.SaveContentAsync(
                ReadObjectArg<SaveContentPayload>(context.Payload, 0, "input"),
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

    private static int ReadIntArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Invalid(argumentName, "Value must be an integer.");
        }

        return number;
    }

    private static string ReadStringArg(JsonElement? payload, int index, string argumentName, bool allowEmpty)
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
