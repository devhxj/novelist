using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface ILlmConfigurationService
{
    ValueTask<LlmConfigViewPayload> GetConfigAsync(CancellationToken cancellationToken);

    ValueTask SaveConfigAsync(LlmConfigViewPayload input, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AvailableModelPayload>> GetModelsAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ModelInfoPayload>> DiscoverModelsAsync(
        string chatUrl,
        string apiKey,
        CancellationToken cancellationToken);

    ValueTask TestConnectionAsync(TestConnectionPayload input, CancellationToken cancellationToken);
}
