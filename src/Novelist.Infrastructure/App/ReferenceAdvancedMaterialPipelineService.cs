using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

// 单本书高级素材生产编排：走节点 → 逐 family 抽观测 → 抽机理 → 聚合策略。
public sealed class ReferenceAdvancedMaterialPipelineService : IReferenceAdvancedMaterialPipelineService
{
    // style 由风格画像提供，不在观测抽取范围内。
    private static readonly string[] ObservationFamilies =
    [
        ReferenceAdvancedMaterialFamilies.World,
        ReferenceAdvancedMaterialFamilies.Craft,
        ReferenceAdvancedMaterialFamilies.Technique,
        ReferenceAdvancedMaterialFamilies.Structure
    ];

    // 节点文本上限。整节点直送模型时，长章的 scene 文本会让单次调用生成过长、更容易
    // 被掐断或超时——长书跑到第 1~2 个节点就失败，这是最可能的诱因。
    // 截断到 8000 字：足够承载一个场景的写法信息，又不至于把单次调用撑爆。
    private const int MaxNodeTextChars = 8_000;

    private readonly IReferenceCorpusDatabasePathResolver _pathResolver;
    private readonly IReferenceAdvancedMaterialAnalyzer _observationAnalyzer;
    private readonly IReferenceAdvancedMaterialSpecimenAnalyzer _specimenAnalyzer;
    private readonly IReferenceAdvancedMaterialIngestionService _ingestion;
    private readonly IReferenceAdvancedMaterialStrategyService _strategy;

    public ReferenceAdvancedMaterialPipelineService(
        IReferenceCorpusDatabasePathResolver pathResolver,
        IReferenceAdvancedMaterialAnalyzer observationAnalyzer,
        IReferenceAdvancedMaterialSpecimenAnalyzer specimenAnalyzer,
        IReferenceAdvancedMaterialIngestionService ingestion,
        IReferenceAdvancedMaterialStrategyService strategy)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _observationAnalyzer = observationAnalyzer ?? throw new ArgumentNullException(nameof(observationAnalyzer));
        _specimenAnalyzer = specimenAnalyzer ?? throw new ArgumentNullException(nameof(specimenAnalyzer));
        _ingestion = ingestion ?? throw new ArgumentNullException(nameof(ingestion));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
    }

    public async ValueTask<ReferenceAdvancedMaterialPipelineResult> ProcessAnchorAsync(
        long anchorId,
        string runId,
        CancellationToken cancellationToken)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId));
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("run_id is required.", nameof(runId));
        }

        await EnsureRunAsync(anchorId, runId, cancellationToken);

        var observationAccepted = 0;
        var observationRejected = 0;
        var specimenAccepted = 0;
        var specimenRejected = 0;
        try
        {
            var nodes = await ReadNodesAsync(anchorId, cancellationToken);

            foreach (var node in nodes)
            {
                // 长章的整节点文本会撑爆单次调用：截断后再送模型，
                // 观测与机理共用同一份截断文本，保证两者看到的范围一致。
                var nodeText = Truncate(node.Text, MaxNodeTextChars);
                foreach (var family in ObservationFamilies)
                {
                    var output = await _observationAnalyzer.AnalyzeAsync(
                        new ReferenceAdvancedMaterialAnalysisInput(anchorId, node.NodeId, node.NodeType, family, nodeText),
                        cancellationToken);
                    var drafts = ReferenceAdvancedMaterialAnalysisParser.ParseObservations(output.Json, node.NodeId, family);
                    if (drafts.Count == 0)
                    {
                        continue;
                    }

                    var result = await _ingestion.IngestObservationsAsync(anchorId, runId, drafts, cancellationToken);
                    observationAccepted += result.Accepted;
                    observationRejected += result.Rejected;
                }

                var specimenOutput = await _specimenAnalyzer.AnalyzeAsync(
                    new ReferenceAdvancedMaterialSpecimenAnalysisInput(anchorId, node.NodeId, node.NodeType, nodeText),
                    cancellationToken);
                var specimenDrafts = ReferenceAdvancedMaterialSpecimenAnalysisParser.ParseSpecimens(specimenOutput.Json, node.NodeId);
                if (specimenDrafts.Count > 0)
                {
                    var result = await _ingestion.IngestSpecimensAsync(anchorId, runId, specimenDrafts, cancellationToken);
                    specimenAccepted += result.Accepted;
                    specimenRejected += result.Rejected;
                }
            }

            var strategy = await _strategy.AggregateStrategyAsync(anchorId, cancellationToken);
            var pipelineResult = new ReferenceAdvancedMaterialPipelineResult(
                observationAccepted,
                observationRejected,
                specimenAccepted,
                specimenRejected,
                strategy.GroupCount);
            await CompleteRunAsync(runId, observationAccepted, cancellationToken);
            return pipelineResult;
        }
        catch (Exception exception)
        {
            // 失败必须落库。此前这里没有 try/catch，异常直接冒到 bridge，
            // 于是 run 永远停在 running、completed_at 为空、diagnostics 是一个空数组——
            // 作者看到"没有错误原因"，其实错误从未被记录过。
            await FailRunAsync(runId, observationAccepted, exception);
            throw;
        }
    }

    private async ValueTask EnsureRunAsync(long anchorId, string runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id, scope, status,
               token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count, diagnostics_json)
            VALUES
              ($run_id, $anchor_id, 'advanced-material-pipeline', $schema_version, '', '', 'anchor', 'running',
               NULL, 0, NULL, $now, NULL, 0, '[]')
            ON CONFLICT(run_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$schema_version", ReferenceAdvancedMaterialAnalysisSchemaVersions.V1);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask CompleteRunAsync(
        string runId,
        int observationCount,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_analysis_runs
            SET status = $status,
                completed_at = $completed_at,
                observation_count = $observation_count
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$status", "completed");
        command.Parameters.AddWithValue("$completed_at", FormatRunTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$observation_count", observationCount);
        command.Parameters.AddWithValue("$run_id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // 失败落库：把状态与原因写进 run 行，作者（和我们）才有据可查。
    // 这里再吞掉异常——记录失败本身绝不能盖掉原始异常。
    private async ValueTask FailRunAsync(string runId, int observationCount, Exception exception)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE reference_analysis_runs
                SET status = $status,
                    completed_at = $completed_at,
                    observation_count = $observation_count,
                    diagnostics_json = $diagnostics_json
                WHERE run_id = $run_id;
                """;
            command.Parameters.AddWithValue("$status", "failed");
            command.Parameters.AddWithValue("$completed_at", FormatRunTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$observation_count", observationCount);
            command.Parameters.AddWithValue(
                "$diagnostics_json",
                JsonSerializer.Serialize(new
                {
                    code = "advanced_material_pipeline_failed",
                    message = Truncate(exception.Message, 1_200),
                    type = exception.GetType().Name,
                }));
            command.Parameters.AddWithValue("$run_id", runId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch
        {
            // 记录失败本身失败时保持沉默：原始异常会照常向上抛。
        }
    }

    private static string FormatRunTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private async ValueTask<IReadOnlyList<TextNode>> ReadNodesAsync(long anchorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var scenes = await ReadNodesByTypeAsync(connection, anchorId, "scene", cancellationToken);
        if (scenes.Count > 0)
        {
            return scenes;
        }

        return await ReadNodesByTypeAsync(connection, anchorId, "chapter", cancellationToken);
    }

    private static async ValueTask<IReadOnlyList<TextNode>> ReadNodesByTypeAsync(
        SqliteConnection connection,
        long anchorId,
        string nodeType,
        CancellationToken cancellationToken)
    {
        var nodes = new List<TextNode>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, node_type, text
            FROM reference_text_nodes
            WHERE anchor_id = $anchor_id AND node_type = $node_type
            ORDER BY sequence_index;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$node_type", nodeType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new TextNode(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return nodes;
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var databasePath = await _pathResolver.ResolveAsync(cancellationToken);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        return connection;
    }

    private sealed record TextNode(string NodeId, string NodeType, string Text);
}
