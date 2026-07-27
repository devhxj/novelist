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

    private static BridgeRequestException ToBridgeException(ReferenceMaterializationException exception) => new(
        exception.ErrorCode,
        exception.Message,
        new { error_code = exception.ErrorCode },
        retryable: true);

    private static ReferenceMaterializationBlueprintPreviewPayload Sanitize(
        ReferenceMaterializationBlueprintPreviewPayload preview) => preview with
    {
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
                                Text = link.Text,
                                Metadata = SanitizeMetadata(link.Metadata),
                                FitExplanation = ReferencePayloadSanitizer.RedactAndBoundText(link.FitExplanation, 360)
                            })
                            .ToArray()
                    })
                    .ToArray()
            })
            .ToArray()
    };

    private static ReferenceMaterialMetadataPayload SanitizeMetadata(ReferenceMaterialMetadataPayload metadata) =>
        metadata with
        {
            SourceKind = ReferencePayloadSanitizer.RedactAndBoundText(metadata.SourceKind, 64),
            Entities = (metadata.Entities ?? [])
                .Select(entity => entity with
                {
                    Name = ReferencePayloadSanitizer.RedactAndBoundText(entity.Name, 120),
                    Kind = ReferencePayloadSanitizer.RedactAndBoundText(entity.Kind, 64)
                })
                .ToArray(),
            Setting = metadata.Setting is null ? null : metadata.Setting with
            {
                Location = SanitizeOptional(metadata.Setting.Location, 240),
                Time = SanitizeOptional(metadata.Setting.Time, 240),
                Environment = SanitizeOptional(metadata.Setting.Environment, 240)
            },
            Perspective = metadata.Perspective is null ? null : metadata.Perspective with
            {
                Mode = ReferencePayloadSanitizer.RedactAndBoundText(metadata.Perspective.Mode, 64),
                FocusEntity = SanitizeOptional(metadata.Perspective.FocusEntity, 240)
            },
            Event = SanitizeOptional(metadata.Event, 600),
            Facts = (metadata.Facts ?? []).Select(fact => fact with
            {
                Content = ReferencePayloadSanitizer.RedactAndBoundText(fact.Content, 600),
                Subject = SanitizeOptional(fact.Subject, 240)
            }).ToArray(),
            Causality = metadata.Causality is null ? null : metadata.Causality with
            {
                Cause = SanitizeOptional(metadata.Causality.Cause, 600),
                Consequence = SanitizeOptional(metadata.Causality.Consequence, 600)
            },
            StateChanges = (metadata.StateChanges ?? []).Select(change => change with
            {
                Subject = ReferencePayloadSanitizer.RedactAndBoundText(change.Subject, 240),
                Before = ReferencePayloadSanitizer.RedactAndBoundText(change.Before, 600),
                After = ReferencePayloadSanitizer.RedactAndBoundText(change.After, 600)
            }).ToArray(),
            CharacterDynamics = SanitizeOptional(metadata.CharacterDynamics, 600),
            Conflict = metadata.Conflict is null ? null : metadata.Conflict with
            {
                Pressure = SanitizeOptional(metadata.Conflict.Pressure, 600),
                Cost = SanitizeOptional(metadata.Conflict.Cost, 600)
            },
            Information = metadata.Information is null ? null : metadata.Information with
            {
                Role = SanitizeOptional(metadata.Information.Role, 64),
                Content = SanitizeOptional(metadata.Information.Content, 600)
            },
            Emotion = metadata.Emotion is null ? null : metadata.Emotion with
            {
                Tone = SanitizeOptional(metadata.Emotion.Tone, 64),
                Subtext = SanitizeOptional(metadata.Emotion.Subtext, 600)
            },
            NarrativeFunctions = (metadata.NarrativeFunctions ?? [])
                .Select(value => ReferencePayloadSanitizer.RedactAndBoundText(value, 64))
                .ToArray(),
            Foreshadowing = (metadata.Foreshadowing ?? []).Select(item => item with
            {
                Phase = ReferencePayloadSanitizer.RedactAndBoundText(item.Phase, 64),
                Target = ReferencePayloadSanitizer.RedactAndBoundText(item.Target, 600)
            }).ToArray(),
            Motifs = (metadata.Motifs ?? []).Select(value => ReferencePayloadSanitizer.RedactAndBoundText(value, 240)).ToArray(),
            ExpressionTechniques = (metadata.ExpressionTechniques ?? []).Select(value => ReferencePayloadSanitizer.RedactAndBoundText(value, 64)).ToArray(),
            ReuseHint = ReferencePayloadSanitizer.RedactAndBoundText(metadata.ReuseHint, 600)
        };

    private static string? SanitizeOptional(string? value, int maximumLength) =>
        value is null ? null : ReferencePayloadSanitizer.RedactAndBoundText(value, maximumLength);

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
