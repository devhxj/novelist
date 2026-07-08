using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class GitVersionControlService : IVersionControlService
{
    private const string GitPathEnvironmentVariable = "NOVELIST_GIT_PATH";
    private const int MaxCommitMessageLength = 4_000;
    private const int MaxHistoryPageSize = 100;
    private const int MaxDiffTextLength = 200_000;
    private const int MaxContentTextLength = 200_000;
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly AppInitializationOptions _options;
    private readonly string? _gitExecutableOverride;
    private readonly IPhase15AppSettingsService _settings;
    private readonly TimeSpan _commandTimeout;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public GitVersionControlService(
        AppInitializationOptions? options = null,
        string? gitExecutableOverride = null,
        IPhase15AppSettingsService? settings = null,
        TimeSpan? commandTimeout = null)
    {
        _options = options ?? new AppInitializationOptions();
        _gitExecutableOverride = string.IsNullOrWhiteSpace(gitExecutableOverride) ? null : gitExecutableOverride;
        _settings = settings ?? new FileSystemAppSettingsService(_options);
        _commandTimeout = ValidateCommandTimeout(commandTimeout ?? DefaultCommandTimeout);
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
            var git = ResolveGitExecutable();
            var gitMetadataExists = Directory.Exists(Path.Combine(workspace, ".git")) ||
                File.Exists(Path.Combine(workspace, ".git"));

            if (!gitMetadataExists)
            {
                await RunGitAsync(git, workspace, ["init"], cancellationToken);
            }
            else
            {
                await RunGitAsync(git, workspace, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
            }

            await ConfigureRepositoryUserAsync(git, workspace, cancellationToken);
            if (!await HasHeadAsync(git, workspace, cancellationToken) && createInitialCommitWhenEmpty)
            {
                await EnsureBaselineFilesAsync(workspace, cancellationToken);
                await CommitIfChangedCoreAsync(git, workspace, "initial commit", cancellationToken);
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
            var git = ResolveGitExecutable();
            return await CommitIfChangedCoreAsync(git, workspace, message, cancellationToken);
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
            var git = ResolveGitExecutable();
            if (!await HasHeadAsync(git, workspace, cancellationToken))
            {
                return [];
            }

            var args = new List<string> { "log", "--format=%H%x00%s%x00%ct" };
            if (count > 0)
            {
                args.Add("-n");
                args.Add(Math.Min(count, 500).ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                args.Add("--");
                args.Add(normalizedPath);
            }

            var result = await RunGitAsync(git, workspace, args, cancellationToken);
            return GitHistoryParser.ParseSimpleLog(result.Stdout);
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
            var git = ResolveGitExecutable();
            if (!await HasHeadAsync(git, workspace, cancellationToken))
            {
                return new PageResultPayload<GitCommitSummaryPayload>([], 0, page, size, 0);
            }

            if (cursor is not null)
            {
                _ = await ResolveCommitAsync(git, workspace, cursor, cancellationToken);
            }

            var total = await CountCommitsAsync(git, workspace, cancellationToken);
            var args = new List<string>
            {
                "log",
                "--format=%H%x00%h%x00%an%x00%ae%x00%ct%x00%s"
            };

            if (cursor is null)
            {
                var skip = checked((page - 1) * size);
                if (skip > 0)
                {
                    args.Add("--skip");
                    args.Add(skip.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                args.Add("--skip");
                args.Add("1");
            }

            args.Add("-n");
            args.Add(size.ToString(CultureInfo.InvariantCulture));
            if (cursor is not null)
            {
                args.Add(cursor);
            }

            var result = await RunGitAsync(git, workspace, args, cancellationToken);
            var metadata = GitHistoryParser.ParseCommitMetadataLog(result.Stdout);
            var summaries = new List<GitCommitSummaryPayload>(metadata.Count);
            foreach (var commit in metadata)
            {
                var files = await GetCommitFilesCoreAsync(git, workspace, commit.CommitId, cancellationToken);
                summaries.Add(new GitCommitSummaryPayload(
                    commit.CommitId,
                    commit.ShortCommitId,
                    commit.AuthorName,
                    commit.AuthorEmail,
                    commit.Message,
                    commit.CommittedAt,
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
            var git = ResolveGitExecutable();
            if (!await HasHeadAsync(git, workspace, cancellationToken))
            {
                return [];
            }

            var resolvedCommit = await ResolveCommitAsync(git, workspace, commitId, cancellationToken);
            return await GetCommitFilesCoreAsync(git, workspace, resolvedCommit, cancellationToken);
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
            var git = ResolveGitExecutable();
            if (!await HasHeadAsync(git, workspace, cancellationToken))
            {
                throw new VersionControlException("Git repository has no commits.");
            }

            var resolvedCommit = await ResolveCommitAsync(git, workspace, commitId, cancellationToken);
            var files = await GetCommitFilesCoreAsync(git, workspace, resolvedCommit, cancellationToken);
            var file = files.FirstOrDefault(item => string.Equals(item.Path, requestedPath, StringComparison.Ordinal)) ??
                files.FirstOrDefault(item => string.Equals(item.OldPath, requestedPath, StringComparison.Ordinal));
            if (file is null)
            {
                throw new VersionControlException("Requested file is not part of the selected commit.");
            }

            var diff = await RunGitAsync(
                git,
                workspace,
                ["show", "--format=", "--find-renames", "--no-ext-diff", "--patch", resolvedCommit, "--", file.Path],
                cancellationToken);
            var (diffText, diffTruncated) = TruncateText(diff.Stdout, MaxDiffTextLength);
            var originalContent = file.Binary
                ? null
                : await ReadOriginalContentAsync(git, workspace, resolvedCommit, file, cancellationToken);
            var modifiedContent = file.Binary
                ? null
                : await ReadModifiedContentAsync(git, workspace, resolvedCommit, file, cancellationToken);
            var contentTruncated = IsTruncated(originalContent) || IsTruncated(modifiedContent);

            return new GitFileDiffPayload(
                resolvedCommit,
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
        string git,
        string workspace,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetGitAuthorSettingsAsync(cancellationToken);
        var name = string.IsNullOrWhiteSpace(settings.Name) ? "Novelist" : settings.Name.Trim();
        var email = string.IsNullOrWhiteSpace(settings.Email) ? "novelist@local" : settings.Email.Trim();
        await RunGitAsync(git, workspace, ["config", "user.name", name], cancellationToken);
        await RunGitAsync(git, workspace, ["config", "user.email", email], cancellationToken);
    }

    private async ValueTask<bool> HasHeadAsync(
        string git,
        string workspace,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            git,
            workspace,
            ["rev-parse", "--verify", "HEAD"],
            cancellationToken,
            throwOnFailure: false);
        return result.ExitCode == 0;
    }

    private async ValueTask<VersionControlCommitResult> CommitIfChangedCoreAsync(
        string git,
        string workspace,
        string message,
        CancellationToken cancellationToken)
    {
        ValidateCommitMessage(message);
        var status = await RunGitAsync(
            git,
            workspace,
            ["status", "--porcelain", "--untracked-files=all"],
            cancellationToken);
        if (string.IsNullOrWhiteSpace(status.Stdout))
        {
            var head = await CurrentHeadAsync(git, workspace, cancellationToken);
            return new VersionControlCommitResult(false, head);
        }

        await RunGitAsync(git, workspace, ["add", "-A"], cancellationToken);
        await RunGitAsync(git, workspace, ["commit", "-m", message], cancellationToken);
        ClearReadOnlyAttributes(workspace);
        var hash = await CurrentHeadAsync(git, workspace, cancellationToken);
        return new VersionControlCommitResult(true, hash);
    }

    private async ValueTask<IReadOnlyList<GitCommitFilePayload>> GetCommitFilesCoreAsync(
        string git,
        string workspace,
        string commitId,
        CancellationToken cancellationToken)
    {
        var nameStatus = await RunGitAsync(
            git,
            workspace,
            ["show", "--format=", "--name-status", "-z", "--find-renames", commitId],
            cancellationToken);
        var numstat = await RunGitAsync(
            git,
            workspace,
            ["show", "--format=", "--numstat", "-z", "--find-renames", commitId],
            cancellationToken);
        return GitHistoryParser.ParseCommitFiles(nameStatus.Stdout, numstat.Stdout);
    }

    private async ValueTask<long> CountCommitsAsync(
        string git,
        string workspace,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(git, workspace, ["rev-list", "--count", "HEAD"], cancellationToken);
        return long.TryParse(result.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : 0;
    }

    private async ValueTask<string> ResolveCommitAsync(
        string git,
        string workspace,
        string commitId,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            git,
            workspace,
            ["rev-parse", "--verify", $"{commitId}^{{commit}}"],
            cancellationToken);
        return result.Stdout.Trim();
    }

    private async ValueTask<string?> ReadOriginalContentAsync(
        string git,
        string workspace,
        string commitId,
        GitCommitFilePayload file,
        CancellationToken cancellationToken)
    {
        if (string.Equals(file.ChangeType, "added", StringComparison.Ordinal))
        {
            return null;
        }

        if (!await HasParentAsync(git, workspace, commitId, cancellationToken))
        {
            return null;
        }

        var path = file.OldPath ?? file.Path;
        return await ReadBlobTextAsync(git, workspace, $"{commitId}^", path, cancellationToken);
    }

    private async ValueTask<string?> ReadModifiedContentAsync(
        string git,
        string workspace,
        string commitId,
        GitCommitFilePayload file,
        CancellationToken cancellationToken)
    {
        if (string.Equals(file.ChangeType, "deleted", StringComparison.Ordinal))
        {
            return null;
        }

        return await ReadBlobTextAsync(git, workspace, commitId, file.Path, cancellationToken);
    }

    private async ValueTask<bool> HasParentAsync(
        string git,
        string workspace,
        string commitId,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            git,
            workspace,
            ["rev-parse", "--verify", $"{commitId}^"],
            cancellationToken,
            throwOnFailure: false);
        return result.ExitCode == 0;
    }

    private async ValueTask<string?> ReadBlobTextAsync(
        string git,
        string workspace,
        string treeish,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            git,
            workspace,
            ["show", $"{treeish}:{path}"],
            cancellationToken,
            throwOnFailure: false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var (text, truncated) = TruncateText(result.Stdout, MaxContentTextLength);
        return truncated ? MarkTruncated(text) : text;
    }

    private async ValueTask<string> CurrentHeadAsync(
        string git,
        string workspace,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(git, workspace, ["rev-parse", "HEAD"], cancellationToken);
        return result.Stdout.Trim();
    }

    private async ValueTask<string> NovelWorkspacePathAsync(long novelId, CancellationToken cancellationToken)
    {
        var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
        return SafeChildPath(Path.Combine(dataDirectory, "novels"), novelId.ToString(CultureInfo.InvariantCulture));
    }

    private string ResolveGitExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_gitExecutableOverride))
        {
            return _gitExecutableOverride;
        }

        var configured = Environment.GetEnvironmentVariable(GitPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (var candidate in GetBundledGitCandidatePaths(AppContext.BaseDirectory, CurrentPlatformKey()))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var fromPath = FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        return OperatingSystem.IsWindows() ? "git.exe" : "git";
    }

    internal static IReadOnlyList<string> GetBundledGitCandidatePaths(string baseDirectory, string platformKey)
    {
        if (string.Equals(platformKey, "windows", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Path.Combine(baseDirectory, "runtime", "git", "mingw64", "bin", "git.exe"),
                Path.Combine(baseDirectory, "runtime", "git", "cmd", "git.exe"),
                Path.Combine(baseDirectory, "runtime", "git", "bin", "git.exe"),
                Path.Combine(baseDirectory, "runtime", "git", "git.exe")
            ];
        }

        if (string.Equals(platformKey, "macos", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources", "runtime", "git", "git")),
                Path.Combine(baseDirectory, "runtime", "git", "git")
            ];
        }

        return
        [
            Path.Combine(baseDirectory, "runtime", "git", "git")
        ];
    }

    private static string CurrentPlatformKey()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "macos" : "linux";
    }

    private static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async ValueTask<GitCommandResult> RunGitAsync(
        string git,
        string workspace,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = git,
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.Environment["LC_ALL"] = "C";
            startInfo.Environment["LANG"] = "C";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new VersionControlException("Git process did not start.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = new CancellationTokenSource(_commandTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new VersionControlException($"Git command timed out: {FormatArgs(args)}");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var result = new GitCommandResult(process.ExitCode, stdout, stderr);
            if (throwOnFailure && result.ExitCode != 0)
            {
                throw new VersionControlException($"Git command failed: {FormatArgs(args)}: {TrimError(stderr)}");
            }

            return result;
        }
        catch (Win32Exception ex)
        {
            throw new VersionControlException("Git executable was not found. Install Git and make sure it is available on PATH.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new VersionControlException("Git command could not be started.", ex);
        }
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

    private static int NormalizePage(int page)
    {
        return page <= 0 ? 1 : Math.Min(page, 10_000);
    }

    private static int NormalizePageSize(int size)
    {
        return size <= 0 ? 20 : Math.Clamp(size, 1, MaxHistoryPageSize);
    }

    private static TimeSpan ValidateCommandTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Git command timeout must be between 0 and 5 minutes.");
        }

        return timeout;
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
        if (normalized.Contains('\0') ||
            normalized.Length > 512 ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Git path must be a safe repository-relative path.", name);
        }

        return normalized;
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
                // Best-effort cleanup support; Git command failures are surfaced separately.
            }
        }
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

    private static string FormatArgs(IReadOnlyList<string> args)
    {
        return string.Join(" ", args.Select(arg => arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg));
    }

    private static string TrimError(string error)
    {
        var trimmed = error.Trim();
        return trimmed.Length == 0 ? "no stderr" : trimmed;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Preserve the original timeout error.
        }
    }

    private sealed record GitCommandResult(int ExitCode, string Stdout, string Stderr);
}
