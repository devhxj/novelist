using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class NovelImportRunServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportRunsPersistStateWarningsAndDiagnosticsWithoutRawSourcePath()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var sourcePath = CreateImportFixture("测试锚定小说.txt");
        var service = new FileSystemNovelImportRunService(options);

        var started = await service.StartRunAsync(
            new StartNovelImportPayload(
                TaskId: "import-task-1",
                SourcePath: sourcePath,
                SourceDisplayName: "测试锚定小说.txt",
                ImportKind: NovelImportKinds.Txt,
                RequestedTitle: "测试锚定小说",
                CommitMessage: "import test novel"),
            CancellationToken.None);

        Assert.Equal("import-task-1", started.TaskId);
        Assert.Equal(NovelImportRunStates.Created, started.State);
        Assert.Equal("created", started.Stage);
        Assert.Equal("测试锚定小说.txt", started.SourceDisplayName);
        Assert.StartsWith("sha256:", started.SourcePathHash, StringComparison.Ordinal);
        Assert.Equal(NovelImportKinds.Txt, started.ParserType);
        Assert.Null(started.CreatedNovelId);
        Assert.Empty(started.CreatedFileRoots);
        Assert.Null(started.CompletedAt);

        await service.UpdateRunAsync(
            new NovelImportRunUpdate(
                TaskId: "import-task-1",
                State: NovelImportRunStates.Parsing,
                Stage: "detect_encoding",
                CreatedNovelId: null,
                CreatedFileRoots: null,
                SkippedChapters: null,
                Diagnostics:
                [
                    new NovelImportDiagnosticPayload(
                        Code: "import.encoding.utf8",
                        Message: "已按 UTF-8 解码。",
                        Detail: "confidence=high",
                        Severity: "info")
                ],
                Warnings: null,
                Error: null),
            CancellationToken.None);

        await service.UpdateRunAsync(
            new NovelImportRunUpdate(
                TaskId: "import-task-1",
                State: NovelImportRunStates.CompletedWithWarning,
                Stage: "git_commit",
                CreatedNovelId: 42,
                CreatedFileRoots: ["novels/42"],
                SkippedChapters:
                [
                    new NovelImportSkippedChapterPayload(
                        Index: 3,
                        Title: "空章节",
                        Reason: "empty_content")
                ],
                Diagnostics: null,
                Warnings:
                [
                    new NovelImportWarningPayload(
                        Code: "git.commit_failed",
                        Message: "导入已完成，但 Git 提交失败。",
                        Detail: "下次保存时可重试提交。")
                ],
                Error: null),
            CancellationToken.None);

        var reloaded = new FileSystemNovelImportRunService(options);
        var persisted = await reloaded.GetRunAsync(
            new GetNovelImportRunPayload("import-task-1"),
            CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(NovelImportRunStates.CompletedWithWarning, persisted.State);
        Assert.Equal("git_commit", persisted.Stage);
        Assert.Equal(42, persisted.CreatedNovelId);
        Assert.Equal(["novels/42"], persisted.CreatedFileRoots);
        Assert.Single(persisted.Diagnostics);
        Assert.Single(persisted.Warnings);
        Assert.Single(persisted.SkippedChapters);
        Assert.NotNull(persisted.CompletedAt);

        var storePath = Path.Combine(options.DefaultDataDirectory, "novel_imports", "runs.json");
        var rawStore = await File.ReadAllTextAsync(storePath);
        Assert.Contains("\"source_path_hash\"", rawStore);
        Assert.Contains("\"source_display_name\"", rawStore);
        Assert.DoesNotContain(sourcePath, rawStore);
    }

    [Fact]
    public async Task ImportRunStateTransitionsAreMonotonicAndTerminal()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelImportRunService(options);
        await service.StartRunAsync(ValidStartPayload("import-state-1"), CancellationToken.None);

        await service.UpdateRunAsync(
            new NovelImportRunUpdate("import-state-1", NovelImportRunStates.Parsing, "parse", null, null, null, null, null, null),
            CancellationToken.None);
        await service.UpdateRunAsync(
            new NovelImportRunUpdate("import-state-1", NovelImportRunStates.WritingFiles, "write_chapters", 7, ["novels/7"], null, null, null, null),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.UpdateRunAsync(
                new NovelImportRunUpdate("import-state-1", NovelImportRunStates.Parsing, "parse_again", null, null, null, null, null, null),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateRunAsync(
                new NovelImportRunUpdate("import-state-1", "half_done", "bad", null, null, null, null, null, null),
                CancellationToken.None));

        await service.UpdateRunAsync(
            new NovelImportRunUpdate("import-state-1", NovelImportRunStates.CleanupPending, "cleanup_created_files", null, null, null, null, null, Error("import.write_failed", "写入章节失败。", "import-state-1")),
            CancellationToken.None);
        var cleanup = await service.UpdateRunAsync(
            new NovelImportRunUpdate("import-state-1", NovelImportRunStates.CleanupCompleted, "cleanup_completed", null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Equal(NovelImportRunStates.CleanupCompleted, cleanup.State);
        Assert.NotNull(cleanup.CompletedAt);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.UpdateRunAsync(
                new NovelImportRunUpdate("import-state-1", NovelImportRunStates.Failed, "failed", null, null, null, null, null, Error("import.failed", "导入失败。", "import-state-1")),
                CancellationToken.None));
    }

    [Fact]
    public async Task ImportRunValidationRejectsUnsafePayloads()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelImportRunService(options);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload(""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-relative") with { SourcePath = "relative.txt" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-display") with { SourceDisplayName = "" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(
                ValidStartPayload("import-display-slash") with { SourceDisplayName = "folder/book.txt" },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(
                ValidStartPayload("import-display-backslash") with { SourceDisplayName = @"folder\book.txt" },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-kind") with { ImportKind = "pdf" }, CancellationToken.None));

        await service.StartRunAsync(ValidStartPayload("import-duplicate"), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-duplicate"), CancellationToken.None));
    }

    [Fact]
    public async Task ImportRunCreatedFileRootsNormalizeBackslashSeparatorsAndRejectRootedPaths()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelImportRunService(options);
        await service.StartRunAsync(ValidStartPayload("import-created-roots"), CancellationToken.None);

        var updated = await service.UpdateRunAsync(
            new NovelImportRunUpdate(
                TaskId: "import-created-roots",
                State: NovelImportRunStates.WritingFiles,
                Stage: "write_chapters",
                CreatedNovelId: 7,
                CreatedFileRoots: [@"novels\7", "novels/7/chapters/"],
                SkippedChapters: null,
                Diagnostics: null,
                Warnings: null,
                Error: null),
            CancellationToken.None);

        Assert.Equal(["novels/7", "novels/7/chapters"], updated.CreatedFileRoots);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateRunAsync(
                new NovelImportRunUpdate(
                    TaskId: "import-created-roots",
                    State: NovelImportRunStates.SavingMetadata,
                    Stage: "saving_metadata",
                    CreatedNovelId: 7,
                    CreatedFileRoots: [Path.GetFullPath(Path.Combine(_root, "outside"))],
                    SkippedChapters: null,
                    Diagnostics: null,
                    Warnings: null,
                    Error: null),
                CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateRunAsync(
                new NovelImportRunUpdate(
                    TaskId: "import-created-roots",
                    State: NovelImportRunStates.SavingMetadata,
                    Stage: "saving_metadata",
                    CreatedNovelId: 7,
                    CreatedFileRoots: [@"C:\novels\7"],
                    SkippedChapters: null,
                    Diagnostics: null,
                    Warnings: null,
                    Error: null),
                CancellationToken.None));
    }

    [Fact]
    public async Task ImportRunValidationRejectsUnsafeSourceFileBoundary()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelImportRunService(options);

        var missingPath = Path.Combine(_root, "fixtures", "missing.txt");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-missing", sourcePath: missingPath), CancellationToken.None));

        var directoryPath = Path.Combine(_root, "fixtures", "directory.txt");
        Directory.CreateDirectory(directoryPath);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-directory", sourcePath: directoryPath), CancellationToken.None));

        var lockedPath = CreateImportFixture("locked.txt");
        using (File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await service.StartRunAsync(ValidStartPayload("import-locked", sourcePath: lockedPath), CancellationToken.None));
        }

        var unsupportedPath = CreateImportFixture("unsupported.pdf");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-unsupported", sourcePath: unsupportedPath), CancellationToken.None));

        var markdownPath = CreateImportFixture("mismatch.md");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(
                ValidStartPayload("import-mismatch", sourcePath: markdownPath, importKind: NovelImportKinds.Txt),
                CancellationToken.None));

        var outsidePath = CreateImportFixture("outside.txt");
        var traversalPath = Path.Combine(_root, "fixtures", "..", Path.GetFileName(outsidePath));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-traversal", sourcePath: traversalPath), CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(
                ValidStartPayload("import-url", sourcePath: "https://example.test/book.txt"),
                CancellationToken.None));

        var devicePath = OperatingSystem.IsWindows()
            ? @"\\?\C:\novelist\book.txt"
            : "/dev/null";
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-device", sourcePath: devicePath), CancellationToken.None));
    }

    [Fact]
    public async Task ImportRunValidationRejectsOversizedSourceFiles()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelImportRunService(options);

        var oversizedTextPath = CreateImportFixture("too-large.txt", length: (50L * 1024 * 1024) + 1);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(ValidStartPayload("import-oversized-txt", sourcePath: oversizedTextPath), CancellationToken.None));

        var oversizedMarkdownPath = CreateImportFixture("too-large.markdown", length: (50L * 1024 * 1024) + 1);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(
                ValidStartPayload("import-oversized-markdown", sourcePath: oversizedMarkdownPath, importKind: NovelImportKinds.Markdown),
                CancellationToken.None));

        var oversizedEpubPath = CreateImportFixture("too-large.epub", length: (100L * 1024 * 1024) + 1);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartRunAsync(
                ValidStartPayload("import-oversized-epub", sourcePath: oversizedEpubPath, importKind: NovelImportKinds.Epub),
                CancellationToken.None));
    }

    [Fact]
    public async Task BridgeNovelImportHandlersPersistAndClassifyRecoveryState()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemNovelImportRunService(options);
        var bridgeSourcePath = CreateImportFixture("bridge.txt");
        var selectedImportPath = CreateImportFixture("selected.epub");
        var sourcePath = JsonEncodedText.Encode(bridgeSourcePath).ToString();
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNovelImportHandlers(service, new RecordingNovelImportFilePicker(selectedImportPath));

        using var pickedImportFile = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_import_pick",
              "method": "PickNovelImportFile",
              "payload": {}
            }
            """));
        Assert.Equal(selectedImportPath, pickedImportFile.RootElement.GetProperty("result").GetString());

        using var startJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_import_start",
              "method": "StartNovelImport",
              "payload": {
                "args": [{
                  "task_id": "import-bridge-1",
                  "source_path": "{{sourcePath}}",
                  "source_display_name": "bridge.txt",
                  "import_kind": "txt",
                  "requested_title": "Bridge Import",
                  "commit_message": "import bridge"
                }]
              }
            }
            """));
        Assert.Equal(NovelImportRunStates.Created, startJson.RootElement.GetProperty("result").GetProperty("state").GetString());

        using var recoveryJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_import_recovery",
              "method": "GetNovelImportRecoveryStatus",
              "payload": {}
            }
            """));
        Assert.Single(recoveryJson.RootElement.GetProperty("result").GetProperty("pending_runs").EnumerateArray());

        await service.UpdateRunAsync(
            new NovelImportRunUpdate(
                TaskId: "import-bridge-1",
                State: NovelImportRunStates.CleanupBlocked,
                Stage: "cleanup_blocked",
                CreatedNovelId: null,
                CreatedFileRoots: ["novels/99"],
                SkippedChapters: null,
                Diagnostics: null,
                Warnings: null,
                Error: Error("import.cleanup_blocked", "清理导入残留失败。", "import-bridge-1")),
            CancellationToken.None);

        using var blockedJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_import_blocked",
              "method": "GetNovelImportRecoveryStatus",
              "payload": {}
            }
            """));
        var blocked = blockedJson.RootElement.GetProperty("result").GetProperty("blocked_runs");
        Assert.Single(blocked.EnumerateArray());
        Assert.Equal(NovelImportRunStates.CleanupBlocked, blocked[0].GetProperty("state").GetString());

        using var invalidJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_import_bad",
              "method": "StartNovelImport",
              "payload": {
                "args": [{
                  "task_id": "",
                  "source_path": "relative.txt",
                  "source_display_name": "bad.txt",
                  "import_kind": "txt",
                  "requested_title": null,
                  "commit_message": null
                }]
              }
            }
            """));
        AssertBridgeError(invalidJson.RootElement, "req_import_bad", BridgeErrorCodes.ValidationError);
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
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private StartNovelImportPayload ValidStartPayload(string taskId)
    {
        var sourcePath = CreateImportFixture($"{(string.IsNullOrWhiteSpace(taskId) ? "fixture" : taskId)}.txt");
        return ValidStartPayload(taskId, sourcePath);
    }

    private StartNovelImportPayload ValidStartPayload(
        string taskId,
        string sourcePath,
        string? sourceDisplayName = null,
        string importKind = NovelImportKinds.Txt)
    {
        return new StartNovelImportPayload(
            TaskId: taskId,
            SourcePath: sourcePath,
            SourceDisplayName: sourceDisplayName ?? Path.GetFileName(sourcePath),
            ImportKind: importKind,
            RequestedTitle: "测试导入",
            CommitMessage: "import novel");
    }

    private string CreateImportFixture(
        string fileName,
        string content = "第一章\n导入测试内容。",
        long? length = null)
    {
        var fixtureDirectory = Path.Combine(_root, "fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        var path = Path.Combine(fixtureDirectory, fileName);
        if (length is null)
        {
            File.WriteAllText(path, content);
            return path;
        }

        using var stream = File.Create(path);
        stream.SetLength(length.Value);
        return path;
    }

    private static CopyableDiagnosticPayload Error(string code, string message, string taskId)
    {
        return new CopyableDiagnosticPayload(
            Code: code,
            Message: message,
            Detail: "",
            Operation: "StartNovelImport",
            TaskId: taskId,
            RunId: null,
            BridgeMethod: null,
            Timestamp: DateTimeOffset.Parse("2026-07-07T00:00:00Z"));
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static void AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class RecordingNovelImportFilePicker : INovelImportFilePicker
    {
        private readonly string? _path;

        public RecordingNovelImportFilePicker(string? path)
        {
            _path = path;
        }

        public ValueTask<string?> PickImportFileAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_path);
        }
    }
}
