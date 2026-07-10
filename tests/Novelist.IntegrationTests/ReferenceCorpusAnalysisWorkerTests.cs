using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusAnalysisWorkerTests : IAsyncLifetime
{
 private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-analysis-worker-tests", Guid.NewGuid().ToString("N"));
 private string DatabasePath => Path.Combine(_root, "index.sqlite");

 [Fact]
 public async Task PumpOnceProcessesAllFrozenFeatureWorkItemsAndCompletesJob()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var queued = await scheduler.EnqueueAsync(new(
 "worker-run-complete", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);
 var analyzer = new FrozenFeatureAnalyzer(tokensPerCall: 10);
 var worker = new ReferenceCorpusAnalysisWorker(path, analyzer, new UnexpectedTechniqueAnalyzer(), "worker-test-1");

 Assert.True(await worker.PumpOnceAsync(CancellationToken.None));

 var completed = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);
 Assert.NotNull(completed);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Completed, completed.Status);
 Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies.Count, completed.ProcessedWorkItems);
 Assert.Equal(50, completed.TokensSpent);
 Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies, analyzer.Calls.Select(call => call.Family));
 Assert.All(analyzer.Calls, call =>
 {
 Assert.Equal("冻结后台句子。", call.NodeText);
 Assert.Equal("provider-a", call.ModelSelection!.ProviderName);
 Assert.Equal("model-a", call.ModelSelection.ModelId);
 });
Assert.Equal(1, await ScalarIntAsync("SELECT COUNT(*) FROM reference_feature_observations;"));
}

 [Fact]
 public async Task PumpOnceProcessesTechniqueWorkItemFromCompletedFrozenFeatureDependency()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var feature = await scheduler.EnqueueAsync(new(
 "worker-run-feature-dependency", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);
 var featureAnalyzer = new FrozenFeatureAnalyzer(tokensPerCall: 10);
 var techniqueAnalyzer = new FrozenTechniqueAnalyzer(tokensPerCall: 19);
 var featureWorker = new ReferenceCorpusAnalysisWorker(path, featureAnalyzer, new UnexpectedTechniqueAnalyzer(), "worker-feature-dependency");

 Assert.True(await featureWorker.PumpOnceAsync(CancellationToken.None));
 var completedFeature = await scheduler.GetAsync(new(feature.JobId), CancellationToken.None);
 Assert.NotNull(completedFeature);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Completed, completedFeature.Status);

 var technique = await scheduler.EnqueueAsync(new(
 "worker-run-technique-dependency", 1, 101, ReferenceCorpusAnalysisJobKinds.TechniqueSpecimen,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000,
 DependencyJobId: feature.JobId), CancellationToken.None);
 await ExecuteAsync("UPDATE reference_feature_observations SET review_state='rejected',value_text='changed live evidence';");
 var paused = await scheduler.PauseAsync(new(technique.JobId, technique.Version), CancellationToken.None);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Paused, paused.Status);
 var restartedWorker = new ReferenceCorpusAnalysisWorker(path, new FrozenFeatureAnalyzer(10), techniqueAnalyzer, "worker-technique-restarted");
 Assert.False(await restartedWorker.PumpOnceAsync(CancellationToken.None));
 Assert.Empty(techniqueAnalyzer.Calls);
 var resumed = await scheduler.ResumeAsync(new(paused.JobId, paused.Version), CancellationToken.None);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, resumed.Status);

 Assert.True(await restartedWorker.PumpOnceAsync(CancellationToken.None));

 var completedTechnique = await scheduler.GetAsync(new(technique.JobId), CancellationToken.None);
 Assert.NotNull(completedTechnique);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Completed, completedTechnique.Status);
 Assert.Equal(1, completedTechnique.ProcessedWorkItems);
 Assert.Equal(19, completedTechnique.TokensSpent);
 var input = Assert.Single(techniqueAnalyzer.Calls);
 Assert.Equal("冻结后台句子。", input.NodeText);
 Assert.Single(input.Observations);
 Assert.Equal(ReferenceCorpusFeatureFamilies.Syntax, input.Observations[0].FeatureFamily);
 Assert.Equal("subject_predicate", input.Observations[0].ValueText);
 Assert.Equal(1, await ScalarIntAsync("SELECT COUNT(*) FROM reference_technique_specimens;"));
 }

 [Fact]
 public async Task TechniqueWorkItemRetriesAfterTransientProviderFailure()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var feature = await scheduler.EnqueueAsync(new(
 "worker-run-feature-retry", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);
 var featureWorker = new ReferenceCorpusAnalysisWorker(path, new FrozenFeatureAnalyzer(10), new UnexpectedTechniqueAnalyzer(), "worker-feature-retry");
 Assert.True(await featureWorker.PumpOnceAsync(CancellationToken.None));

 var technique = await scheduler.EnqueueAsync(new(
 "worker-run-technique-retry", 1, 101, ReferenceCorpusAnalysisJobKinds.TechniqueSpecimen,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 10_000,
 MaxAttempts: 2, DependencyJobId: feature.JobId), CancellationToken.None);
 var failingAnalyzer = new RetryableTechniqueAnalyzer();
 var failingWorker = new ReferenceCorpusAnalysisWorker(path, new FrozenFeatureAnalyzer(10), failingAnalyzer, "worker-technique-retry");

 Assert.True(await failingWorker.PumpOnceAsync(CancellationToken.None));
 var retrying = await scheduler.GetAsync(new(technique.JobId), CancellationToken.None);
 Assert.NotNull(retrying);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.RetryWait, retrying.Status);
 Assert.Equal("provider_transient", retrying.ErrorCode);
 Assert.Equal(1, retrying.FailureAttemptCount);
 Assert.NotNull(retrying.NextAttemptAt);
 Assert.Equal(1, failingAnalyzer.CallCount);

 var store = new SqliteReferenceCorpusAnalysisJobStore(DatabasePath);
 await store.RequeueDueRetriesAsync(retrying.NextAttemptAt.Value, CancellationToken.None);
 var succeedingAnalyzer = new FrozenTechniqueAnalyzer(tokensPerCall: 19);
 var retryWorker = new ReferenceCorpusAnalysisWorker(path, new FrozenFeatureAnalyzer(10), succeedingAnalyzer, "worker-technique-retry-success");

 Assert.True(await retryWorker.PumpOnceAsync(CancellationToken.None));
 var completed = await scheduler.GetAsync(new(technique.JobId), CancellationToken.None);
 Assert.NotNull(completed);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Completed, completed.Status);
 Assert.Equal(2, completed.AttemptCount);
 Assert.Equal(1, completed.FailureAttemptCount);
 Assert.Single(succeedingAnalyzer.Calls);
 }

 [Fact]
 public async Task PumpOnceFailsCorruptFrozenSnapshotWithoutWaitingForLeaseExpiry()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var queued = await scheduler.EnqueueAsync(new(
 "worker-run-corrupt", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);
 await ExecuteAsync("UPDATE reference_analysis_work_items SET input_payload_hash='corrupt' WHERE input_snapshot_id=(SELECT input_snapshot_id FROM reference_analysis_jobs WHERE job_id='" + queued.JobId + "');");
 var worker = new ReferenceCorpusAnalysisWorker(path, new FrozenFeatureAnalyzer(10), new UnexpectedTechniqueAnalyzer(), "worker-corrupt");

 Assert.True(await worker.PumpOnceAsync(CancellationToken.None));

 var failed = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);
 Assert.NotNull(failed);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Failed, failed.Status);
 Assert.Equal("analysis_snapshot_corrupt", failed.ErrorCode);
 Assert.Null(failed.LeaseExpiresAt);
 Assert.Equal(0, failed.ProcessedWorkItems);
Assert.Equal(0, await ScalarIntAsync("SELECT COUNT(*) FROM reference_feature_observations;"));
}

 [Fact]
 public async Task LifecycleStartIsIdempotentAndIdleLoopProcessesQueuedJob()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var analyzer = new FrozenFeatureAnalyzer(10);
 await using var worker = new ReferenceCorpusAnalysisWorker(
 path, analyzer, new UnexpectedTechniqueAnalyzer(), "worker-lifecycle", TimeSpan.FromMilliseconds(20));

 await worker.StartAsync();
 await worker.StartAsync();
 var queued = await scheduler.EnqueueAsync(new(
 "worker-run-lifecycle", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);

 ReferenceCorpusAnalysisJobPayload? completed = null;
 for (var attempt = 0; attempt < 100; attempt++)
 {
 completed = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);
 if (completed?.Status == ReferenceCorpusAnalysisJobStatuses.Completed) break;
 await Task.Delay(20);
 }
 await worker.StopAsync();
 await worker.StopAsync();

 Assert.NotNull(completed);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Completed, completed.Status);
Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies.Count, analyzer.Calls.Count);
}

 [Fact]
 public async Task StopDuringModelCallAbandonsReservationWithoutWaitingForLeaseExpiry()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var analyzer = new CancellationBlockingFeatureAnalyzer();
 await using var worker = new ReferenceCorpusAnalysisWorker(
 path, analyzer, new UnexpectedTechniqueAnalyzer(), "worker-stop", TimeSpan.FromMilliseconds(20));
 var queued = await scheduler.EnqueueAsync(new(
 "worker-run-stop", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 5000),
 CancellationToken.None);

 await worker.StartAsync();
 await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
 await worker.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

 var abandoned = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);
 Assert.NotNull(abandoned);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.RetryWait, abandoned.Status);
 Assert.Equal(4096, abandoned.TokensSpent);
 Assert.Null(abandoned.LeaseExpiresAt);
 Assert.Equal(0, abandoned.ProcessedWorkItems);
Assert.Equal(("pending", 0), await ReadFirstWorkItemReservationAsync(queued.JobId));
}

 [Fact]
 public async Task RealWorkerLoopReclaimsExpiredLeaseAndFencesLostWorkerCommit()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var staleAnalyzer = new BlockingFeatureAnalyzer(10);
 var timing = new ReferenceCorpusAnalysisWorkerOptions(
 TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10), TimeSpan.Zero);
 await using var staleWorker = new ReferenceCorpusAnalysisWorker(
 path, staleAnalyzer, new UnexpectedTechniqueAnalyzer(), "worker-stale-primary",
 TimeSpan.FromMilliseconds(10), timing);
 var recoveryAnalyzer = new FrozenFeatureAnalyzer(10, TimeSpan.FromMilliseconds(80));
 await using var recoveryWorker = new ReferenceCorpusAnalysisWorker(
 path, recoveryAnalyzer, new UnexpectedTechniqueAnalyzer(), "worker-stale-recovery",
 TimeSpan.FromMilliseconds(10), timing);
 var queued = await scheduler.EnqueueAsync(new(
 "worker-run-stale-lease", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 20_000),
 CancellationToken.None);

 await staleWorker.StartAsync();
 await staleAnalyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
 var running = await WaitForJobAsync(scheduler, queued.JobId,
 job => job.LeaseExpiresAt is not null, TimeSpan.FromSeconds(10));
 var leaseExpiresAt = running.LeaseExpiresAt!.Value;
 var remaining = leaseExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(20);
 if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

 Assert.True(await recoveryWorker.PumpOnceAsync(CancellationToken.None));
 var recoveredAt = DateTimeOffset.UtcNow;
 var completed = await WaitForJobAsync(scheduler, queued.JobId,
 job => job.Status == ReferenceCorpusAnalysisJobStatuses.Completed, TimeSpan.FromSeconds(10));
 staleAnalyzer.Release.TrySetResult();
 await staleAnalyzer.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
 await Task.Delay(50);
 await staleWorker.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

 Assert.True(recoveredAt - leaseExpiresAt <= TimeSpan.FromSeconds(30));
 Assert.Equal(2, completed.AttemptCount);
 Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies.Count, recoveryAnalyzer.Calls.Count);
 Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies.Count,
 await ScalarIntAsync("SELECT COUNT(*) FROM reference_analysis_work_item_completions;"));
 Assert.Equal(1, await ScalarIntAsync("SELECT COUNT(*) FROM reference_feature_observations;"));
 Assert.Equal(1, await ScalarIntAsync("SELECT COUNT(*) FROM reference_analysis_job_attempts WHERE outcome='abandoned' AND error_code='lease_expired';"));
 }

 [Fact]
 public async Task DisposeWaitsForInFlightManualPumpBeforeReleasingDatabase()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var analyzer = new BlockingFeatureAnalyzer(10);
 var worker = new ReferenceCorpusAnalysisWorker(
 path, analyzer, new UnexpectedTechniqueAnalyzer(), "worker-dispose-pump");
 await scheduler.EnqueueAsync(new(
 "worker-run-dispose-pump", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);

 var pump = worker.PumpOnceAsync(CancellationToken.None).AsTask();
 await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
 var dispose = worker.DisposeAsync().AsTask();

 await Task.Delay(50);
 Assert.False(dispose.IsCompleted);
 analyzer.Release.TrySetResult();
 Assert.True(await pump.WaitAsync(TimeSpan.FromSeconds(10)));
await dispose.WaitAsync(TimeSpan.FromSeconds(10));

File.Delete(DatabasePath);
Assert.False(File.Exists(DatabasePath));
 await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
 await worker.PumpOnceAsync(CancellationToken.None));
}

 [Fact]
public async Task PumpOnceStopsAtBudgetBoundaryWithoutLeavingRunningLease()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var queued = await scheduler.EnqueueAsync(new(
 "worker-run-budget", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 10),
 CancellationToken.None);
 var worker = new ReferenceCorpusAnalysisWorker(path, new FrozenFeatureAnalyzer(10), new UnexpectedTechniqueAnalyzer(), "worker-test-2");

 Assert.True(await worker.PumpOnceAsync(CancellationToken.None));

 var exhausted = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);
 Assert.NotNull(exhausted);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.BudgetExhausted, exhausted.Status);
 Assert.Equal(1, exhausted.ProcessedWorkItems);
 Assert.Equal(10, exhausted.TokensSpent);
Assert.Null(exhausted.LeaseExpiresAt);
}

 [Fact]
public async Task PumpOncePersistsProviderRetryAfterFromBridgeDetails()
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var queued = await scheduler.EnqueueAsync(new(
 "worker-run-retry-after", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000,
 MaxAttempts: 5), CancellationToken.None);
 var worker = new ReferenceCorpusAnalysisWorker(
 path, new RetryAfterFeatureAnalyzer(), new UnexpectedTechniqueAnalyzer(), "worker-retry-after");
 var before = DateTimeOffset.UtcNow;

 Assert.True(await worker.PumpOnceAsync(CancellationToken.None));

 var retrying = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);
 Assert.NotNull(retrying);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.RetryWait, retrying.Status);
 Assert.NotNull(retrying.NextAttemptAt);
 Assert.InRange(retrying.NextAttemptAt.Value, before.AddSeconds(17), DateTimeOffset.UtcNow.AddSeconds(18));
Assert.Equal(1, retrying.FailureAttemptCount);
}

 [Theory]
 [InlineData(false, ReferenceCorpusAnalysisJobStatuses.Paused)]
 [InlineData(true, ReferenceCorpusAnalysisJobStatuses.Cancelled)]
 public async Task PumpOnceCommitsSuccessfulOutputBeforeAcknowledgingControlRequest(
 bool cancel,
 string expectedStatus)
 {
 await SeedAsync();
 var path = new FixedPathResolver(DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(path, new FixedSettingsService());
 var queued = await scheduler.EnqueueAsync(new(
 $"worker-run-control-{cancel}", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);
 var analyzer = new BlockingFeatureAnalyzer(10);
 var worker = new ReferenceCorpusAnalysisWorker(path, analyzer, new UnexpectedTechniqueAnalyzer(), $"worker-control-{cancel}");

 var pump = worker.PumpOnceAsync(CancellationToken.None).AsTask();
 await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
 var running = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);
 Assert.NotNull(running);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Running, running.Status);
 if (cancel)
 await scheduler.CancelAsync(new(running.JobId, running.Version), CancellationToken.None);
 else
 await scheduler.PauseAsync(new(running.JobId, running.Version), CancellationToken.None);
 analyzer.Release.TrySetResult();

 Assert.True(await pump.WaitAsync(TimeSpan.FromSeconds(10)));
 var settled = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);
 Assert.NotNull(settled);
 Assert.Equal(expectedStatus, settled.Status);
 Assert.Equal(1, settled.ProcessedWorkItems);
 Assert.Equal(1, settled.SucceededWorkItems);
 Assert.Equal(10, settled.TokensSpent);
 Assert.Equal("1", settled.ResumeCursor);
 Assert.Null(settled.LeaseExpiresAt);
 Assert.Equal(1, await ScalarIntAsync("SELECT COUNT(*) FROM reference_feature_observations;"));
 }

 private async ValueTask SeedAsync()
 {
 Directory.CreateDirectory(_root);
 await using var connection = await OpenAsync();
 await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,source_file_hash,build_version,status,created_at,updated_at)
 VALUES (101,1,'Book','Author','book.txt','txt','allowed','source-hash','v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES
 ('node-chapter',101,NULL,'chapter',0,0,1,0,100,100,'hash-chapter','第一章','2026-07-10T00:00:00Z'),
 ('node-sentence',101,'node-chapter','sentence',1,1,1,10,18,8,'hash-sentence','冻结后台句子。','2026-07-10T00:00:00Z');
 """;
 await command.ExecuteNonQueryAsync();
 }

private async ValueTask<int> ScalarIntAsync(string sql)
{
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = sql;
return Convert.ToInt32(await command.ExecuteScalarAsync());
}

 private static async Task<ReferenceCorpusAnalysisJobPayload> WaitForJobAsync(
 SqliteReferenceCorpusAnalysisScheduler scheduler,
 string jobId,
 Func<ReferenceCorpusAnalysisJobPayload, bool> condition,
 TimeSpan timeout)
 {
 var deadline = DateTimeOffset.UtcNow + timeout;
 while (DateTimeOffset.UtcNow < deadline)
 {
 var job = await scheduler.GetAsync(new(jobId), CancellationToken.None);
 if (job is not null && condition(job)) return job;
 await Task.Delay(10);
 }
 throw new TimeoutException($"Analysis job '{jobId}' did not reach the expected state within {timeout}.");
 }

private async ValueTask ExecuteAsync(string sql)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = sql;
await command.ExecuteNonQueryAsync();
}

 private async ValueTask<(string State, int ReservedTokens)> ReadFirstWorkItemReservationAsync(string jobId)
 {
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT work_state,reserved_tokens
 FROM reference_analysis_work_items
 WHERE input_snapshot_id=(SELECT input_snapshot_id FROM reference_analysis_jobs WHERE job_id=$job_id)
 ORDER BY ordinal LIMIT 1;
 """;
 command.Parameters.AddWithValue("$job_id", jobId);
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
 return (reader.GetString(0), reader.GetInt32(1));
 }

 private async ValueTask<SqliteConnection> OpenAsync()
 {
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
 await connection.OpenAsync();
 return connection;
 }

 public Task InitializeAsync() => Task.CompletedTask;
 public Task DisposeAsync()
 {
 if (Directory.Exists(_root)) Directory.Delete(_root, true);
 return Task.CompletedTask;
 }

private sealed class FrozenFeatureAnalyzer(int tokensPerCall, TimeSpan? delay = null) : IReferenceCorpusFeatureFamilyAnalyzer
 {
 public List<ReferenceCorpusFeatureFamilyAnalysisInput> Calls { get; } = [];
 public async ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(ReferenceCorpusFeatureFamilyAnalysisInput input, CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 if (delay is { } configuredDelay) await Task.Delay(configuredDelay, cancellationToken);
 Calls.Add(input);
 var observations = input.Family == ReferenceCorpusFeatureFamilies.Syntax
 ? "[{\"feature_key\":\"sentence_pattern\",\"label\":\"subject_predicate\",\"complexity\":\"simple\",\"confidence\":0.8,\"evidence_start\":0,\"evidence_end\":4,\"explanation\":\"frozen worker fixture\"}]"
 : "[]";
 return new ReferenceCorpusFeatureFamilyAnalysisOutput(
 $"{{\"schema_version\":\"reference-corpus-feature-family-v1\",\"family\":\"{input.Family}\",\"node_type\":\"sentence\",\"observations\":{observations}}}",
 tokensPerCall);
 }
 }

private sealed class BlockingFeatureAnalyzer(int tokensPerCall) : IReferenceCorpusFeatureFamilyAnalyzer
{
 public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
 public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
 public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

 public async ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusFeatureFamilyAnalysisInput input,
 CancellationToken cancellationToken)
 {
 Started.TrySetResult();
 await Release.Task.WaitAsync(cancellationToken);
 var observations = input.Family == ReferenceCorpusFeatureFamilies.Syntax
 ? "[{\"feature_key\":\"sentence_pattern\",\"label\":\"subject_predicate\",\"complexity\":\"simple\",\"confidence\":0.8,\"evidence_start\":0,\"evidence_end\":4,\"explanation\":\"control boundary fixture\"}]"
 : "[]";
 var output = new ReferenceCorpusFeatureFamilyAnalysisOutput(
 $"{{\"schema_version\":\"reference-corpus-feature-family-v1\",\"family\":\"{input.Family}\",\"node_type\":\"sentence\",\"observations\":{observations}}}",
 tokensPerCall);
 Returned.TrySetResult();
 return output;
 }
 }

private sealed class CancellationBlockingFeatureAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
 {
 public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

 public async ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusFeatureFamilyAnalysisInput input,
 CancellationToken cancellationToken)
 {
 Started.TrySetResult();
 await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
 throw new InvalidOperationException("Cancellation should interrupt the analyzer.");
 }
 }

 private sealed class UnexpectedTechniqueAnalyzer : IReferenceCorpusTechniqueSpecimenAnalyzer
 {
 public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(ReferenceCorpusTechniqueSpecimenAnalysisInput input, CancellationToken cancellationToken) =>
 throw new InvalidOperationException("Technique analyzer should not be called.");
 }

 private sealed class FrozenTechniqueAnalyzer(int tokensPerCall) : IReferenceCorpusTechniqueSpecimenAnalyzer
 {
 public List<ReferenceCorpusTechniqueSpecimenAnalysisInput> Calls { get; } = [];

 public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusTechniqueSpecimenAnalysisInput input,
 CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 Calls.Add(input);
 var observationId = input.Observations.Single().ObservationId;
 return ValueTask.FromResult(new ReferenceCorpusTechniqueSpecimenAnalysisOutput(
 $$"""
 {
   "schema_version": "reference-corpus-technique-specimen-v1",
   "source_node_id": "{{input.NodeId}}",
   "technique_family": "action_as_emotion",
   "technique_abstract": "用可见动作承载压抑情绪，并以沉默留白放大张力",
   "trigger_context": "角色有强烈情绪但不能直接说破的短句节点",
   "transfer_template": "[角色] [外化细节动作]，随后留出沉默。",
   "transfer_slots": [
     { "slot_name": "role", "purpose": "当前承压角色", "constraints": "必须处在情绪压抑状态" }
   ],
   "effect_on_reader": "让读者从动作和空白中自行补全情绪",
   "applicability_conditions": ["角色需要压住反应"],
   "failure_modes": ["动作与情境没有因果时会显得装饰化"],
   "anti_patterns": ["直接解释角色情绪"],
   "world_context_dependencies": [],
   "why_it_works": [
     { "factor": "动作提供可见证据", "observation_ids": ["{{observationId}}"], "explanation": "特征证据来自冻结输入。" }
   ],
   "confidence": 0.86,
   "mastery_notes": "适合短句。"
 }
 """,
 tokensPerCall));
 }
 }

 private sealed class RetryableTechniqueAnalyzer : IReferenceCorpusTechniqueSpecimenAnalyzer
 {
 public int CallCount { get; private set; }

 public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusTechniqueSpecimenAnalysisInput input,
 CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 CallCount++;
 throw new BridgeRequestException("provider_transient", "Temporary technique provider failure.", retryable: true);
 }
 }

 private sealed class FixedPathResolver(string path) : IReferenceCorpusDatabasePathResolver
 {
 public ValueTask<string> ResolveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(path);
 }

private sealed class RetryAfterFeatureAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
 {
 public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusFeatureFamilyAnalysisInput input,
 CancellationToken cancellationToken) =>
 throw new BridgeRequestException(
 "provider_rate_limited",
 "Rate limited.",
 new { retry_after_seconds = 17 },
retryable: true);
}

 private sealed class FixedSettingsService : IAppSettingsService
 {
 public ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new AppSettingsPayload(1,0,"provider-a/model-a","high","manual",360,"",""));
 public ValueTask SaveSettingsAsync(CancellationToken cancellationToken)=>throw new NotSupportedException();
 public ValueTask SetSelectedModelAsync(string selectedModelKey,string reasoningEffort,CancellationToken cancellationToken)=>throw new NotSupportedException();
 public ValueTask SetReasoningEffortAsync(string reasoningEffort,CancellationToken cancellationToken)=>throw new NotSupportedException();
 public ValueTask SetChatPanelWidthAsync(int width,CancellationToken cancellationToken)=>throw new NotSupportedException();
 public ValueTask SetLastSessionAsync(string sessionId,CancellationToken cancellationToken)=>throw new NotSupportedException();
 public ValueTask SetLastNovelAsync(long novelId,CancellationToken cancellationToken)=>throw new NotSupportedException();
 public ValueTask SetApprovalModeAsync(string mode,CancellationToken cancellationToken)=>throw new NotSupportedException();
 public ValueTask SaveUserNameAsync(string name,CancellationToken cancellationToken)=>throw new NotSupportedException();
 public ValueTask SaveAvatarAsync(byte[] data,CancellationToken cancellationToken)=>throw new NotSupportedException();
 }
}
