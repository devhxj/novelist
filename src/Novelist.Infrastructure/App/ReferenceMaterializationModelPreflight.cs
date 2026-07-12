using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed class ReferenceMaterializationModelPreflight : IReferenceMaterializationModelPreflight
{
    private readonly IAppSettingsService _settings;
    private readonly IChatCompletionClient _completion;
    private readonly IEmbeddingConfigurationService _embeddingConfiguration;
    private readonly IEmbeddingClient _embeddings;

    public ReferenceMaterializationModelPreflight(
        IAppSettingsService settings,
        IChatCompletionClient completion,
        IEmbeddingConfigurationService embeddingConfiguration,
        IEmbeddingClient embeddings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _embeddingConfiguration = embeddingConfiguration ?? throw new ArgumentNullException(nameof(embeddingConfiguration));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
    }

    public async ValueTask<ReferenceMaterializationModelPreflightResult> VerifyAsync(CancellationToken cancellationToken)
    {
        var llm = await VerifyLlmAsync(cancellationToken);
        var embedding = await VerifyEmbeddingAsync(cancellationToken);
        return new ReferenceMaterializationModelPreflightResult(llm, embedding);
    }

    private async ValueTask<ReferenceMaterializationModelIdentityPayload> VerifyLlmAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetSettingsAsync(cancellationToken);
        var selected = ParseSelectedModel(settings.SelectedModelKey);
        if (selected is null)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.LlmNotConfigured,
                "Materialization requires a selected LLM.");
        }

        try
        {
            var sawContent = false;
            await foreach (var item in _completion.StreamChatAsync(
                new ChatCompletionRequest(
                    selected.Provider,
                    selected.ModelId,
                    settings.ReasoningEffort ?? string.Empty,
                    [
                        new ChatCompletionMessage("system", "Return exactly {\"ok\":true}."),
                        new ChatCompletionMessage("user", "materialization health check")
                    ],
                    MaxOutputTokens: 16),
                cancellationToken))
            {
                if (item.Kind == ChatCompletionStreamEventKind.Content && !string.IsNullOrWhiteSpace(item.Data))
                {
                    sawContent = true;
                    break;
                }
            }

            if (!sawContent)
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.LlmHealthCheckFailed,
                    "Selected LLM health check returned no content.");
            }
        }
        catch (ReferenceMaterializationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.LlmHealthCheckFailed,
                "Selected LLM health check failed.");
        }

        return new ReferenceMaterializationModelIdentityPayload(selected.Provider, selected.ModelId);
    }

    private async ValueTask<ReferenceMaterializationModelIdentityPayload> VerifyEmbeddingAsync(CancellationToken cancellationToken)
    {
        var options = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken);
        if (options is null || string.IsNullOrWhiteSpace(options.ProviderKey) || string.IsNullOrWhiteSpace(options.ModelId))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingNotConfigured,
                "Materialization requires a configured embedding model.");
        }

        try
        {
            var result = await _embeddings.EmbedAsync(["materialization health check"], options, cancellationToken);
            if (result.Dimensions <= 0 || result.Items.Count != 1)
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                    "Embedding health check returned an invalid vector batch.");
            }

            var item = result.Items[0];
            if (item.Index != 0 || item.Vector.Count != result.Dimensions || item.Vector.Any(value => !float.IsFinite(value)))
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                    "Embedding health check returned an invalid vector.");
            }

            return new ReferenceMaterializationModelIdentityPayload(options.ProviderKey, options.ModelId, result.Dimensions);
        }
        catch (ReferenceMaterializationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                "Embedding health check failed.");
        }
    }

    private static SelectedModel? ParseSelectedModel(string value)
    {
        var parts = (value ?? string.Empty).Split('/', 2, StringSplitOptions.None);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        return new SelectedModel(parts[0].Trim().ToLowerInvariant(), parts[1].Trim());
    }

    private sealed record SelectedModel(string Provider, string ModelId);
}
