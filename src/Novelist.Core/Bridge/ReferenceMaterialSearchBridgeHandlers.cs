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
            var input = ReadInput<ListReferenceMaterialsPayload>(context.Payload);
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

        dispatcher.Register("SearchReferenceMaterials", async (context, cancellationToken) =>
        {
            var input = ReadInput<SearchReferenceMaterialsPayload>(context.Payload);
            try
            {
                var result = await service.SearchAsync(
                    new ReferenceMaterialSearchRequest(
                        input.Query,
                        input.MaxResults,
                        input.NovelId,
                        input.SessionId,
                        input.LibraryIds,
                        input.AnchorIds),
                    cancellationToken);
                return result.Select(ToPayload).ToArray();
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

    private static T ReadInput<T>(JsonElement? payload)
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
            return JsonSerializer.Deserialize<T>(
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
            item.Text,
            ToPayload(item.Metadata),
            item.TextHash);

    private static ReferenceMaterialSearchHitPayload ToPayload(ReferenceMaterialSearchHit item) =>
        new(
            item.MaterialId,
            item.GenerationId,
            item.AnchorId,
            item.ChapterIndex,
            item.Ordinal,
            item.Text,
            ToPayload(item.Metadata),
            item.TextHash,
            item.VectorDistance);

    private static ReferenceMaterialMetadataPayload ToPayload(ReferenceMaterialMetadata metadata) =>
        new(
            new ReferenceMaterialSourceSpanPayload(metadata.SourceSpan.StartLine, metadata.SourceSpan.EndLine),
            metadata.SourceKind,
            metadata.Entities.Select(entity => new ReferenceMaterialEntityPayload(entity.Name, entity.Kind)).ToArray(),
            metadata.Setting is null
                ? null
                : new ReferenceMaterialSettingPayload(
                    metadata.Setting.Location,
                    metadata.Setting.Time,
                    metadata.Setting.Environment),
            metadata.Perspective is null
                ? null
                : new ReferenceMaterialPerspectivePayload(metadata.Perspective.Mode, metadata.Perspective.FocusEntity),
            metadata.Event,
            metadata.Facts.Select(fact => new ReferenceMaterialFactPayload(fact.Content, fact.Subject)).ToArray(),
            metadata.Causality is null
                ? null
                : new ReferenceMaterialCausalityPayload(metadata.Causality.Cause, metadata.Causality.Consequence),
            metadata.StateChanges.Select(change => new ReferenceMaterialStateChangePayload(
                change.Subject,
                change.Before,
                change.After)).ToArray(),
            metadata.CharacterDynamics,
            metadata.Conflict is null
                ? null
                : new ReferenceMaterialConflictPayload(metadata.Conflict.Pressure, metadata.Conflict.Cost),
            metadata.Information is null
                ? null
                : new ReferenceMaterialInformationPayload(metadata.Information.Role, metadata.Information.Content),
            metadata.Emotion is null
                ? null
                : new ReferenceMaterialEmotionPayload(metadata.Emotion.Tone, metadata.Emotion.Subtext),
            metadata.NarrativeFunctions,
            metadata.Foreshadowing.Select(item => new ReferenceMaterialForeshadowingPayload(item.Phase, item.Target)).ToArray(),
            metadata.Motifs,
            metadata.ExpressionTechniques,
            metadata.ReuseHint);

    private static BridgeValidationException InvalidInput() =>
        new(
            "Invalid argument 'input'.",
            new Dictionary<string, string> { ["input"] = "Value must match the expected object shape." });
}
