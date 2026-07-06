using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceAnchorBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceAnchorHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceAnchorService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("CreateReferenceAnchor", async (context, cancellationToken) =>
            await service.CreateAnchorAsync(
                ReadObjectArg<CreateReferenceAnchorPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetReferenceAnchors", async (context, cancellationToken) =>
            await service.GetAnchorsAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                cancellationToken));

        dispatcher.Register("DeleteReferenceAnchor", async (context, cancellationToken) =>
        {
            await service.DeleteAnchorAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "anchorId"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteReferenceAnchors", async (context, cancellationToken) =>
        {
            await service.DeleteAnchorsAsync(
                ReadObjectArg<DeleteReferenceAnchorsPayload>(context.Payload, 0, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteReferenceMaterials", async (context, cancellationToken) =>
        {
            await service.DeleteMaterialsAsync(
                ReadObjectArg<DeleteReferenceMaterialsPayload>(context.Payload, 0, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("PromoteReferenceAnchorToWorkspaceCorpus", async (context, cancellationToken) =>
            await service.PromoteAnchorToWorkspaceCorpusAsync(
                ReadObjectArg<PromoteReferenceAnchorToWorkspaceCorpusPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("PromoteReferenceAnchorsToWorkspaceCorpus", async (context, cancellationToken) =>
            await service.PromoteAnchorsToWorkspaceCorpusAsync(
                ReadObjectArg<PromoteReferenceAnchorsToWorkspaceCorpusPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("UpdateReferenceAnchorMetadata", async (context, cancellationToken) =>
            await service.UpdateAnchorMetadataAsync(
                ReadObjectArg<UpdateReferenceAnchorMetadataPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("RebuildReferenceAnchor", async (context, cancellationToken) =>
            await service.RebuildAnchorAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "anchorId"),
                cancellationToken));

        dispatcher.Register("GetReferenceAnchorBuildStatus", async (context, cancellationToken) =>
            await service.GetBuildStatusAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "anchorId"),
                cancellationToken));

        dispatcher.Register("SearchReferenceMaterials", async (context, cancellationToken) =>
            await service.SearchMaterialsAsync(
                ReadObjectArg<SearchReferenceMaterialsPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("UpdateReferenceMaterialTags", async (context, cancellationToken) =>
            await service.UpdateMaterialTagsAsync(
                ReadObjectArg<UpdateReferenceMaterialTagsPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("UpdateReferenceMaterialsTags", async (context, cancellationToken) =>
            await service.UpdateMaterialsTagsAsync(
                ReadObjectArg<UpdateReferenceMaterialsTagsPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("AdaptReferenceMaterial", async (context, cancellationToken) =>
            await service.AdaptMaterialAsync(
                ReadObjectArg<AdaptReferenceMaterialPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("AuditReferenceReuse", async (context, cancellationToken) =>
            await service.AuditCandidateAsync(
                ReadObjectArg<AuditReferenceReusePayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("RecordReferenceUserFeedback", async (context, cancellationToken) =>
            await service.RecordUserFeedbackAsync(
                ReadObjectArg<RecordReferenceUserFeedbackPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetReferenceUserFeedback", async (context, cancellationToken) =>
            await service.GetUserFeedbackAsync(
                ReadObjectArg<GetReferenceUserFeedbackPayload>(context.Payload, 0, "input"),
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
