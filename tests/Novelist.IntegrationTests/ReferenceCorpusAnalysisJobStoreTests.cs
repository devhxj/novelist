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
 var run = await fixture.ReadCanonicalRunAsync("run-1");
 Assert.Equal((101L, "feature-v2", "corpus-analysis-v2", "fake", "fake-model", "sentence",
 ReferenceCorpusAnalysisRunStatuses.Running, 2500, 0, null, queuedAt, null), run);

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
 Assert.Equal(1, await fixture.CountRowsAsync("reference_analysis_runs"));
}

 [Fact]
 public async Task EnqueueAsyncRejectsConflictingLegacyRunMetadataAndRollsBackEverything()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var queuedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.ExecuteAsync("""
 INSERT INTO reference_analysis_runs
 (run_id,anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,
 token_budget,tokens_spent,resume_cursor,started_at,completed_at,observation_count,diagnostics_json)
 VALUES
 ('run-conflict',101,'legacy-analyzer','legacy-schema','legacy','legacy-model','sentence','completed',
 100,75,'legacy-cursor','2026-07-01T00:00:00Z','2026-07-01T00:01:00Z',1,'[]');
 """);

 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.EnqueueAsync(
 CreateSnapshot("snapshot-conflict", 2, queuedAt), CreateWorkItems(),
 CreateEnqueue("job-conflict", "run-conflict", "snapshot-conflict", 2, queuedAt)));

 Assert.Equal(0, await fixture.CountRowsAsync("reference_analysis_input_snapshots"));
 Assert.Equal(0, await fixture.CountRowsAsync("reference_analysis_work_items"));
 Assert.Equal(0, await fixture.CountRowsAsync("reference_analysis_jobs"));
 var run = await fixture.ReadCanonicalRunAsync("run-conflict");
 Assert.Equal((101L, "legacy-analyzer", "legacy-schema", "legacy", "legacy-model", "sentence",
 ReferenceCorpusAnalysisRunStatuses.Completed, 100, 75, "legacy-cursor",
 DateTimeOffset.Parse("2026-07-01T00:00:00Z"), DateTimeOffset.Parse("2026-07-01T00:01:00Z")), run);
 }

 [Fact]
 public async Task EnqueueAsyncRejectsInconsistentFrozenInputBeforeWriting()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var queuedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var workItems = CreateWorkItems();
 var changedPayload = "{\"text\":\"节点二\"}";
 workItems[1] = new ReferenceCorpusAnalysisWorkItemSnapshot(
 3, "node-2", null, "emotion", "hash-2", changedPayload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(changedPayload));

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
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Paused, 0, (string?)null, (DateTimeOffset?)null),
 await fixture.ReadCanonicalRunProgressAsync("run-control"));
 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.RequestCancelAsync("job-control", 0, now.AddSeconds(2)));
var resumed = await fixture.Store.ResumeAsync("job-control", 1, null, now.AddSeconds(3));
Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, resumed.Status);
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Running, 0, (string?)null, (DateTimeOffset?)null),
 await fixture.ReadCanonicalRunProgressAsync("run-control"));
 var cancelled = await fixture.Store.RequestCancelAsync("job-control", 2, now.AddSeconds(4));
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Cancelled, cancelled.Status);
Assert.Equal(now.AddSeconds(4), cancelled.CompletedAt);
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.PartialCompleted, 0, (string?)null,
 (DateTimeOffset?)now.AddSeconds(4)), await fixture.ReadCanonicalRunProgressAsync("run-control"));
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

 [Fact]
 public async Task ClaimNextAgesNormalPriorityAcrossClassBoundariesWithinFifteenMinutes()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var normalQueuedAt = DateTimeOffset.Parse("2026-07-10T08:00:00Z");
 var claimAt = normalQueuedAt.AddMinutes(15);
 await fixture.Store.EnqueueAsync(
 CreateSnapshot("snapshot-aged-normal", 2, normalQueuedAt), CreateWorkItems(),
 CreateEnqueue("job-aged-normal", "run-aged-normal", "snapshot-aged-normal", 2, normalQueuedAt,
 ReferenceCorpusAnalysisPriorityClasses.Normal, 100));
 await fixture.Store.EnqueueAsync(
 CreateSnapshot("snapshot-new-current", 2, claimAt), CreateWorkItems(),
 CreateEnqueue("job-new-current", "run-new-current", "snapshot-new-current", 2, claimAt,
 ReferenceCorpusAnalysisPriorityClasses.CurrentChapter, 300));

 var claimed = await fixture.Store.ClaimNextAsync(
 "worker-aging", claimAt, TimeSpan.FromSeconds(45));

 Assert.NotNull(claimed);
 Assert.Equal("job-aged-normal", claimed.Job.JobId);
 }

 [Fact]
 public async Task ReclaimExpiredRunningLeaseSchedulesRetry()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var claimedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var expiredAt = claimedAt.AddSeconds(46);
 var retryAt = expiredAt.AddMinutes(2);
await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-reclaim-running", 2, claimedAt), CreateWorkItems(),
CreateEnqueue("job-reclaim-running", "run-reclaim-running", "snapshot-reclaim-running", 2, claimedAt));
var claim = await fixture.Store.ClaimNextAsync("worker-1", claimedAt, TimeSpan.FromSeconds(45));
Assert.NotNull(claim);
 var reservation = await fixture.Store.ReserveNextWorkItemAsync(
 "job-reclaim-running", "worker-1", claim.LeaseToken, 400, claimedAt.AddSeconds(1));
 Assert.NotNull(reservation);

 var reclaimed = await fixture.Store.ReclaimExpiredLeasesAsync(expiredAt, retryAt);
 var job = await fixture.Store.GetAsync("job-reclaim-running");

 Assert.Equal(1, reclaimed);
 Assert.NotNull(job);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.RetryWait, job.Status);
 Assert.Equal(retryAt, job.NextAttemptAt);
 Assert.Null(job.CompletedAt);
Assert.Null(job.LeaseOwner);
Assert.Null(job.LeaseToken);
Assert.Equal(400, job.TokensSpent);
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Running, 400, (string?)null, (DateTimeOffset?)null),
 await fixture.ReadCanonicalRunProgressAsync("run-reclaim-running"));
 Assert.Equal(0, await fixture.ReadJobReservedTokensAsync("job-reclaim-running"));
 Assert.Equal(("pending", 0), await fixture.ReadWorkItemReservationAsync("snapshot-reclaim-running", 0));
 var resumed = await fixture.Store.ResumeAsync("job-reclaim-running", job.Version, null, retryAt);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, resumed.Status);
 var secondClaim = await fixture.Store.ClaimNextAsync("worker-2", retryAt, TimeSpan.FromSeconds(45));
 Assert.NotNull(secondClaim);
 var secondReservation = await fixture.Store.ReserveNextWorkItemAsync(
 "job-reclaim-running", "worker-2", secondClaim.LeaseToken, 400, retryAt.AddSeconds(1));
 Assert.NotNull(secondReservation);
Assert.Equal(2, secondReservation.InvocationNumber);
}

 [Fact]
 public async Task ReclaimExpiredFinalAttemptFailsCanonicalRunAndSettlesProgress()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var claimedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var expiredAt = claimedAt.AddSeconds(46);
 var enqueue = CreateEnqueue(
 "job-reclaim-final", "run-reclaim-final", "snapshot-reclaim-final", 2, claimedAt) with
 { MaxAttempts = 1 };
 await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-reclaim-final", 2, claimedAt), CreateWorkItems(), enqueue);
 var claim = await fixture.Store.ClaimNextAsync("worker-1", claimedAt, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 var reservation = await fixture.Store.ReserveNextWorkItemAsync(
 "job-reclaim-final", "worker-1", claim.LeaseToken, 400, claimedAt.AddSeconds(1));
 Assert.NotNull(reservation);

 Assert.Equal(1, await fixture.Store.ReclaimExpiredLeasesAsync(expiredAt, expiredAt.AddMinutes(2)));

 var job = await fixture.Store.GetAsync("job-reclaim-final");
 Assert.NotNull(job);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Failed, job.Status);
 Assert.Equal(1, job.ProcessedWorkItems);
 Assert.Equal(1, job.FailedWorkItems);
 Assert.Equal(400, job.TokensSpent);
 Assert.Equal(expiredAt, job.CompletedAt);
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Failed, 400, (string?)null, (DateTimeOffset?)expiredAt),
 await fixture.ReadCanonicalRunProgressAsync("run-reclaim-final"));
 }

 [Fact]
 public async Task ReclaimExpiredPauseRequestedLeasePausesWithoutCompletionOrRetry()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var claimedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var requestedAt = claimedAt.AddSeconds(5);
 var expiredAt = claimedAt.AddSeconds(46);
await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-reclaim-pause", 2, claimedAt), CreateWorkItems(),
CreateEnqueue("job-reclaim-pause", "run-reclaim-pause", "snapshot-reclaim-pause", 2, claimedAt));
var claim = await fixture.Store.ClaimNextAsync("worker-1", claimedAt, TimeSpan.FromSeconds(45));
Assert.NotNull(claim);
var reservation = await fixture.Store.ReserveNextWorkItemAsync(
"job-reclaim-pause", "worker-1", claim.LeaseToken, 400, claimedAt.AddSeconds(1));
Assert.NotNull(reservation);
 var running = await fixture.Store.GetAsync("job-reclaim-pause");
 Assert.NotNull(running);
var pauseRequested = await fixture.Store.RequestPauseAsync(
 "job-reclaim-pause", running.Version, requestedAt);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.PauseRequested, pauseRequested.Status);

 var reclaimed = await fixture.Store.ReclaimExpiredLeasesAsync(expiredAt, expiredAt.AddMinutes(2));
 var job = await fixture.Store.GetAsync("job-reclaim-pause");

 Assert.Equal(1, reclaimed);
 Assert.NotNull(job);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Paused, job.Status);
 Assert.Null(job.NextAttemptAt);
 Assert.Null(job.CompletedAt);
Assert.Null(job.LeaseOwner);
Assert.Null(job.LeaseToken);
Assert.Equal(400, job.TokensSpent);
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Paused, 400, (string?)null, (DateTimeOffset?)null),
 await fixture.ReadCanonicalRunProgressAsync("run-reclaim-pause"));
 Assert.Equal(("pending", 0), await fixture.ReadWorkItemReservationAsync("snapshot-reclaim-pause", 0));
 }

 [Fact]
 public async Task ReclaimExpiredCancelRequestedLeaseCancelsAndCompletes()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var claimedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var requestedAt = claimedAt.AddSeconds(5);
 var expiredAt = claimedAt.AddSeconds(46);
await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-reclaim-cancel", 2, claimedAt), CreateWorkItems(),
CreateEnqueue("job-reclaim-cancel", "run-reclaim-cancel", "snapshot-reclaim-cancel", 2, claimedAt));
var claim = await fixture.Store.ClaimNextAsync("worker-1", claimedAt, TimeSpan.FromSeconds(45));
Assert.NotNull(claim);
var reservation = await fixture.Store.ReserveNextWorkItemAsync(
"job-reclaim-cancel", "worker-1", claim.LeaseToken, 400, claimedAt.AddSeconds(1));
Assert.NotNull(reservation);
 var running = await fixture.Store.GetAsync("job-reclaim-cancel");
 Assert.NotNull(running);
var cancelRequested = await fixture.Store.RequestCancelAsync(
 "job-reclaim-cancel", running.Version, requestedAt);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.CancelRequested, cancelRequested.Status);

 var reclaimed = await fixture.Store.ReclaimExpiredLeasesAsync(expiredAt, expiredAt.AddMinutes(2));
 var job = await fixture.Store.GetAsync("job-reclaim-cancel");

 Assert.Equal(1, reclaimed);
 Assert.NotNull(job);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Cancelled, job.Status);
 Assert.Null(job.NextAttemptAt);
 Assert.Equal(expiredAt, job.CompletedAt);
Assert.Null(job.LeaseOwner);
Assert.Null(job.LeaseToken);
Assert.Equal(400, job.TokensSpent);
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.PartialCompleted, 400, (string?)null, (DateTimeOffset?)expiredAt),
 await fixture.ReadCanonicalRunProgressAsync("run-reclaim-cancel"));
 Assert.Equal(("pending", 0), await fixture.ReadWorkItemReservationAsync("snapshot-reclaim-cancel", 0));
 }

[Fact]
public async Task ReserveNextWorkItemFreezesNodeAndBudgetUnderLease()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-reserve", 2, now), CreateWorkItems(),
 CreateEnqueue("job-reserve", "run-reserve", "snapshot-reserve", 2, now));
 var claim = await fixture.Store.ClaimNextAsync("worker-1", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);

 var reservation = await fixture.Store.ReserveNextWorkItemAsync(
 "job-reserve", "worker-1", claim.LeaseToken, 400, now.AddSeconds(1));

 Assert.NotNull(reservation);
 Assert.Equal(0, reservation.Ordinal);
 Assert.Equal("node-1", reservation.NodeId);
 Assert.Contains("节点一", reservation.InputPayloadJson, StringComparison.Ordinal);
 Assert.Equal(400, reservation.ReservedTokens);
 Assert.Equal(1, reservation.InvocationNumber);
 Assert.Equal(("in_progress", 400), await fixture.ReadWorkItemReservationAsync("snapshot-reserve", 0));
 Assert.Equal(400, await fixture.ReadJobReservedTokensAsync("job-reserve"));

 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.ReserveNextWorkItemAsync(
 "job-reserve", "worker-1", "stale-token", 400, now.AddSeconds(2)));
 }

 [Fact]
 public async Task ReserveNextWorkItemRejectsChangedNodeBeforeModelCall()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-stale", 2, now), CreateWorkItems(),
 CreateEnqueue("job-stale", "run-stale", "snapshot-stale", 2, now));
 var claim = await fixture.Store.ClaimNextAsync("worker-1", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 await fixture.ExecuteAsync("UPDATE reference_text_nodes SET text_hash='changed-hash', text='已变化' WHERE node_id='node-1';");

 var exception = await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.ReserveNextWorkItemAsync(
 "job-stale", "worker-1", claim.LeaseToken, 400, now.AddSeconds(1)));

 Assert.Contains("analysis_snapshot_stale", exception.Message, StringComparison.Ordinal);
 Assert.Equal(("pending", 0), await fixture.ReadWorkItemReservationAsync("snapshot-stale", 0));
 Assert.Equal(0, await fixture.ReadJobReservedTokensAsync("job-stale"));
 }

 [Fact]
 public async Task CommitWorkItemSettlesOutputProgressAndTokensAtomically()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-commit", 2, now), CreateWorkItems(),
 CreateEnqueue("job-commit", "run-commit", "snapshot-commit", 2, now));
 var claim = await fixture.Store.ClaimNextAsync("worker-1", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 var reservation = await fixture.Store.ReserveNextWorkItemAsync(
 "job-commit", "worker-1", claim.LeaseToken, 400, now.AddSeconds(1));
 Assert.NotNull(reservation);

 var job = await fixture.Store.CommitWorkItemAsync(
 reservation, 275, "run-commit", now.AddSeconds(2),
 async (connection, transaction, cancellationToken) =>
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 INSERT INTO commit_probe(value) VALUES ('persisted');
 INSERT INTO reference_feature_observations
 (observation_id,node_id,node_type,run_id,anchor_id,feature_family,feature_key,value_kind,confidence,created_at)
 VALUES
 ('observation-canonical-fk','node-1','sentence','run-commit',101,'syntax','sentence_kind','text',0.9,
 '2026-07-10T01:02:05Z');
 """;
 await command.ExecuteNonQueryAsync(cancellationToken);
 });

 Assert.Equal(1, job.ProcessedWorkItems);
 Assert.Equal(1, job.SucceededWorkItems);
 Assert.Equal(275, job.TokensSpent);
 Assert.Equal("1", job.ResumeCursor);
Assert.Equal(("succeeded", 0), await fixture.ReadWorkItemReservationAsync("snapshot-commit", 0));
Assert.Equal(0, await fixture.ReadJobReservedTokensAsync("job-commit"));
Assert.Equal((275, false, null), await fixture.ReadAttemptSettlementAsync("job-commit", 1));
Assert.Equal(1, await fixture.CountRowsAsync("commit_probe"));
Assert.Equal(1, await fixture.CountRowsAsync("reference_feature_observations"));
Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Running, 275, "1", (DateTimeOffset?)null),
await fixture.ReadCanonicalRunProgressAsync("run-commit"));

 var finalReservation = await fixture.Store.ReserveNextWorkItemAsync(
 "job-commit", "worker-1", claim.LeaseToken, 300, now.AddSeconds(3));
 Assert.NotNull(finalReservation);
 var completed = await fixture.Store.CommitWorkItemAsync(
 finalReservation, 225, "run-commit", now.AddSeconds(4),
 async (connection, transaction, cancellationToken) =>
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO commit_probe(value) VALUES ('final');";
 await command.ExecuteNonQueryAsync(cancellationToken);
 });

 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Completed, completed.Status);
 Assert.Equal(2, completed.ProcessedWorkItems);
 Assert.Equal(2, completed.SucceededWorkItems);
 Assert.Equal(500, completed.TokensSpent);
 Assert.Equal("2", completed.ResumeCursor);
 Assert.Equal(now.AddSeconds(4), completed.CompletedAt);
 Assert.Null(completed.LeaseOwner);
 Assert.Null(completed.LeaseToken);
 Assert.Equal((500, true, "completed"), await fixture.ReadAttemptSettlementAsync("job-commit", 1));
Assert.Equal(2, await fixture.CountRowsAsync("commit_probe"));
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Completed, 500, "2", (DateTimeOffset?)now.AddSeconds(4)),
 await fixture.ReadCanonicalRunProgressAsync("run-commit"));
 var noMoreWork = await fixture.Store.ReserveNextWorkItemAsync(
 "job-commit", "worker-1", claim.LeaseToken, 300, now.AddSeconds(5));
 Assert.Null(noMoreWork);
}

 [Fact]
 public async Task CommitWorkItemRejectsStaleReservationBeforeWritingOutput()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-fenced", 2, now), CreateWorkItems(),
 CreateEnqueue("job-fenced", "run-fenced", "snapshot-fenced", 2, now));
 var claim = await fixture.Store.ClaimNextAsync("worker-1", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 var reservation = await fixture.Store.ReserveNextWorkItemAsync(
 "job-fenced", "worker-1", claim.LeaseToken, 400, now.AddSeconds(1));
 Assert.NotNull(reservation);
 var stale = reservation with { InvocationNumber = reservation.InvocationNumber + 1 };

 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.CommitWorkItemAsync(
 stale, 275, "run-fenced", now.AddSeconds(2),
 async (connection, transaction, cancellationToken) =>
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO commit_probe(value) VALUES ('must-not-write');";
 await command.ExecuteNonQueryAsync(cancellationToken);
 }));

 Assert.Equal(0, await fixture.CountRowsAsync("commit_probe"));
 Assert.Equal(("in_progress", 400), await fixture.ReadWorkItemReservationAsync("snapshot-fenced", 0));
 Assert.Equal(400, await fixture.ReadJobReservedTokensAsync("job-fenced"));
 }

 [Fact]
public async Task CommitWorkItemRollsBackWhenOutputPersistenceFails()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 await fixture.Store.EnqueueAsync(CreateSnapshot("snapshot-rollback", 2, now), CreateWorkItems(),
 CreateEnqueue("job-rollback", "run-rollback", "snapshot-rollback", 2, now));
 var claim = await fixture.Store.ClaimNextAsync("worker-1", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 var reservation = await fixture.Store.ReserveNextWorkItemAsync(
 "job-rollback", "worker-1", claim.LeaseToken, 400, now.AddSeconds(1));
 Assert.NotNull(reservation);

 await Assert.ThrowsAsync<InvalidOperationException>(async () =>
 await fixture.Store.CommitWorkItemAsync(
 reservation, 275, "run-rollback", now.AddSeconds(2),
 async (connection, transaction, cancellationToken) =>
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO commit_probe(value) VALUES ('rolled-back');";
 await command.ExecuteNonQueryAsync(cancellationToken);
 throw new InvalidOperationException("fault injection");
 }));

 Assert.Equal(0, await fixture.CountRowsAsync("commit_probe"));
 Assert.Equal(("in_progress", 400), await fixture.ReadWorkItemReservationAsync("snapshot-rollback", 0));
 Assert.Equal(400, await fixture.ReadJobReservedTokensAsync("job-rollback"));
 var job = await fixture.Store.GetAsync("job-rollback");
 Assert.NotNull(job);
 Assert.Equal(0, job.ProcessedWorkItems);
Assert.Equal(0, job.TokensSpent);
}

 [Fact]
 public async Task SettleRetryableFailureChargesTokensReleasesReservationAndSchedulesRetry()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var retryAt = now.AddMinutes(2);
 var reservation = await fixture.CreateReservationAsync("retry", now, 400);

 var result = await fixture.Store.SettleWorkItemAsync(new(
 reservation, ReferenceCorpusAnalysisWorkItemSettlementKind.RetryableFailure, 275,
 "provider_timeout", "Provider timed out.", retryAt, now.AddSeconds(2)));

 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.RetryWait, result.Job.Status);
 Assert.Equal(retryAt, result.Job.NextAttemptAt);
Assert.Equal(275, result.Job.TokensSpent);
Assert.Equal(0, result.Job.ProcessedWorkItems);
Assert.Equal(1, result.Job.RetryingWorkItems);
 Assert.Null(result.Job.LeaseOwner);
 Assert.Equal(("pending", 0), await fixture.ReadWorkItemReservationAsync("snapshot-retry", 0));
 Assert.Equal(0, await fixture.ReadJobReservedTokensAsync("job-retry"));
Assert.Equal((275, true, "retryable_failure", "provider_timeout"),
await fixture.ReadAttemptSettlementDetailsAsync("job-retry", 1));
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Running, 275, (string?)null, (DateTimeOffset?)null),
 await fixture.ReadCanonicalRunProgressAsync("run-retry"));
}

 [Fact]
 public async Task SettlePermanentFailureMarksWorkItemAndJobFailed()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var reservation = await fixture.CreateReservationAsync("permanent", now, 400);

 var result = await fixture.Store.SettleWorkItemAsync(new(
 reservation, ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure, 300,
 "invalid_schema", "Structured output cannot be validated.", null, now.AddSeconds(2)));

 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Failed, result.Job.Status);
 Assert.Equal(1, result.Job.ProcessedWorkItems);
 Assert.Equal(1, result.Job.FailedWorkItems);
 Assert.Equal(300, result.Job.TokensSpent);
 Assert.Equal(now.AddSeconds(2), result.Job.CompletedAt);
 Assert.Equal(("failed", 0), await fixture.ReadWorkItemReservationAsync("snapshot-permanent", 0));
Assert.Equal((300, true, "permanent_failure", "invalid_schema"),
await fixture.ReadAttemptSettlementDetailsAsync("job-permanent", 1));
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.Failed, 300, (string?)null, (DateTimeOffset?)now.AddSeconds(2)),
 await fixture.ReadCanonicalRunProgressAsync("run-permanent"));
 }

 [Theory]
 [InlineData(0)]
 [InlineData(250)]
public async Task SettleBudgetExhaustedSupportsNoCallOrActualCharge(int actualTokens)
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var suffix = $"budget-{actualTokens}";
 var reservation = await fixture.CreateReservationAsync(suffix, now, 400);

 var result = await fixture.Store.SettleWorkItemAsync(new(
 reservation, ReferenceCorpusAnalysisWorkItemSettlementKind.BudgetExhausted, actualTokens,
 "token_budget_exhausted", "Token budget is exhausted.", null, now.AddSeconds(2)));

 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.BudgetExhausted, result.Job.Status);
 Assert.Equal(actualTokens, result.Job.TokensSpent);
 Assert.Equal(0, result.Job.ProcessedWorkItems);
 Assert.Null(result.Job.CompletedAt);
 Assert.Null(result.Job.LeaseOwner);
 Assert.Equal(("pending", 0), await fixture.ReadWorkItemReservationAsync($"snapshot-{suffix}", 0));
 Assert.Equal((actualTokens, true, "budget_exhausted", "token_budget_exhausted"),
await fixture.ReadAttemptSettlementDetailsAsync($"job-{suffix}", 1));
 Assert.Equal((ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, actualTokens, (string?)null, (DateTimeOffset?)null),
 await fixture.ReadCanonicalRunProgressAsync($"run-{suffix}"));
}

 [Theory]
 [InlineData(ReferenceCorpusAnalysisWorkItemSettlementKind.PauseBoundary, ReferenceCorpusAnalysisJobStatuses.Paused, "paused")]
 [InlineData(ReferenceCorpusAnalysisWorkItemSettlementKind.CancelBoundary, ReferenceCorpusAnalysisJobStatuses.Cancelled, "cancelled")]
 public async Task SettleControlBoundaryPreservesCommittedProducts(
 ReferenceCorpusAnalysisWorkItemSettlementKind kind, string expectedStatus, string expectedOutcome)
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var suffix = kind == ReferenceCorpusAnalysisWorkItemSettlementKind.PauseBoundary ? "pause-boundary" : "cancel-boundary";
 await fixture.Store.EnqueueAsync(CreateSnapshot($"snapshot-{suffix}", 2, now), CreateWorkItems(),
 CreateEnqueue($"job-{suffix}", $"run-{suffix}", $"snapshot-{suffix}", 2, now));
 var claim = await fixture.Store.ClaimNextAsync("worker-1", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 var first = await fixture.Store.ReserveNextWorkItemAsync(
 $"job-{suffix}", "worker-1", claim.LeaseToken, 300, now.AddSeconds(1));
 Assert.NotNull(first);
 await fixture.Store.CommitWorkItemAsync(first, 200, $"run-{suffix}", now.AddSeconds(2),
 async (connection, transaction, cancellationToken) =>
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO commit_probe(value) VALUES ('preserved');";
 await command.ExecuteNonQueryAsync(cancellationToken);
 });
 var second = await fixture.Store.ReserveNextWorkItemAsync(
 $"job-{suffix}", "worker-1", claim.LeaseToken, 350, now.AddSeconds(3));
 Assert.NotNull(second);
 var running = await fixture.Store.GetAsync($"job-{suffix}");
 Assert.NotNull(running);
 if (kind == ReferenceCorpusAnalysisWorkItemSettlementKind.PauseBoundary)
 await fixture.Store.RequestPauseAsync($"job-{suffix}", running.Version, now.AddSeconds(4));
 else
 await fixture.Store.RequestCancelAsync($"job-{suffix}", running.Version, now.AddSeconds(4));

 var result = await fixture.Store.SettleWorkItemAsync(new(
 second, kind, 125, null, null, null, now.AddSeconds(5)));

 Assert.Equal(expectedStatus, result.Job.Status);
 Assert.Equal(1, result.Job.ProcessedWorkItems);
 Assert.Equal(1, result.Job.SucceededWorkItems);
 Assert.Equal(325, result.Job.TokensSpent);
 Assert.Equal("1", result.Job.ResumeCursor);
 Assert.Equal(1, await fixture.CountRowsAsync("commit_probe"));
 Assert.Equal(("succeeded", 0), await fixture.ReadWorkItemReservationAsync($"snapshot-{suffix}", 0));
 Assert.Equal(("pending", 0), await fixture.ReadWorkItemReservationAsync($"snapshot-{suffix}", 1));
Assert.Equal((325, true, expectedOutcome, null),
await fixture.ReadAttemptSettlementDetailsAsync($"job-{suffix}", 1));
 var expectedRunStatus = kind == ReferenceCorpusAnalysisWorkItemSettlementKind.PauseBoundary
 ? ReferenceCorpusAnalysisRunStatuses.Paused
 : ReferenceCorpusAnalysisRunStatuses.PartialCompleted;
 Assert.Equal((expectedRunStatus, 325, "1",
 kind == ReferenceCorpusAnalysisWorkItemSettlementKind.CancelBoundary ? (DateTimeOffset?)now.AddSeconds(5) : null),
 await fixture.ReadCanonicalRunProgressAsync($"run-{suffix}"));
 }

[Fact]
public async Task SettleWorkItemRejectsStaleInvocationWithZeroWrites()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var reservation = await fixture.CreateReservationAsync("settle-fence", now, 400);

 await Assert.ThrowsAsync<ReferenceCorpusAnalysisJobConflictException>(async () =>
 await fixture.Store.SettleWorkItemAsync(new(
 reservation with { InvocationNumber = reservation.InvocationNumber + 1 },
 ReferenceCorpusAnalysisWorkItemSettlementKind.RetryableFailure, 275,
 "provider_timeout", "Provider timed out.", now.AddMinutes(2), now.AddSeconds(2))));
 var job = await fixture.Store.GetAsync("job-settle-fence");
 Assert.NotNull(job);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Running, job.Status);
 Assert.Equal(0, job.TokensSpent);
 Assert.Equal(400, await fixture.ReadJobReservedTokensAsync("job-settle-fence"));
 Assert.Equal(("in_progress", 400), await fixture.ReadWorkItemReservationAsync("snapshot-settle-fence", 0));
 Assert.Equal((0, false, null, null), await fixture.ReadAttemptSettlementDetailsAsync("job-settle-fence", 1));
 }

 [Fact]
 public async Task SettleWorkItemRecordsUsageOverrunWithoutClampingActualCost()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var reservation = await fixture.CreateReservationAsync("settle-overrun", now, 400);

 var result = await fixture.Store.SettleWorkItemAsync(new(
 reservation, ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure, 401,
 "token_reservation_overrun", "Provider usage exceeded the frozen reservation.", null, now.AddSeconds(2)));

 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Failed, result.Job.Status);
 Assert.Equal(401, result.Job.TokensSpent);
 Assert.Equal(0, await fixture.ReadJobReservedTokensAsync("job-settle-overrun"));
 Assert.Equal(("failed", 0), await fixture.ReadWorkItemReservationAsync("snapshot-settle-overrun", 0));
 Assert.Equal((401, true, "permanent_failure", "token_reservation_overrun"),
 await fixture.ReadAttemptSettlementDetailsAsync("job-settle-overrun", 1));
 }
 [Theory]
 [InlineData(false, false, ReferenceCorpusAnalysisJobStatuses.RetryWait, "pending")]
 [InlineData(true, false, ReferenceCorpusAnalysisJobStatuses.Paused, "pending")]
 [InlineData(false, true, ReferenceCorpusAnalysisJobStatuses.Cancelled, "pending")]
 public async Task AbandonReservationReleasesFenceAndHonorsControlBoundary(
 bool pause,
 bool cancel,
 string expectedStatus,
 string expectedWorkState)
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var now = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var suffix = $"abandon-{pause}-{cancel}";
 var reservation = await fixture.CreateReservationAsync(suffix, now, 400);
 if (pause)
 {
 var running = await fixture.Store.GetAsync($"job-{suffix}");
 await fixture.Store.RequestPauseAsync(running!.JobId, running.Version, now.AddMilliseconds(100));
 }
 if (cancel)
 {
 var running = await fixture.Store.GetAsync($"job-{suffix}");
 await fixture.Store.RequestCancelAsync(running!.JobId, running.Version, now.AddMilliseconds(100));
 }

 var abandoned = await fixture.Store.AbandonReservationAsync(
 reservation, 400, "worker_shutdown", "Worker stopped.",
 now.AddSeconds(1), now.AddSeconds(2));

 Assert.Equal(expectedStatus, abandoned.Status);
 Assert.Equal(400, abandoned.TokensSpent);
 Assert.Null(abandoned.LeaseExpiresAt);
 Assert.Equal((expectedWorkState, 0), await fixture.ReadWorkItemReservationAsync($"snapshot-{suffix}", 0));
 Assert.Equal((400, true, "abandoned", "worker_shutdown"),
 await fixture.ReadAttemptSettlementDetailsAsync($"job-{suffix}", 1));
 }
 [Theory]
 [InlineData(true)]
 [InlineData(false)]
 public async Task ControlRequestCommitBoundaryMeetsSixtySecondP95(bool pause)
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var origin = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var observedLatencies = new List<TimeSpan>();
 for (var sample = 0; sample < 20; sample++)
 {
 var suffix = $"control-p95-{pause}-{sample}";
 var requestedAt = origin.AddMinutes(sample);
 var boundaryAt = requestedAt.AddSeconds(5 + (sample * 35d / 19d));
 var reservation = await fixture.CreateReservationAsync(suffix, requestedAt.AddSeconds(-2), 400);
 var running = await fixture.Store.GetAsync($"job-{suffix}");
 Assert.NotNull(running);
 if (pause) await fixture.Store.RequestPauseAsync(running.JobId, running.Version, requestedAt);
 else await fixture.Store.RequestCancelAsync(running.JobId, running.Version, requestedAt);
 var settled = await fixture.Store.CommitWorkItemAsync(reservation, 200, $"run-{suffix}", boundaryAt,
 async (connection, transaction, cancellationToken) =>
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO commit_probe(value) VALUES ($value);";
 command.Parameters.AddWithValue("$value", suffix);
 await command.ExecuteNonQueryAsync(cancellationToken);
 });
 Assert.Equal(pause ? ReferenceCorpusAnalysisJobStatuses.Paused : ReferenceCorpusAnalysisJobStatuses.Cancelled, settled.Status);
 Assert.Equal(1, settled.ProcessedWorkItems);
 Assert.Equal(1, settled.SucceededWorkItems);
 Assert.Equal(200, settled.TokensSpent);
 observedLatencies.Add(boundaryAt - requestedAt);
 }
 Assert.True(Percentile95(observedLatencies) <= TimeSpan.FromSeconds(60));
 Assert.Equal(20, await fixture.CountRowsAsync("commit_probe"));
 }

 [Fact]
 public async Task ExpiredLeaseIsReclaimedWithinThirtySecondsAndCanBeClaimedAgain()
 {
 await using var fixture = await JobStoreFixture.CreateAsync();
 var claimedAt = DateTimeOffset.Parse("2026-07-10T01:02:03Z");
 var leaseDuration = TimeSpan.FromSeconds(45);
 var expiry = claimedAt.Add(leaseDuration);
 var detectedAt = expiry.AddSeconds(29);
 var reservation = await fixture.CreateReservationAsync("stale-p95", claimedAt, 400);
 Assert.Equal(1, await fixture.Store.ReclaimExpiredLeasesAsync(detectedAt, detectedAt));
 var reclaimed = await fixture.Store.GetAsync(reservation.JobId);
 Assert.NotNull(reclaimed);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.RetryWait, reclaimed.Status);
 Assert.Null(reclaimed.LeaseOwner);
 Assert.Null(reclaimed.LeaseToken);
 Assert.Equal(0, reclaimed.TokensReserved);
 Assert.True(detectedAt - expiry <= TimeSpan.FromSeconds(30));
 Assert.Equal(("pending", 0), await fixture.ReadWorkItemReservationAsync(reservation.InputSnapshotId, reservation.Ordinal));
 var resumed = await fixture.Store.ResumeAsync(reclaimed.JobId, reclaimed.Version, null, detectedAt);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, resumed.Status);
 var secondClaim = await fixture.Store.ClaimNextAsync("worker-2", detectedAt, leaseDuration);
 Assert.NotNull(secondClaim);
 Assert.Equal(2, secondClaim.Job.AttemptCount);
 Assert.NotEqual(reservation.LeaseToken, secondClaim.LeaseToken);
 }

 private static TimeSpan Percentile95(IReadOnlyList<TimeSpan> values)
 {
 var ordered = values.OrderBy(value => value).ToArray();
 var index = (int)Math.Ceiling(ordered.Length * 0.95) - 1;
 return ordered[Math.Max(0, index)];
 }

 private static ReferenceCorpusAnalysisInputSnapshot CreateSnapshot(string id, int workCount, DateTimeOffset at) =>
 new(id, 101, "stage_2", "sentence", "nodes-hash", "[\"syntax\",\"emotion\"]",
 "corpus-analysis-v2", "feature-v2", "fake", "fake-model", 2, workCount, at);

 private static ReferenceCorpusAnalysisWorkItemSnapshot[] CreateWorkItems()
 {
 const string firstPayload = "{\"text\":\"节点一\"}";
 const string secondPayload = "{\"text\":\"节点二\"}";
 return
 [
 new(0, "node-1", null, "syntax", "hash-1", firstPayload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(firstPayload)),
 new(1, "node-2", null, "emotion", "hash-2", secondPayload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(secondPayload))
 ];
 }

 private static ReferenceCorpusAnalysisJobEnqueue CreateEnqueue(
 string jobId, string runId, string snapshotId, int workCount, DateTimeOffset at,
 string priorityClass = ReferenceCorpusAnalysisPriorityClasses.Normal,
 int priorityValue = 100) =>
 new(jobId, runId, snapshotId, 7, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 "{\"node_type\":\"sentence\"}", "input-hash", null,
 priorityClass, priorityValue, 2, workCount, 2500,
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

 public async ValueTask<ReferenceCorpusAnalysisWorkItemReservation> CreateReservationAsync(
 string suffix, DateTimeOffset now, int reservedTokens)
 {
 var snapshotId = $"snapshot-{suffix}";
 var jobId = $"job-{suffix}";
await Store.EnqueueAsync(CreateSnapshot(snapshotId, 2, now), CreateWorkItems(),
 CreateEnqueue(jobId, $"run-{suffix}", snapshotId, 2, now));
 var claim = await Store.ClaimNextAsync("worker-1", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);
 var reservation = await Store.ReserveNextWorkItemAsync(
 jobId, "worker-1", claim.LeaseToken, reservedTokens, now.AddSeconds(1));
 return Assert.IsType<ReferenceCorpusAnalysisWorkItemReservation>(reservation);
 }

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

 CREATE TABLE commit_probe (value TEXT NOT NULL);
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

 public async ValueTask ExecuteAsync(string sql)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = sql;
 await command.ExecuteNonQueryAsync();
 }

 public async ValueTask<(string State, int ReservedTokens)> ReadWorkItemReservationAsync(string snapshotId, int ordinal)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT work_state,reserved_tokens FROM reference_analysis_work_items WHERE input_snapshot_id=$snapshot AND ordinal=$ordinal;";
 command.Parameters.AddWithValue("$snapshot", snapshotId);
 command.Parameters.AddWithValue("$ordinal", ordinal);
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

public async ValueTask<(int TokensSpent, bool Completed, string? Outcome)> ReadAttemptSettlementAsync(
 string jobId, int attemptNumber)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT tokens_spent,completed_at,outcome
 FROM reference_analysis_job_attempts
 WHERE job_id=$job AND attempt_no=$attempt;
 """;
 command.Parameters.AddWithValue("$job", jobId);
 command.Parameters.AddWithValue("$attempt", attemptNumber);
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
return (reader.GetInt32(0), !reader.IsDBNull(1), reader.IsDBNull(2) ? null : reader.GetString(2));
}

public async ValueTask<(int TokensSpent, bool Completed, string? Outcome, string? ErrorCode)> ReadAttemptSettlementDetailsAsync(
 string jobId, int attemptNumber)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT tokens_spent,completed_at,outcome,error_code
 FROM reference_analysis_job_attempts
 WHERE job_id=$job AND attempt_no=$attempt;
 """;
 command.Parameters.AddWithValue("$job", jobId);
 command.Parameters.AddWithValue("$attempt", attemptNumber);
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
return (reader.GetInt32(0), !reader.IsDBNull(1),
reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
}

 public async ValueTask<(long AnchorId, string AnalyzerVersion, string SchemaVersion, string ModelProvider,
 string ModelId, string Scope, string Status, int? TokenBudget, int TokensSpent, string? ResumeCursor,
 DateTimeOffset StartedAt, DateTimeOffset? CompletedAt)> ReadCanonicalRunAsync(string runId)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,
 token_budget,tokens_spent,resume_cursor,started_at,completed_at
 FROM reference_analysis_runs WHERE run_id=$run_id;
 """;
 command.Parameters.AddWithValue("$run_id", runId);
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
 return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
 reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.GetInt32(8),
 reader.IsDBNull(9) ? null : reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10)),
 reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)));
 }

 public async ValueTask<(string Status, int TokensSpent, string? ResumeCursor, DateTimeOffset? CompletedAt)>
 ReadCanonicalRunProgressAsync(string runId)
 {
 var run = await ReadCanonicalRunAsync(runId);
 return (run.Status, run.TokensSpent, run.ResumeCursor, run.CompletedAt);
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
