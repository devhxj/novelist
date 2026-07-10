using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 public async ValueTask<ReferenceCorpusAnalysisWorkItemSettlementResult> SettleWorkItemAsync(
 ReferenceCorpusAnalysisWorkItemSettlementRequest request,
 CancellationToken cancellationToken = default)
 {
 ArgumentNullException.ThrowIfNull(request);
 ArgumentNullException.ThrowIfNull(request.Reservation);
 ValidateSettlementRequest(request);

 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var job = await ReadJobAsync(connection, transaction, request.Reservation.JobId, cancellationToken)
 ?? throw new KeyNotFoundException($"Analysis job '{request.Reservation.JobId}' was not found.");
 ValidateSettlementFence(job, request);
 await ValidateWorkItemFenceAsync(connection, transaction, request.Reservation, cancellationToken);
 await ValidateAttemptFenceAsync(connection, transaction, request.Reservation, cancellationToken);

 var (jobStatus, workItemState, attemptOutcome) = ResolveSettlement(request.Kind);
 await SettleWorkItemRowAsync(connection, transaction, request.Reservation, workItemState, cancellationToken);
 await SettleJobRowAsync(connection, transaction, job, request, jobStatus, cancellationToken);
await SettleAttemptRowAsync(connection, transaction, request, attemptOutcome, cancellationToken);
 await SyncCanonicalRunAsync(connection, transaction, request.Reservation.JobId, cancellationToken);

 var settled = await ReadJobAsync(connection, transaction, request.Reservation.JobId, cancellationToken)
 ?? throw new InvalidOperationException($"Settled analysis job '{request.Reservation.JobId}' disappeared.");
 await transaction.CommitAsync(cancellationToken);
 return new ReferenceCorpusAnalysisWorkItemSettlementResult(settled, workItemState, attemptOutcome);
 }

 private static void ValidateSettlementRequest(ReferenceCorpusAnalysisWorkItemSettlementRequest request)
 {
 if (!Enum.IsDefined(request.Kind))
 throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown work-item settlement kind.");
 if (request.TokensSpent < 0 || request.TokensSpent > request.Reservation.ReservedTokens)
 throw new ArgumentOutOfRangeException(nameof(request), "Tokens spent must fit within the reservation.");
 if (request.ErrorCode is { Length: > MaxErrorCodeLength } || request.ErrorCode?.Any(char.IsControl) == true)
 throw new ArgumentException($"Error code must be at most {MaxErrorCodeLength} non-control characters.", nameof(request));
 if (request.ErrorMessage is { Length: > MaxErrorMessageLength } || request.ErrorMessage?.Any(char.IsControl) == true)
 throw new ArgumentException($"Error message must be at most {MaxErrorMessageLength} non-control characters.", nameof(request));

 var isFailure = request.Kind is ReferenceCorpusAnalysisWorkItemSettlementKind.RetryableFailure
 or ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure;
 if (isFailure && string.IsNullOrWhiteSpace(request.ErrorCode))
 throw new ArgumentException("Failure settlements require an error code.", nameof(request));
 if (request.Kind == ReferenceCorpusAnalysisWorkItemSettlementKind.RetryableFailure)
 {
 if (request.NextAttemptAt is null || request.NextAttemptAt <= request.SettledAt)
 throw new ArgumentException("Retryable failure requires a future next-attempt timestamp.", nameof(request));
 }
 else if (request.NextAttemptAt is not null)
 {
 throw new ArgumentException("Only retryable failure can set a next-attempt timestamp.", nameof(request));
 }
 }

 private static void ValidateSettlementFence(
 ReferenceCorpusAnalysisJob job,
 ReferenceCorpusAnalysisWorkItemSettlementRequest request)
 {
 var reservation = request.Reservation;
 if (!string.Equals(job.RunId, reservation.RunId, StringComparison.Ordinal) ||
 !string.Equals(job.InputSnapshotId, reservation.InputSnapshotId, StringComparison.Ordinal) ||
 job.AttemptCount != reservation.AttemptNumber)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis reservation no longer matches its job attempt.");

 var requiredStatus = request.Kind switch
 {
 ReferenceCorpusAnalysisWorkItemSettlementKind.PauseBoundary => ReferenceCorpusAnalysisJobStatuses.PauseRequested,
 ReferenceCorpusAnalysisWorkItemSettlementKind.CancelBoundary => ReferenceCorpusAnalysisJobStatuses.CancelRequested,
 _ => ReferenceCorpusAnalysisJobStatuses.Running
 };
 if (!string.Equals(job.Status, requiredStatus, StringComparison.Ordinal) ||
 !string.Equals(job.LeaseOwner, reservation.WorkerId, StringComparison.Ordinal) ||
 !string.Equals(job.LeaseToken, reservation.LeaseToken, StringComparison.Ordinal) ||
 job.LeaseExpiresAt is null || job.LeaseExpiresAt <= request.SettledAt)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"Analysis settlement requires an active '{requiredStatus}' lease owned by its reservation worker.");
 }

 private static (string JobStatus, string WorkItemState, string AttemptOutcome) ResolveSettlement(
 ReferenceCorpusAnalysisWorkItemSettlementKind kind) => kind switch
 {
 ReferenceCorpusAnalysisWorkItemSettlementKind.RetryableFailure =>
 (ReferenceCorpusAnalysisJobStatuses.RetryWait, "pending", "retryable_failure"),
 ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure =>
 (ReferenceCorpusAnalysisJobStatuses.Failed, "failed", "permanent_failure"),
 ReferenceCorpusAnalysisWorkItemSettlementKind.BudgetExhausted =>
 (ReferenceCorpusAnalysisJobStatuses.BudgetExhausted, "pending", "budget_exhausted"),
 ReferenceCorpusAnalysisWorkItemSettlementKind.PauseBoundary =>
 (ReferenceCorpusAnalysisJobStatuses.Paused, "pending", "paused"),
 ReferenceCorpusAnalysisWorkItemSettlementKind.CancelBoundary =>
 (ReferenceCorpusAnalysisJobStatuses.Cancelled, "pending", "cancelled"),
 _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown work-item settlement kind.")
 };

 private static async ValueTask SettleWorkItemRowAsync(
 SqliteConnection connection, SqliteTransaction transaction,
 ReferenceCorpusAnalysisWorkItemReservation reservation, string workItemState,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_work_items
 SET work_state=$work_state,reserved_tokens=0,execution_worker_id=NULL,
 execution_lease_token=NULL,execution_attempt_no=NULL
 WHERE input_snapshot_id=$snapshot_id AND ordinal=$ordinal AND node_id=$node_id
 AND feature_family=$feature_family AND node_text_hash=$node_text_hash
 AND work_state='in_progress' AND execution_worker_id=$worker_id
 AND execution_lease_token=$lease_token AND execution_attempt_no=$attempt_no
 AND invocation_no=$invocation_no AND reserved_tokens=$reserved_tokens;
 """;
 AddReservationParameters(command, reservation);
 Add(command, "$work_state", workItemState);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis work item changed during settlement.");
 }

 private static async ValueTask SettleJobRowAsync(
 SqliteConnection connection, SqliteTransaction transaction,
 ReferenceCorpusAnalysisJob job, ReferenceCorpusAnalysisWorkItemSettlementRequest request,
 string jobStatus, CancellationToken cancellationToken)
 {
 var reservation = request.Reservation;
 var permanentFailure = request.Kind == ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure;
 var terminal = request.Kind is ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure
 or ReferenceCorpusAnalysisWorkItemSettlementKind.CancelBoundary;
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET status=$status,
 processed_work_items=processed_work_items+$processed_delta,
 failed_work_items=failed_work_items+$failed_delta,
 retrying_work_items=$retrying_items,
 tokens_spent=tokens_spent+$tokens_spent,
 tokens_reserved=tokens_reserved-$reserved_tokens,
 next_attempt_at=$next_attempt_at,
 completed_at=$completed_at,
 lease_owner=NULL,lease_token=NULL,lease_acquired_at=NULL,lease_expires_at=NULL,heartbeat_at=NULL,
 last_error_code=$error_code,last_error_message=$error_message,
 updated_at=$now,row_version=row_version+1
 WHERE job_id=$job_id AND run_id=$run_id AND input_snapshot_id=$snapshot_id
 AND status=$expected_status AND lease_owner=$worker_id AND lease_token=$lease_token
 AND lease_expires_at>$now AND attempt_count=$attempt_no
 AND tokens_reserved>=$reserved_tokens
 AND processed_work_items=$processed_work_items AND failed_work_items=$failed_work_items;
 """;
 Add(command, "$status", jobStatus);
 Add(command, "$processed_delta", permanentFailure ? 1 : 0);
Add(command, "$failed_delta", permanentFailure ? 1 : 0);
 Add(command, "$retrying_items",
 request.Kind == ReferenceCorpusAnalysisWorkItemSettlementKind.RetryableFailure ? 1 : 0);
 Add(command, "$tokens_spent", request.TokensSpent);
 Add(command, "$reserved_tokens", reservation.ReservedTokens);
 Add(command, "$next_attempt_at", request.NextAttemptAt is { } retryAt ? ToDb(retryAt) : null);
 Add(command, "$completed_at", terminal ? ToDb(request.SettledAt) : null);
 Add(command, "$error_code", request.ErrorCode);
 Add(command, "$error_message", request.ErrorMessage);
 Add(command, "$now", ToDb(request.SettledAt));
 Add(command, "$job_id", reservation.JobId);
 Add(command, "$run_id", reservation.RunId);
 Add(command, "$snapshot_id", reservation.InputSnapshotId);
 Add(command, "$expected_status", job.Status);
 Add(command, "$worker_id", reservation.WorkerId);
 Add(command, "$lease_token", reservation.LeaseToken);
 Add(command, "$attempt_no", reservation.AttemptNumber);
 Add(command, "$processed_work_items", job.ProcessedWorkItems);
 Add(command, "$failed_work_items", job.FailedWorkItems);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis job changed during work-item settlement.");
 }

 private static async ValueTask SettleAttemptRowAsync(
 SqliteConnection connection, SqliteTransaction transaction,
 ReferenceCorpusAnalysisWorkItemSettlementRequest request, string outcome,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_job_attempts
 SET tokens_spent=tokens_spent+$tokens_spent,completed_at=$now,outcome=$outcome,
 error_code=$error_code,error_message=$error_message
 WHERE job_id=$job_id AND attempt_no=$attempt_no AND worker_id=$worker_id
 AND lease_token=$lease_token AND completed_at IS NULL;
 """;
 AddAttemptParameters(command, request.Reservation);
 Add(command, "$tokens_spent", request.TokensSpent);
 Add(command, "$now", ToDb(request.SettledAt));
 Add(command, "$outcome", outcome);
 Add(command, "$error_code", request.ErrorCode);
 Add(command, "$error_message", request.ErrorMessage);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis attempt changed during work-item settlement.");
 }
}
