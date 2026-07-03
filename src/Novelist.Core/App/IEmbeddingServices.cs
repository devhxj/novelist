using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IEmbeddingClient
{
    ValueTask<EmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<string> inputs,
        EmbeddingRequestOptions options,
        CancellationToken cancellationToken);
}

public interface IEmbeddingConfigurationService
{
    ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken);
}

public interface IEmbeddingSettingsService : IEmbeddingConfigurationService
{
    ValueTask<EmbeddingConfigPayload> GetConfigAsync(CancellationToken cancellationToken);

    ValueTask SaveConfigAsync(EmbeddingConfigPayload input, CancellationToken cancellationToken);

    ValueTask TestConnectionAsync(EmbeddingConfigPayload input, CancellationToken cancellationToken);

    ValueTask<SqliteVecStatusPayload> GetSqliteVecStatusAsync(CancellationToken cancellationToken);
}

public sealed record EmbeddingRequestOptions(
    string ProviderKey,
    string EndpointUrl,
    string ApiKey,
    string ModelId,
    int? Dimensions,
    string? User);

public sealed record EmbeddingBatchResult(
    string Model,
    int Dimensions,
    IReadOnlyList<EmbeddingItemResult> Items,
    EmbeddingUsage Usage);

public sealed record EmbeddingItemResult(
    int Index,
    IReadOnlyList<float> Vector);

public sealed record EmbeddingUsage(
    int PromptTokens,
    int TotalTokens);

public sealed class NullEmbeddingConfigurationService : IEmbeddingConfigurationService
{
    public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<EmbeddingRequestOptions?>(null);
    }
}
