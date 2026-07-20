using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceMaterialSearchBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceMaterialSearchHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceMaterialSearch service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("ListReferenceMaterials", async (context, cancellationToken) =>
        {
            var input = ReadInput(context.Payload);
            try
            {
                var result = await service.ListAsync(
                    new ReferenceMaterialListRequest(input.NovelId, input.AnchorId, input.Page, input.Size),
                    cancellationToken);
                return new PageResultPayload<ReferenceMaterialListItemPayload>(
                    result.Items.Select(ToPayload).ToArray(),
                    result.Total,
                    result.Page,
                    result.Size,
                    result.TotalPages);
            }
            catch (ReferenceMaterializationException exception)
            {
                throw new BridgeRequestException(
                    exception.ErrorCode,
                    exception.Message,
                    new { error_code = exception.ErrorCode },
                    retryable: false);
            }
        });

        return dispatcher;
    }

    private static ListReferenceMaterialsPayload ReadInput(JsonElement? payload)
    {
        if (payload is null ||
            payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("args", out var args) ||
            args.ValueKind != JsonValueKind.Array ||
            args.GetArrayLength() == 0 ||
            args[0].ValueKind != JsonValueKind.Object)
        {
            throw InvalidInput();
        }

        try
        {
            return JsonSerializer.Deserialize<ListReferenceMaterialsPayload>(
                args[0].GetRawText(),
                BridgeJson.SerializerOptions) ?? throw InvalidInput();
        }
        catch (JsonException)
        {
            throw InvalidInput();
        }
    }

    private static ReferenceMaterialListItemPayload ToPayload(ReferenceMaterialListItem item) =>
        new(
            item.MaterialId,
            item.GenerationId,
            item.AnchorId,
            item.ChapterIndex,
            item.Ordinal,
            item.MaterialType,
            item.Text,
            item.Description,
            item.Tags,
            item.TextHash);

    private static BridgeValidationException InvalidInput() =>
        new(
            "Invalid argument 'input'.",
            new Dictionary<string, string> { ["input"] = "Value must match the expected object shape." });
}
