using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceWritingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "novelist-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateBlueprintsUsesActiveMaterialsAndPersistsOnlyTheirIdentities()
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        await SeedActiveMaterialAsync(
            options,
            11,
            "material-a",
            "generation-a",
            "dialogue",
            "First line.\n\nSecond line.");
        await SeedActiveMaterialAsync(
            options,
            12,
            "material-b",
            "generation-b",
            "hook",
            "A final knock.");
        var search = new RecordingMaterialSearch(
        [
            Hit("material-a", "generation-a", 11, "dialogue", "First line.\n\nSecond line.", 0.1),
            Hit("material-b", "generation-b", 12, "hook", "A final knock.", 0.2)
        ]);
        var service = new SqliteReferenceWritingService(options, search);

        var generated = await service.GenerateBlueprintsAsync(
            new GenerateReferenceBlueprintsPayload(
                7,
                3,
                "chapter-7-3",
                "Escalate the conflict and end on a hook.",
                2),
            CancellationToken.None);
        var restored = await service.GetSessionAsync(
            new GetReferenceWritingSessionPayload(7, 3, "chapter-7-3"),
            CancellationToken.None);

        var request = Assert.Single(search.Requests);
        Assert.Equal("project:7:default", request.SessionId);
        Assert.Equal("Escalate the conflict and end on a hook.", request.Query);
        Assert.Equal(2, generated.Blueprints.Count);
        Assert.NotNull(restored);
        Assert.Equal(
            JsonSerializer.Serialize(generated),
            JsonSerializer.Serialize(restored));
        Assert.All(
            generated.Blueprints.SelectMany(blueprint => blueprint.Beats).SelectMany(beat => beat.Materials),
            material =>
            {
                Assert.StartsWith("material-", material.MaterialId, StringComparison.Ordinal);
                Assert.StartsWith("generation-", material.GenerationId, StringComparison.Ordinal);
            });
        var json = JsonSerializer.Serialize(generated);
        Assert.DoesNotContain("node_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("First line.", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDraftsReadsOnlySelectedBlueprintMaterialsFromTheirActiveGenerations()
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        await SeedActiveMaterialAsync(
            options,
            11,
            "material-a",
            "generation-a",
            "dialogue",
            "First line.\n\nSecond line.");
        await SeedActiveMaterialAsync(
            options,
            12,
            "material-b",
            "generation-b",
            "hook",
            "A final knock.");
        var search = new RecordingMaterialSearch(
        [
            Hit("material-a", "generation-a", 11, "dialogue", "First line.\n\nSecond line.", 0.1),
            Hit("material-b", "generation-b", 12, "hook", "A final knock.", 0.2)
        ]);
        var service = new SqliteReferenceWritingService(options, search);
        var session = await service.GenerateBlueprintsAsync(
            new GenerateReferenceBlueprintsPayload(7, 3, "chapter-7-3", "Escalate the conflict.", 1),
            CancellationToken.None);
        var blueprint = Assert.Single(session.Blueprints);
        await service.SelectBlueprintAsync(
            new SelectReferenceBlueprintPayload(7, 3, session.SessionId, blueprint.BlueprintId),
            CancellationToken.None);

        var drafts = await service.GenerateDraftCandidatesAsync(
            new GenerateReferenceDraftCandidatesPayload(
                7,
                3,
                session.SessionId,
                blueprint.BlueprintId,
                "Before\nAfter",
                6,
                new Dictionary<string, string>(),
                2),
            CancellationToken.None);

        Assert.Equal(2, drafts.Candidates.Count);
        Assert.All(drafts.Candidates, candidate =>
        {
            Assert.True(candidate.Audit.Passed);
            var source = Assert.Single(candidate.Sources);
            Assert.Contains(
                blueprint.Beats.SelectMany(beat => beat.Materials),
                material => material.MaterialId == source.MaterialId &&
                    material.GenerationId == source.GenerationId);
            Assert.Contains(candidate.Text, candidate.ChapterTextAfterInsertion, StringComparison.Ordinal);
        });
        var json = JsonSerializer.Serialize(drafts);
        Assert.DoesNotContain("node_id", json, StringComparison.Ordinal);
        Assert.Contains("material_id", json, StringComparison.Ordinal);
        Assert.Contains("generation_id", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSessionRejectsAStaleMaterialGeneration()
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        await SeedActiveMaterialAsync(
            options,
            11,
            "material-a",
            "generation-a",
            "dialogue",
            "First line.\n\nSecond line.");
        var service = new SqliteReferenceWritingService(
            options,
            new RecordingMaterialSearch(
            [
                Hit("material-a", "generation-a", 11, "dialogue", "First line.\n\nSecond line.", 0.1)
            ]));
        await service.GenerateBlueprintsAsync(
            new GenerateReferenceBlueprintsPayload(7, 3, "chapter-7-3", "Escalate the conflict.", 1),
            CancellationToken.None);
        await SetActiveGenerationAsync(options, 11, "generation-new");

        var exception = await Assert.ThrowsAsync<ReferenceWritingException>(async () =>
            await service.GetSessionAsync(
                new GetReferenceWritingSessionPayload(7, 3, "chapter-7-3"),
                CancellationToken.None));

        Assert.Equal(ReferenceWritingErrorCodes.BlueprintStale, exception.ErrorCode);
    }

    [Fact]
    public async Task GetSessionRejectsAMissingBlueprintMaterial()
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        await SeedActiveMaterialAsync(
            options,
            11,
            "material-a",
            "generation-a",
            "dialogue",
            "First line.\n\nSecond line.");
        var service = new SqliteReferenceWritingService(
            options,
            new RecordingMaterialSearch(
            [
                Hit("material-a", "generation-a", 11, "dialogue", "First line.\n\nSecond line.", 0.1)
            ]));
        await service.GenerateBlueprintsAsync(
            new GenerateReferenceBlueprintsPayload(7, 3, "chapter-7-3", "Escalate the conflict.", 1),
            CancellationToken.None);
        await ExecuteMaterialCommandAsync(
            options,
            "DELETE FROM reference_materialization_materials WHERE material_id = 'material-a';");

        var exception = await Assert.ThrowsAsync<ReferenceWritingException>(async () =>
            await service.GetSessionAsync(
                new GetReferenceWritingSessionPayload(7, 3, "chapter-7-3"),
                CancellationToken.None));

        Assert.Equal(ReferenceWritingErrorCodes.MaterialMissing, exception.ErrorCode);
    }

    [Fact]
    public async Task GetSessionRejectsMaterialTextThatDoesNotMatchItsFrozenHash()
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        await SeedActiveMaterialAsync(
            options,
            11,
            "material-a",
            "generation-a",
            "dialogue",
            "First line.\n\nSecond line.");
        var service = new SqliteReferenceWritingService(
            options,
            new RecordingMaterialSearch(
            [
                Hit("material-a", "generation-a", 11, "dialogue", "First line.\n\nSecond line.", 0.1)
            ]));
        await service.GenerateBlueprintsAsync(
            new GenerateReferenceBlueprintsPayload(7, 3, "chapter-7-3", "Escalate the conflict.", 1),
            CancellationToken.None);
        await ExecuteMaterialCommandAsync(
            options,
            "UPDATE reference_materialization_materials SET text = 'tampered' WHERE material_id = 'material-a';");

        var exception = await Assert.ThrowsAsync<ReferenceWritingException>(async () =>
            await service.GetSessionAsync(
                new GetReferenceWritingSessionPayload(7, 3, "chapter-7-3"),
                CancellationToken.None));

        Assert.Equal(ReferenceWritingErrorCodes.MaterialTextMismatch, exception.ErrorCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions() => new()
    {
        ConfigDirectory = Path.Combine(_root, "config"),
        DefaultDataDirectory = Path.Combine(_root, "data"),
        EnableLegacyMigration = false
    };

    private static ReferenceMaterialSearchHit Hit(
        string materialId,
        string generationId,
        long anchorId,
        string materialType,
        string text,
        double distance) => new(
            materialId,
            generationId,
            anchorId,
            1,
            0,
            materialType,
            text,
            "Useful for the requested beat.",
            [materialType],
            Hash(text),
            distance);

    private static async ValueTask SeedActiveMaterialAsync(
        AppInitializationOptions options,
        long anchorId,
        string materialId,
        string generationId,
        string materialType,
        string text)
    {
        var databasePath = await new ReferenceCorpusDatabasePathResolver(options).ResolveAsync(CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_corpus_libraries (
              library_id, scope, novel_id, name, created_at)
            VALUES ('project-7', 'project', 7, 'Project references', $now);

            INSERT INTO reference_anchors (
              anchor_id, novel_id, title, author, source_path, source_kind,
              license_status, source_file_hash, build_version, status, created_at, updated_at)
            VALUES (
              $anchor_id, 7, $title, '', $source_path, 'file',
              'authorized', $source_hash, 'test', 'ready', $now, $now);

            INSERT INTO reference_library_members (library_id, anchor_id, enabled)
            VALUES ('project-7', $anchor_id, 1);

            INSERT OR IGNORE INTO reference_session_library_binding (session_id, library_id)
            VALUES ('project:7:default', 'project-7');

            INSERT INTO reference_source_license (
              anchor_id, license_state, reuse_policy, cleared_for_insertion, reviewed_at)
            VALUES ($anchor_id, 'authorized', 'verbatim_ok', 1, $now);

            INSERT INTO reference_chapter_split_profiles (
              split_profile_id, anchor_id, source_hash, split_mode, sample_char_count,
              sample_hash, pattern_kind, delimiter_template, pattern_json,
              status, chapter_count, created_at, confirmed_at)
            VALUES (
              $profile_id, $anchor_id, $source_hash, 'manual', 1,
              $source_hash, 'manual', '', '{}', 'confirmed', 1, $now, $now);

            INSERT INTO reference_materialization_runs (
              run_id, anchor_id, split_profile_id, generation_id,
              policy_version, extractor_schema_version,
              model_provider, model_id, embedding_provider, embedding_model_id,
              embedding_dimensions, status, chapter_batch_size, total_chapters,
              processed_chapters, material_count, vector_count, started_at, completed_at, activated_at)
            VALUES (
              $run_id, $anchor_id, $profile_id, $generation_id,
              'test', 'test', 'test', 'test', 'test', 'test',
              3, 'completed', 5, 1, 1, 1, 1, $now, $now, $now);

            INSERT INTO reference_anchor_materialization_state (
              anchor_id, active_generation_id, updated_at)
            VALUES ($anchor_id, $generation_id, $now);

            INSERT INTO reference_materialization_materials (
              material_id, generation_id, run_id, anchor_id, chapter_index, ordinal,
              material_type, text, description, tags_json, text_hash, created_at)
            VALUES (
              $material_id, $generation_id, $run_id, $anchor_id, 1, 0,
              $material_type, $text, 'Useful for the requested beat.', '[]', $text_hash, $now);
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$title", "Reference " + anchorId);
        command.Parameters.AddWithValue("$source_path", $"reference-{anchorId}.txt");
        command.Parameters.AddWithValue("$source_hash", Hash("source-" + anchorId));
        command.Parameters.AddWithValue("$profile_id", "profile-" + anchorId);
        command.Parameters.AddWithValue("$run_id", "run-" + anchorId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        command.Parameters.AddWithValue("$material_id", materialId);
        command.Parameters.AddWithValue("$material_type", materialType);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$text_hash", Hash(text));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async ValueTask SetActiveGenerationAsync(
        AppInitializationOptions options,
        long anchorId,
        string generationId)
    {
        var databasePath = await new ReferenceCorpusDatabasePathResolver(options).ResolveAsync(CancellationToken.None);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_anchor_materialization_state
            SET active_generation_id = $generation_id,
                updated_at = $updated_at
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$generation_id", generationId);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async ValueTask ExecuteMaterialCommandAsync(
        AppInitializationOptions options,
        string sql)
    {
        var databasePath = await new ReferenceCorpusDatabasePathResolver(options).ResolveAsync(CancellationToken.None);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RecordingMaterialSearch(IReadOnlyList<ReferenceMaterialSearchHit> hits)
        : IReferenceMaterialSearch
    {
        public List<ReferenceMaterialSearchRequest> Requests { get; } = [];

        public ValueTask<ReferenceMaterialListPage> ListAsync(
            ReferenceMaterialListRequest input,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test search only supports vector queries.");

        public ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchAsync(
            ReferenceMaterialSearchRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(input);
            return ValueTask.FromResult(hits);
        }
    }
}
