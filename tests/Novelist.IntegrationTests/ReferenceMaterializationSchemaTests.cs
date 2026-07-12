using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ChapterSplitProvisioningCreatesTheAdditiveMaterializationTablesAndIndexes()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);

        var tables = await ReadNamesAsync(options, "table");
        Assert.Contains("reference_materialization_runs", tables);
        Assert.Contains("reference_materialization_chapter_progress", tables);
        Assert.Contains("reference_material_candidates", tables);
        Assert.Contains("reference_material_candidate_nodes", tables);
        Assert.Contains("reference_anchor_materialization_state", tables);

        var runIndexes = await ReadIndexesAsync(options, "reference_materialization_runs");
        Assert.Contains("ux_reference_materialization_runs_generation", runIndexes);
        Assert.Contains("idx_reference_materialization_runs_anchor_status", runIndexes);
        var candidateIndexes = await ReadIndexesAsync(options, "reference_material_candidates");
        Assert.Contains("ux_reference_material_candidates_run_key", candidateIndexes);
    }

    [Fact]
    public async Task MaterializationRunSchemaRejectsUnsupportedBatchSizesAndDuplicateGenerationKeys()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);

        await InsertRunAsync(options, anchor.AnchorId, profile.SplitProfileId, "run-1", "generation-1", chapterBatchSize: 5);
        var invalidBatch = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertRunAsync(options, anchor.AnchorId, profile.SplitProfileId, "run-invalid", "generation-invalid", chapterBatchSize: 7).AsTask());
        Assert.Equal(19, invalidBatch.SqliteErrorCode);

        var duplicateGeneration = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertRunAsync(options, anchor.AnchorId, profile.SplitProfileId, "run-duplicate", "generation-1", chapterBatchSize: 10).AsTask());
        Assert.Equal(19, duplicateGeneration.SqliteErrorCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(AppInitializationOptions options)
    {
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料化 schema", "", ""), CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "schema.md");
        await File.WriteAllTextAsync(sourcePath, "# 第一章\n\n雨声压住窗沿。\n\n# 第二章\n\n门外响起第三次敲门。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        return await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "schema 来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
    }

    private static async ValueTask InsertRunAsync(
        AppInitializationOptions options,
        long anchorId,
        string profileId,
        string runId,
        string generationId,
        int chapterBatchSize)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_materialization_runs (
              run_id, anchor_id, split_profile_id, generation_id, policy_version, candidate_version, qualifier_version,
              model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
              status, chapter_batch_size, total_chapters, total_chapter_batches, started_at)
            VALUES (
              $run_id, $anchor_id, $split_profile_id, $generation_id, 'policy-v1', 'candidate-v1', 'qualifier-v1',
              'provider', 'model', 'embedding-provider', 'embedding-model', 8,
              'queued', $chapter_batch_size, 2, 1, '2026-07-12T00:00:00.0000000Z');
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$split_profile_id", profileId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        command.Parameters.AddWithValue("$chapter_batch_size", chapterBatchSize);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async ValueTask<IReadOnlySet<string>> ReadNamesAsync(AppInitializationOptions options, string type)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async ValueTask<IReadOnlySet<string>> ReadIndexesAsync(AppInitializationOptions options, string tableName)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({tableName});";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(AppInitializationOptions options)
    {
        var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data"),
            EnableLegacyMigration = false
        };
    }

    private sealed class EmptyChapterSplitAnalyzer : Novelist.Core.App.IReferenceChapterSplitAnalyzer
    {
        public ValueTask<Novelist.Core.App.ReferenceChapterSplitModelResult> AnalyzeAsync(
            Novelist.Core.App.ReferenceChapterSplitModelRequest input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Novelist.Core.App.ReferenceChapterSplitModelResult.Empty);
        }
    }
}
