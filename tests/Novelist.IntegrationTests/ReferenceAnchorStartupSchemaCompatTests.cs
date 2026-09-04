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
