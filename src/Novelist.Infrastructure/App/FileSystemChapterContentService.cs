using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemChapterContentService : IChapterContentService
{
    private const int MaxTitleLength = 200;
    private const int MaxContentPathLength = 512;
    private const int MaxChapterNumber = 999_999;

    private static readonly Regex AllowedContentPathPattern = new(
        @"^(novelist\.md|chapters/\d{3,6}\.md|outlines/\d{3,6}\.md|skills/[^/\\]+\.md|plans/(大纲|部纲|细纲)\.md)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 计划镜像文件由 PlanningService 在保存槽位时单向写出；编辑器可读，改动以时间线面板保存为准。
    public static readonly string[] PlanMirrorPaths = ["plans/大纲.md", "plans/部纲.md", "plans/细纲.md"];

    private static readonly Regex ChapterPathPattern = new(
        @"^chapters/(\d{3,6})\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 大纲伴生文件与正文同生命周期：删除章节时两者一并移除，守卫也须一并覆盖（R3）。
    private static readonly Regex OutlinePathPattern = new(
        @"^outlines/(\d{3,6})\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UserSkillPathPattern = new(
        @"^~/\.novelist/skills/([^/\\:]+)\.md$",
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
    private readonly IVersionControlService _versionControl;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemChapterContentService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IWritingDeltaRecorder? writingDeltaRecorder = null,
        IRagIndexRefreshNotifier? ragRefreshNotifier = null,
        IVersionControlService? versionControl = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _writingDeltaRecorder = writingDeltaRecorder;
        _ragRefreshNotifier = ragRefreshNotifier;
        _versionControl = versionControl ?? new GitVersionControlService(_options);
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
            // O19：与 AllocateChapterNumber 同口径——删除留下的高水位也算"历史最高章号"，
            // 时间线/卷轴的"下一章"推导才不会与实际分配错位（章号永不复用）。
            var maxExisting = store.Items.Count == 0 ? 0 : store.Items.Max(chapter => chapter.ChapterNumber);
            return Math.Max(maxExisting, store.NextChapterNumber - 1);
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

            await _versionControl.CommitIfChangedAsync(
                input.NovelId,
                $"create chapter {chapterNumber.ToString("D3", CultureInfo.InvariantCulture)}",
                cancellationToken);
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

    public async ValueTask DeleteChapterAsync(
        DeleteChapterPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        string? contentRelativePath = null;
        string? outlineRelativePath = null;
        string? contentFullPath = null;
        string? outlineFullPath = null;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(input.NovelId, cancellationToken);
            var index = store.Items.FindIndex(chapter => chapter.Id == input.ChapterId);
            if (index < 0)
            {
                throw new ArgumentException($"Chapter '{input.ChapterId}' does not exist.", nameof(input));
            }

            var chapter = store.Items[index];
            store.Items.RemoveAt(index);

            // 软删除留痕 + 高水位：章节号永不重排、永不复用（O7 产品决策）。
            store.DeletedItems.Add(new ChapterStoreDocument.DeletedChapterRecord(
                chapter.Id,
                chapter.ChapterNumber,
                chapter.Title,
                chapter.FilePath,
                DateTimeOffset.UtcNow));
            if (chapter.ChapterNumber >= store.NextChapterNumber)
            {
                store.NextChapterNumber = chapter.ChapterNumber + 1;
            }

            contentRelativePath = chapter.FilePath;
            outlineRelativePath = $"outlines/{chapter.ChapterNumber.ToString("D3", CultureInfo.InvariantCulture)}.md";
            contentFullPath = await ResolveWorkspaceFilePathAsync(input.NovelId, contentRelativePath, cancellationToken);
            outlineFullPath = await ResolveWorkspaceFilePathAsync(input.NovelId, outlineRelativePath, cancellationToken);

            await SaveAsync(input.NovelId, store, cancellationToken);

            // 删除的章节字数要从写作统计里扣掉（N6），否则速度数据永远虚高。
            // 统计是派生数据：扣减失败不得中断删除（元数据已持久化，中断会让
            // 文件清理、stale 标记与版本提交全部丢失），按尽力而为处理。
            if (_writingDeltaRecorder is not null && chapter.WordCount != 0)
            {
                try
                {
                    await _writingDeltaRecorder.RecordWordDeltaAsync(
                        input.NovelId,
                        chapter.Id,
                        -chapter.WordCount,
                        cancellationToken);
                }
                catch
                {
                    // 写作速度数据缺失一次扣减可接受；删除主流程必须继续。
                }
            }

            try
            {
                if (File.Exists(contentFullPath))
                {
                    File.Delete(contentFullPath);
                }
                if (File.Exists(outlineFullPath))
                {
                    File.Delete(outlineFullPath);
                }
            }
            catch
            {
                // 元数据已持久化、列表已隐藏该章；残留文件无法再通过保存放大
                // （SaveContentAsync 有章节库守卫），留待作者手动清理。
            }

            // stale 标记先于 git 提交：提交失败时索引也不能继续服务已删正文（O16）。
            await TryMarkRagIndexStaleAsync(input.NovelId, contentRelativePath);
            await TryMarkRagIndexStaleAsync(input.NovelId, outlineRelativePath);

            await _versionControl.CommitIfChangedAsync(
                input.NovelId,
                $"delete chapter {chapter.ChapterNumber.ToString("D3", CultureInfo.InvariantCulture)}",
                cancellationToken);
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
        var shouldCommitRepositoryChanges = !IsUserSkillPath(relativePath);
        var chapterNumber = ParseChapterNumber(relativePath);
        // R3：大纲伴生文件与正文同一守卫——否则 Agent 编辑或残留 tab 仍可把已删章节的大纲复活成孤儿。
        var guardedChapterNumber = chapterNumber ?? ParseOutlineChapterNumber(relativePath);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            // O15/R3：章节正文与大纲都必须先过章节库守卫再写盘——章号已删除/不存在时拒绝保存，
            // 防止编辑器残留 tab、自动保存或 Agent 编辑把已删除章节复活成无元数据的孤儿文件。
            ChapterStoreDocument? store = null;
            if (guardedChapterNumber is not null)
            {
                store = await LoadOrCreateAsync(input.NovelId, cancellationToken);
                if (store.Items.FindIndex(chapter => chapter.ChapterNumber == guardedChapterNumber.Value) < 0)
                {
                    throw new ArgumentException(
                        $"Chapter {guardedChapterNumber.Value.ToString(CultureInfo.InvariantCulture)} does not exist (it may have been deleted). " +
                        "Create a new chapter instead of saving to the retired chapter file.",
                        nameof(input));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            // U1：携带基线令牌的保存走比较-交换语义。磁盘内容已不是调用方读到的那份
            // （第二窗口、外部编辑器、Agent 直写等绕过 file:changed 事件流的写入）时拒绝覆盖，
            // 返回 CONTENT_CONFLICT 让前端既有冲突条接管，避免静默丢正文。
            if (!string.IsNullOrWhiteSpace(input.BaselineHash))
            {
                var diskContent = File.Exists(fullPath)
                    ? await File.ReadAllTextAsync(fullPath, cancellationToken)
                    : string.Empty;
                var diskHash = ChapterContentBaselineHash.Compute(diskContent);
                if (!string.Equals(diskHash, input.BaselineHash.Trim(), StringComparison.Ordinal))
                {
                    throw new BridgeRequestException(
                        BridgeErrorCodes.ContentConflict,
                        $"Content for '{relativePath}' changed on disk after it was loaded.",
                        new Dictionary<string, string>
                        {
                            ["path"] = relativePath,
                            ["disk_hash"] = diskHash,
                        });
                }
            }

            await File.WriteAllTextAsync(fullPath, input.Content, cancellationToken);

            if (store is not null && chapterNumber is not null)
            {
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

        if (shouldCommitRepositoryChanges)
        {
            await _versionControl.CommitIfChangedAsync(
                input.NovelId,
                $"update {relativePath}",
                cancellationToken);
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
        // 删除留下的章节号进入高水位（NextChapterNumber），保证不重排也不复用。
        // NextChapterNumber 语义是"下一个可分配的新章号"（删除尾章时 = 被删章号 + 1），
        // 因此取 max(高水位, 现存最大章号 + 1)——直接 +1 会在尾删场景永久跳过一个从未使用的章号（N7）。
        var maxExisting = store.Items.Count == 0 ? 0 : store.Items.Max(chapter => chapter.ChapterNumber);
        var next = Math.Max(store.NextChapterNumber, maxExisting + 1);
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
                "Allowed paths are novelist.md, chapters/001.md..chapters/999999.md, outlines/001.md..outlines/999999.md, and skills/<name>.md.");
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

    private static int? ParseOutlineChapterNumber(string relativePath)
    {
        var match = OutlinePathPattern.Match(relativePath);
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

        [JsonPropertyName("next_chapter_number")]
        public int NextChapterNumber { get; set; }

        [JsonPropertyName("deleted_items")]
        public List<DeletedChapterRecord> DeletedItems { get; set; } = [];

        // 软删除留痕：章节号与元数据保留在案，正文经版本历史（git）可追溯。
        public sealed record DeletedChapterRecord(
            long ChapterId,
            int ChapterNumber,
            string Title,
            string FilePath,
            DateTimeOffset DeletedAt);
    }
}
