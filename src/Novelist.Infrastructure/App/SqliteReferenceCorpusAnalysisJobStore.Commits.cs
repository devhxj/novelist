using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 public async ValueTask<ReferenceCorpusAnalysisJob> CommitWorkItemAsync(
 ReferenceCorpusAnalysisWorkItemReservation reservation,
 int tokensSpent,
 string committedRunId,
 DateTimeOffset now,
 Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask> persistOutputAsync,
 CancellationToken cancellationToken = default)
 {
 ArgumentNullException.ThrowIfNull(reservation);
 ArgumentNullException.ThrowIfNull(persistOutputAsync);
ValidateId(committedRunId, nameof(committedRunId));
 if (!string.Equals(committedRunId, reservation.RunId, StringComparison.Ordinal))
 throw new ArgumentException("Committed output must reference the reservation's canonical run.", nameof(committedRunId));
 if (tokensSpent < 0)
 throw new ArgumentOutOfRangeException(nameof(tokensSpent));

 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var job = await ReadJobAsync(connection, transaction, reservation.JobId, cancellationToken)
 ?? throw new KeyNotFoundException($"Analysis job '{reservation.JobId}' was not found.");
 ValidateCommitFence(job, reservation, now);
 await ValidateWorkItemFenceAsync(connection, transaction, reservation, cancellationToken);
 await ValidateAttemptFenceAsync(connection, transaction, reservation, cancellationToken);

 await persistOutputAsync(connection, transaction, cancellationToken);
 await UpdateCommittedWorkItemAsync(connection, transaction, reservation, committedRunId, now, cancellationToken);

 var disposition = ResolveCommitDisposition(job, tokensSpent);
 await UpdateCommittedJobAsync(connection, transaction, job, reservation, tokensSpent, disposition, now, cancellationToken);
 await UpdateCommittedAttemptAsync(connection, transaction, reservation, tokensSpent, disposition, now, cancellationToken);
 await SyncCanonicalRunAsync(connection, transaction, reservation.JobId, cancellationToken);

 var committed = await ReadJobAsync(connection, transaction, reservation.JobId, cancellationToken)
 ?? throw new InvalidOperationException($"Committed analysis job '{reservation.JobId}' disappeared.");
 await transaction.CommitAsync(cancellationToken);
 return committed;
 }

 private static void ValidateCommitFence(
 ReferenceCorpusAnalysisJob job,
 ReferenceCorpusAnalysisWorkItemReservation reservation,
 DateTimeOffset now)
 {
 if (!string.Equals(job.RunId, reservation.RunId, StringComparison.Ordinal) ||
 !string.Equals(job.InputSnapshotId, reservation.InputSnapshotId, StringComparison.Ordinal) ||
 job.AttemptCount != reservation.AttemptNumber)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis reservation no longer matches its job attempt.");
 if (job.Status is not (ReferenceCorpusAnalysisJobStatuses.Running
 or ReferenceCorpusAnalysisJobStatuses.PauseRequested
 or ReferenceCorpusAnalysisJobStatuses.CancelRequested) ||
 !string.Equals(job.LeaseOwner, reservation.WorkerId, StringComparison.Ordinal) ||
 !string.Equals(job.LeaseToken, reservation.LeaseToken, StringComparison.Ordinal) ||
 job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"Lease for analysis job '{job.JobId}' is missing, expired, or owned by another worker.");
}

 private static ReferenceCorpusAnalysisCommitDisposition ResolveCommitDisposition(
 ReferenceCorpusAnalysisJob job,
 int tokensSpent)
 {
 if (job.Status == ReferenceCorpusAnalysisJobStatuses.CancelRequested)
 return ReferenceCorpusAnalysisCommitDisposition.CancelAfterCommit;
 if (job.Status == ReferenceCorpusAnalysisJobStatuses.PauseRequested)
 return ReferenceCorpusAnalysisCommitDisposition.PauseAfterCommit;
 if (job.ProcessedWorkItems + 1 == job.TotalWorkItems)
 return ReferenceCorpusAnalysisCommitDisposition.Complete;
 if (job.TokenBudget is { } budget && job.TokensSpent + tokensSpent >= budget)
 return ReferenceCorpusAnalysisCommitDisposition.BudgetExhaustedAfterCommit;
 return ReferenceCorpusAnalysisCommitDisposition.Continue;
 }

 private static async ValueTask ValidateWorkItemFenceAsync(
 SqliteConnection connection, SqliteTransaction transaction,
 ReferenceCorpusAnalysisWorkItemReservation reservation, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 SELECT 1 FROM reference_analysis_work_items
 WHERE input_snapshot_id=$snapshot_id AND ordinal=$ordinal AND node_id=$node_id
 AND feature_family=$feature_family AND node_text_hash=$node_text_hash
 AND work_state='in_progress' AND execution_worker_id=$worker_id
 AND execution_lease_token=$lease_token AND execution_attempt_no=$attempt_no
 AND invocation_no=$invocation_no AND reserved_tokens=$reserved_tokens;
 """;
 AddReservationParameters(command, reservation);
 if (await command.ExecuteScalarAsync(cancellationToken) is null)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis work-item reservation is stale or already committed.");
 }

 private static async ValueTask ValidateAttemptFenceAsync(
 SqliteConnection connection, SqliteTransaction transaction,
 ReferenceCorpusAnalysisWorkItemReservation reservation, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 SELECT 1 FROM reference_analysis_job_attempts
 WHERE job_id=$job_id AND attempt_no=$attempt_no AND worker_id=$worker_id
 AND lease_token=$lease_token AND completed_at IS NULL;
 """;
 AddAttemptParameters(command, reservation);
 if (await command.ExecuteScalarAsync(cancellationToken) is null)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis attempt is stale or already completed.");
 }

 private static async ValueTask UpdateCommittedWorkItemAsync(
 SqliteConnection connection, SqliteTransaction transaction,
 ReferenceCorpusAnalysisWorkItemReservation reservation, string committedRunId,
 DateTimeOffset now, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_work_items
 SET work_state='succeeded',reserved_tokens=0,execution_worker_id=NULL,execution_lease_token=NULL,
 execution_attempt_no=NULL,committed_run_id=$committed_run_id,committed_at=$now
 WHERE input_snapshot_id=$snapshot_id AND ordinal=$ordinal AND work_state='in_progress'
 AND execution_worker_id=$worker_id AND execution_lease_token=$lease_token
 AND execution_attempt_no=$attempt_no AND invocation_no=$invocation_no
 AND reserved_tokens=$reserved_tokens;
 """;
 AddReservationParameters(command, reservation);
 Add(command, "$committed_run_id", committedRunId);
 Add(command, "$now", ToDb(now));
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis work item changed during commit.");
 }

 private static async ValueTask UpdateCommittedJobAsync(
 SqliteConnection connection, SqliteTransaction transaction, ReferenceCorpusAnalysisJob job,
ReferenceCorpusAnalysisWorkItemReservation reservation, int tokensSpent,
 ReferenceCorpusAnalysisCommitDisposition disposition,
 DateTimeOffset now, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET processed_work_items=processed_work_items+1,succeeded_work_items=succeeded_work_items+1,
 retrying_work_items=0,
 tokens_spent=tokens_spent+$tokens_spent,tokens_reserved=tokens_reserved-$reserved_tokens,
 resume_cursor=$resume_cursor,status=$target_status,
 completed_at=CASE WHEN $terminal=1 THEN $now ELSE NULL END,
 lease_owner=CASE WHEN $release_lease=1 THEN NULL ELSE lease_owner END,
 lease_token=CASE WHEN $release_lease=1 THEN NULL ELSE lease_token END,
 lease_acquired_at=CASE WHEN $release_lease=1 THEN NULL ELSE lease_acquired_at END,
 lease_expires_at=CASE WHEN $release_lease=1 THEN NULL ELSE lease_expires_at END,
 heartbeat_at=CASE WHEN $release_lease=1 THEN NULL ELSE heartbeat_at END,
 updated_at=$now,row_version=row_version+1
 WHERE job_id=$job_id AND run_id=$run_id AND input_snapshot_id=$snapshot_id
 AND status=$expected_status AND lease_owner=$worker_id AND lease_token=$lease_token
 AND lease_expires_at>$now AND attempt_count=$attempt_no
 AND tokens_reserved>=$reserved_tokens
 AND processed_work_items=$processed_work_items AND succeeded_work_items=$succeeded_work_items;
 """;
 Add(command, "$tokens_spent", tokensSpent);
 Add(command, "$reserved_tokens", reservation.ReservedTokens);
 Add(command, "$resume_cursor", (reservation.Ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
 var targetStatus = disposition switch
 {
 ReferenceCorpusAnalysisCommitDisposition.Complete => ReferenceCorpusAnalysisJobStatuses.Completed,
 ReferenceCorpusAnalysisCommitDisposition.PauseAfterCommit => ReferenceCorpusAnalysisJobStatuses.Paused,
 ReferenceCorpusAnalysisCommitDisposition.CancelAfterCommit => ReferenceCorpusAnalysisJobStatuses.Cancelled,
 ReferenceCorpusAnalysisCommitDisposition.BudgetExhaustedAfterCommit => ReferenceCorpusAnalysisJobStatuses.BudgetExhausted,
 _ => ReferenceCorpusAnalysisJobStatuses.Running
 };
 Add(command, "$target_status", targetStatus);
 Add(command, "$terminal", targetStatus is ReferenceCorpusAnalysisJobStatuses.Completed or ReferenceCorpusAnalysisJobStatuses.Cancelled ? 1 : 0);
 Add(command, "$release_lease", targetStatus == ReferenceCorpusAnalysisJobStatuses.Running ? 0 : 1);
 Add(command, "$expected_status", job.Status);
 Add(command, "$now", ToDb(now));
 Add(command, "$job_id", reservation.JobId);
 Add(command, "$run_id", reservation.RunId);
 Add(command, "$snapshot_id", reservation.InputSnapshotId);
 Add(command, "$worker_id", reservation.WorkerId);
 Add(command, "$lease_token", reservation.LeaseToken);
 Add(command, "$attempt_no", reservation.AttemptNumber);
 Add(command, "$processed_work_items", job.ProcessedWorkItems);
 Add(command, "$succeeded_work_items", job.SucceededWorkItems);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis job changed during work-item commit.");
 }

 private static async ValueTask UpdateCommittedAttemptAsync(
 SqliteConnection connection, SqliteTransaction transaction,
ReferenceCorpusAnalysisWorkItemReservation reservation, int tokensSpent,
 ReferenceCorpusAnalysisCommitDisposition disposition,
 DateTimeOffset now, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_job_attempts
 SET tokens_spent=tokens_spent+$tokens_spent,
 completed_at=CASE WHEN $closes_attempt=1 THEN $now ELSE completed_at END,
 outcome=CASE WHEN $closes_attempt=1 THEN $outcome ELSE outcome END
 WHERE job_id=$job_id AND attempt_no=$attempt_no AND worker_id=$worker_id
 AND lease_token=$lease_token AND completed_at IS NULL;
 """;
 AddAttemptParameters(command, reservation);
 Add(command, "$tokens_spent", tokensSpent);
 Add(command, "$closes_attempt", disposition == ReferenceCorpusAnalysisCommitDisposition.Continue ? 0 : 1);
 Add(command, "$outcome", disposition switch
 {
 ReferenceCorpusAnalysisCommitDisposition.Complete => ReferenceCorpusAnalysisJobStatuses.Completed,
 ReferenceCorpusAnalysisCommitDisposition.PauseAfterCommit => ReferenceCorpusAnalysisJobStatuses.Paused,
 ReferenceCorpusAnalysisCommitDisposition.CancelAfterCommit => ReferenceCorpusAnalysisJobStatuses.Cancelled,
 ReferenceCorpusAnalysisCommitDisposition.BudgetExhaustedAfterCommit => ReferenceCorpusAnalysisJobStatuses.BudgetExhausted,
 _ => ReferenceCorpusAnalysisJobStatuses.Running
 });
 Add(command, "$now", ToDb(now));
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis attempt changed during work-item commit.");
 }

 private static void AddReservationParameters(
 SqliteCommand command, ReferenceCorpusAnalysisWorkItemReservation reservation)
 {
 Add(command, "$snapshot_id", reservation.InputSnapshotId);
 Add(command, "$ordinal", reservation.Ordinal);
 Add(command, "$node_id", reservation.NodeId);
 Add(command, "$feature_family", reservation.FeatureFamily);
 Add(command, "$node_text_hash", reservation.NodeTextHash);
 Add(command, "$worker_id", reservation.WorkerId);
 Add(command, "$lease_token", reservation.LeaseToken);
 Add(command, "$attempt_no", reservation.AttemptNumber);
 Add(command, "$invocation_no", reservation.InvocationNumber);
 Add(command, "$reserved_tokens", reservation.ReservedTokens);
 }

 private static void AddAttemptParameters(
 SqliteCommand command, ReferenceCorpusAnalysisWorkItemReservation reservation)
 {
 Add(command, "$job_id", reservation.JobId);
 Add(command, "$attempt_no", reservation.AttemptNumber);
 Add(command, "$worker_id", reservation.WorkerId);
 Add(command, "$lease_token", reservation.LeaseToken);
 }
}
