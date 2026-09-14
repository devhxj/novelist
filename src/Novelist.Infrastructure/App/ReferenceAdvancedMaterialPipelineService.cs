using System.Globalization;
using Microsoft.Data.Sqlite;
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
        var nodes = await ReadNodesAsync(anchorId, cancellationToken);

        var observationAccepted = 0;
        var observationRejected = 0;
        var specimenAccepted = 0;
        var specimenRejected = 0;

        foreach (var node in nodes)
        {
            foreach (var family in ObservationFamilies)
            {
                var output = await _observationAnalyzer.AnalyzeAsync(
                    new ReferenceAdvancedMaterialAnalysisInput(anchorId, node.NodeId, node.NodeType, family, node.Text),
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
                new ReferenceAdvancedMaterialSpecimenAnalysisInput(anchorId, node.NodeId, node.NodeType, node.Text),
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
        return new ReferenceAdvancedMaterialPipelineResult(
            observationAccepted,
            observationRejected,
            specimenAccepted,
            specimenRejected,
            strategy.GroupCount);
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
