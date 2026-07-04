using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceAnchoredDraftPreflight
{
    public static void EnsureDraftGenerationAllowed(ReferenceChapterBlueprintPayload blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        if (string.Equals(blueprint.Status, ReferenceBlueprintStates.Stale, StringComparison.Ordinal))
        {
            throw new ArgumentException("Stale blueprint must be regenerated before reference-anchored draft generation.", nameof(blueprint));
        }

        if (!string.Equals(blueprint.Status, ReferenceBlueprintStates.Approved, StringComparison.Ordinal) &&
            !string.Equals(blueprint.Status, ReferenceBlueprintStates.MaterialBound, StringComparison.Ordinal))
        {
            throw new ArgumentException("Reference-anchored draft generation requires an approved blueprint.", nameof(blueprint));
        }

        EnsureCurrentPassingReview(blueprint, "Reference-anchored draft generation");
    }

    public static void EnsureCurrentPassingReview(ReferenceChapterBlueprintPayload blueprint, string operationName)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var review = blueprint.LatestReview
            ?? throw new ArgumentException(operationName + " requires a current passing blueprint review.", nameof(blueprint));
        if (!string.Equals(review.Status, ReferenceBlueprintReviewStatuses.Passed, StringComparison.Ordinal) ||
            !ReviewMatchesBlueprint(blueprint, review))
        {
            throw new ArgumentException(operationName + " requires a current passing blueprint review for this exact blueprint contract.", nameof(blueprint));
        }
    }

    public static bool ReviewMatchesBlueprint(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewPayload review)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(review);
        return review.BlueprintId == blueprint.BlueprintId &&
            string.Equals(review.ContextHash, blueprint.ContextHash, StringComparison.Ordinal) &&
            string.Equals(review.SourcePlanHash, blueprint.SourcePlanHash, StringComparison.Ordinal) &&
            string.Equals(review.AnalysisContractHash, blueprint.AnalysisContractHash, StringComparison.Ordinal) &&
            review.ReviewVersion == ReferenceChapterBlueprintReviewer.CurrentReviewVersion;
    }

    public static IReadOnlyList<ReferenceChapterBlueprintBeatPayload> SelectTargetBeats(
        ReferenceChapterBlueprintPayload blueprint,
        IReadOnlyList<string>? requestedBeatIds)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var requested = requestedBeatIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var targetBeats = requested.Count == 0
            ? blueprint.Beats
            : blueprint.Beats.Where(beat => requested.Contains(beat.BeatId)).ToArray();
        if (targetBeats.Count == 0)
        {
            throw new ArgumentException("Draft generation requires at least one valid blueprint beat.", nameof(requestedBeatIds));
        }

        return targetBeats;
    }

    public static IReadOnlyList<string> RequiredMaterialBeatIds(
        IReadOnlyList<ReferenceChapterBlueprintBeatPayload> targetBeats)
    {
        ArgumentNullException.ThrowIfNull(targetBeats);
        return targetBeats
            .Where(beat => string.IsNullOrWhiteSpace(beat.NoReuseReason))
            .Select(beat => beat.BeatId)
            .ToArray();
    }

    public static IReadOnlyDictionary<string, ReferenceBlueprintMaterialLinkPayload> EnsureSelectedMaterialLinksForTargetBeats(
        IReadOnlyList<ReferenceChapterBlueprintBeatPayload> targetBeats,
        IReadOnlyDictionary<string, ReferenceBlueprintMaterialLinkPayload> selectedLinks)
    {
        ArgumentNullException.ThrowIfNull(targetBeats);
        ArgumentNullException.ThrowIfNull(selectedLinks);
        var requiredBeatIds = RequiredMaterialBeatIds(targetBeats);
        if (requiredBeatIds.Count == 0)
        {
            return new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal);
        }

        var missing = requiredBeatIds
            .Where(beatId => !selectedLinks.ContainsKey(beatId))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                "Reference-anchored draft generation requires selected reference material links for every target beat and the current blueprint analysis contract.",
                nameof(targetBeats));
        }

        return selectedLinks;
    }

    public static void EnsureCandidateProvenance(
        IReadOnlyList<ReferenceChapterBlueprintBeatPayload> targetBeats,
        IReadOnlyDictionary<string, ReferenceBlueprintMaterialLinkPayload> selectedLinks,
        IReadOnlyList<ReferenceDraftParagraphCandidatePayload> candidates)
    {
        ArgumentNullException.ThrowIfNull(targetBeats);
        ArgumentNullException.ThrowIfNull(selectedLinks);
        ArgumentNullException.ThrowIfNull(candidates);
        var targetBeatsById = targetBeats.ToDictionary(beat => beat.BeatId, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!targetBeatsById.TryGetValue(candidate.BeatId, out var beat))
            {
                throw new ArgumentException("Draft candidate provenance does not match a requested blueprint beat.", nameof(candidates));
            }

            if (ReferenceDraftProvenanceIds.IsNoReuseMaterialId(candidate.MaterialId))
            {
                if (string.IsNullOrWhiteSpace(beat.NoReuseReason))
                {
                    throw new ArgumentException("Draft candidate no-reuse provenance is not approved by the blueprint beat.", nameof(candidates));
                }

                continue;
            }

            if (!selectedLinks.TryGetValue(candidate.BeatId, out var link) ||
                !string.Equals(link.MaterialId, candidate.MaterialId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Draft candidate provenance does not match the current selected material link.", nameof(candidates));
            }
        }
    }
}
