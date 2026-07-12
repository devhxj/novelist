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
            WHERE status IN ($queued, $running)
              AND current_batch_index IS NOT NULL
            ORDER BY started_at, run_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
        command.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
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
            run.Status is not (ReferenceMaterializationRunStates.Queued or ReferenceMaterializationRunStates.Running))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid().ToString("N");
        if (!await TryAcquireLeaseAsync(connection, transaction, normalizedRunId, workerId, token, now, leaseDuration, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (run.Status == ReferenceMaterializationRunStates.Queued)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Queued,
                ReferenceMaterializationRunStates.Running);
            await StartRunAsync(connection, transaction, normalizedRunId, cancellationToken);
        }

        var chapters = await ReadPendingBatchChaptersAsync(connection, transaction, normalizedRunId, run.CurrentBatchIndex.Value, cancellationToken);
        if (chapters.Count == 0)
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
        if (run.Status == ReferenceMaterializationRunStates.Running)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Running,
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
                  AND status IN ($queued, $running);
                """;
            command.Parameters.AddWithValue("$failed", ReferenceMaterializationRunStates.Failed);
            command.Parameters.AddWithValue("$error_code", errorCode);
            command.Parameters.AddWithValue("$error_message", errorMessage);
            command.Parameters.AddWithValue("$completed_at", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$run_id", normalizedRunId);
            command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
            command.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteLeaseAsync(connection, transaction, normalizedRunId, claim.LeaseToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask CompleteEmptyQualificationAsync(
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET status = $embedding,
                current_stage = $embedding,
                decided_count = 0,
                accepted_count = 0,
                rejected_count = 0,
                review_count = 0,
                row_version = row_version + 1
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index
              AND status = $qualifying
              AND candidate_count = 0;
            """;
        command.Parameters.AddWithValue("$embedding", ReferenceMaterializationChapterStates.Embedding);
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        command.Parameters.AddWithValue("$qualifying", ReferenceMaterializationChapterStates.LlmQualifying);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization chapter is not an empty qualification stage.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask CompleteEmptyEmbeddingAsync(
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET status = $indexing,
                current_stage = $indexing,
                vector_count = 0,
                row_version = row_version + 1
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index
              AND status = $embedding
              AND accepted_count = 0;
            """;
        command.Parameters.AddWithValue("$indexing", ReferenceMaterializationChapterStates.Indexing);
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        command.Parameters.AddWithValue("$embedding", ReferenceMaterializationChapterStates.Embedding);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization chapter is not an empty embedding stage.");
        }

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

    private static async ValueTask<bool> TryAcquireLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string workerId,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_run_leases (
              run_id, worker_id, lease_token, lease_expires_at, updated_at)
            VALUES ($run_id, $worker_id, $lease_token, $lease_expires_at, $updated_at)
            ON CONFLICT(run_id) DO UPDATE SET
              worker_id = excluded.worker_id,
              lease_token = excluded.lease_token,
              lease_expires_at = excluded.lease_expires_at,
              updated_at = excluded.updated_at
            WHERE reference_materialization_run_leases.lease_expires_at <= $updated_at;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$worker_id", workerId);
        command.Parameters.AddWithValue("$lease_token", leaseToken);
        command.Parameters.AddWithValue("$lease_expires_at", FormatTimestamp(now.Add(leaseDuration)));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
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
            SET status = $running
            WHERE run_id = $run_id
              AND status = $queued;
            """;
        command.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
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

    private sealed record BatchRun(string RunId, string Status, int? CurrentBatchIndex);
}

internal sealed record ReferenceMaterializationBatchClaim(
    string RunId,
    int BatchIndex,
    string LeaseToken,
    IReadOnlyList<int> ChapterIndexes);
