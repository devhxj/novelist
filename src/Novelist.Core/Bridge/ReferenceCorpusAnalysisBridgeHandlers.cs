using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceCorpusAnalysisBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceCorpusAnalysisHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceCorpusAnalysisService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("StartReferenceCorpusFeatureAnalysis", async (context, cancellationToken) =>
            await service.StartFeatureAnalysisAsync(
                ReadObjectArg<StartReferenceCorpusFeatureAnalysisPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetReferenceCorpusFeatureAnalysisRun", async (context, cancellationToken) =>
            await service.GetFeatureAnalysisRunAsync(
                ReadObjectArg<GetReferenceCorpusFeatureAnalysisRunPayload>(context.Payload, 0, "input"),
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
            return JsonSerializer.Deserialize<T>(value.GetRawText(), BridgeJson.SerializerOptions)
                ?? throw Invalid(argumentName, "Value must not be null.");
        }
        catch (JsonException)
        {
            throw Invalid(argumentName, "Value must match the expected object shape.");
        }
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
