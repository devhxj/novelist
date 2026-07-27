using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ChapterSplitProvisioningCreatesOnlyTheChapterMaterializationTablesAndIndexes()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);

        var tables = await ReadNamesAsync(options, "table");
        Assert.Contains("reference_materialization_runs", tables);
        Assert.Contains("reference_materialization_chapter_progress", tables);
        Assert.Contains("reference_materials", tables);
        Assert.Contains("reference_material_embeddings", tables);
        Assert.Contains("reference_anchor_materialization_state", tables);
        Assert.DoesNotContain("reference_text_nodes", tables);
        Assert.DoesNotContain("reference_material_candidates", tables);
        Assert.DoesNotContain("reference_materialization_blueprint_preview_sessions", tables);
        Assert.DoesNotContain("reference_session_library_scope_state", tables);
        Assert.Equal("wal", await ReadJournalModeAsync(options));

        var runIndexes = await ReadIndexesAsync(options, "reference_materialization_runs");
        Assert.Contains("ux_reference_materialization_runs_generation", runIndexes);
        Assert.Contains("idx_reference_materialization_runs_anchor_status", runIndexes);
        var runColumns = await ReadColumnsAsync(options, "reference_materialization_runs");
        Assert.Contains("extractor_schema_version", runColumns);
        Assert.Contains("current_chapter_index", runColumns);
        Assert.DoesNotContain("activated_at", runColumns);
        Assert.DoesNotContain("candidate_count", runColumns);
        Assert.DoesNotContain("chapter_batch_size", runColumns);
        Assert.DoesNotContain("total_chapter_batches", runColumns);
        Assert.DoesNotContain("completed_chapter_batches", runColumns);
        Assert.DoesNotContain("current_batch_index", runColumns);
        Assert.DoesNotContain("current_batch_start_chapter", runColumns);
        Assert.DoesNotContain("current_batch_end_chapter", runColumns);
        var progressColumns = await ReadColumnsAsync(options, "reference_materialization_chapter_progress");
        Assert.DoesNotContain("current_stage", progressColumns);
        Assert.DoesNotContain("row_version", progressColumns);
        Assert.DoesNotContain("chapter_node_id", progressColumns);
        Assert.DoesNotContain("batch_index", progressColumns);
        var leaseColumns = await ReadColumnsAsync(options, "reference_materialization_run_leases");
        Assert.DoesNotContain("worker_id", leaseColumns);
        Assert.DoesNotContain("updated_at", leaseColumns);
        var stateColumns = await ReadColumnsAsync(options, "reference_anchor_materialization_state");
        Assert.DoesNotContain("row_version", stateColumns);
        Assert.DoesNotContain("updated_at", stateColumns);
        var vectorIndexColumns = await ReadColumnsAsync(options, "reference_materialization_vector_indexes");
        Assert.DoesNotContain("created_at", vectorIndexColumns);
        Assert.DoesNotContain("updated_at", vectorIndexColumns);
        var materialIndexes = await ReadIndexesAsync(options, "reference_materials");
        Assert.Contains("ux_reference_materials_generation_ordinal", materialIndexes);
        Assert.Contains("ux_reference_materials_generation_text", materialIndexes);
        Assert.Contains("metadata_schema_version", await ReadColumnsAsync(options, "reference_materials"));
    }

    [Fact]
    public async Task MaterializationRunSchemaRejectsDuplicateGenerationKeys()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);

        await InsertRunAsync(options, anchor.AnchorId, profile.SplitProfileId, "run-1", "generation-1");

        var duplicateGeneration = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertRunAsync(options, anchor.AnchorId, profile.SplitProfileId, "run-duplicate", "generation-1").AsTask());
        Assert.Equal(19, duplicateGeneration.SqliteErrorCode);
    }

    [Fact]
    public async Task LegacySchemaUpgradeBacksUpTheDatabaseAndResetsOnlyDerivedReferenceData()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        await SeedLegacyDerivedDataAsync(options, anchor.AnchorId);

        await using (var connection = await OpenConnectionAsync(options))
        {
            await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        }

        var tables = await ReadNamesAsync(options, "table");
        Assert.Contains("reference_materials", tables);
        Assert.DoesNotContain("reference_text_nodes", tables);
        Assert.DoesNotContain("reference_session_library_scope_state", tables);
        Assert.Equal(0, await ReadCountAsync(options, "reference_materials"));
        Assert.Equal(1, await ReadCountAsync(options, "reference_anchors"));
        Assert.Equal("wal", await ReadJournalModeAsync(options));

        var referenceDirectory = Path.Combine(options.DefaultDataDirectory, "reference-anchor");
        var backupPath = Assert.Single(Directory.GetFiles(referenceDirectory, "index.sqlite.reference-schema-v6-*.bak"));
        var manifestPath = Assert.Single(Directory.GetFiles(referenceDirectory, "reference-schema-migration-v6-*.json"));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            Assert.Equal("completed", manifest.RootElement.GetProperty("Status").GetString());
            Assert.Equal(Path.GetFullPath(backupPath), manifest.RootElement.GetProperty("BackupDatabase").GetString());
        }

        await using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
        {
            await backup.OpenAsync(CancellationToken.None);
            Assert.True(await TableExistsAsync(backup, "reference_text_nodes"));
            Assert.Equal(1, await ReadCountAsync(backup, "reference_materials"));
        }

        await using (var connection = await OpenConnectionAsync(options))
        {
            await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        }

        Assert.Single(Directory.GetFiles(referenceDirectory, "index.sqlite.reference-schema-v6-*.bak"));
        Assert.Single(Directory.GetFiles(referenceDirectory, "reference-schema-migration-v6-*.json"));
    }

    [Fact]
    public async Task VersionTwoUpgradePreservesAnchorsAndConfirmedChapterBoundaries()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        await SeedVersionTwoMaterializationSchemaAsync(options);

        await using (var connection = await OpenConnectionAsync(options))
        {
            await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        }

        Assert.Equal(1, await ReadCountAsync(options, "reference_anchors"));
        Assert.Equal(1, await ReadCountAsync(options, "reference_chapter_split_profiles"));
        Assert.Equal(2, await ReadCountAsync(options, "reference_chapter_split_boundaries"));
        Assert.Equal(0, await ReadCountAsync(options, "reference_materialization_runs"));
        var runColumns = await ReadColumnsAsync(options, "reference_materialization_runs");
        Assert.Contains("current_chapter_index", runColumns);
        Assert.DoesNotContain("chapter_batch_size", runColumns);

        var referenceDirectory = Path.Combine(options.DefaultDataDirectory, "reference-anchor");
        Assert.Single(Directory.GetFiles(referenceDirectory, "index.sqlite.reference-schema-v6-*.bak"));
        Assert.Single(Directory.GetFiles(referenceDirectory, "reference-schema-migration-v6-*.json"));
    }

    [Fact]
    public async Task VersionThreeUpgradeRemovesRedundantChapterStageWithoutLosingConfirmedBoundaries()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        await SeedVersionThreeMaterializationSchemaAsync(options);

        await using (var connection = await OpenConnectionAsync(options))
        {
            await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        }

        Assert.Equal(1, await ReadCountAsync(options, "reference_anchors"));
        Assert.Equal(1, await ReadCountAsync(options, "reference_chapter_split_profiles"));
        Assert.Equal(2, await ReadCountAsync(options, "reference_chapter_split_boundaries"));
        var progressColumns = await ReadColumnsAsync(options, "reference_materialization_chapter_progress");
        Assert.DoesNotContain("current_stage", progressColumns);
    }

    [Fact]
    public async Task VersionFourUpgradeResetsDerivedMaterializationData()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await InsertRunAsync(options, anchor.AnchorId, profile.SplitProfileId, "run-v4", "generation-v4");
        await SeedVersionFourWithoutRequestedChapterAsync(options, anchor.AnchorId);

        Assert.DoesNotContain(
            "requested_chapter_index",
            await ReadColumnsAsync(options, "reference_materialization_runs"));

        await using (var connection = await OpenConnectionAsync(options))
        {
            await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        }

        Assert.Contains(
            "requested_chapter_index",
            await ReadColumnsAsync(options, "reference_materialization_runs"));
        Assert.Equal(0, await ReadCountAsync(options, "reference_materialization_runs"));
        Assert.Equal(0, await ReadCountAsync(options, "reference_materialization_chapter_progress"));
        Assert.Equal(0, await ReadCountAsync(options, "reference_materials"));
        Assert.Equal(0, await ReadCountAsync(options, "reference_material_embeddings"));

        var referenceDirectory = Path.Combine(options.DefaultDataDirectory, "reference-anchor");
        Assert.Single(Directory.GetFiles(referenceDirectory, "index.sqlite.reference-schema-v6-*.bak"));
        Assert.Single(Directory.GetFiles(referenceDirectory, "reference-schema-migration-v6-*.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(AppInitializationOptions options)
    {
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料化 schema", "", ""), CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "schema.md");
        await File.WriteAllTextAsync(sourcePath, "# 第一章\n\n雨声压住窗沿。\n\n# 第二章\n\n门外响起第三次敲门。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        return await anchors.RegisterMaterializationSourceAsync(
            new RegisterReferenceMaterializationSourcePayload(novel.Id, "schema 来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
    }

    private static async ValueTask InsertRunAsync(
        AppInitializationOptions options,
        long anchorId,
        string profileId,
        string runId,
        string generationId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_materialization_runs (
              run_id, anchor_id, split_profile_id, generation_id, policy_version, extractor_schema_version,
              model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
              status, total_chapters, current_chapter_index, started_at)
            VALUES (
              $run_id, $anchor_id, $split_profile_id, $generation_id, 'policy-v1', 'extractor-v1',
              'provider', 'model', 'embedding-provider', 'embedding-model', 8,
              'queued', 2, 1, '2026-07-12T00:00:00.0000000Z');
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$split_profile_id", profileId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async ValueTask<IReadOnlySet<string>> ReadNamesAsync(AppInitializationOptions options, string type)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async ValueTask<IReadOnlySet<string>> ReadIndexesAsync(AppInitializationOptions options, string tableName)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({tableName});";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async ValueTask<IReadOnlySet<string>> ReadColumnsAsync(AppInitializationOptions options, string tableName)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async ValueTask SeedLegacyDerivedDataAsync(AppInitializationOptions options, long anchorId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            CREATE TABLE reference_text_nodes (
              node_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              text TEXT NOT NULL
            );
            CREATE TABLE reference_session_library_scope_state (
              session_id TEXT PRIMARY KEY,
              is_explicit INTEGER NOT NULL DEFAULT 1,
              updated_at TEXT NOT NULL
            );
            INSERT INTO reference_session_library_scope_state (session_id, is_explicit, updated_at)
            VALUES ('legacy-session', 1, '2026-07-21T00:00:00.0000000Z');
            DROP TABLE reference_materials;
            CREATE TABLE reference_materials (
              material_id TEXT PRIMARY KEY, generation_id TEXT NOT NULL, run_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL, chapter_index INTEGER NOT NULL, ordinal INTEGER NOT NULL,
              material_type TEXT NOT NULL, text TEXT NOT NULL, description TEXT NOT NULL,
              tags_json TEXT NOT NULL, text_hash TEXT NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO reference_text_nodes (node_id, anchor_id, text)
            VALUES ('legacy-node', $anchor_id, '旧节点材料');
            INSERT INTO reference_materials (
              material_id, generation_id, run_id, anchor_id, chapter_index, ordinal,
              material_type, text, description, tags_json, text_hash, created_at)
            VALUES (
              'legacy-material', 'legacy-generation', 'legacy-run', $anchor_id, 1, 0,
              'dialogue', '旧派生材料', '不应迁移', '[]', 'legacy-hash', '2026-07-21T00:00:00.0000000Z');
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        command.Parameters.Clear();
        command.CommandText = "PRAGMA journal_mode = DELETE;";
        await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async ValueTask SeedVersionTwoMaterializationSchemaAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            DROP TABLE reference_material_embeddings;
            DROP TABLE reference_materials;
            DROP TABLE reference_materialization_vector_indexes;
            DROP TABLE reference_materialization_chapter_progress;
            DROP TABLE reference_materialization_run_leases;
            DROP TABLE reference_materialization_runs;
            DROP TABLE reference_anchor_materialization_state;
            CREATE TABLE reference_materialization_runs (
              run_id TEXT PRIMARY KEY,
              chapter_batch_size INTEGER NOT NULL CHECK(chapter_batch_size IN (5, 10))
            );
            UPDATE reference_schema_metadata
            SET schema_version = 2
            WHERE schema_key = 'reference-materialization';
            PRAGMA foreign_keys = ON;
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async ValueTask SeedVersionThreeMaterializationSchemaAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE reference_materialization_chapter_progress
            ADD COLUMN current_stage TEXT NOT NULL DEFAULT 'pending';
            UPDATE reference_schema_metadata
            SET schema_version = 3
            WHERE schema_key = 'reference-materialization';
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async ValueTask SeedVersionFourWithoutRequestedChapterAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            DROP TABLE reference_materials;
            CREATE TABLE reference_materials (
              material_id TEXT PRIMARY KEY, generation_id TEXT NOT NULL, run_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL, chapter_index INTEGER NOT NULL, ordinal INTEGER NOT NULL,
              material_type TEXT NOT NULL, text TEXT NOT NULL, description TEXT NOT NULL,
              tags_json TEXT NOT NULL, text_hash TEXT NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO reference_materialization_chapter_progress (
              run_id, chapter_index, status, material_count, vector_count, model_call_count,
              started_at, completed_at)
            VALUES (
              'run-v4', 1, 'completed', 1, 1, 1,
              '2026-07-22T00:00:00.0000000Z', '2026-07-22T00:01:00.0000000Z');

            INSERT INTO reference_materials (
              material_id, generation_id, run_id, anchor_id, chapter_index, ordinal,
              material_type, text, description, tags_json, text_hash, created_at)
            VALUES (
              'material-v4', 'generation-v4', 'run-v4', $anchor_id, 1, 0,
              'dialogue', '保留的章节材料', '迁移后仍应存在', '[]', 'material-v4-hash',
              '2026-07-22T00:01:00.0000000Z');

            INSERT INTO reference_material_embeddings (
              material_id, generation_id, provider, model_id, dimensions,
              embedding_hash, embedding_blob, created_at)
            VALUES (
              'material-v4', 'generation-v4', 'embedding-provider', 'embedding-model', 8,
              'embedding-v4-hash', X'00000000', '2026-07-22T00:01:00.0000000Z');

            ALTER TABLE reference_materialization_runs DROP COLUMN requested_chapter_index;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async ValueTask<int> ReadCountAsync(AppInitializationOptions options, string tableName)
    {
        await using var connection = await OpenConnectionAsync(options);
        return await ReadCountAsync(connection, tableName);
    }

    private static async ValueTask<int> ReadCountAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None)) != 0;
    }

    private static async ValueTask<string> ReadJournalModeAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return (string?)await command.ExecuteScalarAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("SQLite did not report a journal mode.");
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(AppInitializationOptions options)
    {
        var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data"),
            EnableLegacyMigration = false
        };
    }

    private sealed class EmptyChapterSplitAnalyzer : Novelist.Core.App.IReferenceChapterSplitAnalyzer
    {
        public ValueTask<Novelist.Core.App.ReferenceChapterSplitModelResult> AnalyzeAsync(
            Novelist.Core.App.ReferenceChapterSplitModelRequest input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Novelist.Core.App.ReferenceChapterSplitModelResult.Empty);
        }
    }
}
