using System.Globalization;
using Microsoft.Data.Sqlite;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusAnalysisJobStoreRetryTests
{
 [Fact]
 public async Task FutureRetryRemainsUnclaimable()
 {
 await using var fixture = await RetryFixture.CreateAsync();
 var now = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
 await fixture.EnqueueRetryAsync(now.AddMinutes(1));

 Assert.Equal(0, await fixture.Store.RequeueDueRetriesAsync(now));
 Assert.Null(await fixture.Store.ClaimNextAsync("worker-a", now, TimeSpan.FromMinutes(1)));
 var state = await fixture.ReadStateAsync();
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.RetryWait, state.Status);
 Assert.Equal(now.AddMinutes(1), state.NextAttemptAt);
 Assert.Equal("validation_failed", state.ErrorCode);
 Assert.Equal("cursor:stable", state.ResumeCursor);
 Assert.Equal("pending", state.WorkState);
 }

 [Fact]
 public async Task DueRetryClearsMetadataWithoutMovingStableCursorOrWorkItem()
 {
 await using var fixture = await RetryFixture.CreateAsync();
 var now = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
 await fixture.EnqueueRetryAsync(now);

 Assert.Equal(1, await fixture.Store.RequeueDueRetriesAsync(now));
 var state = await fixture.ReadStateAsync();
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, state.Status);
 Assert.Null(state.NextAttemptAt);
 Assert.Null(state.ErrorCode);
 Assert.Null(state.ErrorMessage);
 Assert.Equal("cursor:stable", state.ResumeCursor);
Assert.Equal("pending", state.WorkState);
 Assert.Equal(0, state.RetryingWorkItems);
Assert.Equal(2, state.RowVersion);
 }

 [Fact]
 public async Task ConcurrentRequeueTransitionsDueJobOnce()
 {
 await using var fixture = await RetryFixture.CreateAsync();
 var now = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
 await fixture.EnqueueRetryAsync(now);
 var secondStore = new SqliteReferenceCorpusAnalysisJobStore(fixture.DatabasePath);

 var results = await Task.WhenAll(
 fixture.Store.RequeueDueRetriesAsync(now).AsTask(),
 secondStore.RequeueDueRetriesAsync(now).AsTask());

 Assert.Equal(1, results.Sum());
 Assert.Contains(0, results);
 Assert.Contains(1, results);
 var state = await fixture.ReadStateAsync();
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, state.Status);
 Assert.Equal(2, state.RowVersion);
 }

 private sealed class RetryFixture : IAsyncDisposable
 {
 private const string JobId = "job-retry";
 private const string SnapshotId = "snapshot-retry";
 private readonly string _directoryPath;

 private RetryFixture(string directoryPath, string databasePath)
 {
 _directoryPath = directoryPath;
 DatabasePath = databasePath;
 Store = new SqliteReferenceCorpusAnalysisJobStore(databasePath);
 }

 public string DatabasePath { get; }
 public SqliteReferenceCorpusAnalysisJobStore Store { get; }

 public static async ValueTask<RetryFixture> CreateAsync()
 {
 var directory = Path.Combine(
 Path.GetTempPath(),
 $"novelist-job-retry-{Guid.NewGuid():N}");
 Directory.CreateDirectory(directory);
 var fixture = new RetryFixture(directory, Path.Combine(directory, "novelist.db"));
 await fixture.Store.EnsureSchemaAsync();
 await fixture.SeedSourceAsync();
 return fixture;
 }

 public async ValueTask EnqueueRetryAsync(DateTimeOffset retryAt)
 {
 var queuedAt = retryAt.AddMinutes(-5);
 await Store.EnqueueAsync(
 new ReferenceCorpusAnalysisInputSnapshot(
 SnapshotId,
 101,
 "stage_2",
 "all",
 "node-set",
 "[\"emotion\"]",
 "schema-v1",
 "analyzer-v1",
 "provider",
 "model",
 1,
 1,
 queuedAt),
[new ReferenceCorpusAnalysisWorkItemSnapshot(
 0,
 "node-retry",
 null,
 "emotion",
 "hash-retry",
 "{}",
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash("{}"))],
 new ReferenceCorpusAnalysisJobEnqueue(
 JobId,
 "run-retry",
 SnapshotId,
 7,
 101,
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 "{}",
 "input-hash",
 null,
 ReferenceCorpusAnalysisPriorityClasses.Normal,
 100,
 1,
 1,
 1000,
 "stage_2",
 null,
 5,
 queuedAt));
 await MarkRetryWaitAsync(retryAt);
 }

 private async ValueTask SeedSourceAsync()
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT INTO reference_anchors
 (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
 source_file_hash, build_version, status, created_at, updated_at)
 VALUES
 (101, 7, 'fixture', 'fixture', 'fixture.txt', 'txt', 'user_owned',
 'source-hash', 'v2', 'ready', '2026-07-10T00:00:00Z', '2026-07-10T00:00:00Z');

 INSERT INTO reference_text_nodes
 (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
 chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
 VALUES
 ('node-retry', 101, NULL, 'sentence', 0, 0, 1, 0, 2, 2,
 'hash-retry', '节点', '2026-07-10T00:00:00Z');
 """;
 await command.ExecuteNonQueryAsync();
 }

 private async ValueTask MarkRetryWaitAsync(DateTimeOffset retryAt)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = """
 UPDATE reference_analysis_jobs
 SET status = 'retry_wait',
 next_attempt_at = $retry_at,
 last_error_code = 'validation_failed',
 last_error_message = 'invalid structured output',
 resume_cursor = 'cursor:stable',
 row_version = row_version + 1
 WHERE job_id = $job_id;
 """;
 command.Parameters.AddWithValue("$retry_at", retryAt.ToString("O", CultureInfo.InvariantCulture));
 command.Parameters.AddWithValue("$job_id", JobId);
 Assert.Equal(1, await command.ExecuteNonQueryAsync());
 }

 public async ValueTask<RetryState> ReadStateAsync()
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT job.status, job.next_attempt_at, job.last_error_code,
 job.last_error_message, job.resume_cursor, item.work_state,
 job.retrying_work_items, job.row_version
 FROM reference_analysis_jobs AS job
 JOIN reference_analysis_work_items AS item
 ON item.input_snapshot_id = job.input_snapshot_id
 WHERE job.job_id = $job_id;
 """;
 command.Parameters.AddWithValue("$job_id", JobId);
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
 return new RetryState(
 reader.GetString(0),
 reader.IsDBNull(1)
 ? null
 : DateTimeOffset.Parse(
 reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
 reader.IsDBNull(2) ? null : reader.GetString(2),
 reader.IsDBNull(3) ? null : reader.GetString(3),
reader.IsDBNull(4) ? null : reader.GetString(4),
reader.GetString(5),
 reader.GetInt32(6),
 reader.GetInt64(7));
 }

 private async ValueTask<SqliteConnection> OpenAsync()
 {
 var connection = new SqliteConnection(
 $"Data Source={DatabasePath};Pooling=False;Default Timeout=10");
 await connection.OpenAsync();
 await using var pragma = connection.CreateCommand();
 pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
 await pragma.ExecuteNonQueryAsync();
 return connection;
 }

 public ValueTask DisposeAsync()
 {
 Directory.Delete(_directoryPath, recursive: true);
 return ValueTask.CompletedTask;
 }
 }

 private sealed record RetryState(
 string Status,
 DateTimeOffset? NextAttemptAt,
 string? ErrorCode,
 string? ErrorMessage,
string? ResumeCursor,
string WorkState,
 int RetryingWorkItems,
long RowVersion);
}
