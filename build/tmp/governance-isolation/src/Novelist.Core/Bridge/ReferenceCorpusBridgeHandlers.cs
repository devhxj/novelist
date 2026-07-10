using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceCorpusBridgeHandlers
{
    private static readonly PageRequestPolicy CandidateSearchPagePolicy = new(
        AllowedSortFields: ["score", "created_at", "candidate_id"],
        DefaultSortBy: "score",
        StableTieBreakers: ["created_at", "candidate_id"]);

    public static BridgeDispatcher RegisterReferenceCorpusHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceCorpusService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("SearchReferenceCorpusCandidates", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<SearchReferenceCorpusCandidatesPayload>(context.Payload, 0, "input");
            try
            {
                PageRequestNormalizer.Normalize(input.PageRequest, CandidateSearchPagePolicy);
            }
            catch (PageRequestValidationException exception)
            {
                throw Invalid("page_request", $"{exception.Code}: {exception.Message}");
            }

            return await service.SearchCandidatesAsync(input, cancellationToken);
        });

        dispatcher.Register("BackfillReferenceCorpusTechniqueVectorIndex", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<BackfillReferenceCorpusTechniqueVectorIndexPayload>(context.Payload, 0, "input");
            return await service.BackfillTechniqueVectorIndexAsync(input, cancellationToken);
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
