using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceAnchoredDraftBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceAnchoredDraftHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceAnchoredDraftService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GenerateReferenceChapterBlueprint", async (context, cancellationToken) =>
            await service.GenerateChapterBlueprintAsync(
                ReadObjectArg<GenerateReferenceChapterBlueprintPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetReferenceChapterBlueprints", async (context, cancellationToken) =>
            await service.GetChapterBlueprintsAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadNullableIntArg(context.Payload, 1, "chapterNumber"),
                cancellationToken));

        dispatcher.Register("GetReferenceChapterBlueprint", async (context, cancellationToken) =>
            await service.GetChapterBlueprintAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "blueprintId"),
                cancellationToken));

        dispatcher.Register("ReviewReferenceChapterBlueprint", async (context, cancellationToken) =>
            await service.ReviewChapterBlueprintAsync(
                ReadObjectArg<ReviewReferenceChapterBlueprintPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("ReviseReferenceChapterBlueprint", async (context, cancellationToken) =>
            await service.ReviseChapterBlueprintAsync(
                ReadObjectArg<ReviseReferenceChapterBlueprintPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("ApproveReferenceChapterBlueprint", async (context, cancellationToken) =>
            await service.ApproveChapterBlueprintAsync(
                ReadObjectArg<ApproveReferenceChapterBlueprintPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("BindReferenceBlueprintMaterials", async (context, cancellationToken) =>
            await service.BindBlueprintMaterialsAsync(
                ReadObjectArg<BindReferenceBlueprintMaterialsPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GenerateReferenceAnchoredDraft", async (context, cancellationToken) =>
            await service.GenerateDraftFromBlueprintAsync(
                ReadObjectArg<GenerateReferenceAnchoredDraftPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("AuditReferenceAnchoredDraft", async (context, cancellationToken) =>
            await service.AuditDraftAgainstBlueprintAsync(
                ReadObjectArg<AuditReferenceAnchoredDraftPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetReferenceAnchoredDraftAudits", async (context, cancellationToken) =>
            await service.GetDraftAuditsAsync(
                ReadObjectArg<GetReferenceAnchoredDraftAuditsPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("StartReferenceOrchestrationRun", async (context, cancellationToken) =>
            await service.StartOrchestrationRunAsync(
                ReadObjectArg<StartReferenceOrchestrationRunPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetReferenceOrchestrationRuns", async (context, cancellationToken) =>
            await service.GetOrchestrationRunsAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadNullableIntArg(context.Payload, 1, "chapterNumber"),
                cancellationToken));

        dispatcher.Register("GetReferenceOrchestrationRun", async (context, cancellationToken) =>
            await service.GetOrchestrationRunAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadStringArg(context.Payload, 1, "runId"),
                cancellationToken));

        dispatcher.Register("GetReferenceOrchestrationRunEvents", async (context, cancellationToken) =>
            await service.GetOrchestrationRunEventsAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadStringArg(context.Payload, 1, "runId"),
                cancellationToken));

        dispatcher.Register("ResumeReferenceOrchestrationRun", async (context, cancellationToken) =>
            await service.ResumeOrchestrationRunAsync(
                ReadObjectArg<ResumeReferenceOrchestrationRunPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("CancelReferenceOrchestrationRun", async (context, cancellationToken) =>
            await service.CancelOrchestrationRunAsync(
                ReadObjectArg<CancelReferenceOrchestrationRunPayload>(context.Payload, 0, "input"),
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

    private static int? ReadNullableIntArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Invalid(argumentName, "Value must be an integer or null.");
        }

        return number;
    }

    private static string ReadStringArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(argumentName, "Value must be a string.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Invalid(argumentName, "Value must not be empty.");
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
