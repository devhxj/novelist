using System.Globalization;
using System.Text;
using LibGit2Sharp;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class GitVersionControlService : IVersionControlService
{
    private const int MaxCommitMessageLength = 4_000;
    private const int MaxHistoryPageSize = 100;
    private const int MaxDiffTextLength = 200_000;
    private const int MaxContentTextLength = 200_000;
    private const int MaxGitErrorTextLength = 500;
    private static readonly string[] GitLockFileNames = ["index.lock", "HEAD.lock", "config.lock"];

    private readonly AppInitializationOptions _options;
    private readonly IPhase15AppSettingsService _settings;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public GitVersionControlService(
        AppInitializationOptions? options = null,
        IPhase15AppSettingsService? settings = null)
    {
        _options = options ?? new AppInitializationOptions();
        _settings = settings ?? new FileSystemAppSettingsService(_options);
    }

    public async ValueTask EnsureRepositoryAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        await EnsureRepositoryCoreAsync(novelId, createInitialCommitWhenEmpty: true, cancellationToken);
    }

    private async ValueTask EnsureRepositoryCoreAsync(
        long novelId,
        bool createInitialCommitWhenEmpty,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var workspace = await NovelWorkspacePathAsync(novelId, cancellationToken);
            Directory.CreateDirectory(workspace);
            using var repository = OpenOrInitializeRepository(workspace);
            cancellationToken.ThrowIfCancellationRequested();

            ThrowIfRepositoryLockFilesExist(workspace);
            await ConfigureRepositoryUserAsync(repository, cancellationToken);
            if (!HasHead(repository) && createInitialCommitWhenEmpty)
            {
                ThrowIfRepositoryLockFilesExist(workspace);
                await EnsureBaselineFilesAsync(workspace, cancellationToken);
                CommitIfChangedCore(repository, workspace, "initial commit", cancellationToken);
            }

            ClearReadOnlyAttributes(workspace);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<VersionControlCommitResult> CommitIfChangedAsync(
        long novelId,
        string message,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ValidateCommitMessage(message);
        await EnsureRepositoryAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var workspace = await NovelWorkspacePathAsync(novelId, cancellationToken);
            using var repository = OpenRepository(workspace);
            return CommitIfChangedCore(repository, workspace, message, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<VersionControlCommitInfo>> GetLogAsync(
        long novelId,
        string? relativePath,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var normalizedPath = NormalizeOptionalRelativePath(relativePath);
        await EnsureRepositoryCoreAsync(novelId, createInitialCommitWhenEmpty: false, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var workspace = await NovelWorkspacePathAsync(novelId, cancellationToken);
            using var repository = OpenRepository(workspace);
            if (!HasHead(repository))
            {
                return [];
            }

            var limit = count > 0 ? Math.Min(count, 500) : 500;
            var commits = new List<VersionControlCommitInfo>();
            foreach (var commit in EnumerateCommits(repository))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(normalizedPath) &&
                    !CommitTouchesPath(repository, commit, normalizedPath))
                {
                    continue;
                }

                commits.Add(new VersionControlCommitInfo(
                    commit.Sha,
                    FirstCommitMessageLine(commit),
                    commit.Author.When));
                if (commits.Count >= limit)
                {
                    break;
                }
            }

            return commits;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<PageResultPayload<GitCommitSummaryPayload>> GetCommitSummariesAsync(
        GetGitCommitsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var page = NormalizePage(input.Page);
        var size = NormalizePageSize(input.Size);
        var cursor = NormalizeOptionalCommitId(input.CursorCommitId, nameof(input.CursorCommitId));
        await EnsureRepositoryCoreAsync(input.NovelId, createInitialCommitWhenEmpty: false, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var workspace = await NovelWorkspacePathAsync(input.NovelId, cancellationToken);
            using var repository = OpenRepository(workspace);
            if (!HasHead(repository))
            {
                return new PageResultPayload<GitCommitSummaryPayload>([], 0, page, size, 0);
            }

            Commit? cursorCommit = null;
            if (cursor is not null)
            {
                cursorCommit = ResolveCommit(repository, cursor);
            }

            var commits = EnumerateCommits(repository).ToArray();
            var total = commits.LongLength;
            var pageCommits = cursorCommit is null
                ? commits.Skip(checked((page - 1) * size)).Take(size)
                : commits.SkipWhile(commit => !string.Equals(commit.Sha, cursorCommit.Sha, StringComparison.Ordinal))
                    .Skip(1)
                    .Take(size);

            var summaries = new List<GitCommitSummaryPayload>();
            foreach (var commit in pageCommits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = GetCommitFilesCore(repository, commit);
                summaries.Add(new GitCommitSummaryPayload(
                    commit.Sha,
                    ShortSha(commit),
                    commit.Author.Name,
                    commit.Author.Email,
                    FirstCommitMessageLine(commit),
                    commit.Author.When,
                    files.Count,
                    files.Sum(file => file.Additions),
                    files.Sum(file => file.Deletions)));
            }

            var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
            return new PageResultPayload<GitCommitSummaryPayload>(summaries, total, page, size, totalPages);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GitCommitFilePayload>> GetCommitFilesAsync(
        GetGitCommitFilesPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var commitId = NormalizeCommitId(input.CommitId, nameof(input.CommitId));
        await EnsureRepositoryCoreAsync(input.NovelId, createInitialCommitWhenEmpty: false, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var workspace = await NovelWorkspacePathAsync(input.NovelId, cancellationToken);
            using var repository = OpenRepository(workspace);
            if (!HasHead(repository))
            {
                return [];
            }

            var commit = ResolveCommit(repository, commitId);
            return GetCommitFilesCore(repository, commit);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<GitFileDiffPayload> GetFileDiffAsync(
        GetGitFileDiffPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var commitId = NormalizeCommitId(input.CommitId, nameof(input.CommitId));
        var requestedPath = NormalizeRequiredRelativePath(input.Path, nameof(input.Path));
        await EnsureRepositoryCoreAsync(input.NovelId, createInitialCommitWhenEmpty: false, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var workspace = await NovelWorkspacePathAsync(input.NovelId, cancellationToken);
            using var repository = OpenRepository(workspace);
            if (!HasHead(repository))
            {
                throw new VersionControlException("Git repository has no commits.");
            }

            var commit = ResolveCommit(repository, commitId);
            var files = GetCommitFilesCore(repository, commit);
            var file = files.FirstOrDefault(item => string.Equals(item.Path, requestedPath, StringComparison.Ordinal)) ??
                files.FirstOrDefault(item => string.Equals(item.OldPath, requestedPath, StringComparison.Ordinal));
            if (file is null)
            {
                throw new VersionControlException("Requested file is not part of the selected commit.");
            }

            using var patch = CompareCommitPatch(repository, commit, file.Path);
            var (diffText, diffTruncated) = TruncateText(patch.Content, MaxDiffTextLength);
            var originalContent = file.Binary ? null : ReadOriginalContent(commit, file);
            var modifiedContent = file.Binary ? null : ReadModifiedContent(commit, file);
            var contentTruncated = IsTruncated(originalContent) || IsTruncated(modifiedContent);

            return new GitFileDiffPayload(
                commit.Sha,
                file.Path,
                file.OldPath,
                file.ChangeType,
                diffText,
                diffTruncated || contentTruncated,
                file.Binary,
                StripTruncationMarker(originalContent),
                StripTruncationMarker(modifiedContent));
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask ConfigureRepositoryUserAsync(
        Repository repository,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetGitAuthorSettingsAsync(cancellationToken);
        var name = string.IsNullOrWhiteSpace(settings.Name) ? "Novelist" : settings.Name.Trim();
        var email = string.IsNullOrWhiteSpace(settings.Email) ? "novelist@local" : settings.Email.Trim();
        RunGitOperation(
            "configure repository author",
            () =>
            {
                repository.Config.Set("user.name", name, ConfigurationLevel.Local);
                repository.Config.Set("user.email", email, ConfigurationLevel.Local);
                return 0;
            });
    }

    private static bool HasHead(Repository repository)
    {
        return RunGitOperation("read repository HEAD", () => repository.Head.Tip is not null);
    }

    private VersionControlCommitResult CommitIfChangedCore(
        Repository repository,
        string workspace,
        string message,
        CancellationToken cancellationToken)
    {
        ValidateCommitMessage(message);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfRepositoryLockFilesExist(workspace);
        EnsureIndexIsMerged(repository);

        var status = RetrieveRepositoryStatus(repository);
        if (!status.IsDirty)
        {
            return new VersionControlCommitResult(false, CurrentHead(repository));
        }

        RunGitOperation(
            "stage repository changes",
            () =>
            {
                Commands.Stage(repository, "*", new StageOptions { IncludeIgnored = false });
                return 0;
            });
        EnsureIndexIsMerged(repository);

        var stagedStatus = RetrieveRepositoryStatus(repository);
        if (!stagedStatus.Any(IsStagedChange))
        {
            return new VersionControlCommitResult(false, CurrentHead(repository));
        }

        var signature = BuildConfiguredSignature(repository);
        var commit = RunGitOperation(
            "commit repository changes",
            () => repository.Commit(message, signature, signature));
        ClearReadOnlyAttributes(workspace);
        return new VersionControlCommitResult(true, commit.Sha);
    }

    private static IReadOnlyList<GitCommitFilePayload> GetCommitFilesCore(
        Repository repository,
        Commit commit)
    {
        using var patch = CompareCommitPatch(repository, commit);
        return patch
            .Where(entry => IsSafeGitPath(entry.Path) && (entry.OldPath is null || IsSafeGitPath(entry.OldPath)))
            .Select(entry => new GitCommitFilePayload(
                entry.Path,
                string.Equals(entry.OldPath, entry.Path, StringComparison.Ordinal) ? null : entry.OldPath,
                ToPayloadChangeType(entry.Status),
                Math.Max(entry.LinesAdded, 0),
                Math.Max(entry.LinesDeleted, 0),
                entry.IsBinaryComparison))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool CommitTouchesPath(
        Repository repository,
        Commit commit,
        string path)
    {
        return GetCommitFilesCore(repository, commit)
            .Any(file =>
                string.Equals(file.Path, path, StringComparison.Ordinal) ||
                string.Equals(file.OldPath, path, StringComparison.Ordinal));
    }

    private static Patch CompareCommitPatch(
        Repository repository,
        Commit commit,
        string? path = null)
    {
        var oldTree = commit.Parents.FirstOrDefault()?.Tree;
        var paths = string.IsNullOrWhiteSpace(path) ? null : new[] { path };
        return RunGitOperation(
            "read commit diff",
            () => paths is null
                ? repository.Diff.Compare<Patch>(oldTree, commit.Tree, DiffCompareOptions())
                : repository.Diff.Compare<Patch>(
                    oldTree,
                    commit.Tree,
                    paths,
                    new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false },
                    DiffCompareOptions()));
    }

    private static LibGit2Sharp.CompareOptions DiffCompareOptions()
    {
        return new LibGit2Sharp.CompareOptions
        {
            Similarity = SimilarityOptions.Renames
        };
    }

    private static RepositoryStatus RetrieveRepositoryStatus(Repository repository)
    {
        return RunGitOperation(
            "read repository status",
            () => repository.RetrieveStatus(new StatusOptions
            {
                Show = StatusShowOption.IndexAndWorkDir,
                DetectRenamesInIndex = true,
                DetectRenamesInWorkDir = true,
                IncludeIgnored = false,
                IncludeUntracked = true,
                RecurseUntrackedDirs = true
            }));
    }

    private static bool IsStagedChange(StatusEntry entry)
    {
        return (entry.State & (
            FileStatus.NewInIndex |
            FileStatus.ModifiedInIndex |
            FileStatus.DeletedFromIndex |
            FileStatus.RenamedInIndex |
            FileStatus.TypeChangeInIndex)) != 0;
    }

    private static void EnsureIndexIsMerged(Repository repository)
    {
        if (!RunGitOperation("read repository index", () => repository.Index.IsFullyMerged))
        {
            throw new VersionControlException("Git repository has unresolved index conflicts.");
        }
    }

    private static Signature BuildConfiguredSignature(Repository repository)
    {
        return RunGitOperation(
            "build repository author signature",
            () => repository.Config.BuildSignature(DateTimeOffset.Now));
    }

    private static string CurrentHead(Repository repository)
    {
        var head = RunGitOperation("read current HEAD", () => repository.Head.Tip);
        return head?.Sha ?? string.Empty;
    }

    private static Commit ResolveCommit(
        Repository repository,
        string commitId)
    {
        return RunGitOperation(
            "resolve commit",
            () => repository.Lookup<Commit>(commitId)) ??
            throw new VersionControlException("Git commit was not found.");
    }

    private static IReadOnlyList<Commit> EnumerateCommits(Repository repository)
    {
        return RunGitOperation(
            "read commit log",
            () =>
            {
                var commits = new List<Commit>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var current = repository.Head.Tip;
                while (current is not null && visited.Add(current.Sha))
                {
                    commits.Add(current);
                    current = current.Parents.FirstOrDefault();
                }

                return commits;
            });
    }

    private static string? ReadOriginalContent(
        Commit commit,
        GitCommitFilePayload file)
    {
        if (string.Equals(file.ChangeType, "added", StringComparison.Ordinal))
        {
            return null;
        }

        var parent = commit.Parents.FirstOrDefault();
        if (parent is null)
        {
            return null;
        }

        var path = file.OldPath ?? file.Path;
        return ReadBlobText(parent, path);
    }

    private static string? ReadModifiedContent(
        Commit commit,
        GitCommitFilePayload file)
    {
        if (string.Equals(file.ChangeType, "deleted", StringComparison.Ordinal))
        {
            return null;
        }

        return ReadBlobText(commit, file.Path);
    }

    private static string? ReadBlobText(
        Commit commit,
        string path)
    {
        var entry = commit[path];
        if (entry?.Target is not Blob blob || blob.IsBinary)
        {
            return null;
        }

        return RunGitOperation(
            "read blob content",
            () =>
            {
                using var stream = blob.GetContentStream();
                var maxBytes = checked(MaxContentTextLength * 4 + 4);
                var buffer = new byte[16 * 1024];
                using var output = new MemoryStream();
                var truncatedBytes = false;

                while (true)
                {
                    var remaining = maxBytes - (int)output.Length;
                    if (remaining <= 0)
                    {
                        truncatedBytes = stream.ReadByte() != -1;
                        break;
                    }

                    var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                    if (read == 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                }

                var text = Encoding.UTF8.GetString(output.ToArray());
                var (truncatedText, truncatedChars) = TruncateText(text, MaxContentTextLength);
                return truncatedBytes || truncatedChars ? MarkTruncated(truncatedText) : text;
            });
    }

    private static string ToPayloadChangeType(ChangeKind status)
    {
        return status switch
        {
            ChangeKind.Added => "added",
            ChangeKind.Deleted => "deleted",
            ChangeKind.Renamed => "renamed",
            ChangeKind.Copied => "renamed",
            ChangeKind.TypeChanged => "modified",
            _ => "modified"
        };
    }

    private static string FirstCommitMessageLine(Commit commit)
    {
        return commit.MessageShort.Split('\n', 2)[0];
    }

    private static string ShortSha(Commit commit)
    {
        return commit.Sha.Length <= 7 ? commit.Sha : commit.Sha[..7];
    }

    private async ValueTask<string> NovelWorkspacePathAsync(long novelId, CancellationToken cancellationToken)
    {
        var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
        return SafeChildPath(Path.Combine(dataDirectory, "novels"), novelId.ToString(CultureInfo.InvariantCulture));
    }

    private static Repository OpenOrInitializeRepository(string workspace)
    {
        return RunGitOperation(
            "open or initialize repository",
            () =>
            {
                var gitMetadataExists = Directory.Exists(Path.Combine(workspace, ".git")) ||
                    File.Exists(Path.Combine(workspace, ".git"));
                if (!gitMetadataExists)
                {
                    Repository.Init(workspace);
                }
                else if (!IsValidRepository(workspace))
                {
                    throw new VersionControlException("Git repository metadata is invalid or unsupported.");
                }

                return new Repository(workspace);
            });
    }

    private static Repository OpenRepository(string workspace)
    {
        return RunGitOperation(
            "open repository",
            () =>
            {
                if (!IsValidRepository(workspace))
                {
                    throw new VersionControlException("Git repository metadata is invalid or unsupported.");
                }

                return new Repository(workspace);
            });
    }

    private static async ValueTask EnsureBaselineFilesAsync(string workspace, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SafeChildPath(workspace, "chapters"));
        var gitkeep = SafeChildPath(workspace, "chapters/.gitkeep");
        if (!File.Exists(gitkeep))
        {
            await File.WriteAllBytesAsync(gitkeep, [], cancellationToken);
        }
    }

    private static bool IsValidRepository(string workspace)
    {
        try
        {
            return Repository.IsValid(workspace);
        }
        catch (LibGit2SharpException)
        {
            return false;
        }
    }

    private static int NormalizePage(int page)
    {
        return page <= 0 ? 1 : Math.Min(page, 10_000);
    }

    private static int NormalizePageSize(int size)
    {
        return size <= 0 ? 20 : Math.Clamp(size, 1, MaxHistoryPageSize);
    }

    private static string NormalizeCommitId(string? commitId, string name)
    {
        var normalized = NormalizeOptionalCommitId(commitId, name);
        if (normalized is null)
        {
            throw new ArgumentException("Git commit id is required.", name);
        }

        return normalized;
    }

    private static string? NormalizeOptionalCommitId(string? commitId, string name)
    {
        if (string.IsNullOrWhiteSpace(commitId))
        {
            return null;
        }

        var normalized = commitId.Trim();
        if (normalized.Length is < 4 or > 64 ||
            normalized.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new ArgumentException("Git commit id must be a hexadecimal hash.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        return NormalizeRequiredRelativePath(relativePath, nameof(relativePath));
    }

    private static string NormalizeRequiredRelativePath(string? relativePath, string name)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Git repository path is required.", name);
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (!IsSafeGitPath(normalized))
        {
            throw new ArgumentException("Git path must be a safe repository-relative path.", name);
        }

        return normalized;
    }

    private static bool IsSafeGitPath(string? path)
    {
        return !string.IsNullOrEmpty(path) &&
            path.Length <= 512 &&
            !path.Contains('\0') &&
            !path.StartsWith("/", StringComparison.Ordinal) &&
            !path.Contains(':', StringComparison.Ordinal) &&
            path.Split('/').All(segment => segment is not "" and not "." and not "..");
    }

    private static (string Text, bool Truncated) TruncateText(string text, int maxLength)
    {
        return text.Length <= maxLength
            ? (text, false)
            : (text[..maxLength], true);
    }

    private static string MarkTruncated(string text)
    {
        return $"{text}\u001fTRUNCATED";
    }

    private static bool IsTruncated(string? text)
    {
        return text?.EndsWith("\u001fTRUNCATED", StringComparison.Ordinal) == true;
    }

    private static string? StripTruncationMarker(string? text)
    {
        const string marker = "\u001fTRUNCATED";
        return text?.EndsWith(marker, StringComparison.Ordinal) == true
            ? text[..^marker.Length]
            : text;
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
            throw new VersionControlException("Resolved repository path escapes the novelist data directory.");
        }

        return fullPath;
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static void ClearReadOnlyAttributes(string workspace)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!Directory.Exists(workspace))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories))
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
                // Best-effort cleanup support; repository operation failures are surfaced separately.
            }
        }
    }

    private static void ThrowIfRepositoryLockFilesExist(string workspace)
    {
        var gitDirectory = ResolveGitDirectory(workspace);
        if (gitDirectory is null)
        {
            return;
        }

        foreach (var lockFileName in GitLockFileNames)
        {
            var lockPath = Path.Combine(gitDirectory, lockFileName);
            if (File.Exists(lockPath))
            {
                throw new VersionControlException(
                    $"Git repository lock file exists: {FormatRepositoryLockPath(workspace, lockPath)}. " +
                    "Close any running Git operation and retry; Novelist will not delete Git lock files automatically.");
            }
        }
    }

    private static string? ResolveGitDirectory(string workspace)
    {
        var gitPath = Path.Combine(workspace, ".git");
        if (Directory.Exists(gitPath))
        {
            return gitPath;
        }

        if (!File.Exists(gitPath))
        {
            return null;
        }

        var firstLine = File.ReadLines(gitPath).FirstOrDefault();
        const string prefix = "gitdir:";
        if (firstLine is null || !firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var gitDirectory = firstLine[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(gitDirectory))
        {
            return null;
        }

        return Path.GetFullPath(Path.IsPathRooted(gitDirectory)
            ? gitDirectory
            : Path.Combine(workspace, gitDirectory));
    }

    private static string FormatRepositoryLockPath(string workspace, string lockPath)
    {
        var relative = Path.GetRelativePath(workspace, lockPath).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? Path.GetFileName(lockPath)
            : relative;
    }

    private static void ValidateCommitMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Git commit message is required.", nameof(message));
        }

        if (message.Length > MaxCommitMessageLength)
        {
            throw new ArgumentOutOfRangeException(nameof(message), message.Length, $"Git commit message must be at most {MaxCommitMessageLength} characters.");
        }

        if (message.Contains('\0'))
        {
            throw new ArgumentException("Git commit message must not contain NUL characters.", nameof(message));
        }
    }

    private static T RunGitOperation<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (VersionControlException)
        {
            throw;
        }
        catch (LibGit2SharpException ex)
        {
            throw new VersionControlException($"Git operation failed: {operation}: {NormalizeGitErrorText(ex.Message)}", ex);
        }
        catch (IOException ex)
        {
            throw new VersionControlException($"Git operation failed: {operation}: {NormalizeGitErrorText(ex.Message)}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new VersionControlException($"Git operation failed: {operation}: {NormalizeGitErrorText(ex.Message)}", ex);
        }
    }

    private static string NormalizeGitErrorText(string error)
    {
        var trimmed = error.Trim();
        if (trimmed.Length == 0)
        {
            return "no detail";
        }

        var singleLine = string.Join(" ", trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= MaxGitErrorTextLength
            ? singleLine
            : singleLine[..MaxGitErrorTextLength] + "...";
    }
}
