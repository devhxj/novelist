using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IRagIndexService
{
    ValueTask<RagIndexStatePayload?> GetIndexStateAsync(
        long novelId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RagChunkPayload>> GetIndexedChunksAsync(
        long novelId,
        CancellationToken cancellationToken);

    ValueTask<RagIndexStatePayload> RebuildNovelAsync(
        long novelId,
        CancellationToken cancellationToken);
}

public interface IRagSemanticSearchService
{
    ValueTask<IReadOnlyList<RagSearchHitPayload>> SearchAsync(
        long novelId,
        string query,
        int topK,
        CancellationToken cancellationToken);
}

public interface IRagIndexRefreshNotifier
{
    ValueTask MarkNovelIndexStaleAsync(
        long novelId,
        string reason,
        CancellationToken cancellationToken);
}

public sealed class DisabledRagIndexService : IRagIndexService
{
    public ValueTask<RagIndexStatePayload?> GetIndexStateAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RagIndexStatePayload?>(null);
    }

    public ValueTask<IReadOnlyList<RagChunkPayload>> GetIndexedChunksAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<RagChunkPayload>>([]);
    }

    public ValueTask<RagIndexStatePayload> RebuildNovelAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RagIndexStatePayload(
            novelId,
            ProviderKey: string.Empty,
            ModelId: string.Empty,
            Dimensions: 0,
            ChunkerVersion: "paragraph-v1",
            Status: "disabled",
            ChunkCount: 0,
            VectorTable: string.Empty,
            LastError: "RAG index service is not configured.",
            UpdatedAt: DateTimeOffset.UtcNow));
    }
}
