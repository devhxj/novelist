using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceMaterializationService
{
    ValueTask<ReferenceChapterSplitProfilePayload> AnalyzeChapterSplitAsync(
        AnalyzeReferenceChapterSplitPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterSplitProfilePayload> PreviewChapterSplitAsync(
        PreviewReferenceChapterSplitPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterSplitProfilePayload> ConfirmChapterSplitAsync(
        ConfirmReferenceChapterSplitPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceMaterializationStatusPayload> EnqueueMaterializationAsync(
        EnqueueReferenceMaterializationPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceMaterializationStatusPayload?> GetMaterializationStatusAsync(
        GetReferenceMaterializationStatusPayload input,
        CancellationToken cancellationToken);

    ValueTask<PageResultPayload<ReferenceMaterializationChapterProgressPayload>> ListMaterializationChapterProgressAsync(
        ListReferenceMaterializationChapterProgressPayload input,
        CancellationToken cancellationToken);
}
