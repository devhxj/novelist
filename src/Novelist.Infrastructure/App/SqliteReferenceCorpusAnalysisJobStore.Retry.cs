using Microsoft.Data.Sqlite;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 public async ValueTask<int> RequeueDueRetriesAsync(
 DateTimeOffset now,
 CancellationToken cancellationToken = default)
 {
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction =
 (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 await using var update = connection.CreateCommand();
 update.Transaction = transaction;
 update.CommandText = """
 UPDATE reference_analysis_jobs
 SET status = 'queued',
 retrying_work_items = 0,
 next_attempt_at = NULL,
 last_error_code = NULL,
 last_error_message = NULL,
 updated_at = $now,
 row_version = row_version + 1
 WHERE status = 'retry_wait'
 AND next_attempt_at IS NOT NULL
 AND next_attempt_at <= $now;
 """;
 Add(update, "$now", ToDb(now));
 var requeued = await update.ExecuteNonQueryAsync(cancellationToken);
 await transaction.CommitAsync(cancellationToken);
 return requeued;
 }
}
