using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class GitVersionControlService : IVersionControlService
{
    private const string GitPathEnvironmentVariable = "NOVELIST_GIT_PATH";
    private const int MaxCommitMessageLength = 4_000;

    private readonly AppInitializationOptions _options;
    private readonly string? _gitExecutableOverride;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public GitVersionControlService(
        AppInitializationOptions? options = null,
        string? gitExecutableOverride = null)
    {
        _options = options ?? new AppInitializationOptions();
        _gitExecutableOverride = string.IsNullOrWhiteSpace(gitExecutableOverride) ? null : gitExecutableOverride;
    }

    public async ValueTask EnsureRepositoryAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);

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
                Directory.CreateDirectory(SafeChildPath(workspace, "chapters"));
                var gitkeep = SafeChildPath(workspace, "chapters/.gitkeep");
                if (!File.Exists(gitkeep))
                {
                    await File.WriteAllBytesAsync(gitkeep, [], cancellationToken);
                }
            }
            else
            {
                await RunGitAsync(git, workspace, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
            }

            await ConfigureRepositoryUserAsync(git, workspace, cancellationToken);
            if (!await HasHeadAsync(git, workspace, cancellationToken))
            {
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
        await EnsureRepositoryAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var workspace = await NovelWorkspacePathAsync(novelId, cancellationToken);
            var git = ResolveGitExecutable();
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
            return ParseLog(result.Stdout);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async ValueTask ConfigureRepositoryUserAsync(
        string git,
        string workspace,
        CancellationToken cancellationToken)
    {
        await RunGitAsync(git, workspace, ["config", "user.name", "Novelist"], cancellationToken);
        await RunGitAsync(git, workspace, ["config", "user.email", "novelist@local"], cancellationToken);
    }

    private static async ValueTask<bool> HasHeadAsync(
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

    private static async ValueTask<VersionControlCommitResult> CommitIfChangedCoreAsync(
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

    private static async ValueTask<string> CurrentHeadAsync(
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

        foreach (var candidate in BundledGitCandidates())
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

    private static IEnumerable<string> BundledGitCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(baseDirectory, "runtime", "git", "mingw64", "bin", "git.exe");
            yield return Path.Combine(baseDirectory, "runtime", "git", "cmd", "git.exe");
            yield return Path.Combine(baseDirectory, "runtime", "git", "bin", "git.exe");
            yield return Path.Combine(baseDirectory, "runtime", "git", "git.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources", "runtime", "git", "git"));
            yield return Path.Combine(baseDirectory, "runtime", "git", "git");
        }
        else
        {
            yield return Path.Combine(baseDirectory, "runtime", "git", "git");
        }
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

    private static async ValueTask<GitCommandResult> RunGitAsync(
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

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new VersionControlException("Git process did not start.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
            throw new VersionControlException("Git executable was not found. Install Git or package it under runtime/git/.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new VersionControlException("Git command could not be started.", ex);
        }
    }

    private static IReadOnlyList<VersionControlCommitInfo> ParseLog(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var commits = new List<VersionControlCommitInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\0', 3);
            if (parts.Length != 3 || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                continue;
            }

            commits.Add(new VersionControlCommitInfo(
                parts[0],
                parts[1].Split('\n', 2)[0],
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
        }

        return commits
            .OrderBy(item => item.Time)
            .ThenBy(item => item.Hash, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeOptionalRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (normalized.Contains('\0') ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Git log path must be a safe repository-relative path.", nameof(relativePath));
        }

        return normalized;
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
