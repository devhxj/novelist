using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 public async ValueTask<ReferenceCorpusAnalysisJob> FailClaimAsync(
 string jobId,
 string workerId,
 string leaseToken,
 string errorCode,
 string errorMessage,
 DateTimeOffset now,
 CancellationToken cancellationToken = default)
 {
 ValidateId(jobId, nameof(jobId));
 ValidateId(workerId, nameof(workerId));
 ValidateId(leaseToken, nameof(leaseToken));
 ValidateId(errorCode, nameof(errorCode));
 if (errorMessage.Length > MaxErrorMessageLength) errorMessage = errorMessage[..MaxErrorMessageLength];
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var job = await ReadJobAsync(connection, transaction, jobId, cancellationToken)
 ?? throw new KeyNotFoundException($"Analysis job '{jobId}' was not found.");
 if (job.Status != ReferenceCorpusAnalysisJobStatuses.Running ||
 !string.Equals(job.LeaseOwner, workerId, StringComparison.Ordinal) ||
 !string.Equals(job.LeaseToken, leaseToken, StringComparison.Ordinal) ||
 job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis claim failure requires the active worker lease.");

 await using var update = connection.CreateCommand();
 update.Transaction = transaction;
 update.CommandText = """
 UPDATE reference_analysis_jobs
 SET status='failed',completed_at=$now,lease_owner=NULL,lease_token=NULL,
 lease_acquired_at=NULL,lease_expires_at=NULL,heartbeat_at=NULL,
 last_error_code=$error_code,last_error_message=$error_message,
 updated_at=$now,row_version=row_version+1
 WHERE job_id=$job_id AND status='running' AND lease_owner=$worker_id
 AND lease_token=$lease_token AND lease_expires_at>$now AND attempt_count=$attempt_no;
 """;
 Add(update, "$now", ToDb(now));
 Add(update, "$error_code", errorCode);
 Add(update, "$error_message", errorMessage);
 Add(update, "$job_id", jobId);
 Add(update, "$worker_id", workerId);
 Add(update, "$lease_token", leaseToken);
 Add(update, "$attempt_no", job.AttemptCount);
 if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis claim changed during failure settlement.");

 await using var attempt = connection.CreateCommand();
 attempt.Transaction = transaction;
 attempt.CommandText = """
 UPDATE reference_analysis_job_attempts
 SET completed_at=$now,outcome='permanent_failure',error_code=$error_code,error_message=$error_message
 WHERE job_id=$job_id AND attempt_no=$attempt_no AND worker_id=$worker_id
 AND lease_token=$lease_token AND completed_at IS NULL;
 """;
 Add(attempt, "$now", ToDb(now));
 Add(attempt, "$error_code", errorCode);
 Add(attempt, "$error_message", errorMessage);
 Add(attempt, "$job_id", jobId);
 Add(attempt, "$attempt_no", job.AttemptCount);
 Add(attempt, "$worker_id", workerId);
 Add(attempt, "$lease_token", leaseToken);
 if (await attempt.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis attempt changed during claim failure settlement.");
 await SyncCanonicalRunAsync(connection, transaction, jobId, cancellationToken);
 var failed = await ReadJobAsync(connection, transaction, jobId, cancellationToken)
 ?? throw new InvalidOperationException("Analysis job disappeared during claim failure settlement.");
 await transaction.CommitAsync(cancellationToken);
 return failed;
 }

public async ValueTask<ReferenceCorpusAnalysisJob> AcknowledgeControlBoundaryAsync(
 string jobId,string workerId,string leaseToken,DateTimeOffset now,CancellationToken cancellationToken=default)
 {
 await using var connection=await OpenConnectionAsync(cancellationToken);
 await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var job=await ReadJobAsync(connection,transaction,jobId,cancellationToken) ?? throw new KeyNotFoundException($"Analysis job '{jobId}' was not found.");
 var target=job.Status switch
 {
 ReferenceCorpusAnalysisJobStatuses.PauseRequested=>ReferenceCorpusAnalysisJobStatuses.Paused,
 ReferenceCorpusAnalysisJobStatuses.CancelRequested=>ReferenceCorpusAnalysisJobStatuses.Cancelled,
 _=>throw new InvalidOperationException("Analysis job is not waiting for a control-boundary acknowledgement.")
 };
 if(!string.Equals(job.LeaseOwner,workerId,StringComparison.Ordinal)||!string.Equals(job.LeaseToken,leaseToken,StringComparison.Ordinal)||job.LeaseExpiresAt is null||job.LeaseExpiresAt<=now)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis control acknowledgement requires the active worker lease.");
 await using var update=connection.CreateCommand();
 update.Transaction=transaction;
 update.CommandText="UPDATE reference_analysis_jobs SET status=$status,completed_at=$completed_at,lease_owner=NULL,lease_token=NULL,lease_acquired_at=NULL,lease_expires_at=NULL,heartbeat_at=NULL,updated_at=$now,row_version=row_version+1 WHERE job_id=$job_id AND row_version=$version;";
 Add(update,"$status",target); Add(update,"$completed_at",target==ReferenceCorpusAnalysisJobStatuses.Cancelled?ToDb(now):null); Add(update,"$now",ToDb(now)); Add(update,"$job_id",jobId); Add(update,"$version",job.Version);
 if(await update.ExecuteNonQueryAsync(cancellationToken)!=1) throw new ReferenceCorpusAnalysisJobConflictException("Analysis control acknowledgement raced with another update.");
 await using var attempt=connection.CreateCommand(); attempt.Transaction=transaction; attempt.CommandText="UPDATE reference_analysis_job_attempts SET completed_at=$now,outcome=$outcome WHERE job_id=$job_id AND attempt_no=$attempt AND worker_id=$worker AND lease_token=$lease AND completed_at IS NULL;";
 Add(attempt,"$now",ToDb(now)); Add(attempt,"$outcome",target); Add(attempt,"$job_id",jobId); Add(attempt,"$attempt",job.AttemptCount); Add(attempt,"$worker",workerId); Add(attempt,"$lease",leaseToken);
 if(await attempt.ExecuteNonQueryAsync(cancellationToken)!=1) throw new ReferenceCorpusAnalysisJobConflictException("Analysis attempt changed during control acknowledgement.");
 await SyncCanonicalRunAsync(connection,transaction,jobId,cancellationToken);
 var result=await ReadJobAsync(connection,transaction,jobId,cancellationToken) ?? throw new InvalidOperationException("Analysis job disappeared.");
 await transaction.CommitAsync(cancellationToken); return result;
 }

 public ValueTask<ReferenceCorpusAnalysisJob> RequestPauseAsync(
 string jobId,
 long expectedVersion,
 DateTimeOffset requestedAt,
 CancellationToken cancellationToken = default) =>
 MutateAsync(jobId, expectedVersion, requestedAt, (job, command) =>
 {
 var status = ReferenceCorpusAnalysisJobStateMachine.RequestPause(job.Status);
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET status = $status,
 pause_requested_at = $requested_at,
 completed_at = NULL,
 updated_at = $requested_at,
 row_version = row_version + 1
 WHERE job_id = $job_id AND row_version = $expected_version;
 """;
 Add(command, "$status", status);
 Add(command, "$requested_at", ToDb(requestedAt));
 }, cancellationToken);

 public ValueTask<ReferenceCorpusAnalysisJob> RequestCancelAsync(
 string jobId,
 long expectedVersion,
 DateTimeOffset requestedAt,
 CancellationToken cancellationToken = default) =>
 MutateAsync(jobId, expectedVersion, requestedAt, (job, command) =>
 {
 var status = ReferenceCorpusAnalysisJobStateMachine.RequestCancel(job.Status);
 var terminal = status == ReferenceCorpusAnalysisJobStatuses.Cancelled;
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET status = $status,
 cancel_requested_at = $requested_at,
 completed_at = $completed_at,
 lease_owner = CASE WHEN $terminal = 1 THEN NULL ELSE lease_owner END,
 lease_token = CASE WHEN $terminal = 1 THEN NULL ELSE lease_token END,
 lease_acquired_at = CASE WHEN $terminal = 1 THEN NULL ELSE lease_acquired_at END,
 lease_expires_at = CASE WHEN $terminal = 1 THEN NULL ELSE lease_expires_at END,
 heartbeat_at = CASE WHEN $terminal = 1 THEN NULL ELSE heartbeat_at END,
 updated_at = $requested_at,
 row_version = row_version + 1
 WHERE job_id = $job_id AND row_version = $expected_version;
 """;
 Add(command, "$status", status);
 Add(command, "$requested_at", ToDb(requestedAt));
 Add(command, "$completed_at", terminal ? ToDb(requestedAt) : null);
 Add(command, "$terminal", terminal ? 1 : 0);
 }, cancellationToken);

 public ValueTask<ReferenceCorpusAnalysisJob> ResumeAsync(
 string jobId,
 long expectedVersion,
 int? newTokenBudget,
 DateTimeOffset requestedAt,
 CancellationToken cancellationToken = default) =>
 MutateAsync(jobId, expectedVersion, requestedAt, (job, command) =>
 {
 var tokenBudget = newTokenBudget ?? job.TokenBudget;
 var status = ReferenceCorpusAnalysisJobStateMachine.Resume(job.Status, tokenBudget, job.TokensSpent);
 if (newTokenBudget is < 0)
 {
 throw new ArgumentOutOfRangeException(nameof(newTokenBudget));
 }
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET status = $status,
 token_budget = $token_budget,
 next_attempt_at = NULL,
 pause_requested_at = NULL,
 cancel_requested_at = NULL,
 completed_at = NULL,
 last_error_code = NULL,
 last_error_message = NULL,
 updated_at = $requested_at,
 row_version = row_version + 1
 WHERE job_id = $job_id AND row_version = $expected_version;
 """;
 Add(command, "$status", status);
 Add(command, "$token_budget", tokenBudget);
 Add(command, "$requested_at", ToDb(requestedAt));
 }, cancellationToken);

 public ValueTask<ReferenceCorpusAnalysisJob> ReprioritizeAsync(
 string jobId,
 long expectedVersion,
 string priorityClass,
 int priorityValue,
 DateTimeOffset requestedAt,
 CancellationToken cancellationToken = default) =>
 MutateAsync(jobId, expectedVersion, requestedAt, (job, command) =>
 {
 if (ReferenceCorpusAnalysisJobStateMachine.IsTerminal(job.Status))
 {
 throw new InvalidOperationException("Terminal analysis jobs cannot be reprioritized.");
 }
 if (!ReferenceCorpusAnalysisPriorityClasses.All.Contains(priorityClass, StringComparer.Ordinal))
 {
 throw new ArgumentOutOfRangeException(nameof(priorityClass), priorityClass, "Unknown analysis priority class.");
 }
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET priority_class = $priority_class,
 priority_value = $priority_value,
 updated_at = $requested_at,
 row_version = row_version + 1
 WHERE job_id = $job_id AND row_version = $expected_version;
 """;
 Add(command, "$priority_class", priorityClass);
 Add(command, "$priority_value", priorityValue);
 Add(command, "$requested_at", ToDb(requestedAt));
 }, cancellationToken);

 private async ValueTask<ReferenceCorpusAnalysisJob> MutateAsync(
 string jobId,
 long expectedVersion,
 DateTimeOffset requestedAt,
 Action<ReferenceCorpusAnalysisJob, SqliteCommand> configureUpdate,
 CancellationToken cancellationToken)
 {
 ValidateId(jobId, nameof(jobId));
 if (expectedVersion < 0)
 {
 throw new ArgumentOutOfRangeException(nameof(expectedVersion));
 }
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var current = await ReadJobAsync(connection, transaction, jobId, cancellationToken)
 ?? throw new KeyNotFoundException($"Analysis job '{jobId}' was not found.");
 if (current.Version != expectedVersion)
 {
 throw VersionConflict(jobId, expectedVersion, current.Version);
 }

 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 configureUpdate(current, command);
 Add(command, "$job_id", jobId);
 Add(command, "$expected_version", expectedVersion);
if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
{
throw VersionConflict(jobId, expectedVersion, actualVersion: null);
}
 await SyncCanonicalRunAsync(connection, transaction, jobId, cancellationToken);
var updated = await ReadJobAsync(connection, transaction, jobId, cancellationToken)
 ?? throw new InvalidOperationException($"Analysis job '{jobId}' disappeared during mutation.");
 await transaction.CommitAsync(cancellationToken);
 return updated;
 }

 private static ReferenceCorpusAnalysisJobConflictException VersionConflict(
 string jobId,
 long expectedVersion,
 long? actualVersion) =>
 new($"Analysis job '{jobId}' version conflict: expected {expectedVersion}, actual {(actualVersion?.ToString() ?? "changed concurrently")}.");
}
