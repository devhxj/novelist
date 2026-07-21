using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Novelist.Infrastructure.App;

internal static class ReferenceCorpusSchemaProvisioner
{
    private const int CurrentSchemaVersion = 2;
    private const string SchemaKey = "reference-materialization";
    private static readonly SemaphoreSlim ProvisioningGate = new(1, 1);

    private static readonly string[] RetiredTables =
    [
        "reference_text_nodes",
        "reference_source_segments",
        "reference_material_candidates",
        "reference_material_candidate_nodes",
        "reference_materialization_candidate_embeddings",
        "reference_materialization_material_nodes",
        "reference_materialization_blueprint_preview_sessions",
        "reference_materialization_blueprint_preview_sources",
        "reference_materialization_blueprint_preview_candidates",
        "reference_materialization_blueprint_preview_beats",
        "reference_materialization_blueprint_preview_material_links",
        "reference_session_library_scope_state",
        "reference_analysis_runs",
        "reference_feature_observations",
        "reference_text_node_embeddings",
        "reference_current_chapter_embedding_cache",
        "reference_obs_sensory",
        "reference_technique_specimens",
        "reference_technique_vectors",
        "reference_technique_vector_rows",
        "reference_technique_vector_index_state",
        "reference_specimen_evidence",
        "reference_template_examples",
        "reference_blueprint_beat_pieces",
        "reference_corpus_blueprints",
        "reference_corpus_blueprint_beats",
        "reference_user_feedback",
        "reference_aggregate_provenance",
        "reference_analysis_input_snapshots",
        "reference_analysis_work_items",
        "reference_analysis_jobs",
        "reference_analysis_job_attempts",
        "reference_analysis_work_item_completions"
    ];

    // All entries are fixed identifiers. Their order removes FK dependents before parents.
    private static readonly string[] DerivedTablesToReset =
    [
        "reference_materialization_blueprint_preview_material_links",
        "reference_materialization_blueprint_preview_beats",
        "reference_materialization_blueprint_preview_candidates",
        "reference_materialization_blueprint_preview_sources",
        "reference_materialization_blueprint_preview_sessions",
        "reference_materialization_material_nodes",
        "reference_materialization_candidate_embeddings",
        "reference_material_candidate_nodes",
        "reference_material_candidates",
        "reference_materialization_material_embeddings",
        "reference_materialization_materials",
        "reference_material_embeddings",
        "reference_materials",
        "reference_materialization_vector_indexes",
        "reference_materialization_chapter_progress",
        "reference_materialization_run_leases",
        "reference_materialization_runs",
        "reference_anchor_materialization_state",
        "reference_chapter_split_boundaries",
        "reference_chapter_split_profiles",
        "reference_session_library_scope_state",
        "reference_writing_sessions",
        "reference_analysis_work_item_completions",
        "reference_analysis_job_attempts",
        "reference_analysis_jobs",
        "reference_analysis_work_items",
        "reference_analysis_input_snapshots",
        "reference_analysis_runs",
        "reference_aggregate_provenance",
        "reference_user_feedback",
        "reference_corpus_blueprint_beats",
        "reference_corpus_blueprints",
        "reference_blueprint_beat_pieces",
        "reference_template_examples",
        "reference_specimen_evidence",
        "reference_technique_vector_rows",
        "reference_technique_vectors",
        "reference_technique_vector_index_state",
        "reference_technique_specimens",
        "reference_obs_sensory",
        "reference_current_chapter_embedding_cache",
        "reference_text_node_embeddings",
        "reference_feature_observations",
        "reference_source_segments",
        "reference_text_nodes",
        "reference_schema_metadata"
    ];

    public static async ValueTask EnsureCoreTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (await IsCurrentSchemaAsync(connection, cancellationToken))
        {
            return;
        }

        await ProvisioningGate.WaitAsync(cancellationToken);
        try
        {
            if (await IsCurrentSchemaAsync(connection, cancellationToken))
            {
                return;
            }

            if (await HasRetiredTablesAsync(connection, cancellationToken))
            {
                await UpgradeLegacySchemaAsync(connection, cancellationToken);
                return;
            }

            await EnableWriteAheadLoggingAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await EnsureCurrentSchemaAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            ProvisioningGate.Release();
        }
    }

    private static async ValueTask UpgradeLegacySchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var sourcePath = GetDatabasePath(connection);
        var migration = CreateMigrationPaths(sourcePath);
        await CreateBackupAsync(connection, migration.BackupPath, cancellationToken);
        await WriteManifestAsync(migration.ManifestPath, new SchemaMigrationManifest(
            "started",
            sourcePath,
            migration.BackupPath,
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            null), cancellationToken);

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await DropDerivedTablesAsync(connection, transaction, cancellationToken);
            await EnsureCurrentSchemaAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await EnableWriteAheadLoggingAsync(connection, cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteManifestAsync(migration.ManifestPath, new SchemaMigrationManifest(
                "failed",
                sourcePath,
                migration.BackupPath,
                CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                exception.Message), CancellationToken.None);
            throw;
        }

        await WriteManifestAsync(migration.ManifestPath, new SchemaMigrationManifest(
            "completed",
            sourcePath,
            migration.BackupPath,
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            null), cancellationToken);
    }

    private static async ValueTask EnsureCurrentSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS reference_anchors (
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

            CREATE TABLE IF NOT EXISTS reference_chapter_split_profiles (
              split_profile_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              source_hash TEXT NOT NULL,
              split_mode TEXT NOT NULL,
              sample_char_count INTEGER NOT NULL,
              sample_hash TEXT NOT NULL,
              pattern_kind TEXT NOT NULL,
              delimiter_template TEXT NOT NULL,
              pattern_json TEXT NOT NULL,
              model_provider TEXT,
              model_id TEXT,
              confidence REAL,
              status TEXT NOT NULL,
              chapter_count INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              confirmed_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_chapter_split_boundaries (
              split_profile_id TEXT NOT NULL,
              chapter_index INTEGER NOT NULL,
              title TEXT NOT NULL,
              heading_start INTEGER NOT NULL,
              content_start INTEGER NOT NULL,
              content_end INTEGER NOT NULL,
              text_hash TEXT NOT NULL,
              PRIMARY KEY(split_profile_id, chapter_index),
              FOREIGN KEY(split_profile_id) REFERENCES reference_chapter_split_profiles(split_profile_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_chapter_split_profiles_anchor
              ON reference_chapter_split_profiles(anchor_id, status, created_at DESC);

            CREATE TABLE IF NOT EXISTS reference_materialization_runs (
              run_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              split_profile_id TEXT NOT NULL,
              generation_id TEXT NOT NULL,
              policy_version TEXT NOT NULL,
              extractor_schema_version TEXT NOT NULL,
              model_provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              embedding_provider TEXT NOT NULL,
              embedding_model_id TEXT NOT NULL,
              embedding_dimensions INTEGER NOT NULL CHECK(embedding_dimensions > 0),
              status TEXT NOT NULL,
              chapter_batch_size INTEGER NOT NULL CHECK(chapter_batch_size IN (5, 10)),
              total_chapters INTEGER NOT NULL DEFAULT 0 CHECK(total_chapters >= 0),
              processed_chapters INTEGER NOT NULL DEFAULT 0 CHECK(processed_chapters >= 0),
              total_chapter_batches INTEGER NOT NULL DEFAULT 0 CHECK(total_chapter_batches >= 0),
              completed_chapter_batches INTEGER NOT NULL DEFAULT 0 CHECK(completed_chapter_batches >= 0),
              current_batch_index INTEGER,
              current_batch_start_chapter INTEGER,
              current_batch_end_chapter INTEGER,
              material_count INTEGER NOT NULL DEFAULT 0 CHECK(material_count >= 0),
              vector_count INTEGER NOT NULL DEFAULT 0 CHECK(vector_count >= 0),
              last_error_code TEXT,
              last_error_message TEXT,
              started_at TEXT NOT NULL,
              completed_at TEXT,
              activated_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(split_profile_id) REFERENCES reference_chapter_split_profiles(split_profile_id) ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_materialization_runs_generation
              ON reference_materialization_runs(generation_id);

            CREATE INDEX IF NOT EXISTS idx_reference_materialization_runs_anchor_status
              ON reference_materialization_runs(anchor_id, status, started_at DESC);

            CREATE TABLE IF NOT EXISTS reference_anchor_materialization_state (
              anchor_id INTEGER PRIMARY KEY,
              active_generation_id TEXT,
              row_version INTEGER NOT NULL DEFAULT 0 CHECK(row_version >= 0),
              updated_at TEXT NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_materialization_chapter_progress (
              run_id TEXT NOT NULL,
              chapter_index INTEGER NOT NULL CHECK(chapter_index > 0),
              batch_index INTEGER NOT NULL CHECK(batch_index >= 0),
              status TEXT NOT NULL,
              current_stage TEXT NOT NULL,
              material_count INTEGER NOT NULL DEFAULT 0 CHECK(material_count >= 0),
              vector_count INTEGER NOT NULL DEFAULT 0 CHECK(vector_count >= 0),
              model_call_count INTEGER NOT NULL DEFAULT 0 CHECK(model_call_count >= 0),
              started_at TEXT,
              completed_at TEXT,
              last_error_code TEXT,
              last_error_message TEXT,
              row_version INTEGER NOT NULL DEFAULT 0 CHECK(row_version >= 0),
              PRIMARY KEY(run_id, chapter_index),
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_materialization_chapter_progress_run_batch
              ON reference_materialization_chapter_progress(run_id, batch_index, chapter_index);

            CREATE TABLE IF NOT EXISTS reference_materialization_run_leases (
              run_id TEXT PRIMARY KEY,
              worker_id TEXT NOT NULL,
              lease_token TEXT NOT NULL,
              lease_expires_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_materialization_vector_indexes (
              generation_id TEXT PRIMARY KEY,
              run_id TEXT NOT NULL,
              table_name TEXT NOT NULL,
              provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL CHECK(dimensions > 0),
              vector_count INTEGER NOT NULL CHECK(vector_count >= 0),
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_materialization_vector_indexes_run
              ON reference_materialization_vector_indexes(run_id);

            CREATE TABLE IF NOT EXISTS reference_materials (
              material_id TEXT PRIMARY KEY,
              generation_id TEXT NOT NULL,
              run_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              chapter_index INTEGER NOT NULL CHECK(chapter_index > 0),
              ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
              material_type TEXT NOT NULL,
              text TEXT NOT NULL,
              description TEXT NOT NULL,
              tags_json TEXT NOT NULL,
              text_hash TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_materials_generation_ordinal
              ON reference_materials(generation_id, chapter_index, ordinal);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_materials_generation_text
              ON reference_materials(generation_id, text_hash, text);

            CREATE INDEX IF NOT EXISTS idx_reference_materials_active_lookup
              ON reference_materials(anchor_id, generation_id, chapter_index, ordinal);

            CREATE TABLE IF NOT EXISTS reference_material_embeddings (
              material_id TEXT PRIMARY KEY,
              generation_id TEXT NOT NULL,
              provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL CHECK(dimensions > 0),
              embedding_hash TEXT NOT NULL,
              embedding_blob BLOB NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(material_id) REFERENCES reference_materials(material_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_material_embeddings_generation
              ON reference_material_embeddings(generation_id, material_id);

            CREATE TABLE IF NOT EXISTS reference_corpus_libraries (
              library_id TEXT PRIMARY KEY,
              scope TEXT NOT NULL,
              novel_id INTEGER,
              name TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_library_members (
              library_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              enabled INTEGER NOT NULL DEFAULT 1,
              source_quality TEXT,
              disabled_reason TEXT,
              dedup_group_id TEXT,
              PRIMARY KEY(library_id, anchor_id),
              FOREIGN KEY(library_id) REFERENCES reference_corpus_libraries(library_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_library_members_anchor
              ON reference_library_members(anchor_id, enabled);

            CREATE TABLE IF NOT EXISTS reference_session_library_binding (
              session_id TEXT NOT NULL,
              library_id TEXT NOT NULL,
              PRIMARY KEY(session_id, library_id),
              FOREIGN KEY(library_id) REFERENCES reference_corpus_libraries(library_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_source_license (
              anchor_id INTEGER PRIMARY KEY,
              license_state TEXT NOT NULL,
              authorization_evidence TEXT,
              reuse_policy TEXT NOT NULL,
              max_verbatim_ratio REAL,
              cleared_for_insertion INTEGER NOT NULL DEFAULT 0,
              reviewed_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_schema_metadata (
              schema_key TEXT PRIMARY KEY,
              schema_version INTEGER NOT NULL,
              updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureAnchorMetadataColumnsAsync(connection, transaction, cancellationToken);
        await WriteSchemaVersionAsync(connection, transaction, cancellationToken);
    }

    private static async ValueTask EnsureAnchorMetadataColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "PRAGMA table_info(reference_anchors);";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        await EnsureColumnAsync(connection, transaction, columns, "corpus_visibility", "TEXT NOT NULL DEFAULT 'private'", cancellationToken);
        await EnsureColumnAsync(connection, transaction, columns, "source_trust", "TEXT NOT NULL DEFAULT 'user_verified'", cancellationToken);
        await EnsureColumnAsync(connection, transaction, columns, "user_tags_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
    }

    private static async ValueTask EnsureColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ISet<string> columns,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (!columns.Add(columnName))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE reference_anchors ADD COLUMN {columnName} {definition};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask WriteSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_schema_metadata (schema_key, schema_version, updated_at)
            VALUES ($schema_key, $schema_version, $updated_at)
            ON CONFLICT(schema_key) DO UPDATE SET
              schema_version = excluded.schema_version,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$schema_key", SchemaKey);
        command.Parameters.AddWithValue("$schema_version", CurrentSchemaVersion);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<bool> HasRetiredTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var placeholders = RetiredTables.Select((_, index) => "$name" + index).ToArray();
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name IN ({string.Join(", ", placeholders)}));";
        for (var index = 0; index < RetiredTables.Length; index++)
        {
            command.Parameters.AddWithValue(placeholders[index], RetiredTables[index]);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async ValueTask<bool> IsCurrentSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version
            FROM reference_schema_metadata
            WHERE schema_key = $schema_key;
            """;
        command.Parameters.AddWithValue("$schema_key", SchemaKey);

        try
        {
            var version = await command.ExecuteScalarAsync(cancellationToken);
            return version is long schemaVersion &&
                   schemaVersion == CurrentSchemaVersion &&
                   !await HasRetiredTablesAsync(connection, cancellationToken) &&
                   await HasWriteAheadLoggingAsync(connection, cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            return false;
        }
    }

    private static async ValueTask<bool> HasWriteAheadLoggingAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return string.Equals(
            (string?)await command.ExecuteScalarAsync(cancellationToken),
            "wal",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask EnableWriteAheadLoggingAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Reference materialization requires SQLite WAL mode.");
        }
    }

    private static async ValueTask DropDerivedTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var tableName in DerivedTablesToReset)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP TABLE IF EXISTS {tableName};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask CreateBackupAsync(
        SqliteConnection source,
        string backupPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await backup.OpenAsync(cancellationToken);
        source.BackupDatabase(backup);
    }

    private static string GetDatabasePath(SqliteConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.DataSource) ||
            string.Equals(connection.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Legacy reference schema migration requires a file-backed SQLite database.");
        }

        return Path.GetFullPath(connection.DataSource);
    }

    private static SchemaMigrationPaths CreateMigrationPaths(string databasePath)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var suffix = $"{stamp}-{Guid.NewGuid():N}";
        var directory = Path.GetDirectoryName(databasePath)!;
        var fileName = Path.GetFileName(databasePath);
        return new SchemaMigrationPaths(
            Path.Combine(directory, $"{fileName}.reference-schema-v{CurrentSchemaVersion}-{suffix}.bak"),
            Path.Combine(directory, $"reference-schema-migration-v{CurrentSchemaVersion}-{suffix}.json"));
    }

    private static async ValueTask WriteManifestAsync(
        string manifestPath,
        SchemaMigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporaryPath = manifestPath + ".tmp";
        var payload = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, payload, cancellationToken);
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    private sealed record SchemaMigrationPaths(string BackupPath, string ManifestPath);

    private sealed record SchemaMigrationManifest(
        string Status,
        string SourceDatabase,
        string BackupDatabase,
        int TargetSchemaVersion,
        DateTimeOffset RecordedAt,
        string? Error);
}
