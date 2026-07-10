using Microsoft.Data.Sqlite;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusAnalysisJobStoreTests
{
 [Fact]
 public async Task EnqueueAsyncPersistsFrozenInputAndRoundTripsJob()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var queuedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var job = await fixture.Store.EnqueueAsync(
 CreateSnapshot("snapshot-1", 2, queuedAt), CreateWorkItems(),
 CreateEnqueue("job-1", "run-1", "snapshot-1", 2, queuedAt));

 Assert.Equal("job-1", job.JobId);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, job.Status);
 Assert.Equal(2, job.TotalWorkItems);
 Assert.Equal(0, job.ProcessedWorkItems);
 Assert.Equal(2500, job.TokenBudget);
 Assert.Equal(3, job.MaxAttempts);
 Assert.Equal(0, job.Version);
 Assert.Equal(queuedAt, job.QueuedAt);
 Assert.Equal(queuedAt, job.UpdatedAt);
 Assert.Equal(1, await fixture.CountRowsAsync("reference_analysis_input_snapshots"));
 Assert.Equal(2, await fixture.CountRowsAsync("reference_analysis_work_items"));
 Assert.Equal(1, await fixture.CountRowsAsync("reference_analysis_jobs"));

 var listed = await fixture.Store.ListAsync(
 new ReferenceCorpusAnalysisJobListRequest(7, 101, ReferenceCorpusAnalysisJobStatuses.Queued, 0, 20));
 Assert.Single(listed);
 Assert.Equal(job, listed[0]);
 Assert.Equal(1, await fixture.Store.CountAsync(7, 101, ReferenceCorpusAnalysisJobStatuses.Queued));
 }

 [Fact]
 public async Task EnqueueAsyncRollsBackSnapshotAndWorkItemsWhenJobConflicts()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var queuedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.Store.EnqueueAsync(
 CreateSnapshot("snapshot-1", 2, queuedAt), CreateWorkItems(),
 CreateEnqueue("job-1", "run-1", "snapshot-1", 2, queuedAt));

 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.EnqueueAsync(
 CreateSnapshot("snapshot-2", 2, queuedAt.AddMinutes(1)), CreateWorkItems(),
 CreateEnqueue("job-2", "run-1", "snapshot-2", 2, queuedAt.AddMinutes(1))));

 Assert.Equal(1, await fixture.CountRowsAsync("reference_analysis_input_snapshots"));
 Assert.Equal(2, await fixture.CountRowsAsync("reference_analysis_work_items"));
 Assert.Equal(1, await fixture.CountRowsAsync("reference_analysis_jobs"));
 }

 [Fact]
 public async Task EnqueueAsyncRejectsInconsistentFrozenInputBeforeWriting()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var queuedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var workItems = CreateWorkItems();
 workItems[1] = new ReferenceCorpusAnalysisWorkItemSnapshot(3, "node-2", null, "emotion", "hash-2");

 await Assert.ThrowsAsync<ArgumentException>(async () =>
 await fixture.Store.EnqueueAsync(
 CreateSnapshot("snapshot-1", 2, queuedAt), workItems,
 CreateEnqueue("job-1", "run-1", "snapshot-1", 2, queuedAt)));

 Assert.Equal(0, await fixture.CountRowsAsync("reference_analysis_input_snapshots"));
 Assert.Equal(0, await fixture.CountRowsAsync("reference_analysis_jobs"));
 }

 [Fact]
 public async Task ControlMutationsUseVersionCas()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-control", 2, now), CreateWorkItems(),
 CreateEnqueue("job-control", "run-control", "snapshot-control", 2, now));
 var paused = await fixture.Store.RequestPauseAsync("job-control", 0, now.AddSeconds(1));
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Paused, paused.Status);
 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.RequestCancelAsync("job-control", 0, now.AddSeconds(2)));
 var resumed = await fixture.Store.ResumeAsync("job-control", 1, null, now.AddSeconds(3));
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, resumed.Status);
 var cancelled = await fixture.Store.RequestCancelAsync("job-control", 2, now.AddSeconds(4));
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Cancelled, cancelled.Status);
 Assert.Equal(now.AddSeconds(4), cancelled.CompletedAt);
 }

 [Fact]
 public async Task ClaimAndHeartbeatFenceLeaseToken()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-claim", 2, now), CreateWorkItems(),
 CreateEnqueue("job-claim", "run-claim", "snapshot-claim", 2, now));
 var claim = await fixture.Store.ClaimNextAsync("worker-1", now.AddSeconds(1), TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Running, claim.Job.Status);
 Assert.Equal(1, await fixture.CountRowsAsync("reference_analysis_job_attempts"));
 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.HeartbeatAsync("job-claim", "worker-1", "stale-token", now.AddSeconds(2), TimeSpan.FromSeconds(45)));
 var lease = await fixture.Store.HeartbeatAsync(
 "job-claim", "worker-1", claim.LeaseToken, now.AddSeconds(2), TimeSpan.FromSeconds(45));
 Assert.Equal(claim.LeaseToken, lease.LeaseToken);
 }

 private static ReferenceCorpusAnalysisInputSnapshot CreateSnapshot(string id, int workCount, DateTimeOffset at) =>
 new(id, 101, "stage_2", "sentence", "nodes-hash", "[\"syntax\",\"emotion\"]",
 "corpus-analysis-v2", "feature-v2", "fake", "fake-model", 2, workCount, at);

 private static ReferenceCorpusAnalysisWorkItemSnapshot[] CreateWorkItems() =>
 [
 new(0, "node-1", null, "syntax", "hash-1"),
 new(1, "node-2", null, "emotion", "hash-2")
 ];

 private static ReferenceCorpusAnalysisJobEnqueue CreateEnqueue(
 string jobId, string runId, string snapshotId, int workCount, DateTimeOffset at) =>
 new(jobId, runId, snapshotId, 7, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 "{\"node_type\":\"sentence\"}", "input-hash", null,
 ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 2, workCount, 2500,
 "stage_2", null, 3, at);

 private sealed class JobStoreFixture : IAsyncDisposable
 {
 private JobStoreFixture(string directoryPath, string databasePath)
 {
 DirectoryPath = directoryPath;
 DatabasePath = databasePath;
 Store = new SqliteReferenceCorpusAnalysisJobStore(databasePath);
 }

 public string DirectoryPath { get; }
 public string DatabasePath { get; }
 public SqliteReferenceCorpusAnalysisJobStore Store { get; }

 public static async ValueTask<JobStoreFixture> CreateAsync()
 {
 var directory = Path.Combine(Path.GetTempPath(), $"novelist-job-store-{Guid.NewGuid():N}");
 Directory.CreateDirectory(directory);
 var fixture = new JobStoreFixture(directory, Path.Combine(directory, "novelist.db"));
 await fixture.Store.EnsureSchemaAsync();
 await using var connection = await fixture.OpenAsync();
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
 ('node-1', 101, NULL, 'sentence', 0, 0, 1, 0, 3, 3, 'hash-1', '节点一', '2026-07-10T00:00:00Z'),
 ('node-2', 101, NULL, 'sentence', 1, 0, 1, 3, 6, 3, 'hash-2', '节点二', '2026-07-10T00:00:00Z');
 """;
 await command.ExecuteNonQueryAsync();
 return fixture;
 }

 public async ValueTask<long> CountRowsAsync(string tableName)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
 return Convert.ToInt64(await command.ExecuteScalarAsync());
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
