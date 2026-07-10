using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceCorpusAnalysisBridgeHandlers
{
    private static readonly PageRequestPolicy ObservationPagePolicy = new(
        AllowedSortFields: ["created_at", "feature_family", "confidence", "observation_id"],
        DefaultSortBy: "created_at",
        StableTieBreakers: ["created_at", "observation_id"]);

    private static readonly PageRequestPolicy TechniqueSpecimenPagePolicy = new(
        AllowedSortFields: ["created_at", "technique_family", "confidence", "specimen_id"],
        DefaultSortBy: "created_at",
        StableTieBreakers: ["created_at", "specimen_id"]);

    private static readonly HashSet<string> ObservationFilterKeys = new(StringComparer.Ordinal)
    {
        "feature_family",
        "feature_key",
        "node_type",
        "review_state",
        "validity_state",
        "run_id",
        "min_confidence"
    };

    private static readonly HashSet<string> TechniqueSpecimenFilterKeys = new(StringComparer.Ordinal)
    {
        "technique_family",
        "review_state",
        "validity_state",
        "run_id",
        "min_confidence"
    };

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

        dispatcher.Register("StartReferenceCorpusTechniqueSpecimenAnalysis", async (context, cancellationToken) =>
            await service.StartTechniqueSpecimenAnalysisAsync(
                ReadObjectArg<StartReferenceCorpusTechniqueSpecimenAnalysisPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetReferenceCorpusTechniqueSpecimenAnalysisRun", async (context, cancellationToken) =>
            await service.GetTechniqueSpecimenAnalysisRunAsync(
                ReadObjectArg<GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("ListReferenceCorpusFeatureObservations", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<ListReferenceCorpusFeatureObservationsPayload>(context.Payload, 0, "input");
            try
            {
                ValidatePageRequest(input.PageRequest, ObservationPagePolicy, ObservationFilterKeys);
                return await service.ListFeatureObservationsAsync(input, cancellationToken);
            }
            catch (PageRequestValidationException exception)
            {
                throw Invalid("page_request", $"{exception.Code}: {exception.Message}");
            }
        });

        dispatcher.Register("ListReferenceCorpusTechniqueSpecimens", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<ListReferenceCorpusTechniqueSpecimensPayload>(context.Payload, 0, "input");
            try
            {
                ValidatePageRequest(input.PageRequest, TechniqueSpecimenPagePolicy, TechniqueSpecimenFilterKeys);
                return await service.ListTechniqueSpecimensAsync(input, cancellationToken);
            }
            catch (PageRequestValidationException exception)
            {
                throw Invalid("page_request", $"{exception.Code}: {exception.Message}");
            }
        });

        return dispatcher;
    }

    private static void ValidatePageRequest(
        PageRequestPayload pageRequest,
        PageRequestPolicy policy,
        IReadOnlySet<string> allowedFilters)
    {
        try
        {
            var normalized = PageRequestNormalizer.Normalize(pageRequest, policy);
            foreach (var filterKey in normalized.Filters.Keys)
            {
                if (!allowedFilters.Contains(filterKey))
                {
                    throw new PageRequestValidationException(
                        PageRequestErrorCodes.InvalidFilterKey,
                        $"filter key '{filterKey}' is not supported.");
                }
            }
        }
        catch (PageRequestValidationException exception)
        {
            throw Invalid("page_request", $"{exception.Code}: {exception.Message}");
        }
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
