using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 public async ValueTask<ReferenceCorpusAnalysisJobClaim?> ClaimNextAsync(
 string workerId,
 DateTimeOffset now,
 TimeSpan leaseDuration,
 CancellationToken cancellationToken = default)
 {
 ValidateId(workerId, nameof(workerId));
 if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(10))
 {
 throw new ArgumentOutOfRangeException(nameof(leaseDuration));
 }

 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 await using var select = connection.CreateCommand();
 select.Transaction = transaction;
 select.CommandText = """
 SELECT job_id, row_version
 FROM reference_analysis_jobs AS job
 WHERE job.status = 'queued'
 AND (job.next_attempt_at IS NULL OR job.next_attempt_at <= $now)
 AND (job.dependency_job_id IS NULL OR EXISTS (
 SELECT 1
 FROM reference_analysis_jobs AS dependency
 WHERE dependency.job_id = job.dependency_job_id
 AND dependency.status = 'completed'))
 ORDER BY
 job.priority_value + MIN(100, CAST(MAX(0, (julianday($now) - julianday(job.queued_at)) * 288) AS INTEGER)) DESC,
 job.queued_at ASC,
 job.job_id ASC
 LIMIT 1;
 """;
 Add(select, "$now", ToDb(now));
 await using var reader = await select.ExecuteReaderAsync(cancellationToken);
 if (!await reader.ReadAsync(cancellationToken))
 {
 await transaction.CommitAsync(cancellationToken);
 return null;
 }
 var jobId = reader.GetString(0);
 var expectedVersion = reader.GetInt64(1);
 await reader.DisposeAsync();

 var leaseToken = Guid.NewGuid().ToString("N");
 var leaseExpiresAt = now.Add(leaseDuration);
 await using var update = connection.CreateCommand();
 update.Transaction = transaction;
 update.CommandText = """
 UPDATE reference_analysis_jobs
 SET status = 'running',
 lease_owner = $worker_id,
 lease_token = $lease_token,
 lease_acquired_at = $now,
 lease_expires_at = $lease_expires_at,
 heartbeat_at = $now,
 attempt_count = attempt_count + 1,
 started_at = COALESCE(started_at, $now),
 updated_at = $now,
 row_version = row_version + 1
 WHERE job_id = $job_id
 AND status = 'queued'
 AND row_version = $expected_version;
 """;
 Add(update, "$worker_id", workerId);
 Add(update, "$lease_token", leaseToken);
 Add(update, "$now", ToDb(now));
 Add(update, "$lease_expires_at", ToDb(leaseExpiresAt));
 Add(update, "$job_id", jobId);
 Add(update, "$expected_version", expectedVersion);
 if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
 {
 await transaction.RollbackAsync(cancellationToken);
 return null;
 }

 var claimed = await ReadJobAsync(connection, transaction, jobId, cancellationToken)
 ?? throw new InvalidOperationException($"Claimed analysis job '{jobId}' disappeared.");
 await InsertAttemptAsync(connection, transaction, claimed, workerId, leaseToken, now, cancellationToken);
 await transaction.CommitAsync(cancellationToken);
 return new ReferenceCorpusAnalysisJobClaim(claimed, leaseToken);
 }

 public async ValueTask<ReferenceCorpusAnalysisJobLease> HeartbeatAsync(
 string jobId,
 string workerId,
 string leaseToken,
 DateTimeOffset now,
 TimeSpan leaseDuration,
 CancellationToken cancellationToken = default)
 {
 ValidateId(jobId, nameof(jobId));
 ValidateId(workerId, nameof(workerId));
 ValidateId(leaseToken, nameof(leaseToken));
 if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(10))
 {
 throw new ArgumentOutOfRangeException(nameof(leaseDuration));
 }
 var expiresAt = now.Add(leaseDuration);
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET heartbeat_at = $now,
 lease_expires_at = $expires_at,
 updated_at = $now,
 row_version = row_version + 1
 WHERE job_id = $job_id
 AND status IN ('running', 'pause_requested', 'cancel_requested')
 AND lease_owner = $worker_id
 AND lease_token = $lease_token
 AND lease_expires_at > $now;
 """;
 Add(command, "$now", ToDb(now));
 Add(command, "$expires_at", ToDb(expiresAt));
 Add(command, "$job_id", jobId);
 Add(command, "$worker_id", workerId);
 Add(command, "$lease_token", leaseToken);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 {
 throw new ReferenceCorpusAnalysisJobConflictException($"Lease for analysis job '{jobId}' is missing, expired, or owned by another worker.");
 }
 var job = await GetRequiredAsync(jobId, cancellationToken);
 return new ReferenceCorpusAnalysisJobLease(jobId, workerId, leaseToken, job.AttemptCount, expiresAt);
 }

 public async ValueTask<int> ReclaimExpiredLeasesAsync(
 DateTimeOffset now,
 DateTimeOffset retryAt,
 CancellationToken cancellationToken = default)
 {
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var expired = new List<(string JobId, int AttemptCount, int MaxAttempts)>();
 await using (var select = connection.CreateCommand())
 {
 select.Transaction = transaction;
 select.CommandText = """
 SELECT job_id, attempt_count, max_attempts
 FROM reference_analysis_jobs
 WHERE status IN ('running', 'pause_requested', 'cancel_requested')
 AND lease_expires_at IS NOT NULL
 AND lease_expires_at <= $now
 ORDER BY job_id;
 """;
 Add(select, "$now", ToDb(now));
 await using var reader = await select.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 expired.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
 }
 }

 foreach (var item in expired)
 {
 var terminal = item.AttemptCount >= item.MaxAttempts;
 await using var update = connection.CreateCommand();
 update.Transaction = transaction;
 update.CommandText = """
 UPDATE reference_analysis_jobs
 SET status = $status,
 next_attempt_at = $next_attempt_at,
 completed_at = $completed_at,
 lease_owner = NULL,
 lease_token = NULL,
 lease_acquired_at = NULL,
 lease_expires_at = NULL,
 heartbeat_at = NULL,
 last_error_code = 'lease_expired',
 last_error_message = 'Worker lease expired before the attempt completed.',
 updated_at = $now,
 row_version = row_version + 1
 WHERE job_id = $job_id
 AND lease_expires_at IS NOT NULL
 AND lease_expires_at <= $now;
 """;
 Add(update, "$status", terminal ? ReferenceCorpusAnalysisJobStatuses.Failed : ReferenceCorpusAnalysisJobStatuses.RetryWait);
 Add(update, "$next_attempt_at", terminal ? null : ToDb(retryAt));
 Add(update, "$completed_at", terminal ? ToDb(now) : null);
 Add(update, "$now", ToDb(now));
 Add(update, "$job_id", item.JobId);
 await update.ExecuteNonQueryAsync(cancellationToken);

 await using var attempt = connection.CreateCommand();
 attempt.Transaction = transaction;
 attempt.CommandText = """
 UPDATE reference_analysis_job_attempts
 SET completed_at = $now,
 outcome = 'abandoned',
 error_code = 'lease_expired',
 error_message = 'Worker lease expired before the attempt completed.'
 WHERE job_id = $job_id AND attempt_no = $attempt_no AND completed_at IS NULL;
 """;
 Add(attempt, "$now", ToDb(now));
 Add(attempt, "$job_id", item.JobId);
 Add(attempt, "$attempt_no", item.AttemptCount);
 await attempt.ExecuteNonQueryAsync(cancellationToken);
 }

 await transaction.CommitAsync(cancellationToken);
 return expired.Count;
 }

 private static async ValueTask InsertAttemptAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisJob job,
 string workerId,
 string leaseToken,
 DateTimeOffset startedAt,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 INSERT INTO reference_analysis_job_attempts
 (job_id, attempt_no, worker_id, lease_token, started_at)
 VALUES ($job_id, $attempt_no, $worker_id, $lease_token, $started_at);
 """;
 Add(command, "$job_id", job.JobId);
 Add(command, "$attempt_no", job.AttemptCount);
 Add(command, "$worker_id", workerId);
 Add(command, "$lease_token", leaseToken);
 Add(command, "$started_at", ToDb(startedAt));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }
}
