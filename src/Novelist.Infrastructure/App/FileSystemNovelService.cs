using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemNovelService : INovelService
{
    private const int MaxTitleLength = 200;
    private const int MaxGenreLength = 128;
    private const int MaxDescriptionLength = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly IAppSettingsService _settings;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemNovelService(
        AppInitializationOptions? options = null,
        IAppSettingsService? settings = null)
    {
        _options = options ?? new AppInitializationOptions();
        _settings = settings ?? new FileSystemAppSettingsService(_options);
    }

    public async ValueTask<IReadOnlyList<NovelPayload>> GetNovelsAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.Items
                .OrderByDescending(novel => novel.UpdatedAt)
                .ThenBy(novel => novel.Id)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NovelPayload> CreateNovelAsync(
        CreateNovelPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var title = NormalizeRequiredText(input.Title, nameof(input.Title), MaxTitleLength);
        var description = NormalizeOptionalText(input.Description, nameof(input.Description), MaxDescriptionLength);
        var genre = NormalizeOptionalText(input.Genre, nameof(input.Genre), MaxGenreLength);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var id = AllocateId(store);
            var now = DateTimeOffset.UtcNow;
            var novel = new NovelPayload(id, title, genre, description, now, now);
            var workspace = await EnsureNovelWorkspaceAsync(id, cancellationToken);

            store.Items.Add(novel);
            store.NextId = checked(id + 1);

            try
            {
                await SaveAsync(store, cancellationToken);
            }
            catch
            {
                TryDeleteDirectory(workspace);
                throw;
            }

            return novel;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NovelPayload> UpdateNovelAsync(
        long novelId,
        UpdateNovelPayload input,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ArgumentNullException.ThrowIfNull(input);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindNovelIndex(store, novelId);
            var current = store.Items[index];

            var updated = current with
            {
                Title = ShouldApply(input.Title)
                    ? NormalizeRequiredText(input.Title!, nameof(input.Title), MaxTitleLength)
                    : current.Title,
                Description = ShouldApply(input.Description)
                    ? NormalizeOptionalText(input.Description, nameof(input.Description), MaxDescriptionLength)
                    : current.Description,
                Genre = ShouldApply(input.Genre)
                    ? NormalizeOptionalText(input.Genre, nameof(input.Genre), MaxGenreLength)
                    : current.Genre,
                UpdatedAt = DateTimeOffset.UtcNow
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

    public async ValueTask DeleteNovelAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindNovelIndex(store, novelId);
            store.Items.RemoveAt(index);

            await SaveAsync(store, cancellationToken);

            var workspace = await NovelWorkspacePathAsync(novelId, cancellationToken);
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }

            var settings = await _settings.GetSettingsAsync(cancellationToken);
            if (settings.LastNovelId == novelId)
            {
                await _settings.SetLastNovelAsync(0, cancellationToken);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask SetActiveNovelAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindNovelIndex(store, novelId);
        }
        finally
        {
            _mutex.Release();
        }

        await _settings.SetLastNovelAsync(novelId, cancellationToken);
    }

    private async ValueTask<NovelStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new NovelStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<NovelStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Novel store is empty or malformed.");

        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(NovelStoreDocument store, CancellationToken cancellationToken)
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

    private async ValueTask<string> EnsureNovelWorkspaceAsync(long novelId, CancellationToken cancellationToken)
    {
        var workspace = await NovelWorkspacePathAsync(novelId, cancellationToken);
        Directory.CreateDirectory(workspace);

        var goinkPath = SafeChildPath(workspace, "goink.md");
        if (!File.Exists(goinkPath))
        {
            await File.WriteAllTextAsync(goinkPath, string.Empty, cancellationToken);
        }

        return workspace;
    }

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return SafeChildPath(await NovelsDirectoryAsync(cancellationToken), "index.json");
    }

    private async ValueTask<string> NovelWorkspacePathAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        return SafeChildPath(await NovelsDirectoryAsync(cancellationToken), novelId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async ValueTask<string> NovelsDirectoryAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken), "novels");
    }

    private static long AllocateId(NovelStoreDocument store)
    {
        var maxExisting = store.Items.Count == 0 ? 0 : store.Items.Max(novel => novel.Id);
        var nextId = Math.Max(store.NextId, maxExisting + 1);
        if (nextId <= 0 || nextId == long.MaxValue)
        {
            throw new InvalidOperationException("Novel id allocation is exhausted.");
        }

        return nextId;
    }

    private static int FindNovelIndex(NovelStoreDocument store, long novelId)
    {
        var index = store.Items.FindIndex(novel => novel.Id == novelId);
        if (index < 0)
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }

        return index;
    }

    private static void ValidateStore(NovelStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported novel store version '{store.Version}'.");
        }

        if (store.NextId <= 0)
        {
            throw new InvalidOperationException("Novel store next_id must be positive.");
        }

        if (store.Items.Any(novel => novel.Id <= 0))
        {
            throw new InvalidOperationException("Novel store contains an invalid id.");
        }

        if (store.Items.Select(novel => novel.Id).Distinct().Count() != store.Items.Count)
        {
            throw new InvalidOperationException("Novel store contains duplicate ids.");
        }
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
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

    private static string SafeChildPath(string parentDirectory, string childName)
    {
        var parent = Path.GetFullPath(parentDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(parent, childName));
        var parentWithSeparator = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(parentWithSeparator, comparison))
        {
            throw new InvalidOperationException("Resolved path escapes the novelist data directory.");
        }

        return fullPath;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Preserve the original persistence failure; orphan cleanup can be retried manually.
        }
    }

    private sealed class NovelStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<NovelPayload> Items { get; set; } = [];
    }
}
