using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
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
