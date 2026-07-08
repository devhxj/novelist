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
    public async Task FileDiffMarksBinaryFilesAndTruncatesLargeTextContent()
    {
        const int maxTextLength = 200_000;
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new GitVersionControlService(options);
        var novelService = new FileSystemNovelService(options, versionControl: service);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("Diff Boundaries", "", ""), CancellationToken.None);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString());
        var largeText = "large text sentinel prefix\n" + string.Concat(Enumerable.Repeat("large text payload line\n", 12_000));
        var binaryBytes = new byte[1024];
        for (var i = 0; i < binaryBytes.Length; i++)
        {
            binaryBytes[i] = i % 2 == 0 ? (byte)0x00 : (byte)0xFF;
        }

        Assert.True(largeText.Length > maxTextLength);
        await WriteWorkspaceBytesAsync(workspace, "assets/cover.bin", binaryBytes);
        await WriteWorkspaceTextAsync(workspace, "chapters/large.md", largeText);
        var commit = await service.CommitIfChangedAsync(novel.Id, "add binary and large text", CancellationToken.None);

        Assert.True(commit.Committed);
        var files = await service.GetCommitFilesAsync(
            new GetGitCommitFilesPayload(novel.Id, commit.CommitHash),
            CancellationToken.None);
        var binaryFile = Assert.Single(files, item => string.Equals(item.Path, "assets/cover.bin", StringComparison.Ordinal));
        Assert.Equal("added", binaryFile.ChangeType);
        Assert.True(binaryFile.Binary);
        var largeFile = Assert.Single(files, item => string.Equals(item.Path, "chapters/large.md", StringComparison.Ordinal));
        Assert.Equal("added", largeFile.ChangeType);
        Assert.False(largeFile.Binary);

        var binaryDiff = await service.GetFileDiffAsync(
            new GetGitFileDiffPayload(novel.Id, commit.CommitHash, "assets/cover.bin"),
            CancellationToken.None);
        Assert.True(binaryDiff.Binary);
        Assert.Equal("added", binaryDiff.ChangeType);
        Assert.Null(binaryDiff.OriginalContent);
        Assert.Null(binaryDiff.ModifiedContent);
        Assert.True(binaryDiff.DiffText.Length <= maxTextLength);

        var largeDiff = await service.GetFileDiffAsync(
            new GetGitFileDiffPayload(novel.Id, commit.CommitHash, "chapters/large.md"),
            CancellationToken.None);
        Assert.False(largeDiff.Binary);
        Assert.True(largeDiff.Truncated);
        Assert.Null(largeDiff.OriginalContent);
        Assert.NotNull(largeDiff.ModifiedContent);
        Assert.StartsWith("large text sentinel prefix", largeDiff.ModifiedContent, StringComparison.Ordinal);
        Assert.True(largeDiff.ModifiedContent.Length <= maxTextLength);
        Assert.Contains("large text sentinel prefix", largeDiff.DiffText, StringComparison.Ordinal);
        Assert.True(largeDiff.DiffText.Length <= maxTextLength);
    }

    [Fact]
    public async Task CommitDoesNotDependOnConfiguredGitExecutable()
    {
        var previousGitPath = Environment.GetEnvironmentVariable("NOVELIST_GIT_PATH");
        var options = CreateOptions();
        try
        {
            Environment.SetEnvironmentVariable(
                "NOVELIST_GIT_PATH",
                Path.Combine(_root, "missing", "git-does-not-exist.exe"));
            await InitializeAsync(options);
            var service = new GitVersionControlService(options);
            var novelService = new FileSystemNovelService(options, versionControl: service);
            var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("LibGit2Sharp Git", "", ""), CancellationToken.None);
            var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString());
            await WriteWorkspaceTextAsync(workspace, "chapters/libgit.md", "libgit2sharp content\n");

            var result = await service.CommitIfChangedAsync(novel.Id, "commit through libgit2sharp", CancellationToken.None);

            Assert.True(result.Committed);
            Assert.False(string.IsNullOrWhiteSpace(result.CommitHash));
            var commits = await service.GetLogAsync(novel.Id, "chapters/libgit.md", 5, CancellationToken.None);
            Assert.Equal("commit through libgit2sharp", commits[0].Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVELIST_GIT_PATH", previousGitPath);
        }
    }

    [Theory]
    [InlineData("index.lock")]
    [InlineData("HEAD.lock")]
    [InlineData("config.lock")]
    public async Task CommitIfChangedRejectsExistingGitLockWithoutDeletingIt(string lockFileName)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new GitVersionControlService(options);
        var novelService = new FileSystemNovelService(options, versionControl: service);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("Locked Git", "", ""), CancellationToken.None);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString());
        var lockPath = Path.Combine(workspace, ".git", lockFileName);
        await File.WriteAllTextAsync(lockPath, "other git process", CancellationToken.None);
        await WriteWorkspaceTextAsync(workspace, "chapters/locked.md", "locked content\n");

        var ex = await Assert.ThrowsAsync<VersionControlException>(async () =>
            await service.CommitIfChangedAsync(novel.Id, "commit while locked", CancellationToken.None));

        Assert.Contains("Git repository lock file exists", ex.Message, StringComparison.Ordinal);
        Assert.Contains(lockFileName, ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(lockPath), "stale lock files must not be deleted automatically");
    }

    [Fact]
    public async Task InvalidRepositoryMetadataProducesStableVersionControlException()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", "1");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, ".git"), "not a gitdir", CancellationToken.None);
        var service = new GitVersionControlService(options);

        var ex = await Assert.ThrowsAsync<VersionControlException>(async () =>
            await service.GetCommitSummariesAsync(new GetGitCommitsPayload(1, 1, 20), CancellationToken.None));

        Assert.Contains("Git repository metadata is invalid or unsupported", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', ex.Message);
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
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", "1");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, ".git"), "not a gitdir", CancellationToken.None);
        var service = new GitVersionControlService(options);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterGitHistoryHandlers(service);

        using var json = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_git_invalid",
              "method": "GetGitCommits",
              "payload": { "args": [{ "novel_id": 1, "page": 1, "size": 20 }] }
            }
            """));

        var error = AssertBridgeError(json.RootElement, "req_git_invalid", BridgeErrorCodes.VersionControlError);
        Assert.Contains("Git repository metadata is invalid or unsupported", error.GetProperty("message").GetString(), StringComparison.Ordinal);
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

    private static async ValueTask WriteWorkspaceBytesAsync(string workspace, string relativePath, byte[] content)
    {
        var path = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
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
