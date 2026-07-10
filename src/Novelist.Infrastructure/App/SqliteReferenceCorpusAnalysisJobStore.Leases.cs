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
 AND (job.failure_attempt_count < job.max_attempts OR EXISTS (
 SELECT 1 FROM reference_analysis_work_items AS ready
 WHERE ready.input_snapshot_id=job.input_snapshot_id AND ready.work_state='output_ready'))
 AND (job.next_attempt_at IS NULL OR job.next_attempt_at <= $now)
 AND (job.dependency_job_id IS NULL OR EXISTS (
 SELECT 1
 FROM reference_analysis_jobs AS dependency
 WHERE dependency.job_id = job.dependency_job_id
 AND dependency.status = 'completed'))
 ORDER BY
 job.priority_value + CAST(MAX(0, (julianday($now) - julianday(job.queued_at)) * 288) AS INTEGER) DESC,
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
 SET status = CASE
 WHEN cancel_requested_at IS NOT NULL THEN 'cancel_requested'
 WHEN pause_requested_at IS NOT NULL THEN 'pause_requested'
 ELSE 'running' END,
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

 public async ValueTask<ReferenceCorpusAnalysisJob> AbandonReservationAsync(
 ReferenceCorpusAnalysisWorkItemReservation reservation,
 int tokensSpent,
 string errorCode,
 string errorMessage,
 DateTimeOffset now,
 DateTimeOffset retryAt,
 CancellationToken cancellationToken = default)
 {
 ArgumentNullException.ThrowIfNull(reservation);
 if (tokensSpent < 0) throw new ArgumentOutOfRangeException(nameof(tokensSpent));
 if (retryAt <= now) throw new ArgumentOutOfRangeException(nameof(retryAt));
 ValidateId(errorCode, nameof(errorCode));
 if (errorMessage.Length > MaxErrorMessageLength) errorMessage = errorMessage[..MaxErrorMessageLength];

 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var job = await ReadJobAsync(connection, transaction, reservation.JobId, cancellationToken)
 ?? throw new KeyNotFoundException($"Analysis job '{reservation.JobId}' was not found.");
 if (!string.Equals(job.RunId, reservation.RunId, StringComparison.Ordinal) ||
 !string.Equals(job.InputSnapshotId, reservation.InputSnapshotId, StringComparison.Ordinal) ||
 job.AttemptCount != reservation.AttemptNumber ||
 job.Status is not (ReferenceCorpusAnalysisJobStatuses.Running
 or ReferenceCorpusAnalysisJobStatuses.PauseRequested
 or ReferenceCorpusAnalysisJobStatuses.CancelRequested) ||
 !string.Equals(job.LeaseOwner, reservation.WorkerId, StringComparison.Ordinal) ||
 !string.Equals(job.LeaseToken, reservation.LeaseToken, StringComparison.Ordinal) ||
 job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis abandonment requires the active reservation lease.");
 await ValidateWorkItemFenceAsync(connection, transaction, reservation, cancellationToken);
 await ValidateAttemptFenceAsync(connection, transaction, reservation, cancellationToken);

 var nextStatus = job.Status switch
 {
 ReferenceCorpusAnalysisJobStatuses.PauseRequested => ReferenceCorpusAnalysisJobStatuses.Paused,
 ReferenceCorpusAnalysisJobStatuses.CancelRequested => ReferenceCorpusAnalysisJobStatuses.Cancelled,
 _ when job.AttemptCount >= job.MaxAttempts => ReferenceCorpusAnalysisJobStatuses.Failed,
 _ => ReferenceCorpusAnalysisJobStatuses.RetryWait
 };
 var workState = nextStatus == ReferenceCorpusAnalysisJobStatuses.Failed ? "failed" : "pending";
 await using (var work = connection.CreateCommand())
 {
 work.Transaction = transaction;
 work.CommandText = """
 UPDATE reference_analysis_work_items
 SET work_state=$work_state,reserved_tokens=0,execution_worker_id=NULL,
 execution_lease_token=NULL,execution_attempt_no=NULL
 WHERE input_snapshot_id=$snapshot_id AND ordinal=$ordinal AND node_id=$node_id
 AND feature_family=$feature_family AND node_text_hash=$node_text_hash
 AND work_state='in_progress' AND execution_worker_id=$worker_id
 AND execution_lease_token=$lease_token AND execution_attempt_no=$attempt_no
 AND invocation_no=$invocation_no AND reserved_tokens=$reserved_tokens;
 """;
 AddReservationParameters(work, reservation);
 Add(work, "$work_state", workState);
 if (await work.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis work item changed during abandonment.");
 }

 var terminal = nextStatus is ReferenceCorpusAnalysisJobStatuses.Cancelled or ReferenceCorpusAnalysisJobStatuses.Failed;
 await using (var update = connection.CreateCommand())
 {
 update.Transaction = transaction;
 update.CommandText = """
 UPDATE reference_analysis_jobs
 SET status=$status,next_attempt_at=$next_attempt_at,completed_at=$completed_at,
 tokens_spent=tokens_spent+$tokens_spent,tokens_reserved=tokens_reserved-$reserved_tokens,
 processed_work_items=processed_work_items+$processed_delta,
 failed_work_items=failed_work_items+$failed_delta,
 retrying_work_items=$retrying_items,
 lease_owner=NULL,lease_token=NULL,lease_acquired_at=NULL,lease_expires_at=NULL,heartbeat_at=NULL,
 last_error_code=$error_code,last_error_message=$error_message,
 updated_at=$now,row_version=row_version+1
 WHERE job_id=$job_id AND run_id=$run_id AND input_snapshot_id=$snapshot_id
 AND status=$expected_status AND lease_owner=$worker_id AND lease_token=$lease_token
 AND lease_expires_at>$now AND attempt_count=$attempt_no
 AND tokens_reserved>=$reserved_tokens;
 """;
 Add(update, "$status", nextStatus);
 Add(update, "$next_attempt_at", nextStatus == ReferenceCorpusAnalysisJobStatuses.RetryWait ? ToDb(retryAt) : null);
 Add(update, "$completed_at", terminal ? ToDb(now) : null);
 Add(update, "$tokens_spent", tokensSpent);
 Add(update, "$reserved_tokens", reservation.ReservedTokens);
 Add(update, "$processed_delta", nextStatus == ReferenceCorpusAnalysisJobStatuses.Failed ? 1 : 0);
 Add(update, "$failed_delta", nextStatus == ReferenceCorpusAnalysisJobStatuses.Failed ? 1 : 0);
 Add(update, "$retrying_items", nextStatus == ReferenceCorpusAnalysisJobStatuses.RetryWait ? 1 : 0);
 Add(update, "$error_code", errorCode);
 Add(update, "$error_message", errorMessage);
 Add(update, "$now", ToDb(now));
 Add(update, "$job_id", reservation.JobId);
 Add(update, "$run_id", reservation.RunId);
 Add(update, "$snapshot_id", reservation.InputSnapshotId);
 Add(update, "$expected_status", job.Status);
 Add(update, "$worker_id", reservation.WorkerId);
 Add(update, "$lease_token", reservation.LeaseToken);
 Add(update, "$attempt_no", reservation.AttemptNumber);
 if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis job changed during abandonment.");
 }

 await using (var attempt = connection.CreateCommand())
 {
 attempt.Transaction = transaction;
 attempt.CommandText = """
 UPDATE reference_analysis_job_attempts
 SET tokens_spent=tokens_spent+$tokens_spent,completed_at=$now,outcome='abandoned',
 error_code=$error_code,error_message=$error_message
 WHERE job_id=$job_id AND attempt_no=$attempt_no AND worker_id=$worker_id
 AND lease_token=$lease_token AND completed_at IS NULL;
 """;
 AddAttemptParameters(attempt, reservation);
 Add(attempt, "$tokens_spent", tokensSpent);
 Add(attempt, "$now", ToDb(now));
 Add(attempt, "$error_code", errorCode);
 Add(attempt, "$error_message", errorMessage);
 if (await attempt.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis attempt changed during abandonment.");
 }

 await SyncCanonicalRunAsync(connection, transaction, reservation.JobId, cancellationToken);
 var abandoned = await ReadJobAsync(connection, transaction, reservation.JobId, cancellationToken)
 ?? throw new InvalidOperationException("Analysis job disappeared during abandonment.");
 await transaction.CommitAsync(cancellationToken);
 return abandoned;
 }

 public async ValueTask<int> ReclaimExpiredLeasesAsync(
 DateTimeOffset now,
 DateTimeOffset retryAt,
 CancellationToken cancellationToken = default)
 {
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var expired = new List<(string JobId, string InputSnapshotId, string Status, int AttemptCount,
 int FailureAttemptCount, int MaxAttempts, int ReservedTokens, int InProgressItems, int OutputReadyItems)>();
 await using (var select = connection.CreateCommand())
 {
 select.Transaction = transaction;
 select.CommandText = """
 SELECT job.job_id,job.input_snapshot_id,job.status,job.attempt_count,
 job.failure_attempt_count,job.max_attempts,
 COALESCE(SUM(CASE WHEN work.work_state='in_progress' THEN work.reserved_tokens ELSE 0 END),0),
 COALESCE(SUM(CASE WHEN work.work_state='in_progress' THEN 1 ELSE 0 END),0),
 COALESCE(SUM(CASE WHEN work.work_state='output_ready' THEN 1 ELSE 0 END),0)
 FROM reference_analysis_jobs AS job
 LEFT JOIN reference_analysis_work_items AS work
 ON work.input_snapshot_id=job.input_snapshot_id
 AND work.work_state IN ('in_progress','output_ready')
 WHERE status IN ('running', 'pause_requested', 'cancel_requested')
 AND lease_expires_at IS NOT NULL
 AND lease_expires_at <= $now
 GROUP BY job.job_id,job.input_snapshot_id,job.status,job.attempt_count,
 job.failure_attempt_count,job.max_attempts
 ORDER BY job.job_id;
 """;
 Add(select, "$now", ToDb(now));
 await using var reader = await select.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 expired.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),
 reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetInt32(7),reader.GetInt32(8)));
 }
 }

foreach (var item in expired)
{
var hasOutputReady = item.OutputReadyItems > 0;
 if (hasOutputReady)
 {
 // A durable model result must be finalized in place. Reclaiming its lease would
 // sever the frozen attempt fence and make recovery impossible.
 continue;
 }
var nextFailureAttemptCount = item.FailureAttemptCount + (item.InProgressItems > 0 ? 1 : 0);
 var terminal = item.InProgressItems > 0 && nextFailureAttemptCount >= item.MaxAttempts;
var nextStatus = item.Status switch
{
ReferenceCorpusAnalysisJobStatuses.PauseRequested => ReferenceCorpusAnalysisJobStatuses.Paused,
 ReferenceCorpusAnalysisJobStatuses.CancelRequested => ReferenceCorpusAnalysisJobStatuses.Cancelled,
 _ when terminal => ReferenceCorpusAnalysisJobStatuses.Failed,
 _ => ReferenceCorpusAnalysisJobStatuses.RetryWait
 };
var retryable = nextStatus == ReferenceCorpusAnalysisJobStatuses.RetryWait;
var completed = nextStatus is ReferenceCorpusAnalysisJobStatuses.Cancelled or ReferenceCorpusAnalysisJobStatuses.Failed;
 var failedItems = nextStatus == ReferenceCorpusAnalysisJobStatuses.Failed ? item.InProgressItems : 0;
 await using (var releaseWork = connection.CreateCommand())
 {
 releaseWork.Transaction = transaction;
 releaseWork.CommandText = """
 UPDATE reference_analysis_work_items
 SET work_state=$work_state,execution_worker_id=NULL,execution_lease_token=NULL,
 execution_attempt_no=NULL,reserved_tokens=0,
 committed_run_id=CASE WHEN $work_state='failed' THEN committed_run_id ELSE NULL END,
 committed_at=CASE WHEN $work_state='failed' THEN committed_at ELSE NULL END
 WHERE input_snapshot_id=$snapshot_id AND work_state='in_progress';
 """;
 Add(releaseWork, "$work_state", failedItems > 0 ? "failed" : "pending");
 Add(releaseWork, "$snapshot_id", item.InputSnapshotId);
 if (await releaseWork.ExecuteNonQueryAsync(cancellationToken) != item.InProgressItems)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"Analysis work items for expired job '{item.JobId}' changed during lease recovery.");
 }
 await using var update = connection.CreateCommand();
 update.Transaction = transaction;
 update.CommandText = """
 UPDATE reference_analysis_jobs
 SET status = $status,
 failure_attempt_count = failure_attempt_count + $failure_delta,
 next_attempt_at = $next_attempt_at,
 completed_at = $completed_at,
 tokens_spent = tokens_spent + $reserved_tokens,
 tokens_reserved = tokens_reserved - $reserved_tokens,
 processed_work_items = processed_work_items + $failed_items,
 failed_work_items = failed_work_items + $failed_items,
 retrying_work_items = $retrying_items,
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
 AND lease_expires_at <= $now
 AND tokens_reserved >= $reserved_tokens;
 """;
Add(update, "$status", nextStatus);
 Add(update, "$failure_delta", item.InProgressItems > 0 ? 1 : 0);
 Add(update, "$next_attempt_at", retryable ? ToDb(retryAt) : null);
Add(update, "$completed_at", completed ? ToDb(now) : null);
 Add(update, "$reserved_tokens", item.ReservedTokens);
 Add(update, "$failed_items", failedItems);
 Add(update, "$retrying_items", retryable ? item.InProgressItems : 0);
 Add(update, "$now", ToDb(now));
 Add(update, "$job_id", item.JobId);
 if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"Analysis job '{item.JobId}' changed during expired lease recovery.");

 await using var attempt = connection.CreateCommand();
 attempt.Transaction = transaction;
 attempt.CommandText = """
 UPDATE reference_analysis_job_attempts
 SET completed_at = $now,
 outcome = $outcome,
 error_code = $error_code,
 error_message = $error_message,
 tokens_spent = tokens_spent + $reserved_tokens
 WHERE job_id = $job_id AND attempt_no = $attempt_no AND completed_at IS NULL;
 """;
Add(attempt, "$now", ToDb(now));
 Add(attempt, "$outcome", "abandoned");
 Add(attempt, "$error_code", "lease_expired");
 Add(attempt, "$error_message", "Worker lease expired before the attempt completed.");
 Add(attempt, "$job_id", item.JobId);
Add(attempt, "$attempt_no", item.AttemptCount);
 Add(attempt, "$reserved_tokens", item.ReservedTokens);
if (await attempt.ExecuteNonQueryAsync(cancellationToken) != 1)
throw new ReferenceCorpusAnalysisJobConflictException(
$"Analysis attempt for expired job '{item.JobId}' changed during lease recovery.");
 await SyncCanonicalRunAsync(connection, transaction, item.JobId, cancellationToken);
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
