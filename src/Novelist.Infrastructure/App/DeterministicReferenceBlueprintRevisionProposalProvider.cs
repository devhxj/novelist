using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed class DeterministicReferenceBlueprintRevisionProposalProvider : IReferenceBlueprintRevisionProposalProvider
{
    private static readonly JsonSerializerOptions JsonOptions = BridgeJson.SerializerOptions;

    public ValueTask<ReferenceOrchestrationBlueprintRevisionProposalPayload> ProposeRevisionAsync(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewPayload review,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changes = review.Defects
            .Where(defect => string.Equals(defect.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Select(defect => BuildProposedRevisionChange(blueprint, defect))
            .Where(change => change is not null)
            .Select(change => change!)
            .GroupBy(change => change.FieldPath, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        return ValueTask.FromResult(new ReferenceOrchestrationBlueprintRevisionProposalPayload(
            blueprint.BlueprintId,
            review.ReviewId,
            "orchestrator",
            "deterministic blueprint review fix proposal",
            changes));
    }

    private static ReferenceBlueprintRevisionChangePayload? BuildProposedRevisionChange(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewDefectPayload defect)
    {
        var fieldPath = NormalizeFieldPath(defect.FieldPath);
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            return null;
        }

        if (string.Equals(fieldPath, "final_hook", StringComparison.Ordinal))
        {
            return new ReferenceBlueprintRevisionChangePayload(
                fieldPath,
                "hook follows the approved beat consequence without exposing forbidden facts");
        }

        if (string.Equals(fieldPath, "previous_state", StringComparison.Ordinal))
        {
            return new ReferenceBlueprintRevisionChangePayload(
                fieldPath,
                "previous state remains bounded by approved known facts");
        }

        if (string.Equals(fieldPath, "final_state", StringComparison.Ordinal))
        {
            return new ReferenceBlueprintRevisionChangePayload(
                fieldPath,
                "final state follows the approved chapter function");
        }

        if (fieldPath.EndsWith(":causality_out", StringComparison.Ordinal))
        {
            return new ReferenceBlueprintRevisionChangePayload(
                fieldPath,
                "beat consequence carries story pressure forward without exposing forbidden facts");
        }

        if (fieldPath.EndsWith(":transition_out", StringComparison.Ordinal))
        {
            return new ReferenceBlueprintRevisionChangePayload(
                fieldPath,
                "transition pressure carries the beat consequence forward");
        }

        if (fieldPath.EndsWith(":character_state_delta", StringComparison.Ordinal))
        {
            var beat = FindBlueprintBeatForDefect(blueprint, defect);
            return beat is null
                ? null
                : new ReferenceBlueprintRevisionChangePayload(
                    "beat:" + beat.BeatId + ":character_states_after",
                    JsonSerializer.Serialize(new[] { "pressure changes the character's available action" }, JsonOptions));
        }

        if (fieldPath.EndsWith(":scene_facts", StringComparison.Ordinal))
        {
            var beat = FindBlueprintBeatForDefect(blueprint, defect);
            return beat is null
                ? null
                : new ReferenceBlueprintRevisionChangePayload(
                    fieldPath,
                    JsonSerializer.Serialize(
                        RemoveForbiddenValues(beat.SceneFacts, blueprint.ForbiddenFacts),
                        JsonOptions));
        }

        if (fieldPath.EndsWith(":emotion_mechanic", StringComparison.Ordinal))
        {
            var beat = FindBlueprintBeatForDefect(blueprint, defect);
            if (beat is null)
            {
                return null;
            }

            var prefix = "beat:" + beat.BeatId + ":";
            return new ReferenceBlueprintRevisionChangePayload(
                prefix + "external_evidence",
                "a visible pause and changed action show the pressure");
        }

        if (fieldPath.EndsWith(":candidate_rejection_rule", StringComparison.Ordinal))
        {
            return new ReferenceBlueprintRevisionChangePayload(
                fieldPath,
                "reject action-only, dialogue-only, missing-evidence, POV-leaking, or unsupported-reveal prose");
        }

        return null;
    }

    private static string NormalizeFieldPath(string? fieldPath)
    {
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            return string.Empty;
        }

        var trimmed = fieldPath.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static IReadOnlyList<string> RemoveForbiddenValues(
        IReadOnlyList<string> values,
        IReadOnlyList<string> forbiddenFacts)
    {
        return values
            .Where(value => !forbiddenFacts.Any(forbidden =>
                !string.IsNullOrWhiteSpace(forbidden) &&
                !string.IsNullOrWhiteSpace(value) &&
                value.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static ReferenceChapterBlueprintBeatPayload? FindBlueprintBeatForDefect(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewDefectPayload defect)
    {
        if (!string.IsNullOrWhiteSpace(defect.BeatId))
        {
            return blueprint.Beats.FirstOrDefault(beat => string.Equals(beat.BeatId, defect.BeatId, StringComparison.Ordinal));
        }

        const string prefix = "beat:";
        if (!defect.FieldPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var field = defect.FieldPath[prefix.Length..];
        var separator = field.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var beatId = field[..separator];
        return blueprint.Beats.FirstOrDefault(beat => string.Equals(beat.BeatId, beatId, StringComparison.Ordinal));
    }
}
