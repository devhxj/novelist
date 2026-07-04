using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceAnchoredDraftService
{
    ValueTask<ReferenceChapterBlueprintPayload> GenerateChapterBlueprintAsync(
        GenerateReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceChapterBlueprintSummaryPayload>> GetChapterBlueprintsAsync(
        long novelId,
        int? chapterNumber,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterBlueprintPayload?> GetChapterBlueprintAsync(
        long novelId,
        long blueprintId,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterBlueprintReviewPayload> ReviewChapterBlueprintAsync(
        ReviewReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterBlueprintPayload> ReviseChapterBlueprintAsync(
        ReviseReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterBlueprintPayload> ApproveChapterBlueprintAsync(
        ApproveReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceBlueprintMaterialBindingResultPayload> BindBlueprintMaterialsAsync(
        BindReferenceBlueprintMaterialsPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchoredDraftPayload> GenerateDraftFromBlueprintAsync(
        GenerateReferenceAnchoredDraftPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchoredDraftAuditPayload> AuditDraftAgainstBlueprintAsync(
        AuditReferenceAnchoredDraftPayload input,
        CancellationToken cancellationToken);
}
