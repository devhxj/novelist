using System.Globalization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly object _schemaInitializationGate = new();
    private Task<string>? _schemaInitialization;

    public SqliteReferenceMaterializationRunStore(
        IReferenceCorpusDatabasePathResolver databasePathResolver)
    {
        _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
    }

    public async ValueTask<ReferenceMaterializationStatusPayload> CreateAsync(
        ReferenceMaterializationRunSeed seed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ValidateSeed(seed);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var profile = await ReadConfirmedProfileAsync(connection, transaction, seed.AnchorId, seed.SplitProfileId, cancellationToken)
            ?? throw new InvalidOperationException("Reference materialization requires a confirmed chapter split profile.");
        var boundaries = await ReadBoundariesAsync(connection, transaction, seed.SplitProfileId, cancellationToken);
        if (boundaries.Count != profile.ChapterCount || boundaries.Count == 0)
        {
            throw new InvalidOperationException("Confirmed chapter split profile is incomplete.");
        }

        if (await HasActiveRunAsync(connection, transaction, seed.AnchorId, cancellationToken))
        {
            throw new InvalidOperationException("Reference source already has an active materialization run.");
        }

        await InsertRunAsync(connection, transaction, seed, boundaries.Count, cancellationToken);
        await UpsertAnchorStateAsync(connection, transaction, seed.AnchorId, cancellationToken);
        await InsertChapterProgressAsync(connection, transaction, seed.RunId, boundaries, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CreateQueuedStatus(seed, boundaries.Count);
    }

    public async ValueTask<ReferenceMaterializationStatusPayload?> GetAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, anchor_id, split_profile_id, generation_id, status,
                   total_chapters, processed_chapters, current_chapter_index,
                   material_count, vector_count,
                   (SELECT COALESCE(SUM(progress.model_call_count), 0)
                    FROM reference_materialization_chapter_progress progress
                    WHERE progress.run_id = reference_materialization_runs.run_id) AS model_call_count,
                   model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
                   last_error_code, last_error_message, started_at, completed_at,
                   EXISTS(
                     SELECT 1
                     FROM reference_materialization_vector_indexes vector_index
                     JOIN reference_anchor_materialization_state active_state
                       ON active_state.anchor_id = reference_materialization_runs.anchor_id
                      AND active_state.active_generation_id = reference_materialization_runs.generation_id
                     WHERE vector_index.run_id = reference_materialization_runs.run_id
                       AND vector_index.status = 'ready'
                       AND vector_index.vector_count = reference_materialization_runs.vector_count
                   ) AS vector_index_healthy
            FROM reference_materialization_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", NormalizeRunId(runId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStatus(reader) : null;
    }

    public async ValueTask<ReferenceMaterializationStatusPayload?> GetLatestForAnchorAsync(
        long anchorId,
        CancellationToken cancellationToken)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId), "Anchor id must be positive.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, anchor_id, split_profile_id, generation_id, status,
                   total_chapters, processed_chapters, current_chapter_index,
                   material_count, vector_count,
                   (SELECT COALESCE(SUM(progress.model_call_count), 0)
                    FROM reference_materialization_chapter_progress progress
                    WHERE progress.run_id = reference_materialization_runs.run_id) AS model_call_count,
                   model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
                   last_error_code, last_error_message, started_at, completed_at,
                   EXISTS(
                     SELECT 1
                     FROM reference_materialization_vector_indexes vector_index
                     JOIN reference_anchor_materialization_state active_state
                       ON active_state.anchor_id = reference_materialization_runs.anchor_id
                      AND active_state.active_generation_id = reference_materialization_runs.generation_id
                     WHERE vector_index.run_id = reference_materialization_runs.run_id
                       AND vector_index.status = 'ready'
                       AND vector_index.vector_count = reference_materialization_runs.vector_count
                   ) AS vector_index_healthy
            FROM reference_materialization_runs
            WHERE anchor_id = $anchor_id
            ORDER BY started_at DESC, run_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStatus(reader) : null;
    }

    public async ValueTask<PageResultPayload<ReferenceMaterializationChapterProgressPayload>> ListChapterProgressAsync(
        string runId,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        if (page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be positive.");
        }

        if (size is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Page size must be between 1 and 100.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var total = await CountChapterProgressAsync(connection, normalizedRunId, cancellationToken);
        var offset = checked((page - 1) * size);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chapter_index, status, material_count, vector_count,
                   model_call_count, started_at, completed_at, last_error_code, last_error_message
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
            ORDER BY chapter_index ASC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$limit", size);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ReferenceMaterializationChapterProgressPayload>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ReferenceMaterializationChapterProgressPayload(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
        return new PageResultPayload<ReferenceMaterializationChapterProgressPayload>(items, total, page, size, totalPages);
    }

    private async ValueTask<string> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        Task<string> initialization;
        lock (_schemaInitializationGate)
        {
            initialization = _schemaInitialization ??= EnsureSchemaCoreAsync();
        }

        try
        {
            var databasePath = await initialization;
            cancellationToken.ThrowIfCancellationRequested();
            return databasePath;
        }
        catch when (initialization.IsFaulted)
        {
            lock (_schemaInitializationGate)
            {
                if (ReferenceEquals(_schemaInitialization, initialization))
                {
                    _schemaInitialization = null;
                }
            }

            throw;
        }
    }

    private async Task<string> EnsureSchemaCoreAsync()
    {
        var databasePath = await _databasePathResolver.ResolveAsync(CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, CancellationToken.None);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        return databasePath;
    }

    private static async ValueTask<ConfirmedProfile?> ReadConfirmedProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        string splitProfileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT split_profile_id, chapter_count
            FROM reference_chapter_split_profiles
            WHERE split_profile_id = $split_profile_id
              AND anchor_id = $anchor_id
              AND status = $status;
            """;
        command.Parameters.AddWithValue("$split_profile_id", splitProfileId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$status", ReferenceChapterSplitProfileStates.Confirmed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ConfirmedProfile(reader.GetString(0), reader.GetInt32(1))
            : null;
    }

    private static async ValueTask<IReadOnlyList<ChapterBoundary>> ReadBoundariesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string splitProfileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT chapter_index, text_hash
            FROM reference_chapter_split_boundaries
            WHERE split_profile_id = $split_profile_id
            ORDER BY chapter_index ASC;
            """;
        command.Parameters.AddWithValue("$split_profile_id", splitProfileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var boundaries = new List<ChapterBoundary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            boundaries.Add(new ChapterBoundary(reader.GetInt32(0), reader.GetString(1)));
        }

        return boundaries;
    }

    private static async ValueTask<bool> HasActiveRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1
              FROM reference_materialization_runs
              WHERE anchor_id = $anchor_id
                AND status IN ($queued, $extracting, $embedding, $indexing)
            );
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
        command.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
        command.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
        command.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static async ValueTask InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceMaterializationRunSeed seed,
        int totalChapters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_runs (
              run_id, anchor_id, split_profile_id, generation_id, policy_version, extractor_schema_version,
              model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
              status, total_chapters, current_chapter_index, started_at)
            VALUES (
              $run_id, $anchor_id, $split_profile_id, $generation_id, $policy_version, $extractor_schema_version,
              $model_provider, $model_id, $embedding_provider, $embedding_model_id, $embedding_dimensions,
              $status, $total_chapters, 1, $started_at);
            """;
        command.Parameters.AddWithValue("$run_id", seed.RunId);
        command.Parameters.AddWithValue("$anchor_id", seed.AnchorId);
        command.Parameters.AddWithValue("$split_profile_id", seed.SplitProfileId);
        command.Parameters.AddWithValue("$generation_id", seed.GenerationId);
        command.Parameters.AddWithValue("$policy_version", seed.PolicyVersion);
        command.Parameters.AddWithValue("$extractor_schema_version", seed.ExtractorSchemaVersion);
        command.Parameters.AddWithValue("$model_provider", seed.Llm.Provider);
        command.Parameters.AddWithValue("$model_id", seed.Llm.ModelId);
        command.Parameters.AddWithValue("$embedding_provider", seed.Embedding.Provider);
        command.Parameters.AddWithValue("$embedding_model_id", seed.Embedding.ModelId);
        command.Parameters.AddWithValue("$embedding_dimensions", seed.Embedding.Dimensions!.Value);
        command.Parameters.AddWithValue("$status", ReferenceMaterializationRunStates.Queued);
        command.Parameters.AddWithValue("$total_chapters", totalChapters);
        command.Parameters.AddWithValue("$started_at", FormatTimestamp(seed.StartedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask UpsertAnchorStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_anchor_materialization_state (anchor_id, active_generation_id)
            VALUES ($anchor_id, NULL)
            ON CONFLICT(anchor_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertChapterProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        IReadOnlyList<ChapterBoundary> boundaries,
        CancellationToken cancellationToken)
    {
        foreach (var boundary in boundaries)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_materialization_chapter_progress (
                  run_id, chapter_index, status)
                VALUES (
                  $run_id, $chapter_index, $status);
                """;
            command.Parameters.AddWithValue("$run_id", runId);
            command.Parameters.AddWithValue("$chapter_index", boundary.ChapterIndex);
            command.Parameters.AddWithValue("$status", ReferenceMaterializationChapterStates.Pending);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask<int> CountChapterProgressAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_materialization_chapter_progress WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static ReferenceMaterializationStatusPayload CreateQueuedStatus(
        ReferenceMaterializationRunSeed seed,
        int totalChapters)
    {
        return new ReferenceMaterializationStatusPayload(
            seed.RunId,
            seed.AnchorId,
            seed.SplitProfileId,
            seed.GenerationId,
            ReferenceMaterializationRunStates.Queued,
            totalChapters,
            0,
            1,
            0,
            0,
            0,
            seed.Llm,
            seed.Embedding,
            null,
            null,
            seed.StartedAt,
            null,
            false);
    }

    private static ReferenceMaterializationStatusPayload ReadStatus(SqliteDataReader reader)
    {
        var status = reader.GetString(4);
        return new ReferenceMaterializationStatusPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            status,
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            new ReferenceMaterializationModelIdentityPayload(reader.GetString(11), reader.GetString(12)),
            new ReferenceMaterializationModelIdentityPayload(reader.GetString(13), reader.GetString(14), reader.GetInt32(15)),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            ParseTimestamp(reader.GetString(18)),
            reader.IsDBNull(19) ? null : ParseTimestamp(reader.GetString(19)),
            reader.GetInt64(20) != 0);
    }

    private static void ValidateSeed(ReferenceMaterializationRunSeed seed)
    {
        if (seed.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seed), "Anchor id must be positive.");
        }

        Require(seed.RunId, nameof(seed.RunId));
        Require(seed.SplitProfileId, nameof(seed.SplitProfileId));
        Require(seed.GenerationId, nameof(seed.GenerationId));
        Require(seed.PolicyVersion, nameof(seed.PolicyVersion));
        Require(seed.ExtractorSchemaVersion, nameof(seed.ExtractorSchemaVersion));
        Require(seed.Llm.Provider, nameof(seed.Llm.Provider));
        Require(seed.Llm.ModelId, nameof(seed.Llm.ModelId));
        Require(seed.Embedding.Provider, nameof(seed.Embedding.Provider));
        Require(seed.Embedding.ModelId, nameof(seed.Embedding.ModelId));
        if (seed.Embedding.Dimensions is not > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seed), "Embedding dimensions must be positive.");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ArgumentException("Materialization run value is required and bounded.", name);
        }
    }

    private static string NormalizeRunId(string value)
    {
        var runId = value?.Trim() ?? string.Empty;
        if (runId.Length is 0 or > 128)
        {
            throw new ArgumentException("Materialization run id is required.", nameof(value));
        }

        return runId;
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private sealed record ConfirmedProfile(string SplitProfileId, int ChapterCount);
    private sealed record ChapterBoundary(int ChapterIndex, string TextHash);
}

internal sealed record ReferenceMaterializationRunSeed(
    string RunId,
    long AnchorId,
    string SplitProfileId,
    string GenerationId,
    string PolicyVersion,
    string ExtractorSchemaVersion,
    ReferenceMaterializationModelIdentityPayload Llm,
    ReferenceMaterializationModelIdentityPayload Embedding,
    DateTimeOffset StartedAt);
