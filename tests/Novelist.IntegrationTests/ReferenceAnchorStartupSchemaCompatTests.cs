using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;
using System.Text.Json;

namespace Novelist.IntegrationTests;

/// <summary>
/// 2026-09-04 启动事故回归：携带 7/22 v6 迁移前遗留表形状的库（reference_materials 为
/// generation 结构、reference_materialization_chapter_progress 为 v4 结构）曾让
/// EnsureSchemaAsync 在 CREATE INDEX/UPDATE 上抛 no such column，桌面端无法启动。
/// 本测试在合成老结构库上跑启动恢复路径，钉死三处守卫：
/// ① 遗留 materials 检索索引仅在旧列存在时创建；② 遗留 materials node_id 回填仅在
/// source_segment_id 存在时执行；③ v4 chapter_progress 走 copy-first 重建 + manifest。
/// </summary>
public sealed class ReferenceAnchorStartupSchemaCompatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReconcileSurvivesLegacyPreV6ReferenceSchema()
    {
        var dataDir = Path.Combine(_root, "data");
        var configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(dataDir, "reference-anchor"));
        Directory.CreateDirectory(configDir);
        var databasePath = Path.Combine(dataDir, "reference-anchor", "index.sqlite");
        await using (var connection = await OpenConnection(databasePath))
        {
            await CreateLegacyEraTablesAsync(connection);
        }
        await File.WriteAllTextAsync(
            Path.Combine(configDir, "config.json"),
            JsonSerializer.Serialize(new { data_dir = dataDir }));

        var options = new AppInitializationOptions
        {
            ConfigDirectory = configDir,
            DefaultDataDirectory = dataDir,
        };
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var service = new SqliteReferenceAnchorService(options, novelService);

        // 修复前：在 material_type 索引（或后续 batch_index 索引 / source_segment_id 回填）抛 no such column。
        await service.ReconcileRecoverableProcessingAsync(CancellationToken.None);

        await using var verify = await OpenConnection(databasePath);
        var chapterProgressColumns = await ReadColumnNamesAsync(verify, "reference_materialization_chapter_progress");
        Assert.Contains("chapter_node_id", chapterProgressColumns);
        Assert.Contains("batch_index", chapterProgressColumns);

        // copy-first：旧行保留在备份表中，manifest 落盘。
        var backupTables = new List<string>();
        await using (var read = verify.CreateCommand())
        {
            read.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'reference_materialization_chapter_progress_legacy%';";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                backupTables.Add(reader.GetString(0));
            }
        }
        var backupTable = Assert.Single(backupTables);
        Assert.Equal(2, await ScalarIntAsync(verify, $"SELECT COUNT(*) FROM {backupTable};"));

        // v6 generation 形状的 reference_materials 不被遗留回填触碰。
        Assert.Equal(3, await ScalarIntAsync(verify, "SELECT COUNT(*) FROM reference_materials;"));

        var manifests = Directory.GetFiles(
            Path.GetDirectoryName(databasePath)!,
            "reference-schema-chapter-progress-rebuild-*.json");
        Assert.Single(manifests);
    }

    /// <summary>
    /// 2026-09-06 回归：pre-v6 批处理时代的 reference_materialization_runs（无 batch 列）让
    /// GetReferenceMaterializationStatus 每次轮询都抛 no such column: chapter_batch_size，
    /// 材料化面板报 Internal bridge error。EnsureCoreTablesAsync 需为四张运行期表逐列补齐
    /// v6 追加列（runs / anchor_state / run_leases / vector_indexes），旧行保留并取默认值。
    /// </summary>
    [Fact]
    public async Task EnsureCoreTablesAddsV6ColumnsToLegacyMaterializationTables()
    {
        var databasePath = Path.Combine(_root, "legacy-runs", "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnection(databasePath);
        await CreateLegacyMaterializationRunTablesAsync(connection);

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);

        var runColumns = await ReadColumnNamesAsync(connection, "reference_materialization_runs");
        Assert.Contains("candidate_version", runColumns);
        Assert.Contains("qualifier_version", runColumns);
        Assert.Contains("chapter_batch_size", runColumns);
        Assert.Contains("total_chapter_batches", runColumns);
        Assert.Contains("completed_chapter_batches", runColumns);
        Assert.Contains("current_batch_index", runColumns);
        Assert.Contains("current_batch_start_chapter", runColumns);
        Assert.Contains("current_batch_end_chapter", runColumns);
        Assert.Contains("candidate_count", runColumns);
        Assert.Contains("accepted_count", runColumns);
        Assert.Contains("rejected_count", runColumns);
        Assert.Contains("review_count", runColumns);
        Assert.Contains("tokens_spent", runColumns);
        Assert.Contains("activated_at", runColumns);

        var stateColumns = await ReadColumnNamesAsync(connection, "reference_anchor_materialization_state");
        Assert.Contains("previous_generation_id", stateColumns);
        Assert.Contains("row_version", stateColumns);
        Assert.Contains("updated_at", stateColumns);

        var leaseColumns = await ReadColumnNamesAsync(connection, "reference_materialization_run_leases");
        Assert.Contains("worker_id", leaseColumns);
        Assert.Contains("updated_at", leaseColumns);

        var vectorIndexColumns = await ReadColumnNamesAsync(connection, "reference_materialization_vector_indexes");
        Assert.Contains("created_at", vectorIndexColumns);
        Assert.Contains("updated_at", vectorIndexColumns);

        // copy-first：旧行回填到 v6 新表（新增必填列取默认值），同时完整保留在备份表中。
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT status, chapter_batch_size, candidate_version, total_chapter_batches
                FROM reference_materialization_runs
                WHERE run_id = 'legacy-run-1';
                """;
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("completed", reader.GetString(0));
            Assert.Equal(10, reader.GetInt32(1));
            Assert.Equal(string.Empty, reader.GetString(2));
            Assert.Equal(0, reader.GetInt32(3));
        }

        var backupTables = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'reference_materialization_runs_legacy%';";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                backupTables.Add(reader.GetString(0));
            }
        }
        var backupTable = Assert.Single(backupTables);
        Assert.Equal(1, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {backupTable};"));
        // 旧列（v6 插入不再提供）只保留在备份表里。
        Assert.Equal("v1", await ScalarStringAsync(connection, $"SELECT extractor_schema_version FROM {backupTable} WHERE run_id = 'legacy-run-1';"));

        var manifests = Directory.GetFiles(
            Path.GetDirectoryName(databasePath)!,
            "reference-schema-runs-rebuild-*.json");
        Assert.Single(manifests);

        // 补列后 v6 形状的新行可以正常写入（状态查询/InsertRunAsync 依赖的列集合）。
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO reference_materialization_runs (
                  run_id, anchor_id, split_profile_id, generation_id, policy_version, candidate_version, qualifier_version,
                  model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
                  status, chapter_batch_size, total_chapters, total_chapter_batches, started_at)
                VALUES
                  ('v6-run-1', 1, 'profile-1', 'gen-v6-1', 'policy-1', 'candidate-1', 'qualifier-1',
                   'openai', 'gpt-test', 'openai', 'embed-test', 1024,
                   'queued', 10, 12, 2, '2026-09-06T03:00:00Z');
                """;
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateLegacyMaterializationRunTablesAsync(SqliteConnection connection)
    {
        // 列集合逐列对齐一台真实 pre-v6 批处理时代的遗留库（2026-09-06 报障来源）。
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE reference_anchors (
              anchor_id INTEGER PRIMARY KEY,
              title TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE reference_chapter_split_profiles (
              split_profile_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            CREATE TABLE reference_materialization_runs (
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
              embedding_dimensions INTEGER NOT NULL,
              status TEXT NOT NULL,
              total_chapters INTEGER NOT NULL,
              processed_chapters INTEGER NOT NULL,
              current_chapter_index INTEGER,
              requested_chapter_index INTEGER,
              material_count INTEGER NOT NULL,
              vector_count INTEGER NOT NULL,
              last_error_code TEXT,
              last_error_message TEXT,
              started_at TEXT NOT NULL,
              completed_at TEXT
            );

            CREATE TABLE reference_anchor_materialization_state (
              anchor_id INTEGER PRIMARY KEY,
              active_generation_id TEXT
            );

            CREATE TABLE reference_materialization_run_leases (
              run_id TEXT PRIMARY KEY,
              lease_token TEXT NOT NULL,
              lease_expires_at TEXT NOT NULL
            );

            CREATE TABLE reference_materialization_vector_indexes (
              generation_id TEXT PRIMARY KEY,
              run_id TEXT NOT NULL,
              table_name TEXT NOT NULL,
              provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              vector_count INTEGER NOT NULL,
              status TEXT NOT NULL
            );

            INSERT INTO reference_anchors (anchor_id, title, status, created_at, updated_at)
            VALUES (1, '遗留参考书', 'active', '2026-07-22T09:50:28Z', '2026-07-22T09:50:28Z');

            INSERT INTO reference_chapter_split_profiles (split_profile_id, anchor_id, status, created_at)
            VALUES ('profile-1', 1, 'confirmed', '2026-07-22T09:50:28Z');

            INSERT INTO reference_materialization_runs
              (run_id, anchor_id, split_profile_id, generation_id, policy_version, extractor_schema_version,
               model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
               status, total_chapters, processed_chapters, material_count, vector_count, started_at)
            VALUES
              ('legacy-run-1', 1, 'profile-1', 'gen-legacy-1', 'policy-1', 'v1',
               'openai', 'gpt-test', 'openai', 'embed-test', 1024,
               'completed', 12, 12, 30, 30, '2026-07-22T09:50:28Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateLegacyEraTablesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE reference_materials (
              material_id TEXT PRIMARY KEY,
              generation_id TEXT NOT NULL,
              run_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              chapter_index INTEGER NOT NULL,
              ordinal INTEGER NOT NULL,
              text TEXT NOT NULL,
              metadata_schema_version TEXT NOT NULL DEFAULT 'reference-material-archive-v1',
              metadata_json TEXT NOT NULL,
              text_hash TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            CREATE TABLE reference_materialization_chapter_progress (
              run_id TEXT NOT NULL,
              chapter_index INTEGER NOT NULL,
              status TEXT NOT NULL,
              material_count INTEGER NOT NULL DEFAULT 0,
              vector_count INTEGER NOT NULL DEFAULT 0,
              model_call_count INTEGER NOT NULL DEFAULT 0,
              started_at TEXT,
              completed_at TEXT,
              last_error_code TEXT,
              last_error_message TEXT,
              PRIMARY KEY(run_id, chapter_index)
            );

            INSERT INTO reference_materialization_chapter_progress
              (run_id, chapter_index, status, material_count)
            VALUES
              ('legacy-run-1', 1, 'completed', 3),
              ('legacy-run-1', 2, 'completed', 2);

            INSERT INTO reference_materials
              (material_id, generation_id, run_id, anchor_id, chapter_index, ordinal, text, metadata_json, text_hash, created_at)
            VALUES
              ('mat-1', 'gen-1', 'run-1', 1, 1, 0, '示例材料一', '{}', 'hash-1', '2026-07-22T09:50:28Z'),
              ('mat-2', 'gen-1', 'run-1', 1, 1, 1, '示例材料二', '{}', 'hash-2', '2026-07-22T09:50:28Z'),
              ('mat-3', 'gen-1', 'run-1', 1, 2, 0, '示例材料三', '{}', 'hash-3', '2026-07-22T09:50:28Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<List<string>> ReadColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
