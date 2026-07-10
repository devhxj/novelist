using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 private static async ValueTask EnsureCanonicalRunAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisInputSnapshot snapshot,
 ReferenceCorpusAnalysisJobEnqueue request,
 CancellationToken cancellationToken)
 {
 await using var select = connection.CreateCommand();
 select.Transaction = transaction;
 select.CommandText = """
 SELECT anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,
 token_budget,tokens_spent,resume_cursor,started_at,completed_at
 FROM reference_analysis_runs
 WHERE run_id=$run_id;
 """;
 Add(select, "$run_id", request.RunId);
 await using var reader = await select.ExecuteReaderAsync(cancellationToken);
 if (await reader.ReadAsync(cancellationToken))
 {
 var compatible = reader.GetInt64(0) == request.AnchorId
 && string.Equals(reader.GetString(1), snapshot.AnalyzerVersion, StringComparison.Ordinal)
 && string.Equals(reader.GetString(2), snapshot.SchemaVersion, StringComparison.Ordinal)
 && string.Equals(reader.GetString(3), snapshot.ModelProvider, StringComparison.Ordinal)
 && string.Equals(reader.GetString(4), snapshot.ModelId, StringComparison.Ordinal)
 && string.Equals(reader.GetString(5), snapshot.Scope, StringComparison.Ordinal)
 && string.Equals(reader.GetString(6), ReferenceCorpusAnalysisRunStatuses.Running, StringComparison.Ordinal)
 && (reader.IsDBNull(7) ? null : reader.GetInt32(7)) == request.TokenBudget
&& reader.GetInt32(8) == 0
&& reader.IsDBNull(9)
 && ParseTimestamp(reader.GetString(10)) == request.QueuedAt
&& reader.IsDBNull(11);
 if (!compatible)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"Analysis run '{request.RunId}' already exists with incompatible or legacy execution metadata.");
 return;
 }

 await reader.DisposeAsync();
 await using var insert = connection.CreateCommand();
 insert.Transaction = transaction;
 insert.CommandText = """
 INSERT INTO reference_analysis_runs
 (run_id,anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,
 token_budget,tokens_spent,resume_cursor,started_at,completed_at,observation_count,diagnostics_json)
 VALUES
 ($run_id,$anchor_id,$analyzer_version,$schema_version,$model_provider,$model_id,$scope,'running',
 $token_budget,0,NULL,$started_at,NULL,0,'[]');
 """;
 Add(insert, "$run_id", request.RunId);
 Add(insert, "$anchor_id", request.AnchorId);
 Add(insert, "$analyzer_version", snapshot.AnalyzerVersion);
 Add(insert, "$schema_version", snapshot.SchemaVersion);
 Add(insert, "$model_provider", snapshot.ModelProvider);
 Add(insert, "$model_id", snapshot.ModelId);
 Add(insert, "$scope", snapshot.Scope);
 Add(insert, "$token_budget", request.TokenBudget);
 Add(insert, "$started_at", ToDb(request.QueuedAt));
 await insert.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async ValueTask SyncCanonicalRunAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string jobId,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 UPDATE reference_analysis_runs
 SET status=(
 CASE job.status
 WHEN 'paused' THEN 'paused'
 WHEN 'cancelled' THEN 'partial_completed'
 WHEN 'budget_exhausted' THEN 'budget_exhausted'
 WHEN 'completed' THEN 'completed'
 WHEN 'failed' THEN 'failed'
 ELSE 'running'
 END),
 token_budget=job.token_budget,
 tokens_spent=job.tokens_spent,
 resume_cursor=job.resume_cursor,
 completed_at=CASE WHEN job.status IN ('cancelled','completed','failed') THEN job.completed_at ELSE NULL END
 FROM reference_analysis_jobs AS job
 WHERE job.job_id=$job_id AND reference_analysis_runs.run_id=job.run_id;
 """;
 Add(command, "$job_id", jobId);
 if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"Canonical analysis run for job '{jobId}' is missing or changed.");
 }
}
