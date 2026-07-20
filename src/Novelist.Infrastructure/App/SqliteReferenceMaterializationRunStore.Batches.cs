using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<string?> ReadNextRunnableRunIdAsync(CancellationToken cancellationToken)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id
            FROM reference_materialization_runs
            WHERE (status = $queued AND current_batch_index IS NOT NULL)
               OR (status IN ($extracting, $embedding, $indexing)
                   AND (current_batch_index IS NOT NULL OR processed_chapters = total_chapters))
            ORDER BY started_at, run_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
        command.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
        command.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
        command.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async ValueTask<ReferenceMaterializationBatchClaim?> ClaimCurrentBatchAsync(
        string runId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 256 || workerId.Any(char.IsControl))
        {
            throw new ArgumentException("Materialization worker id is invalid.", nameof(workerId));
        }

        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be between zero and thirty minutes.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadBatchRunAsync(connection, transaction, normalizedRunId, cancellationToken);
        if (run is null || run.CurrentBatchIndex is null ||
            run.Status is not (ReferenceMaterializationRunStates.Queued or
                ReferenceMaterializationRunStates.Extracting or
                ReferenceMaterializationRunStates.Embedding or
                ReferenceMaterializationRunStates.Indexing))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid().ToString("N");
        var lease = await TryAcquireLeaseAsync(connection, transaction, normalizedRunId, workerId, token, now, leaseDuration, cancellationToken);
        if (!lease.Acquired)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (lease.ReclaimedExpiredLease)
        {
            await ResetCurrentBatchAsync(connection, transaction, normalizedRunId, run.CurrentBatchIndex.Value, cancellationToken);
        }

        if (run.Status == ReferenceMaterializationRunStates.Queued)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Queued,
                ReferenceMaterializationRunStates.Extracting);
            await StartRunAsync(connection, transaction, normalizedRunId, cancellationToken);
        }

        var chapters = await ReadPendingBatchChaptersAsync(connection, transaction, normalizedRunId, run.CurrentBatchIndex.Value, cancellationToken);
        if (chapters.Count == 0 &&
            !await IsBatchCompleteAsync(
                connection,
                transaction,
                normalizedRunId,
                run.CurrentBatchIndex.Value,
                cancellationToken))
        {
            await DeleteLeaseAsync(connection, transaction, normalizedRunId, token, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReferenceMaterializationBatchClaim(
            normalizedRunId,
            run.CurrentBatchIndex.Value,
            token,
            chapters);
    }

    public async ValueTask ReleaseBatchLeaseAsync(
        ReferenceMaterializationBatchClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await DeleteLeaseAsync(connection, transaction, NormalizeRunId(claim.RunId), claim.LeaseToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<bool> RenewBatchLeaseAsync(
        ReferenceMaterializationBatchClaim claim,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be between zero and thirty minutes.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_materialization_run_leases
            SET lease_expires_at = $lease_expires_at,
                updated_at = $updated_at
            WHERE run_id = $run_id
              AND lease_token = $lease_token
              AND lease_expires_at > $now;
            """;
        command.Parameters.AddWithValue("$lease_expires_at", FormatTimestamp(now.Add(leaseDuration)));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$run_id", NormalizeRunId(claim.RunId));
        command.Parameters.AddWithValue("$lease_token", claim.LeaseToken);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask MarkCurrentBatchEmbeddingAsync(
        ReferenceMaterializationBatchClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadBatchRunAsync(connection, transaction, NormalizeRunId(claim.RunId), cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(claim));
        if (!await IsClaimLeaseOwnedAsync(connection, transaction, claim, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Materialization worker lost the current batch lease.");
        }

        if (run.Status == ReferenceMaterializationRunStates.Extracting)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Extracting,
                ReferenceMaterializationRunStates.Embedding);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $embedding
                WHERE run_id = $run_id
                  AND status = $extracting;
                """;
            update.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
            update.Parameters.AddWithValue("$run_id", run.RunId);
            update.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Materialization run changed before vector indexing.");
            }
        }
        else if (run.Status is not (ReferenceMaterializationRunStates.Embedding or ReferenceMaterializationRunStates.Indexing))
        {
            throw new InvalidOperationException("Materialization run is not ready for vector indexing.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask FailCurrentBatchAsync(
        ReferenceMaterializationBatchClaim claim,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var normalizedRunId = NormalizeRunId(claim.RunId);
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128 ||
            string.IsNullOrWhiteSpace(errorMessage) || errorMessage.Length > 1_200)
        {
            throw new ArgumentException("Materialization failure details are invalid.", nameof(errorCode));
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadBatchRunAsync(connection, transaction, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(claim));
        if (!await IsClaimLeaseOwnedAsync(connection, transaction, claim, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        if (run.Status is ReferenceMaterializationRunStates.Queued or
            ReferenceMaterializationRunStates.Extracting or
            ReferenceMaterializationRunStates.Embedding or
            ReferenceMaterializationRunStates.Indexing)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                run.Status,
                ReferenceMaterializationRunStates.Failed);
        }

        await using (var chapters = connection.CreateCommand())
        {
            chapters.Transaction = transaction;
            chapters.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET status = $failed,
                    current_stage = $failed,
                    last_error_code = $error_code,
                    last_error_message = $error_message,
                    row_version = row_version + 1
                WHERE run_id = $run_id
                  AND batch_index = $batch_index
                  AND status <> $completed;
                """;
            chapters.Parameters.AddWithValue("$failed", ReferenceMaterializationChapterStates.Failed);
            chapters.Parameters.AddWithValue("$error_code", errorCode);
            chapters.Parameters.AddWithValue("$error_message", errorMessage);
            chapters.Parameters.AddWithValue("$run_id", normalizedRunId);
            chapters.Parameters.AddWithValue("$batch_index", claim.BatchIndex);
            chapters.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            await chapters.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $failed,
                    last_error_code = $error_code,
                    last_error_message = $error_message,
                    completed_at = $completed_at
                WHERE run_id = $run_id
                  AND status IN ($queued, $extracting, $embedding, $indexing);
                """;
            command.Parameters.AddWithValue("$failed", ReferenceMaterializationRunStates.Failed);
            command.Parameters.AddWithValue("$error_code", errorCode);
            command.Parameters.AddWithValue("$error_message", errorMessage);
            command.Parameters.AddWithValue("$completed_at", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$run_id", normalizedRunId);
            command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
            command.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
            command.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
            command.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteLeaseAsync(connection, transaction, normalizedRunId, claim.LeaseToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask<BatchRun?> ReadBatchRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, status, current_batch_index
            FROM reference_materialization_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BatchRun(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2))
            : null;
    }

    private static async ValueTask<LeaseAcquisitionResult> TryAcquireLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string workerId,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var reclaimedExpiredLease = false;
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT lease_expires_at
                FROM reference_materialization_run_leases
                WHERE run_id = $run_id;
                """;
            existing.Parameters.AddWithValue("$run_id", runId);
            var expiresAt = (string?)await existing.ExecuteScalarAsync(cancellationToken);
            if (expiresAt is not null && DateTimeOffset.Parse(expiresAt) > now)
            {
                return LeaseAcquisitionResult.Busy;
            }

            reclaimedExpiredLease = expiresAt is not null;
        }

        return await ReplaceLeaseAsync(
            connection,
            transaction,
            runId,
            workerId,
            leaseToken,
            now,
            leaseDuration,
            reclaimedExpiredLease,
            cancellationToken);
    }

    private static async ValueTask<LeaseAcquisitionResult> ReplaceLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string workerId,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        bool reclaimedExpiredLease,
        CancellationToken cancellationToken)
    {

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_materialization_run_leases WHERE run_id = $run_id;";
            delete.Parameters.AddWithValue("$run_id", runId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO reference_materialization_run_leases (
              run_id, worker_id, lease_token, lease_expires_at, updated_at)
            VALUES ($run_id, $worker_id, $lease_token, $lease_expires_at, $updated_at);
            """;
        insert.Parameters.AddWithValue("$run_id", runId);
        insert.Parameters.AddWithValue("$worker_id", workerId);
        insert.Parameters.AddWithValue("$lease_token", leaseToken);
        insert.Parameters.AddWithValue("$lease_expires_at", FormatTimestamp(now.Add(leaseDuration)));
        insert.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        return new LeaseAcquisitionResult(
            await insert.ExecuteNonQueryAsync(cancellationToken) == 1,
            reclaimedExpiredLease);
    }

    private static async ValueTask ResetCurrentBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int batchIndex,
        CancellationToken cancellationToken)
    {
        await using (var materialEmbeddings = connection.CreateCommand())
        {
            materialEmbeddings.Transaction = transaction;
            materialEmbeddings.CommandText = """
                DELETE FROM reference_materialization_material_embeddings
                WHERE material_id IN (
                  SELECT material.material_id
                  FROM reference_materialization_materials material
                  JOIN reference_materialization_chapter_progress progress
                    ON progress.run_id = material.run_id
                   AND progress.chapter_index = material.chapter_index
                  WHERE material.run_id = $run_id
                    AND progress.batch_index = $batch_index
                    AND progress.status <> $completed);
                """;
            materialEmbeddings.Parameters.AddWithValue("$run_id", runId);
            materialEmbeddings.Parameters.AddWithValue("$batch_index", batchIndex);
            materialEmbeddings.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            await materialEmbeddings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var materials = connection.CreateCommand())
        {
            materials.Transaction = transaction;
            materials.CommandText = """
                DELETE FROM reference_materialization_materials
                WHERE run_id = $run_id
                  AND chapter_index IN (
                    SELECT chapter_index
                    FROM reference_materialization_chapter_progress
                    WHERE run_id = $run_id
                      AND batch_index = $batch_index
                      AND status <> $completed);
                """;
            materials.Parameters.AddWithValue("$run_id", runId);
            materials.Parameters.AddWithValue("$batch_index", batchIndex);
            materials.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            await materials.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var chapters = connection.CreateCommand())
        {
            chapters.Transaction = transaction;
            chapters.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET status = $pending,
                    current_stage = $pending,
                    material_count = 0,
                    vector_count = 0,
                    started_at = NULL,
                    completed_at = NULL,
                    last_error_code = NULL,
                    last_error_message = NULL,
                    row_version = row_version + 1
                WHERE run_id = $run_id
                  AND batch_index = $batch_index
                  AND status <> $completed;
                """;
            chapters.Parameters.AddWithValue("$pending", ReferenceMaterializationChapterStates.Pending);
            chapters.Parameters.AddWithValue("$run_id", runId);
            chapters.Parameters.AddWithValue("$batch_index", batchIndex);
            chapters.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            await chapters.ExecuteNonQueryAsync(cancellationToken);
        }

        await RefreshRunCountsAsync(connection, transaction, runId, cancellationToken);
        await using var index = connection.CreateCommand();
        index.Transaction = transaction;
        index.CommandText = """
            UPDATE reference_materialization_vector_indexes
            SET status = 'building',
                updated_at = $updated_at
            WHERE run_id = $run_id;
            """;
        index.Parameters.AddWithValue("$updated_at", FormatTimestamp(DateTimeOffset.UtcNow));
        index.Parameters.AddWithValue("$run_id", runId);
        await index.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask RefreshRunCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_runs
            SET material_count = (
                    SELECT COALESCE(SUM(material_count), 0)
                    FROM reference_materialization_chapter_progress
                    WHERE run_id = $run_id),
                vector_count = (
                    SELECT COALESCE(SUM(vector_count), 0)
                    FROM reference_materialization_chapter_progress
                    WHERE run_id = $run_id)
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask StartRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_runs
            SET status = $extracting
            WHERE run_id = $run_id
              AND status = $queued;
            """;
        command.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization run changed while starting its current batch.");
        }
    }

    private static async ValueTask<IReadOnlyList<int>> ReadPendingBatchChaptersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int batchIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT chapter_index
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND batch_index = $batch_index
              AND status = $pending
            ORDER BY chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$batch_index", batchIndex);
        command.Parameters.AddWithValue("$pending", ReferenceMaterializationChapterStates.Pending);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var chapters = new List<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            chapters.Add(reader.GetInt32(0));
        }

        return chapters;
    }

    private static async ValueTask<bool> IsBatchCompleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int batchIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN status = $completed THEN 1 ELSE 0 END), 0)
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND batch_index = $batch_index;
            """;
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$batch_index", batchIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
               reader.GetInt32(0) > 0 &&
               reader.GetInt32(0) == reader.GetInt32(1);
    }

    private static async ValueTask DeleteLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM reference_materialization_run_leases
            WHERE run_id = $run_id
              AND lease_token = $lease_token;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$lease_token", leaseToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<bool> IsClaimLeaseOwnedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceMaterializationBatchClaim claim,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM reference_materialization_run_leases
            WHERE run_id = $run_id
              AND lease_token = $lease_token
              AND lease_expires_at > $now;
            """;
        command.Parameters.AddWithValue("$run_id", NormalizeRunId(claim.RunId));
        command.Parameters.AddWithValue("$lease_token", claim.LeaseToken);
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private sealed record BatchRun(string RunId, string Status, int? CurrentBatchIndex);

    private sealed record LeaseAcquisitionResult(bool Acquired, bool ReclaimedExpiredLease)
    {
        public static LeaseAcquisitionResult Busy { get; } = new(false, false);
    }
}

internal sealed record ReferenceMaterializationBatchClaim(
    string RunId,
    int BatchIndex,
    string LeaseToken,
    IReadOnlyList<int> ChapterIndexes);
