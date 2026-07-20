using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceMaterializationEmbeddingProcessor : IReferenceMaterializationEmbedder
{
    private const int MaxItemsPerRequest = 64;
    private const int MaxTextCharsPerItem = 1_200;
    private readonly IEmbeddingConfigurationService _configuration;
    private readonly IEmbeddingClient _embeddings;

    public ReferenceMaterializationEmbeddingProcessor(
        IEmbeddingConfigurationService configuration,
        IEmbeddingClient embeddings)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
    }

    public async ValueTask<ReferenceMaterializationEmbeddingResult> EmbedAsync(
        ReferenceMaterializationEmbeddingRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRequest(input);
        var options = await _configuration.GetActiveEmbeddingOptionsAsync(cancellationToken)
            ?? throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingNotConfigured,
                "Materialization requires an active embedding configuration.");
        ValidateFrozenModel(input.Model, options);

        EmbeddingBatchResult response;
        try
        {
            response = await _embeddings.EmbedAsync(
                input.Items.Select(item => item.Text).ToArray(),
                options with
                {
                    Dimensions = input.Model.Dimensions,
                    InputKind = BuiltinOnnxEmbeddingModel.DocumentInputKind
                },
                cancellationToken);
        }
        catch (ReferenceMaterializationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingRequestFailed,
                "Materialization embedding request failed.");
        }

        return ValidateResponse(input, response);
    }

    private static void ValidateRequest(ReferenceMaterializationEmbeddingRequest input)
    {
        if (input.Model is null ||
            string.IsNullOrWhiteSpace(input.Model.Provider) ||
            string.IsNullOrWhiteSpace(input.Model.ModelId) ||
            input.Model.Dimensions <= 0 ||
            input.Items is null || input.Items.Count is 0 or > MaxItemsPerRequest)
        {
            throw new ArgumentException("Materialization embedding request is invalid.", nameof(input));
        }

        var materialIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input.Items)
        {
            if (item is null ||
                string.IsNullOrWhiteSpace(item.MaterialId) ||
                item.MaterialId.Length > 256 ||
                !materialIds.Add(item.MaterialId) ||
                string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > MaxTextCharsPerItem ||
                item.Text.Contains('\0'))
            {
                throw new ArgumentException("Materialization embedding request contains an invalid item.", nameof(input));
            }
        }
    }

    private static void ValidateFrozenModel(
        ReferenceMaterializationEmbeddingModel model,
        EmbeddingRequestOptions options)
    {
        var dimensions = options.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
        if (!string.Equals(model.Provider, options.ProviderKey, StringComparison.Ordinal) ||
            !string.Equals(model.ModelId, options.ModelId, StringComparison.Ordinal) ||
            dimensions != model.Dimensions)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                "The active embedding configuration no longer matches the frozen materialization model.");
        }
    }

    private static ReferenceMaterializationEmbeddingResult ValidateResponse(
        ReferenceMaterializationEmbeddingRequest input,
        EmbeddingBatchResult response)
    {
        if (response is null || response.Dimensions != input.Model.Dimensions || response.Items is null ||
            response.Items.Count != input.Items.Count)
        {
            throw InvalidResponse();
        }

        var byIndex = new Dictionary<int, EmbeddingItemResult>();
        foreach (var item in response.Items)
        {
            if (item is null || !byIndex.TryAdd(item.Index, item) ||
                item.Index < 0 || item.Index >= input.Items.Count ||
                item.Vector is null || item.Vector.Count != input.Model.Dimensions ||
                item.Vector.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            {
                throw InvalidResponse();
            }
        }

        if (byIndex.Count != input.Items.Count)
        {
            throw InvalidResponse();
        }

        return new ReferenceMaterializationEmbeddingResult(
            input.Items.Select((item, index) => new ReferenceMaterializationMaterialEmbedding(
                item.MaterialId,
                byIndex[index].Vector.ToArray())).ToArray());
    }

    private static ReferenceMaterializationException InvalidResponse()
    {
        return new ReferenceMaterializationException(
            ReferenceMaterializationErrorCodes.EmbeddingInvalid,
            "Materialization embedding response did not match the frozen request.");
    }
}
