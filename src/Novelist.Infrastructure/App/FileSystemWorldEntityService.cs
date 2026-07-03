using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemWorldEntityService : IWorldEntityService
{
    private const int MaxNameLength = 200;
    private const int MaxShortTextLength = 512;
    private const int MaxLongTextLength = 20_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemWorldEntityService(
        AppInitializationOptions? options = null,
        INovelService? novels = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<IReadOnlyList<CharacterPayload>> GetCharactersAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.Characters
                .Where(character => character.NovelId == novelId)
                .OrderBy(character => character.Name, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<CharacterRelationPayload>> GetCharacterRelationsAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.CharacterRelations
                .Where(relation => relation.NovelId == novelId && relation.IsCurrent)
                .OrderBy(relation => relation.Id)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<CharacterPayload> CreateCharacterAsync(
        long novelId,
        CreateCharacterPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        var name = NormalizeRequiredText(input.Name, nameof(input.Name), MaxNameLength);
        var description = NormalizeOptionalText(input.Description, nameof(input.Description), MaxLongTextLength);
        var personality = NormalizeOptionalText(input.Personality, nameof(input.Personality), MaxLongTextLength);
        var abilities = NormalizeOptionalText(input.Abilities, nameof(input.Abilities), MaxLongTextLength);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var id = AllocateId(store.NextCharacterId, store.Characters.Select(character => character.Id), "Character");
            var now = DateTimeOffset.UtcNow;
            var character = new CharacterPayload(id, novelId, name, description, personality, abilities, now, now);
            store.Characters.Add(character);
            store.NextCharacterId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
            return character;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask UpdateCharacterAsync(
        long novelId,
        long characterId,
        UpdateCharacterPayload input,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(characterId, nameof(characterId));
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindCharacterIndex(store, novelId, characterId);
            var current = store.Characters[index];
            store.Characters[index] = current with
            {
                Name = ShouldApply(input.Name)
                    ? NormalizeRequiredText(input.Name, nameof(input.Name), MaxNameLength)
                    : current.Name,
                Description = ShouldApply(input.Description)
                    ? NormalizeOptionalText(input.Description, nameof(input.Description), MaxLongTextLength)
                    : current.Description,
                Personality = ShouldApply(input.Personality)
                    ? NormalizeOptionalText(input.Personality, nameof(input.Personality), MaxLongTextLength)
                    : current.Personality,
                Abilities = ShouldApply(input.Abilities)
                    ? NormalizeOptionalText(input.Abilities, nameof(input.Abilities), MaxLongTextLength)
                    : current.Abilities,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteCharacterAsync(long novelId, long characterId, CancellationToken cancellationToken)
    {
        ValidateEntityId(characterId, nameof(characterId));
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindCharacterIndex(store, novelId, characterId);
            store.Characters.RemoveAll(character => character.NovelId == novelId && character.Id == characterId);
            store.CharacterRelations.RemoveAll(relation =>
                relation.NovelId == novelId &&
                (relation.SourceCharacterId == characterId || relation.TargetCharacterId == characterId));
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<LocationPayload>> GetLocationsAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.Locations
                .Where(location => location.NovelId == novelId)
                .OrderBy(location => location.Name, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<LocationRelationPayload>> GetLocationRelationsAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.LocationRelations
                .Where(relation => relation.NovelId == novelId)
                .OrderBy(relation => relation.RelationType, StringComparer.Ordinal)
                .ThenBy(relation => relation.Id)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<LocationPayload> CreateLocationAsync(
        long novelId,
        CreateLocationPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        var name = NormalizeRequiredText(input.Name, nameof(input.Name), MaxNameLength);
        var locationType = NormalizeOptionalText(input.LocationType, nameof(input.LocationType), MaxShortTextLength);
        var description = NormalizeOptionalText(input.Description, nameof(input.Description), MaxLongTextLength);
        var detailJson = NormalizeOptionalText(input.DetailJson, nameof(input.DetailJson), MaxLongTextLength);
        var tags = NormalizeOptionalText(input.Tags, nameof(input.Tags), MaxLongTextLength);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            if (input.ParentLocationId is not null)
            {
                EnsureLocationExists(store, novelId, input.ParentLocationId.Value, nameof(input.ParentLocationId));
            }

            var id = AllocateId(store.NextLocationId, store.Locations.Select(location => location.Id), "Location");
            var now = DateTimeOffset.UtcNow;
            var location = new LocationPayload(
                id,
                novelId,
                name,
                locationType,
                description,
                detailJson,
                input.ParentLocationId,
                tags,
                now,
                now);
            store.Locations.Add(location);
            store.NextLocationId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
            return location;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask UpdateLocationAsync(
        long novelId,
        long locationId,
        UpdateLocationPayload input,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(locationId, nameof(locationId));
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindLocationIndex(store, novelId, locationId);
            var current = store.Locations[index];
            long? parentLocationId = current.ParentLocationId;
            if (input.ClearParent)
            {
                parentLocationId = null;
            }

            if (input.ParentLocationId is not null)
            {
                if (input.ParentLocationId.Value == locationId)
                {
                    throw new ArgumentException("A location cannot be its own parent.", nameof(input.ParentLocationId));
                }

                EnsureLocationExists(store, novelId, input.ParentLocationId.Value, nameof(input.ParentLocationId));
                parentLocationId = input.ParentLocationId;
            }

            store.Locations[index] = current with
            {
                Name = ShouldApply(input.Name)
                    ? NormalizeRequiredText(input.Name, nameof(input.Name), MaxNameLength)
                    : current.Name,
                LocationType = ShouldApply(input.LocationType)
                    ? NormalizeOptionalText(input.LocationType, nameof(input.LocationType), MaxShortTextLength)
                    : current.LocationType,
                Description = ShouldApply(input.Description)
                    ? NormalizeOptionalText(input.Description, nameof(input.Description), MaxLongTextLength)
                    : current.Description,
                DetailJson = ShouldApply(input.DetailJson)
                    ? NormalizeOptionalText(input.DetailJson, nameof(input.DetailJson), MaxLongTextLength)
                    : current.DetailJson,
                ParentLocationId = parentLocationId,
                Tags = ShouldApply(input.Tags)
                    ? NormalizeOptionalText(input.Tags, nameof(input.Tags), MaxLongTextLength)
                    : current.Tags,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteLocationAsync(long novelId, long locationId, CancellationToken cancellationToken)
    {
        ValidateEntityId(locationId, nameof(locationId));
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindLocationIndex(store, novelId, locationId);
            store.Locations.RemoveAll(location => location.NovelId == novelId && location.Id == locationId);
            for (var i = 0; i < store.Locations.Count; i++)
            {
                if (store.Locations[i].NovelId == novelId && store.Locations[i].ParentLocationId == locationId)
                {
                    store.Locations[i] = store.Locations[i] with
                    {
                        ParentLocationId = null,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                }
            }

            store.LocationRelations.RemoveAll(relation =>
                relation.NovelId == novelId &&
                (relation.LocationAId == locationId || relation.LocationBId == locationId));
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<WorldEntityStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new WorldEntityStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<WorldEntityStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("World entity store is empty or malformed.");

        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(WorldEntityStoreDocument store, CancellationToken cancellationToken)
    {
        ValidateStore(store);

        var path = await StorePathAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken), "world", "index.json");
    }

    private static long AllocateId(long nextId, IEnumerable<long> existingIds, string label)
    {
        var ids = existingIds.ToArray();
        var maxExisting = ids.Length == 0 ? 0 : ids.Max();
        var allocated = Math.Max(nextId, maxExisting + 1);
        if (allocated <= 0 || allocated == long.MaxValue)
        {
            throw new InvalidOperationException($"{label} id allocation is exhausted.");
        }

        return allocated;
    }

    private static int FindCharacterIndex(WorldEntityStoreDocument store, long novelId, long characterId)
    {
        var index = store.Characters.FindIndex(character => character.NovelId == novelId && character.Id == characterId);
        if (index < 0)
        {
            throw new ArgumentException($"Character '{characterId}' does not exist.", nameof(characterId));
        }

        return index;
    }

    private static int FindLocationIndex(WorldEntityStoreDocument store, long novelId, long locationId)
    {
        var index = store.Locations.FindIndex(location => location.NovelId == novelId && location.Id == locationId);
        if (index < 0)
        {
            throw new ArgumentException($"Location '{locationId}' does not exist.", nameof(locationId));
        }

        return index;
    }

    private static void EnsureLocationExists(
        WorldEntityStoreDocument store,
        long novelId,
        long locationId,
        string argumentName)
    {
        ValidateEntityId(locationId, argumentName);
        if (!store.Locations.Any(location => location.NovelId == novelId && location.Id == locationId))
        {
            throw new ArgumentException($"Location '{locationId}' does not exist.", argumentName);
        }
    }

    private static bool ShouldApply(string? value)
    {
        return !string.IsNullOrEmpty(value);
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Value must not contain control characters.", name);
        }

        return normalized;
    }

    private static void ValidateStore(WorldEntityStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported world entity store version '{store.Version}'.");
        }

        ValidateNextId(store.NextCharacterId, nameof(store.NextCharacterId));
        ValidateNextId(store.NextCharacterRelationId, nameof(store.NextCharacterRelationId));
        ValidateNextId(store.NextLocationId, nameof(store.NextLocationId));
        ValidateNextId(store.NextLocationRelationId, nameof(store.NextLocationRelationId));

        if (store.Characters.Any(character => character.Id <= 0 || character.NovelId <= 0) ||
            store.CharacterRelations.Any(relation => relation.Id <= 0 || relation.NovelId <= 0) ||
            store.Locations.Any(location => location.Id <= 0 || location.NovelId <= 0) ||
            store.LocationRelations.Any(relation => relation.Id <= 0 || relation.NovelId <= 0))
        {
            throw new InvalidOperationException("World entity store contains invalid ids.");
        }

        EnsureUnique(store.Characters.Select(character => character.Id), "character");
        EnsureUnique(store.CharacterRelations.Select(relation => relation.Id), "character relation");
        EnsureUnique(store.Locations.Select(location => location.Id), "location");
        EnsureUnique(store.LocationRelations.Select(relation => relation.Id), "location relation");
    }

    private static void ValidateNextId(long value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"{name} must be positive.");
        }
    }

    private static void EnsureUnique(IEnumerable<long> values, string label)
    {
        var ids = values.ToArray();
        if (ids.Distinct().Count() != ids.Length)
        {
            throw new InvalidOperationException($"World entity store contains duplicate {label} ids.");
        }
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static void ValidateEntityId(long entityId, string argumentName)
    {
        if (entityId <= 0)
        {
            throw new ArgumentOutOfRangeException(argumentName, entityId, "Entity id must be positive.");
        }
    }

    private sealed class WorldEntityStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_character_id")]
        public long NextCharacterId { get; set; } = 1;

        [JsonPropertyName("next_character_relation_id")]
        public long NextCharacterRelationId { get; set; } = 1;

        [JsonPropertyName("next_location_id")]
        public long NextLocationId { get; set; } = 1;

        [JsonPropertyName("next_location_relation_id")]
        public long NextLocationRelationId { get; set; } = 1;

        [JsonPropertyName("characters")]
        public List<CharacterPayload> Characters { get; set; } = [];

        [JsonPropertyName("character_relations")]
        public List<CharacterRelationPayload> CharacterRelations { get; set; } = [];

        [JsonPropertyName("locations")]
        public List<LocationPayload> Locations { get; set; } = [];

        [JsonPropertyName("location_relations")]
        public List<LocationRelationPayload> LocationRelations { get; set; } = [];
    }
}
