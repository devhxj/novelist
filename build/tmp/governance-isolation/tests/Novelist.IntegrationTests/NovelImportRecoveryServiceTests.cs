using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class NovelImportRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReconcileAsyncCleansPartialNovelRowsFilesAndImportRunIdempotently()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        var novelService = new FileSystemNovelService(options);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("半截导入", "crash fixture", ""), CancellationToken.None);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(Path.Combine(workspace, "chapters", "001.md"), "partial chapter");

        await runService.StartRunAsync(ValidStartPayload("import-recover-clean"), CancellationToken.None);
        await runService.UpdateRunAsync(
            new NovelImportRunUpdate(
                "import-recover-clean",
                NovelImportRunStates.WritingFiles,
                "write_chapters",
                novel.Id,
                [$"novels/{novel.Id}"],
                null,
                null,
                null,
                null),
            CancellationToken.None);

        var service = new FileSystemNovelImportRecoveryService(options, novelService);
        var first = await service.ReconcileAsync(CancellationToken.None);
        var second = await service.ReconcileAsync(CancellationToken.None);

        var reconciled = Assert.Single(first.ReconciledRuns);
        Assert.Equal("import-recover-clean", reconciled.TaskId);
        Assert.Equal(NovelImportRunStates.CleanupCompleted, reconciled.State);
        Assert.Equal("cleanup_completed", reconciled.Stage);
        Assert.Equal("import.recovered_cleanup", reconciled.Error?.Code);
        Assert.Empty(first.BlockedRuns);
        Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.False(Directory.Exists(workspace));

        Assert.Empty(second.ReconciledRuns);
        Assert.Empty(second.BlockedRuns);
    }

    [Fact]
    public async Task ReconcileAsyncNeverDeletesCompletedImportsWithWarnings()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        var novelService = new FileSystemNovelService(options);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("完整导入", "durable fixture", ""), CancellationToken.None);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await runService.StartRunAsync(ValidStartPayload("import-recover-completed"), CancellationToken.None);
        await runService.UpdateRunAsync(
            new NovelImportRunUpdate(
                "import-recover-completed",
                NovelImportRunStates.CompletedWithWarning,
                "done",
                novel.Id,
                [$"novels/{novel.Id}"],
                null,
                null,
                [new NovelImportWarningPayload("git.commit_failed", "导入已完成，但 Git 提交失败。", "retry later")],
                null),
            CancellationToken.None);

        var service = new FileSystemNovelImportRecoveryService(options, novelService);
        var result = await service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(result.ReconciledRuns);
        Assert.Empty(result.BlockedRuns);
        Assert.True(Directory.Exists(workspace));
        Assert.Single(await novelService.GetNovelsAsync(CancellationToken.None));
        var persisted = await runService.GetRunAsync(new GetNovelImportRunPayload("import-recover-completed"), CancellationToken.None);
        Assert.Equal(NovelImportRunStates.CompletedWithWarning, persisted?.State);
    }

    [Fact]
    public async Task ReconcileAsyncPreservesDurableImportsInterruptedDuringGitCommit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        var novelService = new FileSystemNovelService(options);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("提交中断", "durable fixture", ""), CancellationToken.None);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await runService.StartRunAsync(ValidStartPayload("import-recover-git"), CancellationToken.None);
        await runService.UpdateRunAsync(
            new NovelImportRunUpdate(
                "import-recover-git",
                NovelImportRunStates.GitCommit,
                "git_commit",
                novel.Id,
                [$"novels/{novel.Id}"],
                null,
                null,
                null,
                null),
            CancellationToken.None);

        var service = new FileSystemNovelImportRecoveryService(options, novelService);
        var result = await service.ReconcileAsync(CancellationToken.None);

        var recovered = Assert.Single(result.ReconciledRuns);
        Assert.Equal(NovelImportRunStates.CompletedWithWarning, recovered.State);
        Assert.Equal("import.recovered_after_durable_phase", Assert.Single(recovered.Warnings).Code);
        Assert.True(Directory.Exists(workspace));
        Assert.Single(await novelService.GetNovelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReconcileAsyncCleansKnownWorkspaceDirectoryWhenNovelRowIsAlreadyMissing()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        var missingWorkspace = Path.Combine(options.DefaultDataDirectory, "novels", "77");
        Directory.CreateDirectory(Path.Combine(missingWorkspace, "chapters"));
        await File.WriteAllTextAsync(Path.Combine(missingWorkspace, "chapters", "001.md"), "orphan");

        await runService.StartRunAsync(ValidStartPayload("import-recover-directory-only"), CancellationToken.None);
        await runService.UpdateRunAsync(
            new NovelImportRunUpdate(
                "import-recover-directory-only",
                NovelImportRunStates.WritingFiles,
                "write_chapters",
                77,
                ["novels/77"],
                null,
                null,
                null,
                null),
            CancellationToken.None);

        var service = new FileSystemNovelImportRecoveryService(options, new FileSystemNovelService(options));
        var result = await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(NovelImportRunStates.CleanupCompleted, Assert.Single(result.ReconciledRuns).State);
        Assert.False(Directory.Exists(missingWorkspace));
    }

    [Fact]
    public async Task ReconcileAsyncCleansPendingIndexAndReferenceStateOnlyForRecoveredImportNovel()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        var novelService = new FileSystemNovelService(options);
        var partialNovel = await novelService.CreateNovelAsync(new CreateNovelPayload("待恢复导入", "crash fixture", ""), CancellationToken.None);
        var survivorNovel = await novelService.CreateNovelAsync(new CreateNovelPayload("保留小说", "existing user data", ""), CancellationToken.None);

        await CreatePendingRagStateAsync(options, partialNovel.Id, survivorNovel.Id);
        await CreatePendingReferenceStateAsync(options, partialNovel.Id, survivorNovel.Id);
        await runService.StartRunAsync(ValidStartPayload("import-recover-side-effects"), CancellationToken.None);
        await runService.UpdateRunAsync(
            new NovelImportRunUpdate(
                "import-recover-side-effects",
                NovelImportRunStates.SavingMetadata,
                "saving_metadata",
                partialNovel.Id,
                [$"novels/{partialNovel.Id}"],
                null,
                null,
                null,
                null),
            CancellationToken.None);

        var service = new FileSystemNovelImportRecoveryService(options, novelService);
        var result = await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(NovelImportRunStates.CleanupCompleted, Assert.Single(result.ReconciledRuns).State);
        Assert.Empty(result.BlockedRuns);

        Assert.Equal(0, await CountRagIndexRowsAsync(options, partialNovel.Id));
        Assert.Equal(0, await CountRagChunkRowsAsync(options, partialNovel.Id));
        Assert.False(await RagVectorTableExistsAsync(options, partialNovel.Id));
        Assert.Equal(1, await CountRagIndexRowsAsync(options, survivorNovel.Id));
        Assert.Equal(1, await CountRagChunkRowsAsync(options, survivorNovel.Id));
        Assert.True(await RagVectorTableExistsAsync(options, survivorNovel.Id));

        Assert.Equal(0, await CountReferenceAnchorsAsync(options, partialNovel.Id));
        Assert.Equal(0, await CountReferenceStyleBuildsAsync(options, partialNovel.Id));
        Assert.Equal(1, await CountReferenceAnchorsAsync(options, survivorNovel.Id));
        Assert.Equal(1, await CountReferenceStyleBuildsAsync(options, survivorNovel.Id));
    }

    [Theory]
    [InlineData(NovelImportRunStates.CreatingNovel, "create_novel", false)]
    [InlineData(NovelImportRunStates.WritingFiles, "write_chapters", true)]
    [InlineData(NovelImportRunStates.SavingMetadata, "saving_metadata", true)]
    public async Task ReconcileAsyncRequiresManualReviewWhenOwnershipCannotBeProvedPastCreationBoundary(
        string state,
        string stage,
        bool shouldBlock)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        await runService.StartRunAsync(ValidStartPayload($"import-recover-unknown-{state}"), CancellationToken.None);
        await OverwriteRunStoreAsync(
            options,
            $"import-recover-unknown-{state}",
            state,
            stage,
            createdNovelId: null,
            createdFileRoots: []);

        var service = new FileSystemNovelImportRecoveryService(options, new FileSystemNovelService(options));
        var result = await service.ReconcileAsync(CancellationToken.None);

        if (shouldBlock)
        {
            Assert.Empty(result.ReconciledRuns);
            var blocked = Assert.Single(result.BlockedRuns);
            Assert.Equal(NovelImportRunStates.CleanupBlocked, blocked.State);
            Assert.Equal("import.cleanup_manual_review_required", blocked.Error?.Code);
            Assert.Contains("cannot prove which novel", blocked.Error?.Detail, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.Equal(NovelImportRunStates.CleanupCompleted, Assert.Single(result.ReconciledRuns).State);
        Assert.Empty(result.BlockedRuns);
    }

    [Theory]
    [InlineData("after-novel-row-creation", NovelImportRunStates.WritingFiles, "write_chapters", "row_only", NovelImportRunStates.CleanupCompleted)]
    [InlineData("after-directory-creation", NovelImportRunStates.WritingFiles, "write_chapters", "directory_only", NovelImportRunStates.CleanupCompleted)]
    [InlineData("after-partial-chapter-file-writes", NovelImportRunStates.WritingFiles, "write_chapters", "partial_chapter", NovelImportRunStates.CleanupCompleted)]
    [InlineData("after-metadata-writes", NovelImportRunStates.SavingMetadata, "saving_metadata", "metadata_with_side_effects", NovelImportRunStates.CleanupCompleted)]
    [InlineData("during-index-update", NovelImportRunStates.Indexing, "indexing", "durable_indexing", NovelImportRunStates.CompletedWithWarning)]
    [InlineData("during-git-commit", NovelImportRunStates.GitCommit, "git_commit", "durable_git", NovelImportRunStates.CompletedWithWarning)]
    public async Task ReconcileAsyncHandlesSimulatedProcessDeathMatrix(
        string scenario,
        string state,
        string stage,
        string fixtureKind,
        string expectedState)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        var novelService = new FileSystemNovelService(options);
        var taskId = $"import-recover-{scenario}";
        var createdNovelId = await CreateCrashFixtureAsync(options, novelService, fixtureKind);
        var workspace = Path.Combine(
            options.DefaultDataDirectory,
            "novels",
            createdNovelId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await runService.StartRunAsync(ValidStartPayload(taskId), CancellationToken.None);
        await runService.UpdateRunAsync(
            new NovelImportRunUpdate(
                taskId,
                state,
                stage,
                createdNovelId,
                [$"novels/{createdNovelId}"],
                null,
                null,
                null,
                null),
            CancellationToken.None);

        var service = new FileSystemNovelImportRecoveryService(options, novelService);
        var result = await service.ReconcileAsync(CancellationToken.None);
        Assert.Empty(result.BlockedRuns);

        var recovered = Assert.Single(result.ReconciledRuns);
        Assert.Equal(expectedState, recovered.State);

        if (expectedState == NovelImportRunStates.CleanupCompleted)
        {
            Assert.Empty(await novelService.GetNovelsAsync(CancellationToken.None));
            Assert.False(Directory.Exists(workspace));
            if (fixtureKind == "metadata_with_side_effects")
            {
                Assert.Equal(0, await CountRagIndexRowsAsync(options, createdNovelId));
                Assert.Equal(0, await CountReferenceAnchorsAsync(options, createdNovelId));
                Assert.Equal(0, await CountReferenceStyleBuildsAsync(options, createdNovelId));
            }

            return;
        }

        Assert.True(Directory.Exists(workspace));
        Assert.Contains(await novelService.GetNovelsAsync(CancellationToken.None), novel => novel.Id == createdNovelId);
        Assert.Equal("import.recovered_after_durable_phase", Assert.Single(recovered.Warnings).Code);
    }

    [Fact]
    public async Task ReconcileAsyncBlocksCorruptedCreatedFileRootsWithoutDeletingOutsideFiles()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        var outside = Path.Combine(_root, "outside", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "must remain");

        await runService.StartRunAsync(ValidStartPayload("import-recover-blocked"), CancellationToken.None);
        await OverwriteRunStoreAsync(
            options,
            "import-recover-blocked",
            NovelImportRunStates.WritingFiles,
            "write_chapters",
            99,
            [Path.GetFullPath(Path.Combine(_root, "outside"))]);

        var service = new FileSystemNovelImportRecoveryService(options, new FileSystemNovelService(options));
        var result = await service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(result.ReconciledRuns);
        var blocked = Assert.Single(result.BlockedRuns);
        Assert.Equal("import-recover-blocked", blocked.TaskId);
        Assert.Equal(NovelImportRunStates.CleanupBlocked, blocked.State);
        Assert.Equal("import.cleanup_blocked", blocked.Error?.Code);
        Assert.True(File.Exists(outside));

        var status = await runService.GetRecoveryStatusAsync(CancellationToken.None);
        Assert.Empty(status.PendingRuns);
        Assert.Single(status.BlockedRuns);
    }

    [Fact]
    public async Task BridgeRegistersReconcileNovelImportRuns()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var runService = new FileSystemNovelImportRunService(options);
        var novelService = new FileSystemNovelService(options);
        var recoveryService = new FileSystemNovelImportRecoveryService(options, novelService);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterNovelImportHandlers(runService, recoveryService: recoveryService);

        using var json = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_import_reconcile",
              "method": "ReconcileNovelImportRuns",
              "payload": {}
            }
            """));

        Assert.Equal("response", json.RootElement.GetProperty("kind").GetString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("result").GetProperty("reconciled_runs").EnumerateArray());
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
        var fixtures = Path.Combine(_root, "fixtures");
        Directory.CreateDirectory(fixtures);
        var path = Path.Combine(fixtures, $"{taskId}.txt");
        File.WriteAllText(path, "第一章\n导入测试内容。");
        return new StartNovelImportPayload(taskId, path, Path.GetFileName(path), NovelImportKinds.Txt, "测试导入", "import novel");
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static async ValueTask OverwriteRunStoreAsync(
        AppInitializationOptions options,
        string taskId,
        string state,
        string stage,
        long? createdNovelId,
        IReadOnlyList<string> createdFileRoots)
    {
        var storePath = Path.Combine(options.DefaultDataDirectory, "novel_imports", "runs.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(storePath));
        var run = document.RootElement.GetProperty("runs").EnumerateArray().Single(item => item.GetProperty("task_id").GetString() == taskId);
        var updated = $$"""
            {
              "version": 1,
              "runs": [{
                "task_id": "{{taskId}}",
                "state": "{{state}}",
                "stage": "{{stage}}",
                "source_display_name": "{{run.GetProperty("source_display_name").GetString()}}",
                "source_path_hash": "{{run.GetProperty("source_path_hash").GetString()}}",
                "parser_type": "{{run.GetProperty("parser_type").GetString()}}",
                "requested_title": "{{run.GetProperty("requested_title").GetString()}}",
                "commit_message": "{{run.GetProperty("commit_message").GetString()}}",
                "created_novel_id": {{(createdNovelId is null ? "null" : createdNovelId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}},
                "created_file_roots": {{JsonSerializer.Serialize(createdFileRoots)}},
                "skipped_chapters": [],
                "diagnostics": [],
                "warnings": [],
                "error": null,
                "cleanup_state": "not_started",
                "warning_state": "none",
                "started_at": "{{run.GetProperty("started_at").GetDateTimeOffset():O}}",
                "updated_at": "{{DateTimeOffset.UtcNow:O}}",
                "completed_at": null
              }]
            }
            """;
        await File.WriteAllTextAsync(storePath, updated);
    }

    private static async ValueTask<long> CreateCrashFixtureAsync(
        AppInitializationOptions options,
        FileSystemNovelService novelService,
        string fixtureKind)
    {
        if (fixtureKind == "directory_only")
        {
            var missingNovelId = 77L;
            Directory.CreateDirectory(Path.Combine(options.DefaultDataDirectory, "novels", "77", "chapters"));
            return missingNovelId;
        }

        var novel = await novelService.CreateNovelAsync(
            new CreateNovelPayload($"恢复矩阵 {fixtureKind}", "crash matrix fixture", ""),
            CancellationToken.None);
        if (fixtureKind is "partial_chapter" or "metadata_with_side_effects" or "durable_indexing" or "durable_git")
        {
            var chapterPath = Path.Combine(
                options.DefaultDataDirectory,
                "novels",
                novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "chapters",
                "001.md");
            await File.WriteAllTextAsync(chapterPath, $"# 第一章\n{fixtureKind}");
        }

        if (fixtureKind == "metadata_with_side_effects")
        {
            await CreatePendingRagStateAsync(options, novel.Id, survivorNovelId: 900_001);
            await CreatePendingReferenceStateAsync(options, novel.Id, survivorNovelId: 900_001);
        }

        return novel.Id;
    }

    private static async ValueTask CreatePendingRagStateAsync(
        AppInitializationOptions options,
        long partialNovelId,
        long survivorNovelId)
    {
        var databasePath = Path.Combine(options.DefaultDataDirectory, "rag", "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenSqliteAsync(databasePath);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE rag_index_state (
              novel_id INTEGER PRIMARY KEY,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              chunker_version TEXT NOT NULL,
              status TEXT NOT NULL,
              chunk_count INTEGER NOT NULL,
              vector_table TEXT NOT NULL,
              last_error TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE rag_chunks (
              chunk_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              chapter_number INTEGER NOT NULL,
              chunk_type TEXT NOT NULL,
              chunk_index INTEGER NOT NULL,
              start_position INTEGER NOT NULL,
              content TEXT NOT NULL,
              content_hash TEXT NOT NULL,
              file_path TEXT NOT NULL,
              title TEXT NOT NULL
            );
            """);
        await InsertRagStateAsync(connection, partialNovelId, "stale");
        await InsertRagStateAsync(connection, survivorNovelId, "stale");
        await ExecuteAsync(connection, $"""CREATE TABLE "vec_novel_{partialNovelId}_3" (rowid INTEGER PRIMARY KEY, embedding TEXT NOT NULL);""");
        await ExecuteAsync(connection, $"""CREATE TABLE "vec_novel_{survivorNovelId}_3" (rowid INTEGER PRIMARY KEY, embedding TEXT NOT NULL);""");
    }

    private static async ValueTask CreatePendingReferenceStateAsync(
        AppInitializationOptions options,
        long partialNovelId,
        long survivorNovelId)
    {
        var databasePath = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenSqliteAsync(databasePath);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE reference_anchors (
              anchor_id INTEGER PRIMARY KEY,
              novel_id INTEGER,
              title TEXT NOT NULL,
              author TEXT NOT NULL,
              source_path TEXT NOT NULL,
              source_kind TEXT NOT NULL,
              license_status TEXT NOT NULL,
              source_file_hash TEXT NOT NULL,
              build_version TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              corpus_visibility TEXT NOT NULL DEFAULT 'private',
              source_trust TEXT NOT NULL DEFAULT 'user_verified',
              user_tags_json TEXT NOT NULL DEFAULT '[]'
            );
            CREATE TABLE reference_anchor_build_state (
              anchor_id INTEGER PRIMARY KEY,
              status TEXT NOT NULL,
              stage TEXT NOT NULL,
              source_segment_count INTEGER NOT NULL,
              material_count INTEGER NOT NULL,
              slot_count INTEGER NOT NULL,
              vector_count INTEGER NOT NULL,
              last_error TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );
            CREATE TABLE reference_style_profile_builds (
              build_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              profile_id INTEGER,
              title TEXT NOT NULL,
              status TEXT NOT NULL,
              stage TEXT NOT NULL,
              progress_completed INTEGER NOT NULL,
              progress_total INTEGER NOT NULL,
              anchor_ids_json TEXT NOT NULL,
              source_hashes_json TEXT NOT NULL,
              diagnostics_json TEXT NOT NULL,
              error_code TEXT,
              error_message TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              completed_at TEXT,
              cancelled_at TEXT
            );
            """);
        await InsertReferenceStateAsync(connection, anchorId: 1001, partialNovelId, "importing", "partial-style-build");
        await InsertReferenceStateAsync(connection, anchorId: 1002, survivorNovelId, "embedding", "survivor-style-build");
    }

    private static async ValueTask InsertRagStateAsync(SqliteConnection connection, long novelId, string status)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await ExecuteAsync(
            connection,
            """
            INSERT INTO rag_index_state
              (novel_id, provider_key, model_id, dimensions, chunker_version, status, chunk_count, vector_table, last_error, updated_at)
            VALUES
              ($novel_id, 'custom', 'embed-v1', 3, 'paragraph-v1', $status, 1, $vector_table, 'pending import state', $updated_at);
            INSERT INTO rag_chunks
              (chunk_id, novel_id, chapter_number, chunk_type, chunk_index, start_position, content, content_hash, file_path, title)
            VALUES
              ($chunk_id, $novel_id, 1, 'paragraph', 0, 0, '残留索引片段', $chunk_hash, 'chapters/001.md', '第一章');
            """,
            ("$novel_id", novelId),
            ("$status", status),
            ("$vector_table", $"vec_novel_{novelId}_3"),
            ("$updated_at", now),
            ("$chunk_id", $"chunk-{novelId}"),
            ("$chunk_hash", $"hash-{novelId}"));
    }

    private static async ValueTask InsertReferenceStateAsync(
        SqliteConnection connection,
        long anchorId,
        long novelId,
        string status,
        string buildId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await ExecuteAsync(
            connection,
            """
            INSERT INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status, source_file_hash,
               build_version, status, created_at, updated_at, corpus_visibility, source_trust, user_tags_json)
            VALUES
              ($anchor_id, $novel_id, '导入残留参考', '', 'source.md', 'markdown', 'user_provided', $source_hash,
               'reference-anchor-v1', $status, $updated_at, $updated_at, 'private', 'user_verified', '[]');
            INSERT INTO reference_anchor_build_state
              (anchor_id, status, stage, source_segment_count, material_count, slot_count, vector_count, last_error, updated_at)
            VALUES
              ($anchor_id, $status, $status, 0, 0, 0, 0, 'pending import state', $updated_at);
            INSERT INTO reference_style_profile_builds
              (build_id, novel_id, profile_id, title, status, stage, progress_completed, progress_total,
               anchor_ids_json, source_hashes_json, diagnostics_json, error_code, error_message, created_at, updated_at,
               completed_at, cancelled_at)
            VALUES
              ($build_id, $novel_id, NULL, '导入残留风格构建', 'running', 'queued', 0, 7,
               '[]', '[]', '[]', NULL, NULL, $updated_at, $updated_at, NULL, NULL);
            """,
            ("$anchor_id", anchorId),
            ("$novel_id", novelId),
            ("$status", status),
            ("$updated_at", now),
            ("$source_hash", $"source-hash-{novelId}"),
            ("$build_id", buildId));
    }

    private static async ValueTask<int> CountRagIndexRowsAsync(AppInitializationOptions options, long novelId)
    {
        return await CountRowsAsync(
            Path.Combine(options.DefaultDataDirectory, "rag", "index.sqlite"),
            "SELECT COUNT(*) FROM rag_index_state WHERE novel_id = $novel_id;",
            novelId);
    }

    private static async ValueTask<int> CountRagChunkRowsAsync(AppInitializationOptions options, long novelId)
    {
        return await CountRowsAsync(
            Path.Combine(options.DefaultDataDirectory, "rag", "index.sqlite"),
            "SELECT COUNT(*) FROM rag_chunks WHERE novel_id = $novel_id;",
            novelId);
    }

    private static async ValueTask<bool> RagVectorTableExistsAsync(AppInitializationOptions options, long novelId)
    {
        await using var connection = await OpenSqliteAsync(Path.Combine(options.DefaultDataDirectory, "rag", "index.sqlite"));
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $table_name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$table_name", $"vec_novel_{novelId}_3");
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async ValueTask<int> CountReferenceAnchorsAsync(AppInitializationOptions options, long novelId)
    {
        return await CountRowsAsync(
            Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite"),
            "SELECT COUNT(*) FROM reference_anchors WHERE novel_id = $novel_id;",
            novelId);
    }

    private static async ValueTask<int> CountReferenceStyleBuildsAsync(AppInitializationOptions options, long novelId)
    {
        return await CountRowsAsync(
            Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite"),
            "SELECT COUNT(*) FROM reference_style_profile_builds WHERE novel_id = $novel_id;",
            novelId);
    }

    private static async ValueTask<int> CountRowsAsync(string databasePath, string sql, long novelId)
    {
        await using var connection = await OpenSqliteAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$novel_id", novelId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask<SqliteConnection> OpenSqliteAsync(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }
}
