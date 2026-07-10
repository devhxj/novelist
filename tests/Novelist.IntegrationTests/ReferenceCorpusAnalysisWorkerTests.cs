using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
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

private sealed class FrozenFeatureAnalyzer(int tokensPerCall) : IReferenceCorpusFeatureFamilyAnalyzer
 {
 public List<ReferenceCorpusFeatureFamilyAnalysisInput> Calls { get; } = [];
 public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(ReferenceCorpusFeatureFamilyAnalysisInput input, CancellationToken cancellationToken)
 {
 Calls.Add(input);
 var observations = input.Family == ReferenceCorpusFeatureFamilies.Syntax
 ? "[{\"feature_key\":\"sentence_pattern\",\"label\":\"subject_predicate\",\"complexity\":\"simple\",\"confidence\":0.8,\"evidence_start\":0,\"evidence_end\":4,\"explanation\":\"frozen worker fixture\"}]"
 : "[]";
 return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
 $"{{\"schema_version\":\"reference-corpus-feature-family-v1\",\"family\":\"{input.Family}\",\"node_type\":\"sentence\",\"observations\":{observations}}}",
 tokensPerCall));
 }
 }

private sealed class BlockingFeatureAnalyzer(int tokensPerCall) : IReferenceCorpusFeatureFamilyAnalyzer
 {
 public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
 public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

 public async ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusFeatureFamilyAnalysisInput input,
 CancellationToken cancellationToken)
 {
 Started.TrySetResult();
 await Release.Task.WaitAsync(cancellationToken);
 var observations = input.Family == ReferenceCorpusFeatureFamilies.Syntax
 ? "[{\"feature_key\":\"sentence_pattern\",\"label\":\"subject_predicate\",\"complexity\":\"simple\",\"confidence\":0.8,\"evidence_start\":0,\"evidence_end\":4,\"explanation\":\"control boundary fixture\"}]"
 : "[]";
 return new(
 $"{{\"schema_version\":\"reference-corpus-feature-family-v1\",\"family\":\"{input.Family}\",\"node_type\":\"sentence\",\"observations\":{observations}}}",
 tokensPerCall);
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

 private sealed class FixedPathResolver(string path) : IReferenceCorpusDatabasePathResolver
 {
 public ValueTask<string> ResolveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(path);
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
