using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusPackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"novelist-corpus-package-{Guid.NewGuid():N}");

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private static string ReferenceDatabasePath(AppInitializationOptions options)
    {
        return Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
    }

    [Fact]
    public async Task ImportPackageSkipsMissingNodeRowsAndIgnoresEvidenceColumn()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.DefaultDataDirectory);
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var databasePath = ReferenceDatabasePath(options);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO reference_anchors
                  (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
                VALUES
                  (101, 42, '雨门小样', '', 'rain-doorway.md', 'markdown', 'user_provided',
                   'source-hash-101', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'private');

                INSERT INTO reference_text_nodes
                  (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
                   chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
                VALUES
                  ('node-a', 101, NULL, 'sentence', 1, 1,
                   1, 0, 10, 10, 'hash-node-a', '雨声贴着门缝往里挤。', '2026-07-09T00:00:00Z');

                INSERT INTO reference_analysis_runs
                  (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
                   scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
                VALUES
                  ('feature-run-package-import', 101, 'feature-v1', 'reference-corpus-feature-family-v1', 'fake', 'fake-model',
                   'sentence', 'completed', NULL, 0, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 0);
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // 语料包行：一条 node_id 存在（含导出附带的 evidence_text 展示列）、一条 node_id 缺失、一条主键已存在。
        var packageLines = new[]
        {
            SerializePackageLine("observation", new Dictionary<string, object?>
            {
                ["observation_id"] = "obs-import-1",
                ["node_id"] = "node-a",
                ["node_type"] = "sentence",
                ["run_id"] = "feature-run-package-import",
                ["anchor_id"] = 101L,
                ["feature_family"] = "emotion",
                ["feature_key"] = "emotion_state",
                ["value_kind"] = "enum",
                ["value_text"] = "restrained",
                ["confidence"] = 0.9,
                ["review_state"] = "unverified",
                ["validity_state"] = "active",
                ["created_at"] = "2026-07-09T00:00:00Z",
                ["evidence_text"] = "导出附带的展示列，不属于表结构",
            }),
            SerializePackageLine("observation", new Dictionary<string, object?>
            {
                ["observation_id"] = "obs-import-missing-node",
                ["node_id"] = "node-missing",
                ["node_type"] = "sentence",
                ["run_id"] = "feature-run-package-import",
                ["anchor_id"] = 101L,
                ["feature_family"] = "emotion",
                ["feature_key"] = "emotion_state",
                ["value_kind"] = "enum",
                ["confidence"] = 0.8,
                ["review_state"] = "unverified",
                ["validity_state"] = "active",
                ["created_at"] = "2026-07-09T00:00:00Z",
            }),
        };
        var packagePath = Path.Combine(_root, "package.jsonl");
        await File.WriteAllLinesAsync(packagePath, packageLines, Encoding.UTF8);

        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            packageFilePicker: new FixedPackageFilePicker(_root, packagePath));

        var result = await service.ImportPackageAsync(
            new ImportReferenceCorpusPackagePayload(42, 101),
            CancellationToken.None);

        // 缺失节点的行被跳过（不中断事务），有效行导入成功。
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(2, result.ObservationCount);

        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM reference_feature_observations WHERE observation_id = 'obs-import-1';";
            Assert.Equal(1L, await command.ExecuteScalarAsync(CancellationToken.None));
        }

        // 重复导入：主键冲突全部跳过。
        var repeat = await service.ImportPackageAsync(
            new ImportReferenceCorpusPackagePayload(42, 101),
            CancellationToken.None);
        Assert.Equal(0, repeat.ImportedCount);
        Assert.Equal(2, repeat.SkippedCount);
    }

    private static string SerializePackageLine(string type, Dictionary<string, object?> data)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["data"] = data,
        });
    }

    private sealed class FixedPackageFilePicker(string saveDirectory, string openFilePath) : IReferenceCorpusPackageFilePicker
    {
        public ValueTask<string?> PickPackageSaveFileAsync(string defaultFileName, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(Path.Combine(saveDirectory, defaultFileName));
        }

        public ValueTask<string?> PickPackageOpenFileAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(openFilePath);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
