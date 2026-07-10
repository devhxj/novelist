using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 private const string CompletionColumns =
 "completion_key,job_id,run_id,input_snapshot_id,ordinal,invocation_no," +
 "attempt_no,reserved_tokens,output_kind,output_payload_json,output_payload_hash," +
 "tokens_spent,diagnostics_json,model_completed_at";

public async ValueTask RecordCompletionAsync(
 ReferenceCorpusAnalysisWorkItemReservation reservation,
 ReferenceCorpusAnalysisCompletionEnvelope completion,
 DateTimeOffset now,
 CancellationToken cancellationToken = default)
 {
 ValidateCompletion(reservation, completion);
await using var connection = await OpenConnectionAsync(cancellationToken);
await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var existing = await ReadCompletionAsync(
 connection, transaction, completion.CompletionKey, cancellationToken);
 if (existing is not null)
 {
 if (existing != completion)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis completion key conflicts with a different payload.");
 await transaction.CommitAsync(cancellationToken);
 return;
 }

var job = await ReadJobAsync(connection, transaction, reservation.JobId, cancellationToken)
 ?? throw new KeyNotFoundException($"Analysis job '{reservation.JobId}' was not found.");
 ValidateCommitFence(job, reservation, now);
 await ValidateWorkItemFenceAsync(connection, transaction, reservation, cancellationToken);
 await ValidateAttemptFenceAsync(connection, transaction, reservation, cancellationToken);

 await using (var insert = connection.CreateCommand())
 {
 insert.Transaction = transaction;
 insert.CommandText = """
 INSERT INTO reference_analysis_work_item_completions
 (completion_key,job_id,run_id,input_snapshot_id,ordinal,invocation_no,
 attempt_no,reserved_tokens,output_kind,output_payload_json,output_payload_hash,
 tokens_spent,diagnostics_json,model_completed_at)
 VALUES
 ($completion_key,$job_id,$run_id,$snapshot_id,$ordinal,$invocation_no,
 $attempt_no,$reserved_tokens,$output_kind,$payload_json,$payload_hash,
 $tokens_spent,$diagnostics_json,$completed_at)
 ON CONFLICT(completion_key) DO NOTHING;
 """;
 AddCompletionParameters(insert, completion);
 await insert.ExecuteNonQueryAsync(cancellationToken);
 }

 await EnsureMatchingCompletionAsync(connection, transaction, completion, cancellationToken);
 await using (var work = connection.CreateCommand())
 {
 work.Transaction = transaction;
 work.CommandText = """
 UPDATE reference_analysis_work_items
 SET work_state='output_ready'
 WHERE input_snapshot_id=$snapshot_id AND ordinal=$ordinal
 AND work_state='in_progress' AND execution_worker_id=$worker_id
 AND execution_lease_token=$lease_token AND execution_attempt_no=$attempt_no
 AND invocation_no=$invocation_no AND reserved_tokens=$reserved_tokens;
 """;
 AddReservationParameters(work, reservation);
 if (await work.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis work item changed while recording completion.");
 }

 await transaction.CommitAsync(cancellationToken);
 }

public async ValueTask<ReferenceCorpusAnalysisCompletionEnvelope?> ReadNextUnfinalizedCompletionAsync(
string jobId,
CancellationToken cancellationToken = default)
 {
 ValidateId(jobId, nameof(jobId));
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var command = connection.CreateCommand();
 command.CommandText = $"""
 SELECT {CompletionColumns}
 FROM reference_analysis_work_item_completions
 WHERE job_id=$job_id AND finalized_at IS NULL
 ORDER BY ordinal LIMIT 1;
 """;
 Add(command, "$job_id", jobId);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
return await reader.ReadAsync(cancellationToken) ? ReadCompletion(reader) : null;
}

 public async ValueTask<ReferenceCorpusAnalysisCompletionEnvelope?> ReadNextUnfinalizedCompletionAsync(
 CancellationToken cancellationToken = default)
 {
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var command = connection.CreateCommand();
 command.CommandText = $"""
            SELECT {CompletionColumns}
            FROM reference_analysis_work_item_completions
            WHERE finalized_at IS NULL
            ORDER BY model_completed_at,job_id,ordinal
            LIMIT 1;
 """;
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 return await reader.ReadAsync(cancellationToken) ? ReadCompletion(reader) : null;
 }

 public async ValueTask<ReferenceCorpusAnalysisJob> FinalizeCompletionAsync(
 ReferenceCorpusAnalysisCompletionEnvelope completion,
 DateTimeOffset now,
 Func<SqliteConnection, SqliteTransaction, ReferenceCorpusAnalysisCompletionEnvelope,
 CancellationToken, ValueTask> persistOutputAsync,
 CancellationToken cancellationToken = default)
 {
 ArgumentNullException.ThrowIfNull(completion);
 ArgumentNullException.ThrowIfNull(persistOutputAsync);
 ValidateStoredCompletion(completion);

 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var persisted = await ReadCompletionAsync(
 connection, transaction, completion.CompletionKey, cancellationToken)
 ?? throw new KeyNotFoundException(
 $"Analysis completion '{completion.CompletionKey}' was not found.");
 if (persisted != completion)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis completion payload changed before finalize.");

 var job = await ReadJobAsync(connection, transaction, completion.JobId, cancellationToken)
 ?? throw new KeyNotFoundException($"Analysis job '{completion.JobId}' was not found.");
 if (await CompletionAlreadyFinalizedAsync(
 connection, transaction, completion.CompletionKey, cancellationToken))
 {
 await transaction.CommitAsync(cancellationToken);
 return job;
 }

 ValidateFinalizeJob(job, completion);
 await ValidateOutputReadyAsync(connection, transaction, completion, cancellationToken);
 await persistOutputAsync(connection, transaction, completion, cancellationToken);

 var disposition = ResolveCommitDisposition(job, completion.TokensSpent);
 await FinalizeWorkItemAsync(connection, transaction, completion, now, cancellationToken);
 await FinalizeJobAsync(connection, transaction, job, completion, disposition, now, cancellationToken);
 await FinalizeAttemptAsync(connection, transaction, completion, disposition, now, cancellationToken);
 await MarkCompletionFinalizedAsync(connection, transaction, completion.CompletionKey, now, cancellationToken);
 await SyncCanonicalRunAsync(connection, transaction, completion.JobId, cancellationToken);

 var result = await ReadJobAsync(connection, transaction, completion.JobId, cancellationToken)
 ?? throw new InvalidOperationException("Analysis job disappeared during completion finalize.");
 await transaction.CommitAsync(cancellationToken);
 return result;
 }

 private static void ValidateCompletion(
 ReferenceCorpusAnalysisWorkItemReservation reservation,
 ReferenceCorpusAnalysisCompletionEnvelope completion)
 {
 ValidateStoredCompletion(completion);
 if (completion.CompletionKey != ReferenceCorpusAnalysisCompletionCodec.CreateKey(
 reservation.InputSnapshotId, reservation.Ordinal, reservation.InvocationNumber) ||
 completion.JobId != reservation.JobId || completion.RunId != reservation.RunId ||
 completion.InputSnapshotId != reservation.InputSnapshotId ||
 completion.Ordinal != reservation.Ordinal ||
 completion.InvocationNumber != reservation.InvocationNumber ||
 completion.AttemptNumber != reservation.AttemptNumber ||
 completion.ReservedTokens != reservation.ReservedTokens)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "analysis_completion_corrupt: completion identity does not match reservation.");
 }

 private static void ValidateStoredCompletion(ReferenceCorpusAnalysisCompletionEnvelope completion)
 {
 if (completion.AttemptNumber <= 0 || completion.ReservedTokens <= 0 ||
 completion.TokensSpent < 0 ||
 completion.OutputPayloadHash != ReferenceCorpusAnalysisCompletionCodec.Hash(completion.OutputPayloadJson) ||
 completion.OutputKind is not (ReferenceCorpusAnalysisCompletionKinds.FeatureObservations or
 ReferenceCorpusAnalysisCompletionKinds.TechniqueSpecimen))
 throw new ReferenceCorpusAnalysisJobConflictException(
 "analysis_completion_corrupt: completion payload is invalid.");
 }

 private static void ValidateFinalizeJob(
 ReferenceCorpusAnalysisJob job,
 ReferenceCorpusAnalysisCompletionEnvelope completion)
 {
 if (job.RunId != completion.RunId || job.InputSnapshotId != completion.InputSnapshotId ||
 job.AttemptCount != completion.AttemptNumber ||
 job.Status is not (ReferenceCorpusAnalysisJobStatuses.Running or
 ReferenceCorpusAnalysisJobStatuses.PauseRequested or
 ReferenceCorpusAnalysisJobStatuses.CancelRequested))
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis completion no longer matches the active job attempt.");
 }

 private static async ValueTask FinalizeWorkItemAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisCompletionEnvelope completion,
 DateTimeOffset now,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_work_items
 SET work_state='succeeded',reserved_tokens=0,execution_worker_id=NULL,
 execution_lease_token=NULL,execution_attempt_no=NULL,
 committed_run_id=$run_id,committed_at=$now
 WHERE input_snapshot_id=$snapshot_id AND ordinal=$ordinal
 AND invocation_no=$invocation_no AND work_state='output_ready'
 AND execution_attempt_no=$attempt_no AND reserved_tokens=$reserved_tokens;
 """;
 Add(command, "$run_id", completion.RunId);
 Add(command, "$now", ToDb(now));
 Add(command, "$snapshot_id", completion.InputSnapshotId);
 Add(command, "$ordinal", completion.Ordinal);
 Add(command, "$invocation_no", completion.InvocationNumber);
 Add(command, "$attempt_no", completion.AttemptNumber);
 Add(command, "$reserved_tokens", completion.ReservedTokens);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis output-ready work item changed during finalize.");
 }

 private static async ValueTask FinalizeJobAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisJob job,
 ReferenceCorpusAnalysisCompletionEnvelope completion,
 ReferenceCorpusAnalysisCommitDisposition disposition,
 DateTimeOffset now,
 CancellationToken cancellationToken)
 {
 var targetStatus = ResolveTargetStatus(disposition);
 var releaseLease = targetStatus != ReferenceCorpusAnalysisJobStatuses.Running;
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET processed_work_items=processed_work_items+1,succeeded_work_items=succeeded_work_items+1,
 retrying_work_items=0,tokens_spent=tokens_spent+$tokens_spent,
 tokens_reserved=tokens_reserved-$reserved_tokens,resume_cursor=$resume_cursor,
 status=$target_status,completed_at=CASE WHEN $terminal=1 THEN $now ELSE NULL END,
 lease_owner=CASE WHEN $release_lease=1 THEN NULL ELSE lease_owner END,
 lease_token=CASE WHEN $release_lease=1 THEN NULL ELSE lease_token END,
 lease_acquired_at=CASE WHEN $release_lease=1 THEN NULL ELSE lease_acquired_at END,
 lease_expires_at=CASE WHEN $release_lease=1 THEN NULL ELSE lease_expires_at END,
 heartbeat_at=CASE WHEN $release_lease=1 THEN NULL ELSE heartbeat_at END,
 updated_at=$now,row_version=row_version+1
 WHERE job_id=$job_id AND run_id=$run_id AND input_snapshot_id=$snapshot_id
 AND status=$expected_status AND attempt_count=$attempt_no
 AND processed_work_items=$processed AND succeeded_work_items=$succeeded
 AND tokens_reserved>=$reserved_tokens;
 """;
 Add(command, "$tokens_spent", completion.TokensSpent);
 Add(command, "$reserved_tokens", completion.ReservedTokens);
 Add(command, "$resume_cursor", (completion.Ordinal + 1).ToString(
 System.Globalization.CultureInfo.InvariantCulture));
 Add(command, "$target_status", targetStatus);
 Add(command, "$terminal", targetStatus is ReferenceCorpusAnalysisJobStatuses.Completed or
 ReferenceCorpusAnalysisJobStatuses.Cancelled ? 1 : 0);
 Add(command, "$release_lease", releaseLease ? 1 : 0);
 Add(command, "$now", ToDb(now));
 Add(command, "$job_id", completion.JobId);
 Add(command, "$run_id", completion.RunId);
 Add(command, "$snapshot_id", completion.InputSnapshotId);
 Add(command, "$expected_status", job.Status);
 Add(command, "$attempt_no", completion.AttemptNumber);
 Add(command, "$processed", job.ProcessedWorkItems);
 Add(command, "$succeeded", job.SucceededWorkItems);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis job changed during completion finalize.");
 }

 private static async ValueTask FinalizeAttemptAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisCompletionEnvelope completion,
 ReferenceCorpusAnalysisCommitDisposition disposition,
 DateTimeOffset now,
 CancellationToken cancellationToken)
 {
 var closesAttempt = disposition != ReferenceCorpusAnalysisCommitDisposition.Continue;
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_job_attempts
 SET tokens_spent=tokens_spent+$tokens_spent,
 completed_at=CASE WHEN $close_attempt=1 THEN $now ELSE completed_at END,
 outcome=CASE WHEN $close_attempt=1 THEN $outcome ELSE outcome END
 WHERE job_id=$job_id AND attempt_no=$attempt_no AND completed_at IS NULL;
 """;
 Add(command, "$tokens_spent", completion.TokensSpent);
 Add(command, "$close_attempt", closesAttempt ? 1 : 0);
 Add(command, "$now", ToDb(now));
 Add(command, "$outcome", ResolveTargetStatus(disposition));
 Add(command, "$job_id", completion.JobId);
 Add(command, "$attempt_no", completion.AttemptNumber);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis attempt changed during completion finalize.");
 }

 private static async ValueTask MarkCompletionFinalizedAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string completionKey,
 DateTimeOffset now,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_work_item_completions
 SET finalized_at=$now
 WHERE completion_key=$key AND finalized_at IS NULL;
 """;
 Add(command, "$now", ToDb(now));
 Add(command, "$key", completionKey);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis completion changed during finalize.");
 }

 private static string ResolveTargetStatus(ReferenceCorpusAnalysisCommitDisposition disposition) =>
 disposition switch
 {
 ReferenceCorpusAnalysisCommitDisposition.Complete => ReferenceCorpusAnalysisJobStatuses.Completed,
 ReferenceCorpusAnalysisCommitDisposition.PauseAfterCommit => ReferenceCorpusAnalysisJobStatuses.Paused,
 ReferenceCorpusAnalysisCommitDisposition.CancelAfterCommit => ReferenceCorpusAnalysisJobStatuses.Cancelled,
 ReferenceCorpusAnalysisCommitDisposition.BudgetExhaustedAfterCommit =>
 ReferenceCorpusAnalysisJobStatuses.BudgetExhausted,
 _ => ReferenceCorpusAnalysisJobStatuses.Running
 };

 private static void AddCompletionParameters(
 SqliteCommand command,
 ReferenceCorpusAnalysisCompletionEnvelope completion)
 {
 Add(command, "$completion_key", completion.CompletionKey);
 Add(command, "$job_id", completion.JobId);
 Add(command, "$run_id", completion.RunId);
 Add(command, "$snapshot_id", completion.InputSnapshotId);
 Add(command, "$ordinal", completion.Ordinal);
 Add(command, "$invocation_no", completion.InvocationNumber);
 Add(command, "$attempt_no", completion.AttemptNumber);
 Add(command, "$reserved_tokens", completion.ReservedTokens);
 Add(command, "$output_kind", completion.OutputKind);
 Add(command, "$payload_json", completion.OutputPayloadJson);
 Add(command, "$payload_hash", completion.OutputPayloadHash);
 Add(command, "$tokens_spent", completion.TokensSpent);
 Add(command, "$diagnostics_json", completion.DiagnosticsJson);
 Add(command, "$completed_at", ToDb(completion.ModelCompletedAt));
 }

 private static async ValueTask EnsureMatchingCompletionAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisCompletionEnvelope completion,
 CancellationToken cancellationToken)
 {
 var existing = await ReadCompletionAsync(
 connection, transaction, completion.CompletionKey, cancellationToken);
 if (existing != completion)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis completion key conflicts with a different payload.");
 }

 private static async ValueTask<ReferenceCorpusAnalysisCompletionEnvelope?> ReadCompletionAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string key,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = $"""
 SELECT {CompletionColumns}
 FROM reference_analysis_work_item_completions
 WHERE completion_key=$key;
 """;
 Add(command, "$key", key);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 return await reader.ReadAsync(cancellationToken) ? ReadCompletion(reader) : null;
 }

 private static ReferenceCorpusAnalysisCompletionEnvelope ReadCompletion(SqliteDataReader reader) => new(
 reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
 reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
 reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetInt32(11),
 reader.GetString(12), ParseTimestamp(reader.GetString(13)));

 private static async ValueTask ValidateOutputReadyAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisCompletionEnvelope completion,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 SELECT 1 FROM reference_analysis_work_items
 WHERE input_snapshot_id=$snapshot_id AND ordinal=$ordinal
 AND invocation_no=$invocation_no AND work_state='output_ready'
 AND execution_attempt_no=$attempt_no AND reserved_tokens=$reserved_tokens;
 """;
 Add(command, "$snapshot_id", completion.InputSnapshotId);
 Add(command, "$ordinal", completion.Ordinal);
 Add(command, "$invocation_no", completion.InvocationNumber);
 Add(command, "$attempt_no", completion.AttemptNumber);
 Add(command, "$reserved_tokens", completion.ReservedTokens);
 if (await command.ExecuteScalarAsync(cancellationToken) is null)
 throw new ReferenceCorpusAnalysisJobConflictException(
 "Analysis completion work item is not output-ready.");
 }

 private static async ValueTask<bool> CompletionAlreadyFinalizedAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string completionKey,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 SELECT CASE WHEN finalized_at IS NULL THEN 0 ELSE 1 END
 FROM reference_analysis_work_item_completions
 WHERE completion_key=$key;
 """;
 Add(command, "$key", completionKey);
 return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
 }
}
