using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class GitVersionControlServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParserHandlesRenamesBinaryFilesAndSimpleLogLatestFirst()
    {
        var files = GitHistoryParser.ParseCommitFiles(
            "A\0chapters/001.md\0M\0cover.bin\0D\0notes/old.md\0R087\0chapters/002.md\0chapters/renamed.md\0",
            "3\t0\tchapters/001.md\0-\t-\tcover.bin\00\t4\tnotes/old.md\01\t2\t\0chapters/002.md\0chapters/renamed.md\0");

        Assert.Collection(
            files,
            item =>
            {
                Assert.Equal("chapters/001.md", item.Path);
                Assert.Equal("added", item.ChangeType);
                Assert.Equal(3, item.Additions);
                Assert.Equal(0, item.Deletions);
                Assert.False(item.Binary);
            },
            item =>
            {
                Assert.Equal("chapters/renamed.md", item.Path);
                Assert.Equal("chapters/002.md", item.OldPath);
                Assert.Equal("renamed", item.ChangeType);
                Assert.Equal(1, item.Additions);
                Assert.Equal(2, item.Deletions);
            },
            item =>
            {
                Assert.Equal("cover.bin", item.Path);
                Assert.Equal("modified", item.ChangeType);
                Assert.True(item.Binary);
            },
            item =>
            {
                Assert.Equal("notes/old.md", item.Path);
                Assert.Equal("deleted", item.ChangeType);
                Assert.Equal(0, item.Additions);
                Assert.Equal(4, item.Deletions);
            });

        var simpleLog = GitHistoryParser.ParseSimpleLog(
            "bbbb\0newer\01720000001\n" +
            "aaaa\0older\01720000000\n");

        Assert.Equal(["bbbb", "aaaa"], simpleLog.Select(item => item.Hash));

        var sameSecondLog = GitHistoryParser.ParseSimpleLog(
            "zzzz\0first from git\01720000001\n" +
            "aaaa\0second from git\01720000001\n");

        Assert.Equal(["zzzz", "aaaa"], sameSecondLog.Select(item => item.Hash));
    }

    [Fact]
    public async Task EmptyRepositoryHistoryReturnsEmptyPagesWithoutCreatingInitialCommit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new GitVersionControlService(options);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", "1");
        Directory.CreateDirectory(workspace);

        var commits = await service.GetCommitSummariesAsync(
            new GetGitCommitsPayload(1, Page: 1, Size: 20),
            CancellationToken.None);
        var log = await service.GetLogAsync(1, null, 20, CancellationToken.None);

        Assert.Empty(commits.Items);
        Assert.Equal(0, commits.Total);
        Assert.Equal(0, commits.TotalPages);
        Assert.Empty(log);
        Assert.True(Directory.Exists(Path.Combine(workspace, ".git")) || File.Exists(Path.Combine(workspace, ".git")));
        Assert.False(File.Exists(Path.Combine(workspace, "chapters", ".gitkeep")));
    }

    [Fact]
    public async Task EnsureRepositoryCompletesPartiallyInitializedRepositoryWithInitialCommit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new GitVersionControlService(options);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", "1");
        Directory.CreateDirectory(workspace);

        var emptyHistory = await service.GetCommitSummariesAsync(
            new GetGitCommitsPayload(1, Page: 1, Size: 20),
            CancellationToken.None);
        Assert.Empty(emptyHistory.Items);
        Assert.True(Directory.Exists(Path.Combine(workspace, ".git")) || File.Exists(Path.Combine(workspace, ".git")));
        Assert.False(File.Exists(Path.Combine(workspace, "chapters", ".gitkeep")));

        await service.EnsureRepositoryAsync(1, CancellationToken.None);

        var commits = await service.GetCommitSummariesAsync(
            new GetGitCommitsPayload(1, Page: 1, Size: 20),
            CancellationToken.None);
        var commit = Assert.Single(commits.Items);
        Assert.Equal("initial commit", commit.Message);
        Assert.Equal(1, commit.ChangedFileCount);
        Assert.True(File.Exists(Path.Combine(workspace, "chapters", ".gitkeep")));
    }

    [Fact]
    public async Task DetailedHistorySupportsPagingAuthorRenameDeleteAndContentDiffs()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveGitAuthorSettingsAsync(
            new SaveGitAuthorSettingsPayload("History Author", "history-author@example.com"),
            CancellationToken.None);
        var service = new GitVersionControlService(options, settings: settings);
        var novels = new FileSystemNovelService(options, settings, service);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("Git 历史", "", ""), CancellationToken.None);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString());

        await WriteWorkspaceTextAsync(workspace, "chapters/alpha.md", "alpha line 1\nalpha line 2\n");
        await WriteWorkspaceTextAsync(workspace, "notes.md", "note line\n");
        await service.CommitIfChangedAsync(novel.Id, "add alpha and notes", CancellationToken.None);

        await WriteWorkspaceTextAsync(workspace, "chapters/alpha.md", "alpha line 1\nalpha line 2\nalpha line 3\n");
        await service.CommitIfChangedAsync(novel.Id, "modify alpha", CancellationToken.None);

        File.Delete(Path.Combine(workspace, "notes.md"));
        await service.CommitIfChangedAsync(novel.Id, "delete notes", CancellationToken.None);

        File.Move(
            Path.Combine(workspace, "chapters", "alpha.md"),
            Path.Combine(workspace, "chapters", "beta.md"));
        await File.AppendAllTextAsync(Path.Combine(workspace, "chapters", "beta.md"), "renamed line\n");
        await service.CommitIfChangedAsync(novel.Id, "rename alpha to beta", CancellationToken.None);

        var firstPage = await service.GetCommitSummariesAsync(
            new GetGitCommitsPayload(novel.Id, Page: 1, Size: 2),
            CancellationToken.None);

        Assert.Equal(5, firstPage.Total);
        Assert.Equal(3, firstPage.TotalPages);
        Assert.Equal(["rename alpha to beta", "delete notes"], firstPage.Items.Select(item => item.Message));
        Assert.All(firstPage.Items, item =>
        {
            Assert.Equal("History Author", item.AuthorName);
            Assert.Equal("history-author@example.com", item.AuthorEmail);
            Assert.False(string.IsNullOrWhiteSpace(item.ShortCommitId));
            Assert.True(item.ChangedFileCount >= 1);
        });
        Assert.True(firstPage.Items[0].Insertions >= 1);

        var cursorPage = await service.GetCommitSummariesAsync(
            new GetGitCommitsPayload(
                novel.Id,
                Page: 1,
                Size: 2,
                CursorCommitId: firstPage.Items[^1].CommitId),
            CancellationToken.None);
        Assert.Equal(["modify alpha", "add alpha and notes"], cursorPage.Items.Select(item => item.Message));

        var renameFiles = await service.GetCommitFilesAsync(
            new GetGitCommitFilesPayload(novel.Id, firstPage.Items[0].CommitId),
            CancellationToken.None);
        var renamed = Assert.Single(renameFiles);
        Assert.Equal("renamed", renamed.ChangeType);
        Assert.Equal("chapters/alpha.md", renamed.OldPath);
        Assert.Equal("chapters/beta.md", renamed.Path);
        Assert.False(renamed.Binary);

        var renameDiff = await service.GetFileDiffAsync(
            new GetGitFileDiffPayload(novel.Id, firstPage.Items[0].CommitId, "chapters/beta.md"),
            CancellationToken.None);
        Assert.Equal("renamed", renameDiff.ChangeType);
        Assert.Equal("chapters/alpha.md", renameDiff.OldPath);
        Assert.Contains("alpha line 3", renameDiff.OriginalContent, StringComparison.Ordinal);
        Assert.Contains("renamed line", renameDiff.ModifiedContent, StringComparison.Ordinal);
        Assert.Contains("rename", renameDiff.DiffText, StringComparison.OrdinalIgnoreCase);

        var deleteFiles = await service.GetCommitFilesAsync(
            new GetGitCommitFilesPayload(novel.Id, firstPage.Items[1].CommitId),
            CancellationToken.None);
        var deleted = Assert.Single(deleteFiles);
        Assert.Equal("deleted", deleted.ChangeType);
        Assert.Equal("notes.md", deleted.Path);
        Assert.Equal(0, deleted.Additions);
        Assert.True(deleted.Deletions >= 1);

        var deleteDiff = await service.GetFileDiffAsync(
            new GetGitFileDiffPayload(novel.Id, firstPage.Items[1].CommitId, "notes.md"),
            CancellationToken.None);
        Assert.Equal("deleted", deleteDiff.ChangeType);
        Assert.Contains("note line", deleteDiff.OriginalContent, StringComparison.Ordinal);
        Assert.Null(deleteDiff.ModifiedContent);
    }

    [Fact]
    public async Task MissingGitExecutableProducesVersionControlException()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new GitVersionControlService(
            options,
            gitExecutableOverride: Path.Combine(_root, "missing", "git-does-not-exist.exe"));

        var ex = await Assert.ThrowsAsync<VersionControlException>(async () =>
            await service.GetCommitSummariesAsync(new GetGitCommitsPayload(1, 1, 20), CancellationToken.None));
        Assert.Contains("Git executable was not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitCommandTimeoutProducesVersionControlException()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var fakeGit = await CreateSleepingGitExecutableAsync();
        var service = new GitVersionControlService(
            options,
            gitExecutableOverride: fakeGit,
            commandTimeout: TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<VersionControlException>(async () =>
            await service.GetCommitSummariesAsync(new GetGitCommitsPayload(1, 1, 20), CancellationToken.None));

        Assert.Contains("Git command timed out", ex.Message, StringComparison.Ordinal);
        Assert.Contains("init", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BundledGitCandidatePathsCoverWindowsMacOsAndLinuxRuntimeLayouts()
    {
        var windowsBase = Path.Combine(_root, "windows", "app");
        var windows = GitVersionControlService.GetBundledGitCandidatePaths(windowsBase, "windows");

        Assert.Equal(Path.Combine(windowsBase, "runtime", "git", "mingw64", "bin", "git.exe"), windows[0]);
        Assert.Contains(Path.Combine(windowsBase, "runtime", "git", "cmd", "git.exe"), windows);
        Assert.Contains(Path.Combine(windowsBase, "runtime", "git", "bin", "git.exe"), windows);
        Assert.Contains(Path.Combine(windowsBase, "runtime", "git", "git.exe"), windows);

        var macBase = Path.Combine(_root, "Novelist.app", "Contents", "MacOS");
        var macos = GitVersionControlService.GetBundledGitCandidatePaths(macBase, "macos");
        Assert.Equal(Path.GetFullPath(Path.Combine(macBase, "..", "Resources", "runtime", "git", "git")), macos[0]);
        Assert.Equal(Path.Combine(macBase, "runtime", "git", "git"), macos[1]);

        var linuxBase = Path.Combine(_root, "linux", "app");
        var linux = GitVersionControlService.GetBundledGitCandidatePaths(linuxBase, "linux");
        Assert.Equal([Path.Combine(linuxBase, "runtime", "git", "git")], linux);
    }

    [Fact]
    public async Task BridgeGitHistoryHandlersExposeReadOnlyHistoryMethods()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new GitVersionControlService(options);
        var novelService = new FileSystemNovelService(options, versionControl: service);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("Bridge Git", "", ""), CancellationToken.None);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString());
        await WriteWorkspaceTextAsync(workspace, "chapters/bridge.md", "bridge chapter line 1\nbridge chapter line 2\n");
        await service.CommitIfChangedAsync(novel.Id, "bridge add chapter", CancellationToken.None);

        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterGitHistoryHandlers(service);

        using var commitsJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_git_commits",
              "method": "GetGitCommits",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "page": 1, "size": 5 }] }
            }
            """));
        Assert.True(commitsJson.RootElement.GetProperty("ok").GetBoolean());
        var commits = commitsJson.RootElement.GetProperty("result");
        var latest = commits.GetProperty("items")[0];
        Assert.Equal("bridge add chapter", latest.GetProperty("message").GetString());
        Assert.True(latest.GetProperty("insertions").GetInt32() >= 1);
        Assert.True(latest.GetProperty("changed_file_count").GetInt32() >= 1);
        var commitId = latest.GetProperty("commit_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(commitId));

        using var filesJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_git_files",
              "method": "GetGitCommitFiles",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "commit_id": "{{commitId}}" }] }
            }
            """));
        Assert.True(filesJson.RootElement.GetProperty("ok").GetBoolean());
        var file = Assert.Single(filesJson.RootElement.GetProperty("result").EnumerateArray());
        Assert.Equal("chapters/bridge.md", file.GetProperty("path").GetString());
        Assert.Equal("added", file.GetProperty("change_type").GetString());

        using var diffJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_git_diff",
              "method": "GetGitFileDiff",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "commit_id": "{{commitId}}", "path": "chapters/bridge.md" }] }
            }
            """));
        Assert.True(diffJson.RootElement.GetProperty("ok").GetBoolean());
        var diff = diffJson.RootElement.GetProperty("result");
        Assert.Equal("chapters/bridge.md", diff.GetProperty("path").GetString());
        Assert.Equal("added", diff.GetProperty("change_type").GetString());
        Assert.True(diff.GetProperty("original_content").ValueKind is JsonValueKind.Null);
        Assert.Contains("bridge chapter line 2", diff.GetProperty("modified_content").GetString(), StringComparison.Ordinal);

        using var invalidJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_git",
              "method": "GetGitCommits",
              "payload": { "args": [] }
            }
            """));
        AssertBridgeError(invalidJson.RootElement, "req_bad_git", BridgeErrorCodes.ValidationError);
    }

    [Fact]
    public async Task BridgeGitHistoryHandlersReturnStableGitErrorForGitFailures()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new GitVersionControlService(
            options,
            gitExecutableOverride: Path.Combine(_root, "missing", "git-does-not-exist.exe"));
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterGitHistoryHandlers(service);

        using var json = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_git_missing",
              "method": "GetGitCommits",
              "payload": { "args": [{ "novel_id": 1, "page": 1, "size": 20 }] }
            }
            """));

        var error = AssertBridgeError(json.RootElement, "req_git_missing", BridgeErrorCodes.VersionControlError);
        Assert.Contains("Git executable was not found", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config", Guid.NewGuid().ToString("N")),
            DefaultDataDirectory = Path.Combine(_root, "data", Guid.NewGuid().ToString("N"))
        };
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static async ValueTask WriteWorkspaceTextAsync(string workspace, string relativePath, string content)
    {
        var path = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async ValueTask<string> CreateSleepingGitExecutableAsync()
    {
        var directory = Path.Combine(_root, "fake-git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, OperatingSystem.IsWindows() ? "git.cmd" : "git");
        var content = OperatingSystem.IsWindows()
            ? "@echo off\r\nping -n 6 127.0.0.1 > nul\r\n"
            : "#!/bin/sh\nsleep 5\n";
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        return path;
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static JsonElement AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        return error;
    }
}
