using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemNovelImportRecoveryService : INovelImportRecoveryService
{
    private const int MaxDiagnosticDetailLength = 4_000;
    private const string RagDatabaseRelativePath = "rag/index.sqlite";
    private const string ReferenceDatabaseRelativePath = "reference-anchor/index.sqlite";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> NonTerminalKnownStates = new(StringComparer.Ordinal)
    {
        NovelImportRunStates.Created,
        NovelImportRunStates.Parsing,
        NovelImportRunStates.CreatingNovel,
        NovelImportRunStates.WritingFiles,
        NovelImportRunStates.SavingMetadata,
        NovelImportRunStates.Indexing,
        NovelImportRunStates.GitCommit,
        NovelImportRunStates.CleanupPending
    };

    private static readonly string[] PendingReferenceAnchorStatuses =
    [
        ReferenceAnchorBuildStates.Created,
        ReferenceAnchorBuildStates.Importing,
        ReferenceAnchorBuildStates.SourceImported,
        ReferenceAnchorBuildStates.Segmenting,
        ReferenceAnchorBuildStates.SegmentsBuilt,
        ReferenceAnchorBuildStates.ExtractingMaterials,
        ReferenceAnchorBuildStates.MaterialsExtracted,
        ReferenceAnchorBuildStates.DetectingSlots,
        ReferenceAnchorBuildStates.SlotsDetected,
        ReferenceAnchorBuildStates.Embedding,
        ReferenceAnchorBuildStates.Stale
    ];

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemNovelImportRecoveryService(
        AppInitializationOptions? options = null,
        INovelService? novelService = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novelService ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<NovelImportReconciliationResultPayload> ReconcileAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var storePath = await StorePathAsync(cancellationToken);
            if (!File.Exists(storePath))
            {
                return new NovelImportReconciliationResultPayload(
                    [],
                    [],
                    [],
                    DateTimeOffset.UtcNow);
            }

            var store = await LoadStoreAsync(storePath, cancellationToken);
            var dataDirectory = Path.GetFullPath(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken));
            var novels = await _novels.GetNovelsAsync(cancellationToken);
            var reconciled = new List<NovelImportRunPayload>();
            var blocked = new List<NovelImportRunPayload>();
            var diagnostics = new List<NovelImportDiagnosticPayload>();
            var changed = false;

            foreach (var run in store.Runs.OrderBy(run => run.StartedAt).ToArray())
            {
                if (run.State == NovelImportRunStates.CleanupBlocked)
                {
                    blocked.Add(ToPayload(run));
                    continue;
                }

                if (IsTerminalState(run.State))
                {
                    continue;
                }

                var classification = ClassifyRun(run, dataDirectory, novels);
                switch (classification.Kind)
                {
                    case RecoveryClassificationKind.CompletedWithWarning:
                        MarkCompletedWithWarning(run, classification.WarningDetail);
                        reconciled.Add(ToPayload(run));
                        changed = true;
                        break;
                    case RecoveryClassificationKind.Cleanup:
                        var cleanupResult = await CleanupRunAsync(run, classification.SafeRoots, cancellationToken);
                        if (cleanupResult.BlockedDetail is null)
                        {
                            MarkCleanupCompleted(run);
                            reconciled.Add(ToPayload(run));
                        }
                        else
                        {
                            MarkCleanupBlocked(run, "import.cleanup_blocked", "导入恢复清理被阻止。", cleanupResult.BlockedDetail);
                            blocked.Add(ToPayload(run));
                            diagnostics.Add(Diagnostic("import.cleanup_blocked", "导入恢复清理被阻止。", cleanupResult.BlockedDetail));
                        }

                        changed = true;
                        break;
                    case RecoveryClassificationKind.Blocked:
                        MarkCleanupBlocked(run, classification.BlockedCode, classification.BlockedMessage, classification.BlockedDetail);
                        blocked.Add(ToPayload(run));
                        diagnostics.Add(Diagnostic(classification.BlockedCode, classification.BlockedMessage, classification.BlockedDetail));
                        changed = true;
                        break;
                }
            }

            if (changed)
            {
                await SaveStoreAsync(storePath, store, cancellationToken);
            }

            return new NovelImportReconciliationResultPayload(
                reconciled,
                blocked,
                diagnostics,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static RecoveryClassification ClassifyRun(
        NovelImportRecoveryStoreItem run,
        string dataDirectory,
        IReadOnlyList<NovelPayload> novels)
    {
        if (!NonTerminalKnownStates.Contains(run.State))
        {
            return RecoveryClassification.ManualReview(
                $"Import run '{run.TaskId}' has unknown non-terminal state '{run.State}'.");
        }

        var rootResult = ResolveCreatedRoots(run, dataDirectory);
        if (rootResult.BlockedDetail is not null)
        {
            return RecoveryClassification.Blocked(rootResult.BlockedDetail);
        }

        if (run.CreatedNovelId is null &&
            run.CreatedFileRoots.Count > 0)
        {
            return RecoveryClassification.ManualReview(
                $"Import run '{run.TaskId}' recorded created file roots but no created novel id; recovery cannot prove which novel owns those paths.");
        }

        if (run.CreatedNovelId is null &&
            run.State is NovelImportRunStates.WritingFiles or NovelImportRunStates.SavingMetadata or NovelImportRunStates.CleanupPending)
        {
            return RecoveryClassification.ManualReview(
                $"Import run '{run.TaskId}' reached phase '{run.State}' but recovery cannot prove which novel or workspace it created.");
        }

        if (run.State is NovelImportRunStates.Indexing or NovelImportRunStates.GitCommit)
        {
            if (run.CreatedNovelId is null || !novels.Any(novel => novel.Id == run.CreatedNovelId.Value))
            {
                return RecoveryClassification.ManualReview(
                    $"Import run '{run.TaskId}' reached durable phase '{run.State}' but its created novel row is missing.");
            }

            return RecoveryClassification.CompletedWithWarning(
                $"Import run '{run.TaskId}' was interrupted during '{run.State}'. Imported files were preserved; rerun index/Git actions if needed.");
        }

        return RecoveryClassification.Cleanup(rootResult.SafeRoots);
    }

    private async ValueTask<CleanupRunResult> CleanupRunAsync(
        NovelImportRecoveryStoreItem run,
        IReadOnlyList<string> safeRoots,
        CancellationToken cancellationToken)
    {
        if (run.CreatedNovelId is not null)
        {
            var sideEffectCleanupError = await CleanupPendingSideEffectsAsync(run.CreatedNovelId.Value, cancellationToken);
            if (sideEffectCleanupError is not null)
            {
                return new CleanupRunResult(sideEffectCleanupError);
            }

            var novels = await _novels.GetNovelsAsync(cancellationToken);
            if (novels.Any(novel => novel.Id == run.CreatedNovelId.Value))
            {
                try
                {
                    await _novels.DeleteNovelAsync(run.CreatedNovelId.Value, cancellationToken);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return new CleanupRunResult(Truncate(ex.Message));
                }
            }
        }

        foreach (var root in safeRoots.OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(root))
                {
                    ClearReadOnlyAttributes(root);
                    Directory.Delete(root, recursive: true);
                }
                else if (File.Exists(root))
                {
                    File.SetAttributes(root, File.GetAttributes(root) & ~FileAttributes.ReadOnly);
                    File.Delete(root);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CleanupRunResult(Truncate(ex.Message));
            }
        }

        return new CleanupRunResult(null);
    }

    private async ValueTask<string?> CleanupPendingSideEffectsAsync(long novelId, CancellationToken cancellationToken)
    {
        try
        {
            var dataDirectory = Path.GetFullPath(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken));
            await CleanupRagSideEffectsAsync(dataDirectory, novelId, cancellationToken);
            await CleanupReferenceSideEffectsAsync(dataDirectory, novelId, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            return Truncate(ex.Message);
        }
    }

    private static async ValueTask CleanupRagSideEffectsAsync(
        string dataDirectory,
        long novelId,
        CancellationToken cancellationToken)
    {
        var databasePath = BuildSafeDataFilePath(dataDirectory, RagDatabaseRelativePath);
        if (!File.Exists(databasePath))
        {
            return;
        }

        await using var connection = await OpenSqliteAsync(databasePath, cancellationToken);
        if (await TableExistsAsync(connection, "rag_index_state", cancellationToken))
        {
            var vectorTables = await ReadNovelVectorTablesAsync(connection, novelId, cancellationToken);
            foreach (var vectorTable in vectorTables)
            {
                if (IsExpectedNovelVectorTable(vectorTable, novelId))
                {
                    await ExecuteSqliteAsync(
                        connection,
                        $"DROP TABLE IF EXISTS {QuoteSqliteIdentifier(vectorTable)};",
                        cancellationToken);
                }
            }
        }

        if (await TableExistsAsync(connection, "rag_chunks", cancellationToken))
        {
            await ExecuteSqliteAsync(
                connection,
                "DELETE FROM rag_chunks WHERE novel_id = $novel_id;",
                cancellationToken,
                ("$novel_id", novelId));
        }

        if (await TableExistsAsync(connection, "rag_index_state", cancellationToken))
        {
            await ExecuteSqliteAsync(
                connection,
                "DELETE FROM rag_index_state WHERE novel_id = $novel_id;",
                cancellationToken,
                ("$novel_id", novelId));
        }
    }

    private static async ValueTask CleanupReferenceSideEffectsAsync(
        string dataDirectory,
        long novelId,
        CancellationToken cancellationToken)
    {
        var databasePath = BuildSafeDataFilePath(dataDirectory, ReferenceDatabaseRelativePath);
        if (!File.Exists(databasePath))
        {
            return;
        }

        await using var connection = await OpenSqliteAsync(databasePath, cancellationToken);
        var hasReferenceAnchors = await TableExistsAsync(connection, "reference_anchors", cancellationToken);
        if (hasReferenceAnchors)
        {
            var anchorIds = await ReadPendingReferenceAnchorIdsAsync(connection, novelId, cancellationToken);
            if (anchorIds.Count > 0)
            {
                await DeletePendingReferenceAnchorsAsync(connection, anchorIds, cancellationToken);
            }
        }

        if (await TableExistsAsync(connection, "reference_style_profile_builds", cancellationToken))
        {
            await ExecuteSqliteAsync(
                connection,
                """
                DELETE FROM reference_style_profile_builds
                WHERE novel_id = $novel_id
                  AND status = $status;
                """,
                cancellationToken,
                ("$novel_id", novelId),
                ("$status", ReferenceStyleProfileBuildStatuses.Running));
        }
    }

    private static string BuildSafeDataFilePath(string dataDirectory, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            dataDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInsideOrEqual(path, dataDirectory))
        {
            throw new InvalidOperationException("Recovery side-effect database path resolves outside the app data directory.");
        }

        return path;
    }

    private static async ValueTask<SqliteConnection> OpenSqliteAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteSqliteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        return connection;
    }

    private static async ValueTask<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $table_name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async ValueTask<IReadOnlyList<string>> ReadNovelVectorTablesAsync(
        SqliteConnection connection,
        long novelId,
        CancellationToken cancellationToken)
    {
        var vectorTables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vector_table
            FROM rag_index_state
            WHERE novel_id = $novel_id AND vector_table <> '';
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            vectorTables.Add(reader.GetString(0));
        }

        return vectorTables;
    }

    private static bool IsExpectedNovelVectorTable(string tableName, long novelId)
    {
        var prefix = $"vec_novel_{novelId.ToString(CultureInfo.InvariantCulture)}_";
        if (!tableName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = tableName[prefix.Length..];
        return suffix.Length > 0 &&
            suffix.All(char.IsAsciiDigit) &&
            int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var dimensions) &&
            dimensions > 0;
    }

    private static async ValueTask<IReadOnlyList<long>> ReadPendingReferenceAnchorIdsAsync(
        SqliteConnection connection,
        long novelId,
        CancellationToken cancellationToken)
    {
        var statuses = AddParameters(
            connection.CreateCommand(),
            "status",
            PendingReferenceAnchorStatuses,
            out var statusPlaceholders);

        await using var command = statuses;
        command.CommandText = $"""
            SELECT anchor_id
            FROM reference_anchors
            WHERE novel_id = $novel_id
              AND status IN ({statusPlaceholders});
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);

        var anchorIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            anchorIds.Add(reader.GetInt64(0));
        }

        return anchorIds;
    }

    private static async ValueTask DeletePendingReferenceAnchorsAsync(
        SqliteConnection connection,
        IReadOnlyList<long> anchorIds,
        CancellationToken cancellationToken)
    {
        var anchorCommand = AddParameters(connection.CreateCommand(), "anchor", anchorIds, out var placeholders);
        await using (anchorCommand)
        {
            if (await TableExistsAsync(connection, "reference_anchor_build_state", cancellationToken))
            {
                anchorCommand.CommandText = $"DELETE FROM reference_anchor_build_state WHERE anchor_id IN ({placeholders});";
                await anchorCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var deleteAnchors = AddParameters(connection.CreateCommand(), "anchor", anchorIds, out placeholders);
        await using (deleteAnchors)
        {
            deleteAnchors.CommandText = $"DELETE FROM reference_anchors WHERE anchor_id IN ({placeholders});";
            await deleteAnchors.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static SqliteCommand AddParameters<T>(
        SqliteCommand command,
        string prefix,
        IReadOnlyList<T> values,
        out string placeholders)
    {
        var names = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var name = $"${prefix}_{index.ToString(CultureInfo.InvariantCulture)}";
            names.Add(name);
            command.Parameters.AddWithValue(name, values[index] ?? throw new ArgumentException("SQLite parameter value cannot be null."));
        }

        placeholders = string.Join(", ", names);
        return command;
    }

    private static async ValueTask ExecuteSqliteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteSqliteIdentifier(string identifier)
    {
        return '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static CreatedRootResolution ResolveCreatedRoots(NovelImportRecoveryStoreItem run, string dataDirectory)
    {
        var safeRoots = new List<string>();
        var novelId = run.CreatedNovelId;
        var expectedWorkspaceRelative = novelId is null
            ? null
            : $"novels/{novelId.Value.ToString(CultureInfo.InvariantCulture)}";
        var expectedWorkspace = expectedWorkspaceRelative is null
            ? null
            : Path.GetFullPath(Path.Combine(dataDirectory, "novels", novelId!.Value.ToString(CultureInfo.InvariantCulture)));

        foreach (var rawRoot in run.CreatedFileRoots ?? [])
        {
            var relativeRoot = NormalizeRelativeRoot(rawRoot);
            if (relativeRoot is null)
            {
                return CreatedRootResolution.Blocked(
                    $"Import run '{run.TaskId}' contains an unsafe created file root.");
            }

            if (expectedWorkspaceRelative is not null &&
                !string.Equals(relativeRoot, expectedWorkspaceRelative, StringComparison.Ordinal) &&
                !relativeRoot.StartsWith($"{expectedWorkspaceRelative}/", StringComparison.Ordinal))
            {
                return CreatedRootResolution.Blocked(
                    $"Import run '{run.TaskId}' created file root is outside its recorded novel workspace.");
            }

            var absolute = Path.GetFullPath(Path.Combine(dataDirectory, relativeRoot.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInsideOrEqual(absolute, dataDirectory) ||
                expectedWorkspace is not null && !IsInsideOrEqual(absolute, expectedWorkspace))
            {
                return CreatedRootResolution.Blocked(
                    $"Import run '{run.TaskId}' created file root resolves outside the allowed cleanup boundary.");
            }

            if (!safeRoots.Contains(absolute, StringComparer.OrdinalIgnoreCase))
            {
                safeRoots.Add(absolute);
            }
        }

        if (expectedWorkspace is not null &&
            !safeRoots.Contains(expectedWorkspace, StringComparer.OrdinalIgnoreCase))
        {
            safeRoots.Add(expectedWorkspace);
        }

        return CreatedRootResolution.Safe(safeRoots);
    }

    private static string? NormalizeRelativeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var raw = root.Trim();
        if (raw.Contains("://", StringComparison.Ordinal) ||
            raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(raw) ||
            LooksLikeWindowsRootedPath(raw))
        {
            return null;
        }

        var normalized = raw.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return null;
        }

        return string.Join("/", segments);
    }

    private static bool LooksLikeWindowsRootedPath(string value)
    {
        return value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';
    }

    private static bool IsInsideOrEqual(string candidate, string root)
    {
        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(normalizedCandidate, normalizedRoot, comparison) ||
            normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void MarkCleanupCompleted(NovelImportRecoveryStoreItem run)
    {
        var now = DateTimeOffset.UtcNow;
        run.State = NovelImportRunStates.CleanupCompleted;
        run.Stage = "cleanup_completed";
        run.CleanupState = "completed";
        run.Error = Error(
            "import.recovered_cleanup",
            "启动恢复已清理未完成的导入。",
            "The previous import stopped before durable completion; created rows/files were removed.",
            run.TaskId,
            now);
        run.CreatedFileRoots = NormalizeStoredRoots(run.CreatedFileRoots);
        run.UpdatedAt = now;
        run.CompletedAt ??= now;
    }

    private static void MarkCleanupBlocked(
        NovelImportRecoveryStoreItem run,
        string code,
        string message,
        string detail)
    {
        var now = DateTimeOffset.UtcNow;
        run.State = NovelImportRunStates.CleanupBlocked;
        run.Stage = "cleanup_blocked";
        run.CleanupState = "blocked";
        run.Error = Error(code, message, detail, run.TaskId, now);
        run.CreatedFileRoots = NormalizeStoredRoots(run.CreatedFileRoots);
        run.UpdatedAt = now;
        run.CompletedAt ??= now;
    }

    private static void MarkCompletedWithWarning(NovelImportRecoveryStoreItem run, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        run.State = NovelImportRunStates.CompletedWithWarning;
        run.Stage = "recovered_with_warning";
        run.WarningState = "present";
        run.Warnings =
        [
            new NovelImportWarningPayload(
                "import.recovered_after_durable_phase",
                "导入已保留，但启动恢复发现后续步骤未确认完成。",
                detail)
        ];
        run.CreatedFileRoots = NormalizeStoredRoots(run.CreatedFileRoots);
        run.UpdatedAt = now;
        run.CompletedAt ??= now;
    }

    private static List<string> NormalizeStoredRoots(List<string>? roots)
    {
        var normalized = new List<string>();
        foreach (var root in roots ?? [])
        {
            var value = NormalizeRelativeRoot(root);
            if (value is not null && !normalized.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private static CopyableDiagnosticPayload Error(
        string code,
        string message,
        string detail,
        string taskId,
        DateTimeOffset timestamp)
    {
        return new CopyableDiagnosticPayload(
            code,
            message,
            Truncate(detail),
            "ReconcileNovelImportRuns",
            taskId,
            null,
            "ReconcileNovelImportRuns",
            timestamp);
    }

    private static NovelImportDiagnosticPayload Diagnostic(string code, string message, string detail)
    {
        return new NovelImportDiagnosticPayload(code, message, Truncate(detail), "warning");
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxDiagnosticDetailLength
            ? value
            : value[..MaxDiagnosticDetailLength];
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
    }

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "novel_imports",
            "runs.json");
    }

    private static async ValueTask<NovelImportRecoveryStoreDocument> LoadStoreAsync(
        string storePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(storePath);
        return await JsonSerializer.DeserializeAsync<NovelImportRecoveryStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? new NovelImportRecoveryStoreDocument();
    }

    private static async ValueTask SaveStoreAsync(
        string storePath,
        NovelImportRecoveryStoreDocument store,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var tempPath = $"{storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool IsTerminalState(string state)
    {
        return state is NovelImportRunStates.Completed
            or NovelImportRunStates.CompletedWithWarning
            or NovelImportRunStates.CleanupCompleted
            or NovelImportRunStates.CleanupBlocked
            or NovelImportRunStates.Failed
            or NovelImportRunStates.Cancelled;
    }

    private static NovelImportRunPayload ToPayload(NovelImportRecoveryStoreItem run)
    {
        return new NovelImportRunPayload(
            run.TaskId,
            run.State,
            run.Stage,
            run.SourceDisplayName,
            run.SourcePathHash,
            run.ParserType,
            run.CreatedNovelId,
            run.CreatedFileRoots.ToArray(),
            run.SkippedChapters.ToArray(),
            run.Diagnostics.ToArray(),
            run.Warnings.ToArray(),
            run.Error,
            run.StartedAt,
            run.UpdatedAt,
            run.CompletedAt);
    }

    private sealed class NovelImportRecoveryStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("runs")]
        public List<NovelImportRecoveryStoreItem> Runs { get; set; } = [];
    }

    private sealed class NovelImportRecoveryStoreItem
    {
        [JsonPropertyName("task_id")]
        public string TaskId { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = NovelImportRunStates.Created;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = NovelImportRunStates.Created;

        [JsonPropertyName("source_display_name")]
        public string SourceDisplayName { get; set; } = "unknown";

        [JsonPropertyName("source_path_hash")]
        public string SourcePathHash { get; set; } = "sha256:unknown";

        [JsonPropertyName("parser_type")]
        public string ParserType { get; set; } = NovelImportKinds.Txt;

        [JsonPropertyName("requested_title")]
        public string RequestedTitle { get; set; } = string.Empty;

        [JsonPropertyName("commit_message")]
        public string CommitMessage { get; set; } = string.Empty;

        [JsonPropertyName("created_novel_id")]
        public long? CreatedNovelId { get; set; }

        [JsonPropertyName("created_file_roots")]
        public List<string> CreatedFileRoots { get; set; } = [];

        [JsonPropertyName("skipped_chapters")]
        public List<NovelImportSkippedChapterPayload> SkippedChapters { get; set; } = [];

        [JsonPropertyName("diagnostics")]
        public List<NovelImportDiagnosticPayload> Diagnostics { get; set; } = [];

        [JsonPropertyName("warnings")]
        public List<NovelImportWarningPayload> Warnings { get; set; } = [];

        [JsonPropertyName("error")]
        public CopyableDiagnosticPayload? Error { get; set; }

        [JsonPropertyName("cleanup_state")]
        public string CleanupState { get; set; } = "not_started";

        [JsonPropertyName("warning_state")]
        public string WarningState { get; set; } = "none";

        [JsonPropertyName("started_at")]
        public DateTimeOffset StartedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public DateTimeOffset? CompletedAt { get; set; }
    }

    private sealed record CreatedRootResolution(
        IReadOnlyList<string> SafeRoots,
        string? BlockedDetail)
    {
        public static CreatedRootResolution Safe(IReadOnlyList<string> safeRoots)
        {
            return new CreatedRootResolution(safeRoots, null);
        }

        public static CreatedRootResolution Blocked(string detail)
        {
            return new CreatedRootResolution([], detail);
        }
    }

    private sealed record CleanupRunResult(string? BlockedDetail);

    private sealed record RecoveryClassification(
        RecoveryClassificationKind Kind,
        IReadOnlyList<string> SafeRoots,
        string BlockedCode,
        string BlockedMessage,
        string BlockedDetail,
        string WarningDetail)
    {
        public static RecoveryClassification Cleanup(IReadOnlyList<string> safeRoots)
        {
            return new RecoveryClassification(RecoveryClassificationKind.Cleanup, safeRoots, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        public static RecoveryClassification Blocked(string detail)
        {
            return new RecoveryClassification(
                RecoveryClassificationKind.Blocked,
                [],
                "import.cleanup_blocked",
                "导入恢复清理被阻止。",
                detail,
                string.Empty);
        }

        public static RecoveryClassification ManualReview(string detail)
        {
            return new RecoveryClassification(
                RecoveryClassificationKind.Blocked,
                [],
                "import.cleanup_manual_review_required",
                "导入恢复需要手动检查。",
                detail,
                string.Empty);
        }

        public static RecoveryClassification CompletedWithWarning(string detail)
        {
            return new RecoveryClassification(
                RecoveryClassificationKind.CompletedWithWarning,
                [],
                string.Empty,
                string.Empty,
                string.Empty,
                detail);
        }
    }

    private enum RecoveryClassificationKind
    {
        Cleanup,
        CompletedWithWarning,
        Blocked
    }
}
