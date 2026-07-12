using System.Globalization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly ReferenceCandidateWindowBuilder _candidateWindowBuilder;
    private readonly object _schemaInitializationGate = new();
    private Task<string>? _schemaInitialization;

    public SqliteReferenceMaterializationRunStore(
        IReferenceCorpusDatabasePathResolver databasePathResolver,
        ReferenceCandidateWindowBuilder? candidateWindowBuilder = null)
    {
        _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
        _candidateWindowBuilder = candidateWindowBuilder ?? new ReferenceCandidateWindowBuilder();
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

        var totalBatches = (boundaries.Count + seed.ChapterBatchSize - 1) / seed.ChapterBatchSize;
        await InsertRunAsync(connection, transaction, seed, boundaries.Count, totalBatches, cancellationToken);
        await UpsertAnchorStateAsync(connection, transaction, seed.AnchorId, seed.StartedAt, cancellationToken);
        await InsertChapterProgressAsync(connection, transaction, seed.RunId, boundaries, seed.ChapterBatchSize, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CreateQueuedStatus(seed, boundaries.Count, totalBatches);
    }

    public async ValueTask<ReferenceMaterializationStatusPayload?> GetAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, anchor_id, split_profile_id, generation_id, status, chapter_batch_size,
                   total_chapters, processed_chapters, total_chapter_batches, completed_chapter_batches,
                   current_batch_index, current_batch_start_chapter, current_batch_end_chapter,
                   candidate_count, accepted_count, rejected_count, review_count, vector_count,
                   model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
                   last_error_code, last_error_message, started_at, completed_at,
                   EXISTS(
                     SELECT 1
                     FROM reference_materialization_vector_indexes vector_index
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
            SELECT chapter_index, batch_index, status, current_stage,
                   candidate_count, decided_count, accepted_count, rejected_count, review_count, vector_count,
                   model_call_count, started_at, completed_at, last_error_code, last_error_message, row_version
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
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
                reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12)),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetInt64(15)));
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
            return await initialization.WaitAsync(cancellationToken);
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

    public async ValueTask<string?> GetQualifierVersionAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT qualifier_version
            FROM reference_materialization_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
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
                AND status IN ($queued, $running)
            );
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
        command.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static async ValueTask InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceMaterializationRunSeed seed,
        int totalChapters,
        int totalBatches,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_runs (
              run_id, anchor_id, split_profile_id, generation_id, policy_version, candidate_version, qualifier_version,
              model_provider, model_id, embedding_provider, embedding_model_id, embedding_dimensions,
              status, chapter_batch_size, total_chapters, total_chapter_batches,
              current_batch_index, current_batch_start_chapter, current_batch_end_chapter, started_at)
            VALUES (
              $run_id, $anchor_id, $split_profile_id, $generation_id, $policy_version, $candidate_version, $qualifier_version,
              $model_provider, $model_id, $embedding_provider, $embedding_model_id, $embedding_dimensions,
              $status, $chapter_batch_size, $total_chapters, $total_chapter_batches,
              0, 1, $current_batch_end_chapter, $started_at);
            """;
        command.Parameters.AddWithValue("$run_id", seed.RunId);
        command.Parameters.AddWithValue("$anchor_id", seed.AnchorId);
        command.Parameters.AddWithValue("$split_profile_id", seed.SplitProfileId);
        command.Parameters.AddWithValue("$generation_id", seed.GenerationId);
        command.Parameters.AddWithValue("$policy_version", seed.PolicyVersion);
        command.Parameters.AddWithValue("$candidate_version", seed.CandidateVersion);
        command.Parameters.AddWithValue("$qualifier_version", seed.QualifierVersion);
        command.Parameters.AddWithValue("$model_provider", seed.Llm.Provider);
        command.Parameters.AddWithValue("$model_id", seed.Llm.ModelId);
        command.Parameters.AddWithValue("$embedding_provider", seed.Embedding.Provider);
        command.Parameters.AddWithValue("$embedding_model_id", seed.Embedding.ModelId);
        command.Parameters.AddWithValue("$embedding_dimensions", seed.Embedding.Dimensions!.Value);
        command.Parameters.AddWithValue("$status", ReferenceMaterializationRunStates.Queued);
        command.Parameters.AddWithValue("$chapter_batch_size", seed.ChapterBatchSize);
        command.Parameters.AddWithValue("$total_chapters", totalChapters);
        command.Parameters.AddWithValue("$total_chapter_batches", totalBatches);
        command.Parameters.AddWithValue("$current_batch_end_chapter", Math.Min(seed.ChapterBatchSize, totalChapters));
        command.Parameters.AddWithValue("$started_at", FormatTimestamp(seed.StartedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask UpsertAnchorStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_anchor_materialization_state (anchor_id, active_generation_id, previous_generation_id, row_version, updated_at)
            VALUES ($anchor_id, NULL, NULL, 0, $updated_at)
            ON CONFLICT(anchor_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertChapterProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        IReadOnlyList<ChapterBoundary> boundaries,
        int chapterBatchSize,
        CancellationToken cancellationToken)
    {
        foreach (var boundary in boundaries)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_materialization_chapter_progress (
                  run_id, chapter_node_id, chapter_index, batch_index, status, current_stage)
                VALUES (
                  $run_id, $chapter_node_id, $chapter_index, $batch_index, $status, $current_stage);
                """;
            command.Parameters.AddWithValue("$run_id", runId);
            command.Parameters.AddWithValue("$chapter_node_id", $"split-chapter:{boundary.ChapterIndex}:{boundary.TextHash}");
            command.Parameters.AddWithValue("$chapter_index", boundary.ChapterIndex);
            command.Parameters.AddWithValue("$batch_index", (boundary.ChapterIndex - 1) / chapterBatchSize);
            command.Parameters.AddWithValue("$status", ReferenceMaterializationChapterStates.Pending);
            command.Parameters.AddWithValue("$current_stage", ReferenceMaterializationChapterStates.Pending);
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
        int totalChapters,
        int totalBatches)
    {
        return new ReferenceMaterializationStatusPayload(
            seed.RunId,
            seed.AnchorId,
            seed.SplitProfileId,
            seed.GenerationId,
            ReferenceMaterializationRunStates.Queued,
            seed.ChapterBatchSize,
            totalChapters,
            0,
            totalBatches,
            0,
            0,
            1,
            Math.Min(seed.ChapterBatchSize, totalChapters),
            0,
            0,
            0,
            0,
            0,
            seed.Llm,
            seed.Embedding,
            null,
            null,
            seed.StartedAt,
            null,
            false,
            "start_processing");
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
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetInt32(17),
            new ReferenceMaterializationModelIdentityPayload(reader.GetString(18), reader.GetString(19)),
            new ReferenceMaterializationModelIdentityPayload(reader.GetString(20), reader.GetString(21), reader.GetInt32(22)),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            ParseTimestamp(reader.GetString(25)),
            reader.IsDBNull(26) ? null : ParseTimestamp(reader.GetString(26)),
            reader.GetInt64(27) != 0,
            NextActionFor(status));
    }

    private static void ValidateSeed(ReferenceMaterializationRunSeed seed)
    {
        if (seed.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seed), "Anchor id must be positive.");
        }

        ReferenceMaterializationBatchSizes.Validate(seed.ChapterBatchSize);
        Require(seed.RunId, nameof(seed.RunId));
        Require(seed.SplitProfileId, nameof(seed.SplitProfileId));
        Require(seed.GenerationId, nameof(seed.GenerationId));
        Require(seed.PolicyVersion, nameof(seed.PolicyVersion));
        Require(seed.CandidateVersion, nameof(seed.CandidateVersion));
        Require(seed.QualifierVersion, nameof(seed.QualifierVersion));
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

    private static string NextActionFor(string status)
    {
        return status switch
        {
            ReferenceMaterializationRunStates.Queued => "start_processing",
            ReferenceMaterializationRunStates.Running => "view_progress",
            ReferenceMaterializationRunStates.Failed => "retry",
            ReferenceMaterializationRunStates.Cancelled => "retry",
            ReferenceMaterializationRunStates.Completed => "activate_generation",
            _ => "view_error"
        };
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
    string CandidateVersion,
    string QualifierVersion,
    ReferenceMaterializationModelIdentityPayload Llm,
    ReferenceMaterializationModelIdentityPayload Embedding,
    int ChapterBatchSize,
    DateTimeOffset StartedAt);
