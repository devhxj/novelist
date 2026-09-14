using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

// 高级写作素材（L2）只读 + 复核 bridge：List / Get / Review 三件套。
public static class ReferenceAdvancedMaterialBridgeHandlers
{
    private static readonly PageRequestPolicy ListPagePolicy = new(
        AllowedSortFields: ["created_at", "confidence", "family", "layer", "material_id"],
        DefaultSortBy: "created_at",
        StableTieBreakers: ["material_id"]);

    public static BridgeDispatcher RegisterReferenceAdvancedMaterialHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceAdvancedMaterialService service,
        IReferenceAdvancedMaterialPipelineService pipeline)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pipeline);

        // 生产触发入口：单本书一趟走完观测→机理→策略（LLM 长任务，前端按长超时调用）。
        dispatcher.Register("StartReferenceAdvancedMaterialAnalysis", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<StartReferenceAdvancedMaterialAnalysisPayload>(context.Payload, 0, "input");
            var runId = string.IsNullOrWhiteSpace(input.RunId)
                ? $"advm-{Guid.NewGuid():N}"
                : input.RunId!;
            try
            {
                var result = await pipeline.ProcessAnchorAsync(input.AnchorId, runId, cancellationToken);
                return new ReferenceAdvancedMaterialPipelinePayload(
                    runId,
                    result.ObservationAccepted,
                    result.ObservationRejected,
                    result.SpecimenAccepted,
                    result.SpecimenRejected,
                    result.StrategyGroups);
            }
            catch (ArgumentException exception)
            {
                throw Invalid("input", exception.Message);
            }
        });

        dispatcher.Register("ListReferenceAdvancedMaterials", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<ListReferenceAdvancedMaterialsPayload>(context.Payload, 0, "input");
            try
            {
                PageRequestNormalizer.Normalize(
                    input.PageRequest ?? new PageRequestPayload(null, 50, "created_at", "desc"),
                    ListPagePolicy);
            }
            catch (PageRequestValidationException exception)
            {
                throw Invalid("page_request", $"{exception.Code}: {exception.Message}");
            }

            try
            {
                return await service.ListAsync(input, cancellationToken);
            }
            catch (ArgumentException exception)
            {
                throw Invalid("input", exception.Message);
            }
        });

        dispatcher.Register("GetReferenceAdvancedMaterialDetail", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<GetReferenceAdvancedMaterialDetailPayload>(context.Payload, 0, "input");
            try
            {
                return await service.GetAsync(input, cancellationToken);
            }
            catch (ArgumentException exception)
            {
                throw Invalid("input", exception.Message);
            }
        });

        dispatcher.Register("ReviewReferenceAdvancedMaterial", async (context, cancellationToken) =>
        {
            var input = ReadObjectArg<ReviewReferenceAdvancedMaterialPayload>(context.Payload, 0, "input");
            try
            {
                return await service.ReviewAsync(input, cancellationToken);
            }
            catch (ArgumentException exception)
            {
                throw Invalid("input", exception.Message);
            }
            catch (KeyNotFoundException exception)
            {
                throw Invalid("material_id", exception.Message);
            }
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
