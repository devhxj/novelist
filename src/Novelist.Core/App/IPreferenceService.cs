using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IPreferenceService
{
    ValueTask<PreferenceResultPayload> GetPreferencesAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<PreferenceItemPayload> CreatePreferenceAsync(
        long novelId,
        CreatePreferencePayload input,
        CancellationToken cancellationToken);

    ValueTask<PreferenceItemPayload> UpdatePreferenceAsync(
        long preferenceId,
        UpdatePreferencePayload input,
        CancellationToken cancellationToken);

    ValueTask DeletePreferenceAsync(long preferenceId, CancellationToken cancellationToken);
}
