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

    ValueTask<IReadOnlyList<ReferenceAnchoredDraftAuditPayload>> GetDraftAuditsAsync(
        GetReferenceAnchoredDraftAuditsPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceOrchestrationRunPayload> StartOrchestrationRunAsync(
        StartReferenceOrchestrationRunPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceOrchestrationRunPayload>> GetOrchestrationRunsAsync(
        long novelId,
        int? chapterNumber,
        CancellationToken cancellationToken);

    ValueTask<ReferenceOrchestrationRunPayload?> GetOrchestrationRunAsync(
        long novelId,
        string runId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceOrchestrationRunEventPayload>> GetOrchestrationRunEventsAsync(
        long novelId,
        string runId,
        CancellationToken cancellationToken);

    ValueTask<ReferenceOrchestrationRunPayload> ResumeOrchestrationRunAsync(
        ResumeReferenceOrchestrationRunPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceOrchestrationRunPayload> CancelOrchestrationRunAsync(
        CancelReferenceOrchestrationRunPayload input,
        CancellationToken cancellationToken);
}
