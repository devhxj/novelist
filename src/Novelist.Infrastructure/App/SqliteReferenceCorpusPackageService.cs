using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

// 语料包（JSONL）：同书备份/恢复语义。
// - 导出：观察与标本全量行 + 证据节点原文，逐行 JSON 写入作者选择的目标文件。
// - 导入：按主键 INSERT OR IGNORE——已存在的 observation_id/specimen_id 跳过；
//   外键目标（文本树节点）缺失的行同样跳过，保证不会破坏引用完整性。
// 跨设备迁移需要连同文本树一起重建，属后续工作（见 user-perspective-review F5）。
public sealed partial class SqliteReferenceCorpusAnalysisService
{
    private const long PackageMaxBytes = 200L * 1024L * 1024L;

    public async ValueTask<ReferenceCorpusPackageExportResult> ExportPackageAsync(
        ExportReferenceCorpusPackagePayload input,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(input.NovelId);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.AnchorId, "Anchor id must be positive.");
        }

        var destination = _packageFilePicker is null
            ? throw new NotSupportedException("Package export requires a file picker.")
            : await _packageFilePicker.PickPackageSaveFileAsync(
                $"corpus-package-{input.AnchorId.ToString(CultureInfo.InvariantCulture)}.jsonl",
                cancellationToken);
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ReferenceMaterializationException(
                "materialization_cancelled",
                "语料包导出已取消。");
        }

        var databasePath = await DatabasePathAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var (observations, specimens) = await ReadPackageRowsAsync(connection, input.NovelId, input.AnchorId, cancellationToken);

        var builder = new StringBuilder();
        foreach (var row in observations)
        {
            builder.Append("{\"type\":\"observation\",\"data\":").Append(row).Append('\n');
        }

        foreach (var row in specimens)
        {
            builder.Append("{\"type\":\"specimen\",\"data\":").Append(row).Append('\n');
        }

        await File.WriteAllTextAsync(destination, builder.ToString(), new UTF8Encoding(false), cancellationToken);
        return new ReferenceCorpusPackageExportResult(destination, observations.Count, specimens.Count);
    }

    public async ValueTask<ReferenceCorpusPackageImportResult> ImportPackageAsync(
        ImportReferenceCorpusPackagePayload input,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(input.NovelId);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.AnchorId, "Anchor id must be positive.");
        }

        var source = _packageFilePicker is null
            ? throw new NotSupportedException("Package import requires a file picker.")
            : await _packageFilePicker.PickPackageOpenFileAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ReferenceMaterializationException(
                "materialization_cancelled",
                "语料包导入已取消。");
        }

        if (!File.Exists(source))
        {
            throw new ArgumentException("语料包文件不存在。", nameof(input));
        }

        var info = new FileInfo(source);
        if (info.Length > PackageMaxBytes)
        {
            throw new ArgumentException("语料包文件超过 200MB 上限。", nameof(input));
        }

        var lines = (await File.ReadAllLinesAsync(source, cancellationToken))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        var (allObservations, allSpecimens) = ParsePackageLines(lines);
        // 证据原文列是导出附带的展示字段，不属于表结构；插入前剥离。
        foreach (var row in allObservations)
        {
            row.Remove("evidence_text");
        }

        foreach (var row in allSpecimens)
        {
            row.Remove("evidence_text");
        }

        var databasePath = await DatabasePathAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);

        // SQLite 的 INSERT OR IGNORE 不忽略外键冲突：证据节点缺失的行先过滤掉，保证“跳过”语义。
        var observations = await FilterRowsWithExistingNodesAsync(
            connection, allObservations, nodeIdColumn: "node_id", cancellationToken);
        var specimens = await FilterRowsWithExistingNodesAsync(
            connection, allSpecimens, nodeIdColumn: "source_node_id", cancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var importedObservations = 0;
        foreach (var row in observations)
        {
            importedObservations += await InsertOrIgnoreAsync(connection, transaction, "reference_feature_observations", row, cancellationToken);
        }

        var importedSpecimens = 0;
        foreach (var row in specimens)
        {
            importedSpecimens += await InsertOrIgnoreAsync(connection, transaction, "reference_technique_specimens", row, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReferenceCorpusPackageImportResult(
            importedObservations + importedSpecimens,
            allObservations.Count + allSpecimens.Count - importedObservations - importedSpecimens,
            allObservations.Count,
            allSpecimens.Count);
    }

    private async ValueTask<(List<string> Observations, List<string> Specimens)> ReadPackageRowsAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var observations = new List<string>();
        var specimens = new List<string>();
        var observationColumns = await ReadColumnNamesAsync(connection, "reference_feature_observations", cancellationToken);
        var specimenColumns = await ReadColumnNamesAsync(connection, "reference_technique_specimens", cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT o.*, n.text AS evidence_text
                FROM reference_feature_observations o
                INNER JOIN reference_text_nodes n ON n.node_id = o.node_id AND n.anchor_id = o.anchor_id
                WHERE n.novel_id = $novel_id AND o.anchor_id = $anchor_id
                ORDER BY o.observation_id;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            command.Parameters.AddWithValue("$anchor_id", anchorId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                observations.Add(ReadRowAsJson(reader, observationColumns, "evidence_text"));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.*, node.text AS evidence_text
                FROM reference_technique_specimens s
                INNER JOIN reference_text_nodes node ON node.node_id = s.source_node_id AND node.anchor_id = s.source_anchor_id
                WHERE node.novel_id = $novel_id AND s.source_anchor_id = $anchor_id
                ORDER BY s.specimen_id;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            command.Parameters.AddWithValue("$anchor_id", anchorId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                specimens.Add(ReadRowAsJson(reader, specimenColumns, "evidence_text"));
            }
        }

        return (observations, specimens);
    }

    private static async ValueTask<List<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{tableName}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static string ReadRowAsJson(SqliteDataReader reader, IReadOnlyList<string> columns, string evidenceColumn)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++)
        {
            payload[columns[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        }

        payload[evidenceColumn] = reader.IsDBNull(reader.FieldCount - 1)
            ? null
            : reader.GetString(reader.FieldCount - 1);
        return JsonSerializer.Serialize(payload);
    }

    // System.Text.Json 反序列化 object? 得到 JsonElement 装箱值；转成 CLR 原语便于后续绑定与判断。
    private static Dictionary<string, object?>? DeserializeRow(JsonElement data)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, object?>>(data.GetRawText());
        if (raw is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            result[key] = value is JsonElement element ? ConvertElement(element) : value;
        }

        return result;
    }

    private static object? ConvertElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }

    private static (List<Dictionary<string, object?>> Observations, List<Dictionary<string, object?>> Specimens) ParsePackageLines(
        string[] lines)
    {
        var observations = new List<Dictionary<string, object?>>();
        var specimens = new List<Dictionary<string, object?>>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("语料包行格式无效（应为 {type, data} JSON 对象）。");
            }

            var payload = DeserializeRow(data)
                ?? throw new ArgumentException("语料包行数据无效。");
            var typeName = type.GetString();
            if (string.Equals(typeName, "observation", StringComparison.Ordinal))
            {
                observations.Add(payload);
            }
            else if (string.Equals(typeName, "specimen", StringComparison.Ordinal))
            {
                specimens.Add(payload);
            }
            else
            {
                throw new ArgumentException($"语料包行类型未知：{typeName}。");
            }
        }

        return (observations, specimens);
    }

    private static async ValueTask<List<Dictionary<string, object?>>> FilterRowsWithExistingNodesAsync(
        SqliteConnection connection,
        List<Dictionary<string, object?>> rows,
        string nodeIdColumn,
        CancellationToken cancellationToken)
    {
        var kept = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            if (row.TryGetValue(nodeIdColumn, out var nodeId) && nodeId is string id && !string.IsNullOrEmpty(id))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM reference_text_nodes WHERE node_id = $node_id;";
                command.Parameters.AddWithValue("$node_id", id);
                var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (count > 0)
                {
                    kept.Add(row);
                }
            }
        }

        return kept;
    }

    private static async ValueTask<int> InsertOrIgnoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        Dictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var columns = row.Keys.ToArray();
        var parameterNames = columns.Select((_, index) => $"$p{index.ToString(CultureInfo.InvariantCulture)}").ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT OR IGNORE INTO {tableName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", parameterNames)});";
        for (var index = 0; index < columns.Length; index++)
        {
            var value = row[columns[index]];
            command.Parameters.AddWithValue(parameterNames[index], value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
