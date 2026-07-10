using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceCorpusWritingBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceCorpusWritingHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceCorpusWritingService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GenerateReferenceCorpusBlueprintCandidates", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<GenerateReferenceCorpusBlueprintCandidatesPayload>(
                context.Payload,
                0,
                "input");
            return await service.GenerateBlueprintCandidatesAsync(input, cancellationToken);
        });

        dispatcher.Register("GenerateReferenceCorpusInsertionDraft", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<GenerateReferenceCorpusInsertionDraftPayload>(
                context.Payload,
                0,
                "input");
            return await service.GenerateInsertionDraftAsync(input, cancellationToken);
        });

        dispatcher.Register("GenerateReferenceCorpusInsertionDraftCandidates", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<GenerateReferenceCorpusInsertionDraftCandidatesPayload>(
                context.Payload,
                0,
                "input");
            return await service.GenerateInsertionDraftCandidatesAsync(input, cancellationToken);
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
