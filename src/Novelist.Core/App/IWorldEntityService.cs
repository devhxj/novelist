using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IWorldEntityService
{
    ValueTask<IReadOnlyList<CharacterPayload>> GetCharactersAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CharacterRelationPayload>> GetCharacterRelationsAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<CharacterPayload> CreateCharacterAsync(
        long novelId,
        CreateCharacterPayload input,
        CancellationToken cancellationToken);

    ValueTask UpdateCharacterAsync(
        long novelId,
        long characterId,
        UpdateCharacterPayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteCharacterAsync(long novelId, long characterId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<LocationPayload>> GetLocationsAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<LocationRelationPayload>> GetLocationRelationsAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<LocationPayload> CreateLocationAsync(
        long novelId,
        CreateLocationPayload input,
        CancellationToken cancellationToken);

    ValueTask UpdateLocationAsync(
        long novelId,
        long locationId,
        UpdateLocationPayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteLocationAsync(long novelId, long locationId, CancellationToken cancellationToken);
}
