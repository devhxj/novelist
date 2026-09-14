using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

// 生产侧明细表 → 统一外壳表：family 映射、证据校验与作废计数。
public sealed class ReferenceAdvancedMaterialProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"novelist-advm-projection-{Guid.NewGuid():N}");

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

    private async Task<SqliteReferenceAdvancedMaterialProjectionService> SeedAsync(AppInitializationOptions options)
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
              (101, 42, '投影参考', 'fixture', 'rain.md', 'markdown', 'user_provided',
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
              ('run-1', 101, 'a1', 's1', 'p', 'm', 'anchor', 'completed', NULL, 0, NULL,
               '2026-09-14T00:00:00Z', '2026-09-14T00:05:00Z', 2, '[]');

            INSERT INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ('obs-valid', 'node-1', 'scene', 'run-1', 101, 'narrative', 'narrative_function',
               'enum', 'reveal', NULL, NULL, '{"function":"reveal"}', NULL, 0.8,
               0, 10, '门口动作后接环境回响。', 'unverified', 'active', NULL, '2026-09-14T00:00:00Z'),
              ('obs-invalid', 'node-1', 'scene', 'run-1', 101, 'sensory', 'senses',
               'array', 'hearing', NULL, NULL, NULL, NULL, 0.6,
               0, 999, '越界证据。', 'unverified', 'active', NULL, '2026-09-14T00:00:01Z');

            INSERT INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ('spec-1', 'node-1', 101, 'run-1', 'sensory_detail',
               '用听觉细节压住场景', '推门后安静', '主体[动作]后接[感官回响]', '{"action":"推门"}',
               '读者感到压迫', '密闭空间', '描写过长', '堆砌感官',
               '依赖雨夜设定', '["先动作后感官更贴近视角"]', 0.75, 'unverified',
               'active', NULL, NULL, '2026-09-14T00:00:02Z');
            """;
        await command.ExecuteNonQueryAsync();

        return new SqliteReferenceAdvancedMaterialProjectionService(new ReferenceCorpusDatabasePathResolver(options));
    }

    [Fact]
    public async Task ProjectsLegacyRowsAndCountsInvalidEvidence()
    {
        var options = CreateOptions();
        var projection = await SeedAsync(options);

        var result = await projection.ProjectAnchorAsync(101, CancellationToken.None);
        Assert.Equal(1, result.ObservationCount);
        Assert.Equal(1, result.SpecimenCount);
        Assert.Equal(1, result.InvalidEvidenceCount);

        var service = new SqliteReferenceAdvancedMaterialService(new ReferenceCorpusDatabasePathResolver(options));
        var observations = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Observation),
            CancellationToken.None);
        Assert.Equal(1, observations.Total);
        Assert.Equal(ReferenceAdvancedMaterialFamilies.Craft, observations.Items[0].Family);
        Assert.Equal("narrative_function", observations.Items[0].FeatureKey);

        var specimens = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Specimen),
            CancellationToken.None);
        Assert.Equal(1, specimens.Total);
        Assert.Equal(ReferenceAdvancedMaterialFamilies.Technique, specimens.Items[0].Family);
    }

    [Fact]
    public async Task ProjectionIsIdempotent()
    {
        var options = CreateOptions();
        var projection = await SeedAsync(options);

        await projection.ProjectAnchorAsync(101, CancellationToken.None);
        await projection.ProjectAnchorAsync(101, CancellationToken.None);

        var service = new SqliteReferenceAdvancedMaterialService(new ReferenceCorpusDatabasePathResolver(options));
        var all = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101),
            CancellationToken.None);
        Assert.Equal(2, all.Total);
    }

    [Fact]
    public async Task ProjectedSpecimenCarriesRationaleBoundaryAndEvidence()
    {
        var options = CreateOptions();
        var projection = await SeedAsync(options);
        await projection.ProjectAnchorAsync(101, CancellationToken.None);

        var service = new SqliteReferenceAdvancedMaterialService(new ReferenceCorpusDatabasePathResolver(options));
        var specimens = await service.ListAsync(
            new ListReferenceAdvancedMaterialsPayload(101, Layer: ReferenceAdvancedMaterialLayers.Specimen),
            CancellationToken.None);
        var detail = await service.GetAsync(
            new GetReferenceAdvancedMaterialDetailPayload(101, specimens.Items[0].MaterialId),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.RationaleJson);
        Assert.NotNull(detail.BoundaryJson);
        Assert.Contains("why_it_works", detail.RationaleJson, StringComparison.Ordinal);
        Assert.Contains("failure_modes", detail.BoundaryJson, StringComparison.Ordinal);
        Assert.Equal("主体[动作]后接[感官回响]", detail.TransferTemplate);
        Assert.Single(detail.Evidence);
        Assert.Equal(0, detail.Evidence[0].StartOffset);
        Assert.Equal(17, detail.Evidence[0].EndOffset);
        Assert.Equal("他推门而入，屋里安静得能听见雨声。", detail.Evidence[0].Text);
    }
}
