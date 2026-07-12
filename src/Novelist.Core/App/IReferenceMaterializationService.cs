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
}
