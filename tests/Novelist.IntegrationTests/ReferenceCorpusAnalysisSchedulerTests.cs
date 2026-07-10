using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusAnalysisSchedulerTests : IAsyncLifetime
{
 private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-analysis-scheduler-tests", Guid.NewGuid().ToString("N"));
 private string DatabasePath => Path.Combine(_root, "index.sqlite");

 [Fact]
 public async Task EnqueuePersistsFrozenSnapshotAndSurvivesSchedulerRestart()
 {
 await SeedAsync();
 var scheduler = CreateScheduler();
 var queued = await scheduler.EnqueueAsync(new(
 "scheduler-run-1", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);

 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, queued.Status);
 Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies.Count, queued.TotalWorkItems);
 var restarted = CreateScheduler();
 var restored = await restarted.GetAsync(new(queued.JobId), CancellationToken.None);
 Assert.NotNull(restored);
 Assert.Equal(queued.JobId, restored.JobId);
 Assert.Equal("sentence", restored.Scope);

 var list = await restarted.ListAsync(new(new(null, 20, "updated_at", "desc",
 new Dictionary<string, string> { ["anchor_id"] = "101" })), CancellationToken.None);
 Assert.Single(list.Items);
 Assert.Equal(1, list.Total);

 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT input_payload_json,input_payload_hash FROM reference_analysis_work_items ORDER BY ordinal LIMIT 1;";
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
 var payload = ReferenceCorpusAnalysisFrozenInputCodec.Deserialize<ReferenceCorpusFrozenFeatureWorkItem>(reader.GetString(0), reader.GetString(1));
 Assert.Equal("冻结调度句子。", payload.NodeText);
 Assert.Equal("provider-a", payload.Model.ProviderName);
 Assert.Equal("model-a", payload.Model.ModelId);
 Assert.Equal("high", payload.Model.ReasoningEffort);
 }

 [Fact]
public async Task ControlOperationsUsePersistentCasVersions()
 {
 await SeedAsync();
 var scheduler = CreateScheduler();
 var queued = await scheduler.EnqueueAsync(new(
 "scheduler-run-2", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);

 var paused = await scheduler.PauseAsync(new(queued.JobId, queued.Version), CancellationToken.None);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Paused, paused.Status);
 var resumed = await CreateScheduler().ResumeAsync(new(paused.JobId, paused.Version, 2000), CancellationToken.None);
 Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Queued, resumed.Status);
 Assert.Equal(2000, resumed.TokenBudget);
 var reprioritized = await scheduler.ReprioritizeAsync(new(resumed.JobId, resumed.Version, ReferenceCorpusAnalysisPriorityClasses.CurrentChapter, 300), CancellationToken.None);
 Assert.Equal(300, reprioritized.PriorityValue);
 var cancelled = await scheduler.CancelAsync(new(reprioritized.JobId, reprioritized.Version), CancellationToken.None);
Assert.Equal(ReferenceCorpusAnalysisJobStatuses.Cancelled, cancelled.Status);
}

 [Fact]
 public async Task GetExposesCurrentTokenReservationAfterWorkItemReservation()
 {
 await SeedAsync();
 var scheduler = CreateScheduler();
 var queued = await scheduler.EnqueueAsync(new(
 "scheduler-run-reservation", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);
 var store = new SqliteReferenceCorpusAnalysisJobStore(DatabasePath);
 var now = DateTimeOffset.UtcNow;
 var claim = await store.ClaimNextAsync("worker-reservation", now, TimeSpan.FromSeconds(45));
 Assert.NotNull(claim);

 var reservation = await store.ReserveNextWorkItemAsync(
 queued.JobId, "worker-reservation", claim.LeaseToken, 64, now.AddSeconds(1));
 var payload = await scheduler.GetAsync(new(queued.JobId), CancellationToken.None);

 Assert.NotNull(reservation);
 Assert.NotNull(payload);
 Assert.Equal(64, payload.TokensReserved);
 Assert.Contains("pause", payload.AllowedActions!);
 Assert.Equal(1, payload.AttemptCount);
 Assert.Equal(0, payload.FailureAttemptCount);
 }

 [Fact]
 public async Task ListCursorIsStableAndRejectedWhenFiltersChange()
 {
 await SeedAsync();
 var scheduler = CreateScheduler();
 await scheduler.EnqueueAsync(new(
 "scheduler-run-page-1", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);
 await scheduler.EnqueueAsync(new(
 "scheduler-run-page-2", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1000),
 CancellationToken.None);
 var request = new ListReferenceCorpusAnalysisJobsPayload(new(
 null, 1, "updated_at", "desc", new Dictionary<string, string> { ["anchor_id"] = "101" }));

 var first = await scheduler.ListAsync(request, CancellationToken.None);
 var second = await scheduler.ListAsync(
 request with { PageRequest = request.PageRequest with { Cursor = first.NextCursor } },
 CancellationToken.None);

 Assert.True(first.HasMore);
 Assert.NotNull(first.NextCursor);
 Assert.Single(second.Items);
 Assert.NotEqual(first.Items[0].JobId, second.Items[0].JobId);
 var mismatched = request with
 {
 PageRequest = request.PageRequest with
 {
 Cursor = first.NextCursor,
 Filters = new Dictionary<string, string>
 {
 ["anchor_id"] = "101",
 ["status"] = ReferenceCorpusAnalysisJobStatuses.Queued
 }
 }
 };
 var exception = await Assert.ThrowsAsync<PageRequestValidationException>(async () =>
 await scheduler.ListAsync(mismatched, CancellationToken.None));
 Assert.Equal(PageRequestErrorCodes.InvalidCursor, exception.Code);
 }

 private SqliteReferenceCorpusAnalysisScheduler CreateScheduler() => new(
 new FixedPathResolver(DatabasePath),
 new FixedSettingsService());

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
 ('node-sentence',101,'node-chapter','sentence',1,1,1,10,19,9,'hash-sentence','冻结调度句子。','2026-07-10T00:00:00Z');
 """;
 await command.ExecuteNonQueryAsync();
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
 if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
 return Task.CompletedTask;
 }

 private sealed class FixedPathResolver(string path) : IReferenceCorpusDatabasePathResolver
 {
 public ValueTask<string> ResolveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(path);
 }

 private sealed class FixedSettingsService : IAppSettingsService
 {
 public ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken) =>
 ValueTask.FromResult(new AppSettingsPayload(1, 0, "provider-a/model-a", "high", "manual", 360, "", ""));
 public ValueTask SaveSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
 public ValueTask SetSelectedModelAsync(string selectedModelKey, string reasoningEffort, CancellationToken cancellationToken) => throw new NotSupportedException();
 public ValueTask SetReasoningEffortAsync(string reasoningEffort, CancellationToken cancellationToken) => throw new NotSupportedException();
 public ValueTask SetChatPanelWidthAsync(int width, CancellationToken cancellationToken) => throw new NotSupportedException();
 public ValueTask SetLastSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
 public ValueTask SetLastNovelAsync(long novelId, CancellationToken cancellationToken) => throw new NotSupportedException();
 public ValueTask SetApprovalModeAsync(string mode, CancellationToken cancellationToken) => throw new NotSupportedException();
 public ValueTask SaveUserNameAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
 public ValueTask SaveAvatarAsync(byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();
 }
}
