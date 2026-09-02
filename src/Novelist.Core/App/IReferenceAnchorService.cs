using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceAnchorService
{
    ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
        CreateReferenceAnchorPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchorPayload> RegisterMaterializationSourceAsync(
        CreateReferenceAnchorPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceAnchorPayload>> CreateAnchorsAsync(
        CreateReferenceAnchorsPayload input,
        CancellationToken cancellationToken);

    ValueTask<CreateReferenceAnchorsResultPayload> CreateAnchorsWithResultAsync(
        CreateReferenceAnchorsPayload input,
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

 ValueTask<ReferenceMaterialEmbeddingBackfillPayload> BackfillMaterialEmbeddingsAsync(
 BackfillReferenceMaterialEmbeddingsPayload input,
 CancellationToken cancellationToken);

    ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(
        SearchReferenceMaterialsPayload input,
        CancellationToken cancellationToken);

    /// <summary>
    /// 批量检索：同一进程内共享一次互斥与一次材料全量读取，逐查询独立打分。
    /// 供细纲 beat 级覆盖度等多次检索场景复用，避免 N 次全表扫描。
    /// </summary>
    ValueTask<IReadOnlyList<PageResultPayload<ReferenceMaterialPayload>>> SearchMaterialsBatchAsync(
        IReadOnlyList<SearchReferenceMaterialsPayload> inputs,
        CancellationToken cancellationToken);

    ValueTask<ReferenceMaterialCoveragePayload> GetMaterialCoverageAsync(
        GetReferenceMaterialCoveragePayload input,
        CancellationToken cancellationToken);

    ValueTask<PageResultPayload<ReferenceMaterialTagReviewItemPayload>> GetMaterialTagReviewQueueAsync(
        GetReferenceMaterialTagReviewQueuePayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceMaterialDetailPayload?> GetMaterialDetailAsync(
        GetReferenceMaterialDetailPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceSourceSegmentDetailPayload?> GetSourceSegmentDetailAsync(
        GetReferenceSourceSegmentDetailPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceSourceProcessingDetailPayload?> GetSourceProcessingDetailAsync(
        GetReferenceSourceProcessingDetailPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceMaterialPayload> UpdateMaterialTagsAsync(
        UpdateReferenceMaterialTagsPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceMaterialPayload>> UpdateMaterialsTagsAsync(
        UpdateReferenceMaterialsTagsPayload input,
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

    ValueTask DeleteAnchorsAsync(
        DeleteReferenceAnchorsPayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteMaterialsAsync(
        DeleteReferenceMaterialsPayload input,
        CancellationToken cancellationToken);

    ValueTask RestoreMaterialsAsync(
        RestoreReferenceMaterialsPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchorPayload> PromoteAnchorToWorkspaceCorpusAsync(
        PromoteReferenceAnchorToWorkspaceCorpusPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceAnchorPayload>> PromoteAnchorsToWorkspaceCorpusAsync(
        PromoteReferenceAnchorsToWorkspaceCorpusPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
        UpdateReferenceAnchorMetadataPayload input,
        CancellationToken cancellationToken);
}
