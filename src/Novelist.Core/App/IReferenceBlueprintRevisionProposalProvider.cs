using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceBlueprintRevisionProposalProvider
{
    ValueTask<ReferenceOrchestrationBlueprintRevisionProposalPayload> ProposeRevisionAsync(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewPayload review,
        CancellationToken cancellationToken);
}
