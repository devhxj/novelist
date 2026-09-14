using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

// 书级策略聚合与单本书生产编排（用假分析器隔离 LLM）。
public sealed class ReferenceAdvancedMaterialPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"novelist-advm-pipeline-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private AppInitializationOptions CreateOptions() => new()
    {
        ConfigDirectory = Path.Combine(_root, "config"),
        DefaultDataDirectory = Path.Combine(_root, "data")
    };

    private async Task<AppInitializationOptions> SeedAsync()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.DefaultDataDirectory);
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);

        var databasePath = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
            VALUES
              (101, 42, '编排参考', 'fixture', 'rain.md', 'markdown', 'user_provided',
               'hash-101', 'v1', 'ready', '2026-09-14T00:00:00Z', '2026-09-14T00:00:00Z', 'private');

            INSERT INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth, chapter_index,
               start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-1', 101, NULL, 'scene', 1, 1, 1, 0, 17, 17, 'text-hash-1', '他推门而入，屋里安静得能听见雨声。', '2026-09-14T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
        return options;
    }

    private static IReadOnlyList<AdvancedMaterialObservationDraft> CraftObservation() =>
    [
        new(ReferenceAdvancedMaterialFamilies.Craft, "information_delivery", "drip_fed", "逐点放料", 0.8,
            [new AdvancedMaterialEvidenceDraft("node-1", 0, 10)])
    ];

    [Fact]
    public async Task StrategyAggregationMergesRowsAndKeepsEvidence()
    {
        var options = await SeedAsync();
        var ingestion = new SqliteReferenceAdvancedMaterialIngestionService(new ReferenceCorpusDatabasePathResolver(options));
        var anchor = 101;
        var runId = "run-strategy";

        // 需要 run 行以满足外壳表 analysis_run_id 外键。
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite"),
                Pooling = false
            }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO reference_analysis_runs
                  (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id, scope, status,
                   token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count, diagnostics_json)
                VALUES
                  ('run-strategy', 101, 'a1', 's1', 'p', 'm', 'anchor', 'completed', NULL, 0, NULL,
                   '2026-09-14T00:00:00Z', NULL, 0, '[]');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await ingestion.IngestObservationsAsync(anchor, runId,
            [CraftObservation()[0], CraftObservation()[0] with { Value = "front_loaded" }], CancellationToken.None);

        var strategy = new SqliteReferenceAdvancedMaterialStrategyService(new ReferenceCorpusDatabasePathResolver(options));
        var result = await strategy.AggregateStrategyAsync(anchor, CancellationToken.None);

        Assert.Equal(1, result.GroupCount);
        Assert.True(result.EvidenceRefCount >= 1);

        var service = new SqliteReferenceAdvancedMaterialService(new ReferenceCorpusDatabasePathResolver(options));
        var strategyRows = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(anchor, Layer: ReferenceAdvancedMaterialLayers.Strategy),
            CancellationToken.None);
        Assert.Equal(1, strategyRows.Total);
        Assert.Equal("information_delivery", strategyRows.Items[0].FeatureKey);

        var detail = await service.GetAsync(
            new GetReferenceAdvancedMaterialDetailPayload(anchor, strategyRows.Items[0].MaterialId),
            CancellationToken.None);
        Assert.Contains("\"source_layer\":\"observation\"", detail!.ValueJson, StringComparison.Ordinal);
        Assert.NotEmpty(detail.Evidence);
    }

    [Fact]
    public async Task PipelineWalksNodesAndProducesObservationAndStrategy()
    {
        var options = await SeedAsync();
        var pathResolver = new ReferenceCorpusDatabasePathResolver(options);
        var pipeline = new ReferenceAdvancedMaterialPipelineService(
            pathResolver,
            new FakeObservationAnalyzer(),
            new FakeSpecimenAnalyzer(),
            new SqliteReferenceAdvancedMaterialIngestionService(pathResolver),
            new SqliteReferenceAdvancedMaterialStrategyService(pathResolver));

        var result = await pipeline.ProcessAnchorAsync(101, "run-pipe", CancellationToken.None);

        Assert.Equal(1, result.ObservationAccepted);
        Assert.Equal(0, result.ObservationRejected);
        Assert.Equal(0, result.SpecimenAccepted);
        Assert.True(result.StrategyGroups >= 1);

        var service = new SqliteReferenceAdvancedMaterialService(pathResolver);
        var observations = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Observation),
            CancellationToken.None);
        Assert.Equal(1, observations.Total);
        Assert.Equal(ReferenceAdvancedMaterialFamilies.Craft, observations.Items[0].Family);

        var strategyRows = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Strategy),
            CancellationToken.None);
        Assert.Equal(1, strategyRows.Total);
    }

    private sealed class FakeObservationAnalyzer : IReferenceAdvancedMaterialAnalyzer
    {
        public ValueTask<ReferenceAdvancedMaterialAnalysisOutput> AnalyzeAsync(
            ReferenceAdvancedMaterialAnalysisInput input,
            CancellationToken cancellationToken)
        {
            var json = input.Family == ReferenceAdvancedMaterialFamilies.Craft
                ? """{"family":"craft","observations":[{"feature_key":"information_delivery","value":"drip_fed","explanation":"x","confidence":0.8,"evidence":[{"start":0,"end":10}]}]}"""
                : $$"""{"family":"{{input.Family}}","observations":[]}""";
            return ValueTask.FromResult(new ReferenceAdvancedMaterialAnalysisOutput(json, 0));
        }
    }

    private sealed class FakeSpecimenAnalyzer : IReferenceAdvancedMaterialSpecimenAnalyzer
    {
        public ValueTask<ReferenceAdvancedMaterialAnalysisOutput> AnalyzeAsync(
            ReferenceAdvancedMaterialSpecimenAnalysisInput input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReferenceAdvancedMaterialAnalysisOutput("""{"specimens":[]}""", 0));
    }
}
