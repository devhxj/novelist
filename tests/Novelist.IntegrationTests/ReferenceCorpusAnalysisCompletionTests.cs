using Microsoft.Data.Sqlite;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusAnalysisCompletionTests
{
 [Fact]
 public async Task RecordCompletionMovesWorkItemToOutputReadyAndRoundTripsEnvelope()
 {
 await using var fixture = await CompletionFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var reservation = await fixture.CreateReservationAsync("record", now, 400);
 var completion = CreateCompletion(reservation, 275, now.AddSeconds(2));

 await fixture.Store.RecordCompletionAsync(reservation, completion, now.AddSeconds(2));

 Assert.Equal(("output_ready", 400), await fixture.ReadWorkItemAsync(reservation));
 Assert.Equal(completion, await fixture.Store.ReadNextUnfinalizedCompletionAsync(reservation.JobId));
 Assert.Equal(1, await fixture.CountAsync("reference_analysis_work_item_completions"));
 }

 [Fact]
 public async Task RecordCompletionIsIdempotentAndRejectsConflictingPayload()
 {
 await using var fixture = await CompletionFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var reservation = await fixture.CreateReservationAsync("record-idempotent", now, 400);
 var completion = CreateCompletion(reservation, 275, now.AddSeconds(2));
 await fixture.Store.RecordCompletionAsync(reservation, completion, now.AddSeconds(2));

 await fixture.Store.RecordCompletionAsync(reservation, completion, now.AddSeconds(3));
 var conflictJson = "{\"result\":\"different\"}";
 var conflicting = completion with
 {
 OutputPayloadJson = conflictJson,
 OutputPayloadHash = ReferenceCorpusAnalysisCompletionCodec.Hash(conflictJson)
 };
 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.RecordCompletionAsync(reservation, conflicting, now.AddSeconds(4)));

 Assert.Equal(1, await fixture.CountAsync("reference_analysis_work_item_completions"));
 Assert.Equal(completion, await fixture.Store.ReadNextUnfinalizedCompletionAsync(reservation.JobId));
 Assert.Equal(("output_ready", 400), await fixture.ReadWorkItemAsync(reservation));
 }

 [Fact]
 public async Task FinalizeCompletionRollsBackOutputAndSettlementWhenPersistenceFails()
 {
 await using var fixture = await CompletionFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var reservation = await fixture.CreateReservationAsync("finalize-rollback", now, 400);
 var completion = CreateCompletion(reservation, 275, now.AddSeconds(2));
 await fixture.Store.RecordCompletionAsync(reservation, completion, now.AddSeconds(2));

 await Assert.ThrowsAsync<InvalidOperationException>(async () =>
 await fixture.Store.FinalizeCompletionAsync(completion, now.AddSeconds(3),
 async (connection, transaction, _, cancellationToken) =>
 {
 await InsertProbeAsync(connection, transaction, "rolled-back", cancellationToken);
 throw new InvalidOperationException("fault injection");
 }));

 var job = await fixture.Store.GetAsync(reservation.JobId);
 Assert.NotNull(job);
 Assert.Equal(0, job.ProcessedWorkItems);
 Assert.Equal(0, job.TokensSpent);
 Assert.Equal(400, await fixture.ReadJobReservedTokensAsync(reservation.JobId));
 Assert.Equal(("output_ready", 400), await fixture.ReadWorkItemAsync(reservation));
 Assert.Equal((0, false, null), await fixture.ReadAttemptAsync(reservation));
 Assert.Equal(0, await fixture.CountAsync("completion_probe"));
 Assert.False(await fixture.IsFinalizedAsync(completion.CompletionKey));
 }

 [Fact]
 public async Task FinalizeCompletionSettlesActualTokensAttemptAndReservationExactlyOnce()
 {
 await using var fixture = await CompletionFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var reservation = await fixture.CreateReservationAsync("finalize-success", now, 400);
 var completion = CreateCompletion(reservation, 401, now.AddSeconds(2));
 await fixture.Store.RecordCompletionAsync(reservation, completion, now.AddSeconds(2));

 var finalized = await fixture.Store.FinalizeCompletionAsync(completion, now.AddSeconds(3),
 (connection, transaction, _, cancellationToken) =>
 InsertProbeAsync(connection, transaction, "persisted", cancellationToken));

 Assert.Equal(1, finalized.ProcessedWorkItems);
 Assert.Equal(1, finalized.SucceededWorkItems);
 Assert.Equal(401, finalized.TokensSpent);
 Assert.Equal(0, await fixture.ReadJobReservedTokensAsync(reservation.JobId));
 Assert.Equal((401, false, null), await fixture.ReadAttemptAsync(reservation));
 Assert.Equal(("succeeded", 0), await fixture.ReadWorkItemAsync(reservation));
 Assert.Equal(1, await fixture.CountAsync("completion_probe"));
 Assert.True(await fixture.IsFinalizedAsync(completion.CompletionKey));

 var retried = await fixture.Store.FinalizeCompletionAsync(completion, now.AddSeconds(4),
 (connection, transaction, _, cancellationToken) =>
 InsertProbeAsync(connection, transaction, "must-not-run", cancellationToken));
 Assert.Equal(finalized, retried);
 Assert.Equal(1, await fixture.CountAsync("completion_probe"));
 Assert.Equal((401, false, null), await fixture.ReadAttemptAsync(reservation));
 }

 private static ReferenceCorpusAnalysisCompletionEnvelope CreateCompletion(
 ReferenceCorpusAnalysisWorkItemReservation reservation,
 int tokensSpent,
 DateTimeOffset completedAt)
 {
 const string payload = "{\"result\":\"accepted\"}";
 return new ReferenceCorpusAnalysisCompletionEnvelope(
 ReferenceCorpusAnalysisCompletionCodec.CreateKey(
 reservation.InputSnapshotId, reservation.Ordinal, reservation.InvocationNumber),
 reservation.JobId, reservation.RunId, reservation.InputSnapshotId,
 reservation.Ordinal, reservation.InvocationNumber, reservation.AttemptNumber,
 reservation.ReservedTokens, ReferenceCorpusAnalysisCompletionKinds.FeatureObservations,
 payload, ReferenceCorpusAnalysisCompletionCodec.Hash(payload), tokensSpent, "[]", completedAt);
 }

 private static async ValueTask InsertProbeAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string value,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO completion_probe(value) VALUES ($value);";
 command.Parameters.AddWithValue("$value", value);
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private sealed class CompletionFixture : IAsyncDisposable
 {
 private CompletionFixture(string directoryPath, string databasePath)
 {
 DirectoryPath = directoryPath;
 DatabasePath = databasePath;
 Store = new SqliteReferenceCorpusAnalysisJobStore(databasePath);
 }

 public string DirectoryPath { get; }
 public string DatabasePath { get; }
 public SqliteReferenceCorpusAnalysisJobStore Store { get; }

 public static async ValueTask<CompletionFixture> CreateAsync()
 {
 var directory = Path.Combine(Path.GetTempPath(), $"novelist-completion-{Guid.NewGuid():N}");
 Directory.CreateDirectory(directory);
 var fixture = new CompletionFixture(directory, Path.Combine(directory, "novelist.db"));
 await fixture.Store.EnsureSchemaAsync();
 await fixture.ExecuteAsync("""
 INSERT INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,
 source_file_hash,build_version,status,created_at,updated_at)
 VALUES
 (101,7,'fixture','fixture','fixture.txt','txt','user_owned',
 'source-hash','v2','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');

 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,
 chapter_index,start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES
 ('node-1',101,NULL,'sentence',0,0,1,0,3,3,'hash-1','节点一','2026-07-10T00:00:00Z'),
 ('node-2',101,NULL,'sentence',1,0,1,3,6,3,'hash-2','节点二','2026-07-10T00:00:00Z');

 CREATE TABLE completion_probe(value TEXT NOT NULL);
 """);
 return fixture;
 }

 public async ValueTask<ReferenceCorpusAnalysisWorkItemReservation> CreateReservationAsync(
 string suffix, DateTimeOffset now, int reservedTokens)
 {
 var snapshotId = $"snapshot-{suffix}";
 var jobId = $"job-{suffix}";
 const string firstPayload = "{\"text\":\"节点一\"}";
 const string secondPayload = "{\"text\":\"节点二\"}";
 var workItems = new ReferenceCorpusAnalysisWorkItemSnapshot[]
 {
 new(0, "node-1", null, "syntax", "hash-1", firstPayload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(firstPayload)),
 new(1, "node-2", null, "emotion", "hash-2", secondPayload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(secondPayload))
 };
 await Store.EnqueueAsync(
 new ReferenceCorpusAnalysisInputSnapshot(snapshotId,101,"stage_2","sentence","nodes-hash",
 "[\"syntax\",\"emotion\"]","corpus-analysis-v2","feature-v2","fake","fake-model",2,2,now),
 workItems,
 new ReferenceCorpusAnalysisJobEnqueue(jobId,$"run-{suffix}",snapshotId,7,101,
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,"{\"node_type\":\"sentence\"}","input-hash",null,
 ReferenceCorpusAnalysisPriorityClasses.Normal,100,2,2,2500,"stage_2",null,3,now));
 var claim = await Store.ClaimNextAsync("worker-1", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 var reservation = await Store.ReserveNextWorkItemAsync(
 jobId, "worker-1", claim.LeaseToken, reservedTokens, now.AddSeconds(1));
 return Assert.IsType<ReferenceCorpusAnalysisWorkItemReservation>(reservation);
 }

 public async ValueTask<(string State, int ReservedTokens)> ReadWorkItemAsync(
 ReferenceCorpusAnalysisWorkItemReservation reservation)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT work_state,reserved_tokens FROM reference_analysis_work_items WHERE input_snapshot_id=$snapshot AND ordinal=$ordinal;";
 command.Parameters.AddWithValue("$snapshot", reservation.InputSnapshotId);
 command.Parameters.AddWithValue("$ordinal", reservation.Ordinal);
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
 return (reader.GetString(0), reader.GetInt32(1));
 }

 public async ValueTask<int> ReadJobReservedTokensAsync(string jobId)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT tokens_reserved FROM reference_analysis_jobs WHERE job_id=$job;";
 command.Parameters.AddWithValue("$job", jobId);
 return Convert.ToInt32(await command.ExecuteScalarAsync());
 }

 public async ValueTask<(int TokensSpent, bool Completed, string? Outcome)> ReadAttemptAsync(
 ReferenceCorpusAnalysisWorkItemReservation reservation)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT tokens_spent,completed_at,outcome FROM reference_analysis_job_attempts WHERE job_id=$job AND attempt_no=$attempt;";
 command.Parameters.AddWithValue("$job", reservation.JobId);
 command.Parameters.AddWithValue("$attempt", reservation.AttemptNumber);
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
 return (reader.GetInt32(0), !reader.IsDBNull(1), reader.IsDBNull(2) ? null : reader.GetString(2));
 }

 public async ValueTask<bool> IsFinalizedAsync(string completionKey)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT CASE WHEN finalized_at IS NULL THEN 0 ELSE 1 END FROM reference_analysis_work_item_completions WHERE completion_key=$key;";
 command.Parameters.AddWithValue("$key", completionKey);
 return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
 }

 public async ValueTask<long> CountAsync(string tableName)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
 return Convert.ToInt64(await command.ExecuteScalarAsync());
 }

 private async ValueTask ExecuteAsync(string sql)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = sql;
 await command.ExecuteNonQueryAsync();
 }

 private async ValueTask<SqliteConnection> OpenAsync()
 {
 var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
 await connection.OpenAsync();
 await using var pragma = connection.CreateCommand();
 pragma.CommandText = "PRAGMA foreign_keys = ON;";
 await pragma.ExecuteNonQueryAsync();
 return connection;
 }

 public ValueTask DisposeAsync()
 {
 Directory.Delete(DirectoryPath, recursive: true);
 return ValueTask.CompletedTask;
 }
 }
}
