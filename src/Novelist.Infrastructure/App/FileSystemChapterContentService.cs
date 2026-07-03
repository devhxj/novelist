using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemChapterContentService : IChapterContentService
{
    private const int MaxTitleLength = 200;
    private const int MaxContentPathLength = 512;
    private const int MaxChapterNumber = 999_999;

    private static readonly Regex AllowedContentPathPattern = new(
        @"^(goink\.md|chapters/\d{3,6}\.md|outlines/\d{3,6}\.md|skills/[^/\\]+\.md)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChapterPathPattern = new(
        @"^chapters/(\d{3,6})\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UserSkillPathPattern = new(
        @"^~/\.goink/skills/([^/\\:]+)\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BuiltinSkillPathPattern = new(
        @"^/builtin/skills/([^/\\:]+)\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnglishWordPattern = new(
        "[a-zA-Z]+(?:'[a-zA-Z]+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IWritingDeltaRecorder? _writingDeltaRecorder;
    private readonly IRagIndexRefreshNotifier? _ragRefreshNotifier;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemChapterContentService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IWritingDeltaRecorder? writingDeltaRecorder = null,
        IRagIndexRefreshNotifier? ragRefreshNotifier = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _writingDeltaRecorder = writingDeltaRecorder;
        _ragRefreshNotifier = ragRefreshNotifier;
    }

    public async ValueTask<IReadOnlyList<ChapterPayload>> GetChaptersAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(novelId, cancellationToken);
            return store.Items
                .OrderBy(chapter => chapter.ChapterNumber)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<int> GetMaxChapterNumberAsync(long novelId, CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(novelId, cancellationToken);
            return store.Items.Count == 0 ? 0 : store.Items.Max(chapter => chapter.ChapterNumber);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ChapterPayload> CreateChapterAsync(
        CreateChapterPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var title = NormalizeRequiredText(input.Title, nameof(input.Title), MaxTitleLength);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(input.NovelId, cancellationToken);
            var chapterNumber = AllocateChapterNumber(store);
            var id = AllocateId(store);
            var now = DateTimeOffset.UtcNow;
            var filePath = ChapterPath(chapterNumber);
            var chapter = new ChapterPayload(
                id,
                input.NovelId,
                chapterNumber,
                title,
                Summary: string.Empty,
                WordCount: 0,
                CreatedAt: now,
                UpdatedAt: now,
                FilePath: filePath);

            var fullPath = await ResolveWorkspaceFilePathAsync(input.NovelId, filePath, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (!File.Exists(fullPath))
            {
                await File.WriteAllTextAsync(fullPath, string.Empty, cancellationToken);
            }

            store.Items.Add(chapter);
            store.NextId = checked(id + 1);

            try
            {
                await SaveAsync(input.NovelId, store, cancellationToken);
            }
            catch
            {
                TryDeleteFile(fullPath);
                throw;
            }

            return chapter;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask UpdateChapterTitleAsync(
        long novelId,
        int chapterNumber,
        string title,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ValidateChapterNumber(chapterNumber);
        var normalizedTitle = NormalizeRequiredText(title, nameof(title), MaxTitleLength);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(novelId, cancellationToken);
            var index = FindChapterIndex(store, chapterNumber);
            store.Items[index] = store.Items[index] with
            {
                Title = normalizedTitle,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveAsync(novelId, store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<string> GetContentAsync(
        long novelId,
        string path,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        var relativePath = NormalizeContentPath(path);
        if (TryReadBuiltinSkillContent(relativePath, out var builtinContent))
        {
            return builtinContent;
        }

        var fullPath = await ResolveContentFilePathAsync(novelId, relativePath, cancellationToken);

        if (!File.Exists(fullPath))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    public async ValueTask SaveContentAsync(
        SaveContentPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ArgumentNullException.ThrowIfNull(input.Content);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var relativePath = NormalizeContentPath(input.Path);
        if (IsBuiltinSkillPath(relativePath))
        {
            throw new InvalidContentPathException(relativePath, "Builtin skills are read-only.");
        }

        if (IsSkillPath(relativePath))
        {
            _ = SkillDocuments.Parse(input.Content, "user");
        }

        var fullPath = await ResolveContentFilePathAsync(input.NovelId, relativePath, cancellationToken);
        var shouldMarkRagStale = false;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, input.Content, cancellationToken);

            var chapterNumber = ParseChapterNumber(relativePath);
            if (chapterNumber is not null)
            {
                var store = await LoadOrCreateAsync(input.NovelId, cancellationToken);
                var index = store.Items.FindIndex(chapter => chapter.ChapterNumber == chapterNumber.Value);
                if (index >= 0)
                {
                    var previous = store.Items[index];
                    var newWordCount = ComputeWordCount(input.Content);
                    store.Items[index] = store.Items[index] with
                    {
                        WordCount = newWordCount,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await SaveAsync(input.NovelId, store, cancellationToken);

                    if (_writingDeltaRecorder is not null)
                    {
                        var delta = newWordCount - previous.WordCount;
                        await _writingDeltaRecorder.RecordWordDeltaAsync(
                            input.NovelId,
                            previous.Id,
                            delta,
                            cancellationToken);
                    }

                    shouldMarkRagStale = true;
                }
            }
        }
        finally
        {
            _mutex.Release();
        }

        if (shouldMarkRagStale)
        {
            await TryMarkRagIndexStaleAsync(input.NovelId, relativePath);
        }
    }

    private async ValueTask TryMarkRagIndexStaleAsync(long novelId, string relativePath)
    {
        if (_ragRefreshNotifier is null)
        {
            return;
        }

        try
        {
            await _ragRefreshNotifier.MarkNovelIndexStaleAsync(
                novelId,
                $"Chapter content changed: {relativePath}",
                CancellationToken.None);
        }
        catch
        {
            // A stale-marker failure must not turn a successfully persisted chapter save into a failed save.
        }
    }

    private async ValueTask<ChapterStoreDocument> LoadOrCreateAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(novelId, cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new ChapterStoreDocument();
            await SaveAsync(novelId, empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<ChapterStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Chapter store is empty or malformed.");

        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(
        long novelId,
        ChapterStoreDocument store,
        CancellationToken cancellationToken)
    {
        ValidateStore(store);

        var path = await StorePathAsync(novelId, cancellationToken);
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

    private async ValueTask<string> StorePathAsync(long novelId, CancellationToken cancellationToken)
    {
        return SafeChildPath(await NovelWorkspacePathAsync(novelId, cancellationToken), "metadata/chapters.json");
    }

    private async ValueTask<string> ResolveWorkspaceFilePathAsync(
        long novelId,
        string path,
        CancellationToken cancellationToken)
    {
        var relativePath = NormalizeContentPath(path);
        return SafeChildPath(await NovelWorkspacePathAsync(novelId, cancellationToken), relativePath);
    }

    private async ValueTask<string> ResolveContentFilePathAsync(
        long novelId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (TryGetUserSkillFileName(relativePath, out var fileName))
        {
            return SafeChildPath(
                await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
                $"skills/{fileName}.md");
        }

        return SafeChildPath(await NovelWorkspacePathAsync(novelId, cancellationToken), relativePath);
    }

    private async ValueTask<string> NovelWorkspacePathAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
        return SafeChildPath(Path.Combine(dataDirectory, "novels"), novelId.ToString(CultureInfo.InvariantCulture));
    }

    private static long AllocateId(ChapterStoreDocument store)
    {
        var maxExisting = store.Items.Count == 0 ? 0 : store.Items.Max(chapter => chapter.Id);
        var nextId = Math.Max(store.NextId, maxExisting + 1);
        if (nextId <= 0 || nextId == long.MaxValue)
        {
            throw new InvalidOperationException("Chapter id allocation is exhausted.");
        }

        return nextId;
    }

    private static int AllocateChapterNumber(ChapterStoreDocument store)
    {
        var next = store.Items.Count == 0 ? 1 : store.Items.Max(chapter => chapter.ChapterNumber) + 1;
        if (next > MaxChapterNumber)
        {
            throw new InvalidOperationException("Chapter number allocation is exhausted.");
        }

        return next;
    }

    private static int FindChapterIndex(ChapterStoreDocument store, int chapterNumber)
    {
        var index = store.Items.FindIndex(chapter => chapter.ChapterNumber == chapterNumber);
        if (index < 0)
        {
            throw new ArgumentException($"Chapter '{chapterNumber}' does not exist.", nameof(chapterNumber));
        }

        return index;
    }

    private static string NormalizeContentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidContentPathException(path ?? string.Empty, "Path is required.");
        }

        var normalized = path.Trim();
        if (normalized.Length > MaxContentPathLength)
        {
            throw new InvalidContentPathException(path, $"Path must be at most {MaxContentPathLength} characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new InvalidContentPathException(path, "Path must not contain control characters.");
        }

        if (normalized.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidContentPathException(path, "Use forward slashes in content paths.");
        }

        if (IsBuiltinSkillPath(normalized) || IsUserSkillPath(normalized))
        {
            return normalized;
        }

        if (Path.IsPathRooted(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.StartsWith("~/", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidContentPathException(path, "Absolute and workspace-external paths are not allowed.");
        }

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidContentPathException(path, "Parent-directory and empty path segments are not allowed.");
        }

        if (!AllowedContentPathPattern.IsMatch(normalized))
        {
            throw new InvalidContentPathException(
                path,
                "Allowed paths are goink.md, chapters/001.md..chapters/999999.md, outlines/001.md..outlines/999999.md, and skills/<name>.md.");
        }

        return normalized;
    }

    private static bool IsSkillPath(string path)
    {
        return path.StartsWith("skills/", StringComparison.Ordinal) ||
            IsUserSkillPath(path) ||
            IsBuiltinSkillPath(path);
    }

    private static bool IsUserSkillPath(string path)
    {
        return UserSkillPathPattern.IsMatch(path);
    }

    private static bool IsBuiltinSkillPath(string path)
    {
        return BuiltinSkillPathPattern.IsMatch(path);
    }

    private static bool TryGetUserSkillFileName(string path, out string fileName)
    {
        var match = UserSkillPathPattern.Match(path);
        if (!match.Success)
        {
            fileName = string.Empty;
            return false;
        }

        fileName = SkillDocuments.NormalizeSkillName(match.Groups[1].Value);
        return true;
    }

    private static bool TryReadBuiltinSkillContent(string path, out string content)
    {
        var match = BuiltinSkillPathPattern.Match(path);
        if (!match.Success)
        {
            content = string.Empty;
            return false;
        }

        var name = match.Groups[1].Value;
        var skill = SkillDocuments.LoadBuiltin().FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal));
        content = skill?.RawContent ?? string.Empty;
        return skill is not null;
    }

    private static string SafeChildPath(string parentDirectory, string relativePath)
    {
        var parent = Path.GetFullPath(parentDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(parent, relativePath));
        var parentWithSeparator = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(parentWithSeparator, comparison))
        {
            throw new InvalidContentPathException(relativePath, "Resolved path escapes the novelist workspace.");
        }

        return fullPath;
    }

    private static int? ParseChapterNumber(string relativePath)
    {
        var match = ChapterPathPattern.Match(relativePath);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string ChapterPath(int chapterNumber)
    {
        ValidateChapterNumber(chapterNumber);
        return $"chapters/{chapterNumber:000}.md";
    }

    private static int ComputeWordCount(string content)
    {
        var chineseChars = content.Count(IsChineseCharacter);
        return chineseChars + EnglishWordPattern.Matches(content).Count;
    }

    private static bool IsChineseCharacter(char value)
    {
        return value is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uF900' and <= '\uFAFF';
    }

    private static void ValidateStore(ChapterStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported chapter store version '{store.Version}'.");
        }

        if (store.NextId <= 0)
        {
            throw new InvalidOperationException("Chapter store next_id must be positive.");
        }

        if (store.Items.Any(chapter => chapter.Id <= 0 || chapter.NovelId <= 0))
        {
            throw new InvalidOperationException("Chapter store contains invalid ids.");
        }

        if (store.Items.Any(chapter => chapter.ChapterNumber is <= 0 or > MaxChapterNumber))
        {
            throw new InvalidOperationException("Chapter store contains invalid chapter numbers.");
        }

        if (store.Items.Select(chapter => chapter.Id).Distinct().Count() != store.Items.Count)
        {
            throw new InvalidOperationException("Chapter store contains duplicate ids.");
        }

        if (store.Items.Select(chapter => chapter.ChapterNumber).Distinct().Count() != store.Items.Count)
        {
            throw new InvalidOperationException("Chapter store contains duplicate chapter numbers.");
        }
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static void ValidateChapterNumber(int chapterNumber)
    {
        if (chapterNumber is <= 0 or > MaxChapterNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chapterNumber),
                chapterNumber,
                $"Chapter number must be between 1 and {MaxChapterNumber}.");
        }
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original metadata persistence failure.
        }
    }

    private sealed class ChapterStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<ChapterPayload> Items { get; set; } = [];
    }
}
