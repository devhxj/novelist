using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

// 抽取落库：词表/证据/置信校验，机理层 boundary 硬门。
public sealed class ReferenceAdvancedMaterialIngestionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"novelist-advm-ingest-{Guid.NewGuid():N}");

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

    private async Task<SqliteReferenceAdvancedMaterialIngestionService> SeedAsync(AppInitializationOptions options)
    {
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
              (101, 42, '抽取参考', 'fixture', 'rain.md', 'markdown', 'user_provided',
               'hash-101', 'v1', 'ready', '2026-09-14T00:00:00Z', '2026-09-14T00:00:00Z', 'private');

            INSERT INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth, chapter_index,
               start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-1', 101, NULL, 'scene', 1, 1, 1, 0, 17, 17, 'text-hash-1', '他推门而入，屋里安静得能听见雨声。', '2026-09-14T00:00:00Z');

            INSERT INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id, scope, status,
               token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count, diagnostics_json)
            VALUES
              ('run-advm', 101, 'a1', 's1', 'p', 'm', 'anchor', 'completed', NULL, 0, NULL,
               '2026-09-14T00:00:00Z', NULL, 0, '[]');
            """;
        await command.ExecuteNonQueryAsync();

        return new SqliteReferenceAdvancedMaterialIngestionService(new ReferenceCorpusDatabasePathResolver(options));
    }

    private static AdvancedMaterialObservationDraft Observation(string featureKey, string value, int end, double confidence = 0.8) =>
        new(ReferenceAdvancedMaterialFamilies.Craft, featureKey, value, "解释", confidence,
            [new AdvancedMaterialEvidenceDraft("node-1", 0, end)]);

    private static AdvancedMaterialSpecimenDraft Specimen(
        string featureKey,
        IReadOnlyList<string>? failureModes = null,
        string transferTemplate = "主体[动作]后接[感官回响]") =>
        new(
            ReferenceAdvancedMaterialFamilies.Technique,
            featureKey,
            "用听觉细节压住场景",
            "推门后安静",
            ["先动作后感官更贴近视角"],
            "读者感到压迫",
            transferTemplate,
            new Dictionary<string, string> { ["action"] = "推门" },
            ["依赖雨夜设定"],
            failureModes ?? ["描写过长"],
            ["堆砌感官"],
            0.75,
            [new AdvancedMaterialEvidenceDraft("node-1", 0, 17)]);

    [Fact]
    public async Task ObservationsAcceptValidAndRejectInvalidDrafts()
    {
        var options = CreateOptions();
        var ingestion = await SeedAsync(options);

        var result = await ingestion.IngestObservationsAsync(
            101,
            "run-advm",
            [
                Observation("information_delivery", "drip_fed", 10),
                Observation("information_delivery", "not_a_value", 10),
                Observation("unknown_feature", "drip_fed", 10),
                Observation("information_delivery", "drip_fed", 999),
                Observation("information_delivery", "drip_fed", 10, confidence: 1.5)
            ],
            CancellationToken.None);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(4, result.Rejected);

        var service = new SqliteReferenceAdvancedMaterialService(new ReferenceCorpusDatabasePathResolver(options));
        var listed = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Observation),
            CancellationToken.None);
        Assert.Equal(1, listed.Total);
        Assert.Equal("information_delivery", listed.Items[0].FeatureKey);
        Assert.Equal(ReferenceAdvancedMaterialReviewStates.Unverified, listed.Items[0].ReviewState);
    }

    [Fact]
    public async Task SpecimenRequiresBoundaryAndTransfer()
    {
        var options = CreateOptions();
        var ingestion = await SeedAsync(options);

        var result = await ingestion.IngestSpecimensAsync(
            101,
            "run-advm",
            [
                Specimen("technique_kind"),
                Specimen("technique_kind", failureModes: []),
                Specimen("technique_kind", transferTemplate: "   "),
                Specimen("unknown_feature")
            ],
            CancellationToken.None);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(3, result.Rejected);

        var service = new SqliteReferenceAdvancedMaterialService(new ReferenceCorpusDatabasePathResolver(options));
        var specimens = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Specimen),
            CancellationToken.None);
        var detail = await service.GetAsync(
            new GetReferenceAdvancedMaterialDetailPayload(101, specimens.Items[0].MaterialId),
            CancellationToken.None);
        Assert.NotNull(detail!.BoundaryJson);
        Assert.Contains("failure_modes", detail.BoundaryJson, StringComparison.Ordinal);
        Assert.Contains("why_it_works", detail.RationaleJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IngestionIsIdempotentPerSourceRef()
    {
        var options = CreateOptions();
        var ingestion = await SeedAsync(options);

        await ingestion.IngestObservationsAsync(101, "run-advm", [Observation("information_delivery", "drip_fed", 10)], CancellationToken.None);
        await ingestion.IngestObservationsAsync(101, "run-advm", [Observation("information_delivery", "drip_fed", 10)], CancellationToken.None);

        var service = new SqliteReferenceAdvancedMaterialService(new ReferenceCorpusDatabasePathResolver(options));
        var listed = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Observation),
            CancellationToken.None);
        Assert.Equal(1, listed.Total);
    }

    [Fact]
    public async Task SpecimenCopyingSourceTextIsRejected()
    {
        var options = CreateOptions();
        var ingestion = await SeedAsync(options);

        var result = await ingestion.IngestSpecimensAsync(
            101,
            "run-advm",
            [Specimen("technique_kind", transferTemplate: "他推门而入，屋里安静得能听见雨声。")],
            CancellationToken.None);

        Assert.Equal(0, result.Accepted);
        Assert.Equal(1, result.Rejected);
    }
}
