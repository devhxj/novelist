using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceMaterializationBlueprintPreviewBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceMaterializationBlueprintPreviewHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceMaterializationBlueprintPreviewService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GenerateReferenceMaterializationBlueprintPreview", async (context, cancellationToken) =>
            await ExecuteAsync(() => service.GenerateAsync(
                ReadObjectArg<GenerateReferenceMaterializationBlueprintPreviewPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("GetReferenceMaterializationBlueprintPreview", async (context, cancellationToken) =>
            await ExecuteOptionalAsync(() => service.GetAsync(
                ReadObjectArg<GetReferenceMaterializationBlueprintPreviewPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        return dispatcher;
    }

    private static async ValueTask<ReferenceMaterializationBlueprintPreviewPayload> ExecuteAsync(
        Func<ValueTask<ReferenceMaterializationBlueprintPreviewPayload>> operation)
    {
        try
        {
            return Sanitize(await operation());
        }
        catch (ReferenceMaterializationException exception)
        {
            throw ToBridgeException(exception);
        }
    }

    private static async ValueTask<ReferenceMaterializationBlueprintPreviewPayload?> ExecuteOptionalAsync(
        Func<ValueTask<ReferenceMaterializationBlueprintPreviewPayload?>> operation)
    {
        try
        {
            var preview = await operation();
            return preview is null ? null : Sanitize(preview);
        }
        catch (ReferenceMaterializationException exception)
        {
            throw ToBridgeException(exception);
        }
    }

    private static BridgeRequestException ToBridgeException(ReferenceMaterializationException exception) => new(
        exception.ErrorCode,
        exception.Message,
        new { error_code = exception.ErrorCode },
        retryable: true);

    private static ReferenceMaterializationBlueprintPreviewPayload Sanitize(
        ReferenceMaterializationBlueprintPreviewPayload preview) => preview with
    {
        SessionId = ReferencePayloadSanitizer.RedactAndBoundText(preview.SessionId, 128),
        Status = ReferencePayloadSanitizer.RedactAndBoundText(preview.Status, 32),
        NextAction = ReferencePayloadSanitizer.RedactAndBoundText(preview.NextAction, 64),
        Goal = ReferencePayloadSanitizer.RedactAndBoundText(preview.Goal, 800),
        Sources = (preview.Sources ?? Array.Empty<ReferenceMaterializationBlueprintPreviewSourcePayload>())
            .Take(10)
            .Select(source => source with
            {
                GenerationId = ReferencePayloadSanitizer.RedactAndBoundText(source.GenerationId, 128)
            })
            .ToArray(),
        Candidates = (preview.Candidates ?? Array.Empty<ReferenceMaterializationBlueprintPreviewCandidatePayload>())
            .Take(3)
            .Select(candidate => candidate with
            {
                BlueprintId = ReferencePayloadSanitizer.RedactAndBoundText(candidate.BlueprintId, 128),
                Strategy = ReferencePayloadSanitizer.RedactAndBoundText(candidate.Strategy, 64),
                Beats = (candidate.Beats ?? Array.Empty<ReferenceMaterializationBlueprintPreviewBeatPayload>())
                    .Take(3)
                    .Select(beat => beat with
                    {
                        BeatId = ReferencePayloadSanitizer.RedactAndBoundText(beat.BeatId, 128),
                        Intent = ReferencePayloadSanitizer.RedactAndBoundText(beat.Intent, 320),
                        NarrativeFunction = ReferencePayloadSanitizer.RedactAndBoundText(beat.NarrativeFunction, 96),
                        Materials = (beat.Materials ?? Array.Empty<ReferenceMaterializationBlueprintPreviewMaterialLinkPayload>())
                            .Take(6)
                            .Select(link => link with
                            {
                                MaterialId = ReferencePayloadSanitizer.RedactAndBoundText(link.MaterialId, 128),
                                GenerationId = ReferencePayloadSanitizer.RedactAndBoundText(link.GenerationId, 128),
                                MaterialType = ReferencePayloadSanitizer.RedactAndBoundText(link.MaterialType, 64),
                                TextPreview = ReferencePayloadSanitizer.RedactAndBoundText(link.TextPreview, 360),
                                FitExplanation = ReferencePayloadSanitizer.RedactAndBoundText(link.FitExplanation, 360)
                            })
                            .ToArray()
                    })
                    .ToArray()
            })
            .ToArray(),
        StaleAnchorIds = (preview.StaleAnchorIds ?? Array.Empty<long>())
            .Where(anchorId => anchorId > 0)
            .Distinct()
            .Take(10)
            .ToArray()
    };

    private static T ReadObjectArg<T>(JsonElement? payload, int index, string argumentName)
    {
        if (payload is null ||
            payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("args", out var args) ||
            args.ValueKind != JsonValueKind.Array ||
            args.GetArrayLength() <= index ||
            args[index].ValueKind != JsonValueKind.Object)
        {
            throw Invalid(argumentName, "Value must be an object.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(args[index].GetRawText(), BridgeJson.SerializerOptions)
                ?? throw Invalid(argumentName, "Value must not be null.");
        }
        catch (JsonException)
        {
            throw Invalid(argumentName, "Value must match the expected object shape.");
        }
    }

    private static BridgeValidationException Invalid(string argumentName, string message) => new(
        $"Invalid argument '{argumentName}'.",
        new Dictionary<string, string> { [argumentName] = message });
}
