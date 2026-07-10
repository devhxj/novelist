using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemPreferenceService : IPreferenceService
{
    private const int MaxCategoryLength = 128;
    private const int MaxContentLength = 20_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemPreferenceService(
        AppInitializationOptions? options = null,
        INovelService? novels = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<PreferenceResultPayload> GetPreferencesAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var global = store.Items
                .Where(item => item.IsGlobal)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToArray();
            var novel = store.Items
                .Where(item => !item.IsGlobal && item.NovelId == novelId)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToArray();

            return new PreferenceResultPayload(global, novel);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<PreferenceItemPayload> CreatePreferenceAsync(
        long novelId,
        CreatePreferencePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        var category = NormalizeOptionalText(input.Category, nameof(input.Category), MaxCategoryLength);
        var content = NormalizeRequiredText(input.Content, nameof(input.Content), MaxContentLength);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var id = AllocateId(store);
            var item = new PreferenceItemPayload(
                id,
                novelId,
                input.IsGlobal,
                category,
                content,
                DateTimeOffset.UtcNow);

            store.Items.Add(item);
            store.NextId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
            return item;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<PreferenceItemPayload> UpdatePreferenceAsync(
        long preferenceId,
        UpdatePreferencePayload input,
        CancellationToken cancellationToken)
    {
        ValidatePreferenceId(preferenceId);
        ArgumentNullException.ThrowIfNull(input);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindPreferenceIndex(store, preferenceId);
            var current = store.Items[index];
            var updated = current with
            {
                Category = ShouldApply(input.Category)
                    ? NormalizeOptionalText(input.Category, nameof(input.Category), MaxCategoryLength)
                    : current.Category,
                Content = ShouldApply(input.Content)
                    ? NormalizeRequiredText(input.Content, nameof(input.Content), MaxContentLength)
                    : current.Content,
                IsGlobal = input.IsGlobal ?? current.IsGlobal
            };

            store.Items[index] = updated;
            await SaveAsync(store, cancellationToken);
            return updated;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeletePreferenceAsync(long preferenceId, CancellationToken cancellationToken)
    {
        ValidatePreferenceId(preferenceId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var removed = store.Items.RemoveAll(item => item.Id == preferenceId);
            if (removed > 0)
            {
                await SaveAsync(store, cancellationToken);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<PreferenceStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new PreferenceStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<PreferenceStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Preference store is empty or malformed.");

        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(PreferenceStoreDocument store, CancellationToken cancellationToken)
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
        return Path.Combine(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken), "preferences", "index.json");
    }

    private static long AllocateId(PreferenceStoreDocument store)
    {
        var maxExisting = store.Items.Count == 0 ? 0 : store.Items.Max(item => item.Id);
        var nextId = Math.Max(store.NextId, maxExisting + 1);
        if (nextId <= 0 || nextId == long.MaxValue)
        {
            throw new InvalidOperationException("Preference id allocation is exhausted.");
        }

        return nextId;
    }

    private static int FindPreferenceIndex(PreferenceStoreDocument store, long preferenceId)
    {
        var index = store.Items.FindIndex(item => item.Id == preferenceId);
        if (index < 0)
        {
            throw new ArgumentException($"Preference '{preferenceId}' does not exist.", nameof(preferenceId));
        }

        return index;
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

    private static void ValidateStore(PreferenceStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported preference store version '{store.Version}'.");
        }

        if (store.NextId <= 0)
        {
            throw new InvalidOperationException("Preference store next_id must be positive.");
        }

        if (store.Items.Any(item => item.Id <= 0 || item.NovelId <= 0))
        {
            throw new InvalidOperationException("Preference store contains invalid ids.");
        }

        if (store.Items.Select(item => item.Id).Distinct().Count() != store.Items.Count)
        {
            throw new InvalidOperationException("Preference store contains duplicate ids.");
        }
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static void ValidatePreferenceId(long preferenceId)
    {
        if (preferenceId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferenceId), preferenceId, "Preference id must be positive.");
        }
    }

    private sealed class PreferenceStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<PreferenceItemPayload> Items { get; set; } = [];
    }
}
