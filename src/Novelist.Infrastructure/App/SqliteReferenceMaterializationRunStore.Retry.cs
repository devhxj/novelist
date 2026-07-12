using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<ReferenceMaterializationStatusPayload> RetryCurrentBatchAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadBatchRunAsync(connection, transaction, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(runId));
        if (run.Status is not (ReferenceMaterializationRunStates.Failed or ReferenceMaterializationRunStates.Cancelled) ||
            run.CurrentBatchIndex is null)
        {
            throw new InvalidOperationException("Only a failed or cancelled materialization batch can be retried.");
        }

        await DeleteExpiredLeaseOrRejectActiveLeaseAsync(connection, transaction, normalizedRunId, cancellationToken);
        ReferenceMaterializationRunStateMachine.EnsureCanTransition(run.Status, ReferenceMaterializationRunStates.Running);
        await ResetCurrentBatchAsync(connection, transaction, normalizedRunId, run.CurrentBatchIndex.Value, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $running,
                    last_error_code = NULL,
                    last_error_message = NULL,
                    completed_at = NULL
                WHERE run_id = $run_id
                  AND status = $expected_status;
                """;
            command.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
            command.Parameters.AddWithValue("$run_id", normalizedRunId);
            command.Parameters.AddWithValue("$expected_status", run.Status);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Materialization run changed while retrying its current batch.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(normalizedRunId, cancellationToken)
            ?? throw new InvalidOperationException("Materialization run disappeared after retry.");
    }

    private static async ValueTask DeleteExpiredLeaseOrRejectActiveLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT lease_expires_at
                FROM reference_materialization_run_leases
                WHERE run_id = $run_id;
                """;
            existing.Parameters.AddWithValue("$run_id", runId);
            var expiry = (string?)await existing.ExecuteScalarAsync(cancellationToken);
            if (expiry is not null && DateTimeOffset.Parse(expiry) > DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("Materialization run still has an active worker lease.");
            }
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM reference_materialization_run_leases WHERE run_id = $run_id;";
        delete.Parameters.AddWithValue("$run_id", runId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }
}
