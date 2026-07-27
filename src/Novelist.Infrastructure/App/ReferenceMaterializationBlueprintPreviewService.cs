using System.Security.Cryptography;
using System.Text;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceMaterializationBlueprintPreviewService : IReferenceMaterializationBlueprintPreviewService
{
    private const int MaximumSelectedSources = 10;
    private const int MaximumGoalCharacters = 800;
    private const int MaximumCandidates = 3;
    private const int MaximumMaterialsPerCandidate = 6;
    private const int MaximumSearchResultsPerSource = 18;
    private readonly IReferenceMaterialSearch _materials;

    public ReferenceMaterializationBlueprintPreviewService(IReferenceMaterialSearch materials)
    {
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
    }

    public async ValueTask<ReferenceMaterializationBlueprintPreviewPayload> GenerateAsync(
        GenerateReferenceMaterializationBlueprintPreviewPayload input,
        CancellationToken cancellationToken)
    {
        var request = Validate(input);
        var hits = new List<ReferenceMaterialSearchHit>();
        var sources = new List<ReferenceMaterializationBlueprintPreviewSourcePayload>(request.AnchorIds.Count);
        foreach (var anchorId in request.AnchorIds)
        {
            var sourceHits = await _materials.SearchAsync(
                new ReferenceMaterialSearchRequest(
                    request.Goal,
                    MaximumSearchResultsPerSource,
                    AnchorIds: [anchorId]),
                cancellationToken);
            if (sourceHits.Count == 0)
            {
                continue;
            }

            ValidateHits(sourceHits, anchorId);
            var generationId = sourceHits[0].GenerationId;
            sources.Add(new ReferenceMaterializationBlueprintPreviewSourcePayload(
                anchorId,
                generationId,
                sourceHits.Count));
            hits.AddRange(sourceHits);
        }

        if (hits.Count == 0)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.BlueprintNoRelevantMaterial,
                "Selected references returned no material for this blueprint goal.");
        }

        return new ReferenceMaterializationBlueprintPreviewPayload(
            request.Goal,
            sources,
            BuildCandidates(request.Goal, hits, request.RequestedCount));
    }

    private static IReadOnlyList<ReferenceMaterializationBlueprintPreviewCandidatePayload> BuildCandidates(
        string goal,
        IReadOnlyList<ReferenceMaterialSearchHit> hits,
        int requestedCount)
    {
        var strategies = new[] { "progressive", "contrast", "focused" };
        var candidates = new List<ReferenceMaterializationBlueprintPreviewCandidatePayload>(requestedCount);
        for (var candidateIndex = 0; candidateIndex < requestedCount; candidateIndex++)
        {
            var selected = Rotate(hits, candidateIndex)
                .Take(Math.Min(MaximumMaterialsPerCandidate, hits.Count))
                .ToArray();
            var blueprintId = "preview-" + Hash(
                goal + "|" + candidateIndex + "|" + string.Join('|', selected.Select(hit => hit.MaterialId)))[..24];
            var beats = selected
                .Chunk(2)
                .Take(3)
                .Select((chunk, beatIndex) => CreateBeat(blueprintId, beatIndex, chunk))
                .ToArray();
            candidates.Add(new ReferenceMaterializationBlueprintPreviewCandidatePayload(
                blueprintId,
                strategies[candidateIndex],
                beats));
        }

        return candidates;
    }

    private static ReferenceMaterializationBlueprintPreviewBeatPayload CreateBeat(
        string blueprintId,
        int beatIndex,
        IReadOnlyList<ReferenceMaterialSearchHit> hits)
    {
        var first = hits[0];
        return new ReferenceMaterializationBlueprintPreviewBeatPayload(
            "beat-" + Hash(blueprintId + "|" + beatIndex)[..20],
            beatIndex,
            first.Metadata.ReuseHint,
            first.Metadata.NarrativeFunctions.FirstOrDefault() ?? first.Metadata.SourceKind,
            hits.Select(hit => new ReferenceMaterializationBlueprintPreviewMaterialLinkPayload(
                hit.MaterialId,
                hit.AnchorId,
                hit.GenerationId,
                hit.Text,
                ToPayload(hit.Metadata),
                hit.VectorDistance,
                hit.Metadata.ReuseHint)).ToArray());
    }

    private static ReferenceMaterialMetadataPayload ToPayload(ReferenceMaterialMetadata metadata) =>
        new(
            new ReferenceMaterialSourceSpanPayload(metadata.SourceSpan.StartLine, metadata.SourceSpan.EndLine),
            metadata.SourceKind,
            metadata.Entities.Select(entity => new ReferenceMaterialEntityPayload(entity.Name, entity.Kind)).ToArray(),
            metadata.Setting is null ? null : new ReferenceMaterialSettingPayload(
                metadata.Setting.Location,
                metadata.Setting.Time,
                metadata.Setting.Environment),
            metadata.Perspective is null ? null : new ReferenceMaterialPerspectivePayload(
                metadata.Perspective.Mode,
                metadata.Perspective.FocusEntity),
            metadata.Event,
            metadata.Facts.Select(fact => new ReferenceMaterialFactPayload(fact.Content, fact.Subject)).ToArray(),
            metadata.Causality is null ? null : new ReferenceMaterialCausalityPayload(
                metadata.Causality.Cause,
                metadata.Causality.Consequence),
            metadata.StateChanges.Select(change => new ReferenceMaterialStateChangePayload(
                change.Subject,
                change.Before,
                change.After)).ToArray(),
            metadata.CharacterDynamics,
            metadata.Conflict is null ? null : new ReferenceMaterialConflictPayload(
                metadata.Conflict.Pressure,
                metadata.Conflict.Cost),
            metadata.Information is null ? null : new ReferenceMaterialInformationPayload(
                metadata.Information.Role,
                metadata.Information.Content),
            metadata.Emotion is null ? null : new ReferenceMaterialEmotionPayload(
                metadata.Emotion.Tone,
                metadata.Emotion.Subtext),
            metadata.NarrativeFunctions,
            metadata.Foreshadowing.Select(item => new ReferenceMaterialForeshadowingPayload(
                item.Phase,
                item.Target)).ToArray(),
            metadata.Motifs,
            metadata.ExpressionTechniques,
            metadata.ReuseHint);

    private static IEnumerable<ReferenceMaterialSearchHit> Rotate(
        IReadOnlyList<ReferenceMaterialSearchHit> hits,
        int offset)
    {
        for (var index = 0; index < hits.Count; index++)
        {
            yield return hits[(index + offset) % hits.Count];
        }
    }

    private static void ValidateHits(IReadOnlyList<ReferenceMaterialSearchHit> hits, long anchorId)
    {
        var generationId = hits[0].GenerationId;
        if (string.IsNullOrWhiteSpace(generationId) ||
            hits.Any(hit =>
                hit.AnchorId != anchorId ||
                !string.Equals(hit.GenerationId, generationId, StringComparison.Ordinal)) ||
            hits.Select(hit => hit.MaterialId).Distinct(StringComparer.Ordinal).Count() != hits.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Reference material search returned an inconsistent active generation.");
        }
    }

    private static PreviewRequest Validate(GenerateReferenceMaterializationBlueprintPreviewPayload input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Novel id must be positive.");
        }

        var anchorIds = (input.AnchorIds ?? [])
            .Distinct()
            .OrderBy(anchorId => anchorId)
            .ToArray();
        if (anchorIds.Length is 0 or > MaximumSelectedSources || anchorIds.Any(anchorId => anchorId <= 0))
        {
            throw new ArgumentException("Blueprint preview requires one to ten reference sources.", nameof(input));
        }

        var goal = input.Goal?.Trim() ?? string.Empty;
        if (goal.Length is 0 or > MaximumGoalCharacters || goal.Any(char.IsControl))
        {
            throw new ArgumentException("Blueprint preview goal is invalid.", nameof(input));
        }

        if (input.RequestedCount is < 1 or > MaximumCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Blueprint preview count must be between one and three.");
        }

        return new PreviewRequest(anchorIds, goal, input.RequestedCount);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record PreviewRequest(IReadOnlyList<long> AnchorIds, string Goal, int RequestedCount);
}
