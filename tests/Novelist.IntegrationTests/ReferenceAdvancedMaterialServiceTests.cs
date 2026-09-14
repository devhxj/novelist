using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

// 高级写作素材（L2）统一外壳表：List/Get/Review 三个只读 + 复核行为。
public sealed class ReferenceAdvancedMaterialServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"novelist-advanced-materials-{Guid.NewGuid():N}");

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

    private static string ReferenceDatabasePath(AppInitializationOptions options) =>
        Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");

    private async Task<SqliteReferenceAdvancedMaterialService> SeedAsync(AppInitializationOptions options)
    {
        Directory.CreateDirectory(options.DefaultDataDirectory);
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);

        var databasePath = ReferenceDatabasePath(options);
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
              (101, 42, '雨门参考', 'fixture', 'rain.md', 'markdown', 'user_provided',
               'hash-101', 'v1', 'ready', '2026-09-14T00:00:00Z', '2026-09-14T00:00:00Z', 'private');

            INSERT INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth, chapter_index,
               start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-1', 101, NULL, 'scene', 1, 1, 1, 0, 24, 24, 'text-hash-1', '他推门而入，屋里安静得能听见雨声。', '2026-09-14T00:00:00Z');

            INSERT INTO reference_advanced_materials
              (material_id, anchor_id, layer, family, feature_key, source_ref,
               value_text, value_json, rationale_json, boundary_json, transfer_template,
               transfer_slots_json, evidence_refs_json, confidence, review_state, validity_state,
               superseded_by_run_id, analysis_run_id, extractor_version, created_at, updated_at)
            VALUES
              ('adv-craft-1', 101, 'specimen', 'craft', 'information_delivery', 'obs-1',
               '推门后用环境音交代安静', NULL,
               '{"why_it_works":["先给动作，再让环境回响"]}', '{"failure_modes":["环境描写过长"]}',
               '主体[动作]后接[环境回响]，用于[压低信息投放节奏]', '{"action":"推门","environment":"雨声"}',
               '[{"node_id":"node-1","material_id":null,"start_offset":0,"end_offset":10}]',
               0.8, 'unverified', 'active', NULL, NULL, 'test-v1',
               '2026-09-14T00:00:00Z', '2026-09-14T00:00:00Z'),
              ('adv-world-1', 101, 'strategy', 'world', 'world_introduction', '',
               '设定在动作里顺带交代', NULL, NULL, NULL, NULL, NULL,
               '[{"node_id":"node-1","material_id":null,"start_offset":0,"end_offset":24}]',
               0.7, 'confirmed', 'active', NULL, NULL, 'test-v1',
               '2026-09-14T00:01:00Z', '2026-09-14T00:01:00Z'),
              ('adv-tech-1', 101, 'observation', 'technique', 'sensory_detail', 'obs-2',
               '听觉细节', NULL, NULL, NULL, NULL, NULL,
               '[{"node_id":"node-1","material_id":null,"start_offset":9,"end_offset":24}]',
               0.6, 'rejected', 'active', NULL, NULL, 'test-v1',
               '2026-09-14T00:02:00Z', '2026-09-14T00:02:00Z');
            """;
        await command.ExecuteNonQueryAsync();

        return new SqliteReferenceAdvancedMaterialService(new ReferenceCorpusDatabasePathResolver(options));
    }

    [Fact]
    public async Task ListFiltersByFamilyLayerAndReviewState()
    {
        var options = CreateOptions();
        var service = await SeedAsync(options);

        var all = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101),
            CancellationToken.None);
        Assert.Equal(3, all.Total);
        Assert.Equal(3, all.Items.Count);
        Assert.Equal("adv-tech-1", all.Items[0].MaterialId);

        var craftOnly = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Family: "craft"),
            CancellationToken.None);
        Assert.Equal(1, craftOnly.Total);
        Assert.Equal("craft", craftOnly.Items[0].Family);

        var confirmed = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, ReviewState: ReferenceAdvancedMaterialReviewStates.Confirmed),
            CancellationToken.None);
        Assert.Equal(1, confirmed.Total);
        Assert.Equal("adv-world-1", confirmed.Items[0].MaterialId);

        var specimens = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Specimen),
            CancellationToken.None);
        Assert.Equal(1, specimens.Total);
    }

    [Fact]
    public async Task GetResolvesEvidenceTextFromSourceNode()
    {
        var options = CreateOptions();
        var service = await SeedAsync(options);

        var detail = await service.GetAsync(
            new GetReferenceAdvancedMaterialDetailPayload(101, "adv-craft-1"),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("craft", detail!.Family);
        Assert.Equal("specimen", detail.Layer);
        Assert.Single(detail.Evidence);
        Assert.Equal("他推门而入，屋里安静", detail.Evidence[0].Text);
        Assert.Contains("why_it_works", detail.RationaleJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReturnsNullForUnknownMaterial()
    {
        var options = CreateOptions();
        var service = await SeedAsync(options);

        var detail = await service.GetAsync(
            new GetReferenceAdvancedMaterialDetailPayload(101, "missing"),
            CancellationToken.None);

        Assert.Null(detail);
    }

    [Fact]
    public async Task ReviewTransitionsReviewState()
    {
        var options = CreateOptions();
        var service = await SeedAsync(options);

        var result = await service.ReviewAsync(
            new ReviewReferenceAdvancedMaterialPayload(
                101,
                "adv-craft-1",
                ReferenceAdvancedMaterialReviewDecisions.Confirm),
            CancellationToken.None);
        Assert.Equal(ReferenceAdvancedMaterialReviewStates.Confirmed, result.ReviewState);

        var detail = await service.GetAsync(
            new GetReferenceAdvancedMaterialDetailPayload(101, "adv-craft-1"),
            CancellationToken.None);
        Assert.Equal(ReferenceAdvancedMaterialReviewStates.Confirmed, detail!.ReviewState);
    }

    [Fact]
    public async Task ListRejectsUnknownFamily()
    {
        var options = CreateOptions();
        var service = await SeedAsync(options);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ListAsync(
                new ListReferenceAdvancedMaterialsPayload(101, Family: "not_a_family"),
                CancellationToken.None));
    }
}
