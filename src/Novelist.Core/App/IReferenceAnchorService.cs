using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceAnchorService
{
    ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
        CreateReferenceAnchorPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
        long novelId,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchorBuildStatusPayload?> GetBuildStatusAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken);

    ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(
        SearchReferenceMaterialsPayload input,
        CancellationToken cancellationToken);

    ValueTask<AdaptReferenceMaterialResultPayload> AdaptMaterialAsync(
        AdaptReferenceMaterialPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceReuseAuditPayload> AuditCandidateAsync(
        AuditReferenceReusePayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceUserFeedbackPayload> RecordUserFeedbackAsync(
        RecordReferenceUserFeedbackPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceUserFeedbackPayload>> GetUserFeedbackAsync(
        GetReferenceUserFeedbackPayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteAnchorAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken);
}
