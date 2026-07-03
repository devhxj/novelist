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
    private const string CoverFileName = "cover.jpg";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly IAppSettingsService _settings;
    private readonly IVersionControlService _versionControl;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemNovelService(
        AppInitializationOptions? options = null,
        IAppSettingsService? settings = null,
        IVersionControlService? versionControl = null)
    {
        _options = options ?? new AppInitializationOptions();
        _settings = settings ?? new FileSystemAppSettingsService(_options);
        _versionControl = versionControl ?? new GitVersionControlService(_options);
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

            try
            {
                await _versionControl.EnsureRepositoryAsync(id, cancellationToken);
                store.Items.Add(novel);
                store.NextId = checked(id + 1);
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
                ClearReadOnlyAttributes(workspace);
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

    public async ValueTask SaveCoverAsync(
        long novelId,
        IReadOnlyList<byte> data,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var coverBytes = ValidateCoverBytes(data);
        var shouldCommit = false;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindNovelIndex(store, novelId);
            var coverPath = await CoverPathAsync(novelId, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(coverPath)!);

            var tempPath = $"{coverPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, coverBytes, cancellationToken);
                File.Move(tempPath, coverPath, overwrite: true);
                shouldCommit = true;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            _mutex.Release();
        }

        if (shouldCommit)
        {
            await _versionControl.CommitIfChangedAsync(novelId, "update cover", cancellationToken);
        }
    }

    public async ValueTask DeleteCoverAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var shouldCommit = false;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindNovelIndex(store, novelId);
            var coverPath = await CoverPathAsync(novelId, cancellationToken);
            if (File.Exists(coverPath))
            {
                File.Delete(coverPath);
                shouldCommit = true;
            }
        }
        finally
        {
            _mutex.Release();
        }

        if (shouldCommit)
        {
            await _versionControl.CommitIfChangedAsync(novelId, "remove cover", cancellationToken);
        }
    }

    public async ValueTask<NovelCoverFile?> GetCoverAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindNovelIndex(store, novelId);
            var coverPath = await CoverPathAsync(novelId, cancellationToken);
            if (!File.Exists(coverPath))
            {
                return null;
            }

            var info = new FileInfo(coverPath);
            if (info.Length <= 0 || info.Length > NovelCoverConstraints.MaxBytes)
            {
                return null;
            }

            var probe = new byte[(int)Math.Min(16L, info.Length)];
            await using (var stream = File.OpenRead(coverPath))
            {
                _ = await stream.ReadAsync(probe, cancellationToken);
            }

            var contentType = DetectCoverContentType(probe);
            return new NovelCoverFile(
                coverPath,
                contentType,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        finally
        {
            _mutex.Release();
        }
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

    private async ValueTask<string> CoverPathAsync(long novelId, CancellationToken cancellationToken)
    {
        return SafeChildPath(await NovelWorkspacePathAsync(novelId, cancellationToken), CoverFileName);
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

    private static byte[] ValidateCoverBytes(IReadOnlyList<byte>? data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (data.Count == 0)
        {
            throw new ArgumentException("Cover image data is required.", nameof(data));
        }

        if (data.Count > NovelCoverConstraints.MaxBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                data.Count,
                $"Cover image must be at most {NovelCoverConstraints.MaxBytes} bytes.");
        }

        var bytes = data as byte[] ?? data.ToArray();
        _ = DetectCoverContentType(bytes);
        return bytes;
    }

    private static string DetectCoverContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xD8 &&
            bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x4E &&
            bytes[3] == 0x47 &&
            bytes[4] == 0x0D &&
            bytes[5] == 0x0A &&
            bytes[6] == 0x1A &&
            bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' &&
            bytes[1] == (byte)'I' &&
            bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' &&
            bytes[9] == (byte)'E' &&
            bytes[10] == (byte)'B' &&
            bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' &&
            bytes[1] == (byte)'I' &&
            bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' &&
            (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') &&
            bytes[5] == (byte)'a')
        {
            return "image/gif";
        }

        throw new ArgumentException("Cover image must be JPEG, PNG, WebP, or GIF.");
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
                ClearReadOnlyAttributes(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Preserve the original persistence failure; orphan cleanup can be retried manually.
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch
            {
                // Best-effort cleanup; callers still get the actual delete error if removal fails.
            }
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
