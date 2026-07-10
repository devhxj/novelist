using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class HybridEmbeddingClient : IEmbeddingClient
{
    private const string ProviderTypeApi = "api";
    private const string ProviderTypeOnnx = "onnx";

    private readonly IEmbeddingClient _api;
    private readonly IEmbeddingClient _onnx;

    public HybridEmbeddingClient(
        IEmbeddingClient? api = null,
        IEmbeddingClient? onnx = null)
    {
        _api = api ?? new StandardEmbeddingClient();
        _onnx = onnx ?? new LocalOnnxEmbeddingClient();
    }

    public ValueTask<EmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<string> inputs,
        EmbeddingRequestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ResolveProviderType(options) == ProviderTypeOnnx
            ? _onnx.EmbedAsync(inputs, options, cancellationToken)
            : _api.EmbedAsync(inputs, options, cancellationToken);
    }

    private static string ResolveProviderType(EmbeddingRequestOptions options)
    {
        var providerType = (options.ProviderType ?? string.Empty).Trim().ToLowerInvariant();
        if (providerType.Length == 0)
        {
            return string.Equals(options.ProviderKey, ProviderTypeOnnx, StringComparison.OrdinalIgnoreCase)
                ? ProviderTypeOnnx
                : ProviderTypeApi;
        }

        return providerType switch
        {
            ProviderTypeApi or "online" or "remote" => ProviderTypeApi,
            ProviderTypeOnnx or "local" or "local_onnx" or "local-onnx" => ProviderTypeOnnx,
            _ => throw new ArgumentException("Embedding provider type must be api or onnx.", nameof(options))
        };
    }
}
