using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ChapterCorpusBridgeHandlers
{
    public static BridgeDispatcher RegisterChapterCorpusHandlers(
        this BridgeDispatcher dispatcher,
        IChapterCorpusCoverageService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetChapterCorpusCoverage", async (context, cancellationToken) =>
            await service.ComputeCoverageAsync(
                ReadObjectArg<GetChapterCorpusCoveragePayload>(context.Payload, 0, "input"),
                cancellationToken));

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
            return value.Deserialize<T>(BridgeJson.SerializerOptions)
                ?? throw Invalid(argumentName, "Value must be a valid object.");
        }
        catch (JsonException)
        {
            throw Invalid(argumentName, "Value must match the expected object shape.");
        }
    }

    private static JsonElement ReadArg(JsonElement? payload, int index, string argumentName)
    {
        if (payload is not { ValueKind: JsonValueKind.Object })
        {
            throw Invalid(argumentName, "Payload must be an object.");
        }

        if (!payload.Value.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(argumentName, "Payload must contain an args array.");
        }

        if (index < 0 || index >= args.GetArrayLength())
        {
            throw Invalid(argumentName, $"Argument index {index} is out of range.");
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
