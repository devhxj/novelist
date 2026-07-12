using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceMaterializationSemanticSearch
{
    ValueTask<IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload>> SearchAsync(
        long anchorId,
        string query,
        int maxResults,
        CancellationToken cancellationToken);
}
