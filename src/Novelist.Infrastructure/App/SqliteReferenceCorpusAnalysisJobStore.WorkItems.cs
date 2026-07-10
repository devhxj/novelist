using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
public async ValueTask<ReferenceCorpusAnalysisWorkItemReservation?> ReserveNextWorkItemAsync(
 string jobId,
 string workerId,
 string leaseToken,
 int tokenReservation,
 DateTimeOffset now,
 CancellationToken cancellationToken = default)
 {
 ValidateId(jobId, nameof(jobId));
 ValidateId(workerId, nameof(workerId));
 ValidateId(leaseToken, nameof(leaseToken));
 if (tokenReservation <= 0) throw new ArgumentOutOfRangeException(nameof(tokenReservation));

 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var job = await ReadJobAsync(connection, transaction, jobId, cancellationToken)
 ?? throw new KeyNotFoundException($"Analysis job '{jobId}' was not found.");
 ValidateActiveLease(job, workerId, leaseToken, now);
 if (job.TokenBudget is { } budget && budget - job.TokensSpent < tokenReservation)
 {
 var reserved = await ReadTokensReservedAsync(connection, transaction, jobId, cancellationToken);
 if (budget - job.TokensSpent - reserved < tokenReservation)
 throw new InvalidOperationException("Analysis token budget cannot cover the requested reservation.");
 }

 await using var select = connection.CreateCommand();
 select.Transaction = transaction;
 select.CommandText = """
 SELECT work.ordinal,work.node_id,work.chapter_node_id,work.feature_family,work.node_text_hash,
 node.text_hash,work.invocation_no,work.input_payload_json,work.input_payload_hash
 FROM reference_analysis_work_items AS work
 JOIN reference_analysis_jobs AS job ON job.input_snapshot_id=work.input_snapshot_id
 JOIN reference_text_nodes AS node ON node.node_id=work.node_id
 WHERE job.job_id=$job_id AND work.work_state='pending'
 ORDER BY work.ordinal
 LIMIT 1;
 """;
 Add(select, "$job_id", jobId);
 await using var reader = await select.ExecuteReaderAsync(cancellationToken);
 if (!await reader.ReadAsync(cancellationToken))
 {
 await transaction.CommitAsync(cancellationToken);
 return null;
 }
 var ordinal = reader.GetInt32(0);
 var nodeId = reader.GetString(1);
 var chapterNodeId = reader.IsDBNull(2) ? null : reader.GetString(2);
 var featureFamily = reader.GetString(3);
 var frozenHash = reader.GetString(4);
 var currentHash = reader.GetString(5);
var invocationNumber = reader.GetInt32(6) + 1;
 var inputPayloadJson = reader.IsDBNull(7) ? null : reader.GetString(7);
 var inputPayloadHash = reader.IsDBNull(8) ? null : reader.GetString(8);
await reader.DisposeAsync();
 if (inputPayloadJson is null || inputPayloadHash is null)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"legacy_snapshot_not_executable: work item '{job.InputSnapshotId}/{ordinal}' has no frozen input payload.");
 string computedPayloadHash;
 try
 {
 computedPayloadHash = ComputeInputPayloadHash(inputPayloadJson);
 }
 catch (JsonException exception)
 {
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"analysis_snapshot_corrupt: work item '{job.InputSnapshotId}/{ordinal}' contains invalid frozen payload JSON ({exception.Message}).");
 }
 if (!string.Equals(inputPayloadHash, computedPayloadHash, StringComparison.Ordinal))
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"analysis_snapshot_corrupt: work item '{job.InputSnapshotId}/{ordinal}' frozen payload hash does not match.");
if (!string.Equals(frozenHash, currentHash, StringComparison.Ordinal))
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"analysis_snapshot_stale: node '{nodeId}' changed after job enqueue.");

 await using var updateWork = connection.CreateCommand();
 updateWork.Transaction = transaction;
 updateWork.CommandText = """
 UPDATE reference_analysis_work_items
 SET work_state='in_progress',execution_worker_id=$worker_id,execution_lease_token=$lease_token,
 execution_attempt_no=$attempt_no,invocation_no=$invocation_no,reserved_tokens=$reserved_tokens
 WHERE input_snapshot_id=$snapshot_id AND ordinal=$ordinal AND work_state='pending';
 """;
 Add(updateWork, "$worker_id", workerId);
 Add(updateWork, "$lease_token", leaseToken);
 Add(updateWork, "$attempt_no", job.AttemptCount);
 Add(updateWork, "$invocation_no", invocationNumber);
 Add(updateWork, "$reserved_tokens", tokenReservation);
 Add(updateWork, "$snapshot_id", job.InputSnapshotId);
 Add(updateWork, "$ordinal", ordinal);
 if (await updateWork.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis work item was claimed concurrently.");

 await using var updateJob = connection.CreateCommand();
 updateJob.Transaction = transaction;
 updateJob.CommandText = """
 UPDATE reference_analysis_jobs
 SET tokens_reserved=tokens_reserved+$reserved_tokens,updated_at=$now,row_version=row_version+1
 WHERE job_id=$job_id AND status='running' AND lease_owner=$worker_id AND lease_token=$lease_token
 AND lease_expires_at>$now
 AND (token_budget IS NULL OR tokens_spent+tokens_reserved+$reserved_tokens<=token_budget);
 """;
 Add(updateJob, "$reserved_tokens", tokenReservation);
 Add(updateJob, "$now", ToDb(now));
 Add(updateJob, "$job_id", jobId);
 Add(updateJob, "$worker_id", workerId);
 Add(updateJob, "$lease_token", leaseToken);
 if (await updateJob.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException("Analysis lease or token budget changed during reservation.");

 await transaction.CommitAsync(cancellationToken);
return new ReferenceCorpusAnalysisWorkItemReservation(
job.JobId,job.RunId,job.InputSnapshotId,ordinal,nodeId,chapterNodeId,featureFamily,frozenHash,
 inputPayloadJson,inputPayloadHash,job.AttemptCount,invocationNumber,tokenReservation,workerId,leaseToken);
 }

 private static void ValidateActiveLease(
 ReferenceCorpusAnalysisJob job,string workerId,string leaseToken,DateTimeOffset now)
 {
 if (job.Status != ReferenceCorpusAnalysisJobStatuses.Running ||
 !string.Equals(job.LeaseOwner, workerId, StringComparison.Ordinal) ||
 !string.Equals(job.LeaseToken, leaseToken, StringComparison.Ordinal) ||
 job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"Lease for analysis job '{job.JobId}' is missing, expired, or owned by another worker.");
 }

 private static async ValueTask<int> ReadTokensReservedAsync(
 SqliteConnection connection,SqliteTransaction transaction,string jobId,CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "SELECT tokens_reserved FROM reference_analysis_jobs WHERE job_id=$job_id;";
 Add(command, "$job_id", jobId);
 return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
 }
}
