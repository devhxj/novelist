using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

return await CorpusHarnessHost.RunAsync(args);

internal static class CorpusHarnessHost
{
 private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
 private const int ReservedTokens = 400;
 private const int ActualTokens = 275;
 private const string WorkerId = "quantitative-worker";

 public static async Task<int> RunAsync(string[] args)
 {
 try
 {
 var options = Arguments.Parse(args);
 object result = options.Command switch
 {
 "fault" => await RunFaultAsync(options),
 "recover" => await RunRecoveryAsync(options),
 "runtime-control" => await RunRuntimeControlAsync(options),
 "runtime-stale-lease" => await RunRuntimeStaleLeaseAsync(options),
 "scale" => await RunScaleAsync(options),
 "scale-full" => await RunFullScaleAsync(options),
 _ => throw new ArgumentException($"Unknown command '{options.Command}'.")
 };
 Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
 return 0;
 }
 catch (Exception exception)
 {
 Console.Error.WriteLine(exception);
 return 1;
 }
 }

 private static async Task<object> RunFaultAsync(Arguments options)
 {
 Require(options.DatabasePath, "--database");
 Require(options.CheckpointPath, "--checkpoint");
 Require(options.Point, "--point");
if (File.Exists(options.DatabasePath)) File.Delete(options.DatabasePath);
 Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
var store = new SqliteReferenceCorpusAnalysisJobStore(options.DatabasePath);
await store.EnsureSchemaAsync();
await SeedAnchorAndProbeAsync(options.DatabasePath);
 var now = DateTimeOffset.UtcNow;
 var reservation = await CreateReservationAsync(store, options.ScenarioId, now, ReservedTokens);
 await WriteScenarioAsync(options, reservation, now);

 if (options.Point == "after_reservation") await CheckpointAndBlockAsync(options);
 if (options.Point == "after_model")
 {
 await File.WriteAllTextAsync(options.ModelResultPath, "model-result-ready");
 await CheckpointAndBlockAsync(options);
 }

 var completion = CreateCompletion(reservation, ActualTokens, now.AddSeconds(1));
 await store.RecordCompletionAsync(reservation, completion, now.AddSeconds(1));
 if (options.Point == "after_record") await CheckpointAndBlockAsync(options);

 if (options.Point == "during_finalize")
 {
 await store.FinalizeCompletionAsync(completion, now.AddSeconds(2), async (connection, transaction, _, cancellationToken) =>
 {
 await InsertProbeAsync(connection, transaction, completion.CompletionKey, cancellationToken);
 await CheckpointAndBlockAsync(options);
 });
 }
 else
 {
 await store.FinalizeCompletionAsync(completion, now.AddSeconds(2),
 (connection, transaction, envelope, cancellationToken) =>
 InsertProbeAsync(connection, transaction, envelope.CompletionKey, cancellationToken));
 }

 if (options.Point == "after_commit") await CheckpointAndBlockAsync(options);
 throw new InvalidOperationException("Fault host reached an unsupported checkpoint path.");
 }

 private static async Task<object> RunRecoveryAsync(Arguments options)
 {
 Require(options.DatabasePath, "--database");
 Require(options.ScenarioPath, "--scenario");
 var scenario = JsonSerializer.Deserialize<ScenarioState>(await File.ReadAllTextAsync(options.ScenarioPath), JsonOptions)
 ?? throw new InvalidOperationException("Scenario state is empty.");
 var store = new SqliteReferenceCorpusAnalysisJobStore(options.DatabasePath);
 await store.EnsureSchemaAsync();
 var started = Stopwatch.StartNew();
 var reservation = scenario.Reservation;
 var completion = CreateCompletion(reservation, ActualTokens, scenario.CreatedAt.AddSeconds(1));
 var point = scenario.Point;

 if (point is "after_reservation" or "after_model")
 {
 var future = DateTimeOffset.UtcNow.AddMinutes(2);
 await store.ReclaimExpiredLeasesAsync(future, future.AddMilliseconds(1));
 await store.RequeueDueRetriesAsync(future.AddSeconds(1));
 var claim = await store.ClaimNextAsync(WorkerId + "-recovery", future.AddSeconds(2), TimeSpan.FromSeconds(45))
 ?? throw new InvalidOperationException("Recovered job was not claimable.");
 reservation = await store.ReserveNextWorkItemAsync(
 scenario.Reservation.JobId, WorkerId + "-recovery", claim.LeaseToken,
 ReservedTokens, future.AddSeconds(3))
 ?? throw new InvalidOperationException("Recovered work item was not reservable.");
 completion = CreateCompletion(reservation, ActualTokens, future.AddSeconds(4));
 await store.RecordCompletionAsync(reservation, completion, future.AddSeconds(4));
 }

 await store.FinalizeCompletionAsync(completion, DateTimeOffset.UtcNow,
 (connection, transaction, envelope, cancellationToken) =>
 InsertProbeAsync(connection, transaction, envelope.CompletionKey, cancellationToken));
 started.Stop();
 var audit = await ReadAuditAsync(options.DatabasePath, scenario.Reservation.JobId, completion.CompletionKey);
 return new
{
scenario = scenario.ScenarioId,
 point,
 recovery_ms = started.Elapsed.TotalMilliseconds,
 model_replayed = point == "after_model",
 audit,
 passed = audit.SucceededWorkItems == 1 && audit.PendingWorkItems == 0 &&
 audit.CompletionRows == 1 && audit.FinalizedCompletionRows == 1 &&
 audit.OutputRows == 1 && audit.DuplicateOutputRows == 0 &&
 audit.TokensReserved == 0 && audit.TokensSpent == audit.ExpectedTokensSpent
 };
 }

 private static async Task<object> RunRuntimeControlAsync(Arguments options)
 {
 Require(options.RootPath, "--root");
 if (options.Samples < 1) throw new ArgumentOutOfRangeException(nameof(options.Samples));
 var root = Path.GetFullPath(options.RootPath);
 if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
 Directory.CreateDirectory(root);

 var pauseCases = new List<RuntimeControlSample>(options.Samples);
 var cancelCases = new List<RuntimeControlSample>(options.Samples);
 for (var sample = 1; sample <= options.Samples; sample++)
 {
 pauseCases.Add(await RunControlSampleAsync(root, sample, cancel: false));
 cancelCases.Add(await RunControlSampleAsync(root, sample, cancel: true));
 }

 var pauseLatencies = pauseCases.Select(item => item.RequestToSettledMs).ToArray();
 var cancelLatencies = cancelCases.Select(item => item.RequestToSettledMs).ToArray();
 var passed = pauseCases.All(item => item.Passed) && cancelCases.All(item => item.Passed) &&
 Percentile(pauseLatencies, 0.95) <= 60_000 && Percentile(cancelLatencies, 0.95) <= 60_000;
 var report = new
 {
 schema_version = "corpus-m2-runtime-control-metrics-v1",
 generated_at = DateTimeOffset.UtcNow,
 samples_per_control = options.Samples,
 pause = Distribution(pauseLatencies),
 cancel = Distribution(cancelLatencies),
 cases = new { pause = pauseCases, cancel = cancelCases },
 passed
 };
 if (!string.IsNullOrWhiteSpace(options.MetricsOutputPath))
 await WriteAtomicJsonAsync(options.MetricsOutputPath, report);
 return report;
 }

 private static async Task<RuntimeControlSample> RunControlSampleAsync(
 string root,
 int sample,
 bool cancel)
 {
 var name = cancel ? "cancel" : "pause";
 var databasePath = Path.Combine(root, $"{name}-{sample:D3}.sqlite");
 var store = new SqliteReferenceCorpusAnalysisJobStore(databasePath);
 await store.EnsureSchemaAsync();
 await SeedRuntimeCorpusAsync(databasePath);
 var resolver = new FixedPathResolver(databasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(resolver, new FixedSettingsService());
 var analyzer = new GateFeatureAnalyzer();
 await using var worker = new ReferenceCorpusAnalysisWorker(
 resolver, analyzer, new UnexpectedTechniqueAnalyzer(), $"runtime-{name}-{sample}",
 TimeSpan.FromMilliseconds(10));
 var queued = await scheduler.EnqueueAsync(new(
 $"runtime-{name}-run-{sample}", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 1_000),
 CancellationToken.None);

 await worker.StartAsync();
 await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
 var running = await WaitForRuntimeJobAsync(scheduler, queued.JobId,
 job => job.Status == ReferenceCorpusAnalysisJobStatuses.Running, TimeSpan.FromSeconds(10));
 var started = Stopwatch.StartNew();
 if (cancel)
 await scheduler.CancelAsync(new(running.JobId, running.Version), CancellationToken.None);
 else
 await scheduler.PauseAsync(new(running.JobId, running.Version), CancellationToken.None);
 analyzer.Release.TrySetResult();
 var expectedStatus = cancel ? ReferenceCorpusAnalysisJobStatuses.Cancelled : ReferenceCorpusAnalysisJobStatuses.Paused;
 var settled = await WaitForRuntimeJobAsync(scheduler, queued.JobId,
 job => job.Status == expectedStatus, TimeSpan.FromSeconds(10));
 started.Stop();
 await worker.StopAsync();
 var passed = settled.ProcessedWorkItems == 1 && settled.SucceededWorkItems == 1 &&
 settled.TokensSpent > 0 && started.Elapsed <= TimeSpan.FromSeconds(60);
 return new RuntimeControlSample(name, sample, started.Elapsed.TotalMilliseconds, settled.Status,
 settled.ProcessedWorkItems, settled.SucceededWorkItems, passed);
 }

 private static async Task<object> RunRuntimeStaleLeaseAsync(Arguments options)
 {
 Require(options.RootPath, "--root");
 if (options.Samples < 1) throw new ArgumentOutOfRangeException(nameof(options.Samples));
 var root = Path.GetFullPath(options.RootPath);
 if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
 Directory.CreateDirectory(root);

 var cases = new List<RuntimeStaleLeaseSample>(options.Samples);
 for (var sample = 1; sample <= options.Samples; sample++)
 cases.Add(await RunStaleLeaseSampleAsync(root, sample));
 var reclaimLatencies = cases.Select(item => item.ExpiryToReclaimMs).ToArray();
 var passed = cases.All(item => item.Passed) && Percentile(reclaimLatencies, 0.95) <= 30_000;
 var report = new
 {
 schema_version = "corpus-m2-runtime-stale-lease-metrics-v1",
 generated_at = DateTimeOffset.UtcNow,
 samples = options.Samples,
 reclaim_after_expiry_ms = Distribution(reclaimLatencies),
 cases,
 passed
 };
 if (!string.IsNullOrWhiteSpace(options.MetricsOutputPath))
 await WriteAtomicJsonAsync(options.MetricsOutputPath, report);
 return report;
 }

 private static async Task<RuntimeStaleLeaseSample> RunStaleLeaseSampleAsync(string root, int sample)
 {
 var databasePath = Path.Combine(root, $"stale-{sample:D3}.sqlite");
 var store = new SqliteReferenceCorpusAnalysisJobStore(databasePath);
 await store.EnsureSchemaAsync();
 await SeedRuntimeCorpusAsync(databasePath);
 var resolver = new FixedPathResolver(databasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(resolver, new FixedSettingsService());
 var timing = new ReferenceCorpusAnalysisWorkerOptions(
 TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10), TimeSpan.Zero);
 var staleAnalyzer = new GateFeatureAnalyzer();
 await using var staleWorker = new ReferenceCorpusAnalysisWorker(
 resolver, staleAnalyzer, new UnexpectedTechniqueAnalyzer(), $"runtime-stale-primary-{sample}",
 TimeSpan.FromMilliseconds(10), timing);
 await using var recoveryWorker = new ReferenceCorpusAnalysisWorker(
 resolver, new FrozenFeatureAnalyzer(), new UnexpectedTechniqueAnalyzer(), $"runtime-stale-recovery-{sample}",
 TimeSpan.FromMilliseconds(10), timing);
 var queued = await scheduler.EnqueueAsync(new(
 $"runtime-stale-run-{sample}", 1, 101, ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusAnalysisPriorityClasses.Normal, 100, 20_000),
 CancellationToken.None);

 await staleWorker.StartAsync();
 await staleAnalyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
 var running = await WaitForRuntimeJobAsync(scheduler, queued.JobId,
 job => job.LeaseExpiresAt is not null, TimeSpan.FromSeconds(10));
 var leaseExpiresAt = running.LeaseExpiresAt!.Value;
 var remaining = leaseExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(20);
 if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

 await recoveryWorker.StartAsync();
 var completed = await WaitForRuntimeJobAsync(scheduler, queued.JobId,
 job => job.Status == ReferenceCorpusAnalysisJobStatuses.Completed, TimeSpan.FromSeconds(10));
 var reclaimAt = await ReadAttemptCompletedAtAsync(databasePath, queued.JobId, 1);
 staleAnalyzer.Release.TrySetResult();
 await staleAnalyzer.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
 await Task.Delay(50);
 await staleWorker.StopAsync();
 await recoveryWorker.StopAsync();

 var completionRows = await ScalarAsync(databasePath, "SELECT COUNT(*) FROM reference_analysis_work_item_completions;");
 var observationRows = await ScalarAsync(databasePath, "SELECT COUNT(*) FROM reference_feature_observations;");
 var abandonedAttempts = await ScalarAsync(databasePath, "SELECT COUNT(*) FROM reference_analysis_job_attempts WHERE outcome='abandoned' AND error_code='lease_expired';");
 var expiryToReclaim = (reclaimAt - leaseExpiresAt).TotalMilliseconds;
 var passed = expiryToReclaim >= 0 && expiryToReclaim <= 30_000 && completed.AttemptCount == 2 &&
 completionRows == ReferenceCorpusFeatureFamilies.SentenceFamilies.Count && observationRows == 1 &&
 abandonedAttempts == 1;
 return new RuntimeStaleLeaseSample(sample, expiryToReclaim,
 (DateTimeOffset.UtcNow - leaseExpiresAt).TotalMilliseconds, completed.AttemptCount,
 completionRows, observationRows, abandonedAttempts, passed);
 }

 private static async Task<object> RunScaleAsync(Arguments options)
 {
Require(options.DatabasePath, "--database");
Require(options.FixturePath, "--fixture");
 Require(options.MetricsOutputPath, "--metrics-output");
 Require(options.ProgressOutputPath, "--progress-output");
 await WriteScaleProgressAsync(options, "loading_fixture", 0, 0, 0, 0, null);
if (File.Exists(options.DatabasePath)) File.Delete(options.DatabasePath);
 var records = new List<ScaleRecord>();
 long characters = 0;
 await foreach (var line in File.ReadLinesAsync(options.FixturePath))
 {
 var record = JsonSerializer.Deserialize<ScaleRecord>(line, JsonOptions)
 ?? throw new InvalidOperationException("Scale fixture contains an empty record.");
 records.Add(record);
 characters += record.Text.Length;
 }
if (characters < options.MinimumCharacters)
throw new InvalidOperationException($"Scale fixture has {characters} characters; expected at least {options.MinimumCharacters}.");
 await WriteScaleProgressAsync(options, "seeding", records.Count, 0, characters, 0, null);

 var store = new SqliteReferenceCorpusAnalysisJobStore(options.DatabasePath);
await store.EnsureSchemaAsync();
await SeedAnchorAndProbeAsync(options.DatabasePath);
 var nodeSeedStarted = Stopwatch.StartNew();
 await SeedScaleNodesAsync(options.DatabasePath, records);
 nodeSeedStarted.Stop();
var enqueueStarted = Stopwatch.StartNew();
 var jobCount = 0;
 foreach (var chunk in records.Chunk(options.JobSize))
 {
 var suffix = $"scale-{jobCount:D5}";
 await EnqueueScaleJobAsync(store, suffix, chunk, DateTimeOffset.UtcNow.AddMilliseconds(jobCount));
 jobCount++;
 }
enqueueStarted.Stop();
 await WriteScaleProgressAsync(options, "running", records.Count, 0, characters, jobCount, 0);

 var claimSamples = new List<double>(jobCount);
 var workSamples = new List<double>(records.Count);
 var listSamples = new List<double>();
 var tokenSpent = 0L;
 var processed = 0;
 var runStarted = Stopwatch.StartNew();
 while (processed < records.Count)
 {
 var claimWatch = Stopwatch.StartNew();
 var claim = await store.ClaimNextAsync(WorkerId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(45));
 claimWatch.Stop();
 if (claim is null) throw new InvalidOperationException($"No claim available after {processed}/{records.Count} work items.");
 claimSamples.Add(claimWatch.Elapsed.TotalMilliseconds);
 while (true)
 {
 var itemWatch = Stopwatch.StartNew();
 var reservation = await store.ReserveNextWorkItemAsync(
 claim.Job.JobId, WorkerId, claim.LeaseToken, 32, DateTimeOffset.UtcNow);
 if (reservation is null) break;
 var completion = CreateCompletion(reservation, 24, DateTimeOffset.UtcNow);
 await store.RecordCompletionAsync(reservation, completion, DateTimeOffset.UtcNow);
 await store.FinalizeCompletionAsync(completion, DateTimeOffset.UtcNow,
 (connection, transaction, envelope, cancellationToken) =>
 InsertScaleOutputAsync(connection, transaction, envelope.CompletionKey, cancellationToken));
 itemWatch.Stop();
 workSamples.Add(itemWatch.Elapsed.TotalMilliseconds);
 tokenSpent += 24;
 processed++;
 if (processed % 250 == 0)
 {
 var listWatch = Stopwatch.StartNew();
 await store.ListAsync(new(null, null, null, 0, 50));
listWatch.Stop();
listSamples.Add(listWatch.Elapsed.TotalMilliseconds);
 await WriteScaleProgressAsync(
 options, "running", records.Count, processed, characters, jobCount,
 records.Count == 0 ? 100 : processed * 100d / records.Count);
 }
}
 }
 runStarted.Stop();
 var outputRows = await ScalarAsync(options.DatabasePath, "SELECT COUNT(*) FROM harness_scale_outputs;");
 var result = new
{
fixture = Path.GetFullPath(options.FixturePath),
 characters,
 work_items = records.Count,
jobs = jobCount,
 node_seed_ms = nodeSeedStarted.Elapsed.TotalMilliseconds,
 enqueue_ms = enqueueStarted.Elapsed.TotalMilliseconds,
 elapsed_ms = runStarted.Elapsed.TotalMilliseconds,
 throughput_work_items_per_second = records.Count / runStarted.Elapsed.TotalSeconds,
 claim_ms = Distribution(claimSamples),
 work_item_ms = Distribution(workSamples),
 task_list_ms = Distribution(listSamples),
 tokens = new { spent = tokenSpent, reserved = 0, budget_penetration = 0 },
 output_rows = outputRows,
duplicate_outputs = records.Count - outputRows,
 post_finalize_job_reads = 0,
 passed = outputRows == records.Count &&
 records.Count / runStarted.Elapsed.TotalSeconds >= options.MinimumThroughput &&
 Percentile(claimSamples, 0.95) <= options.MaximumClaimP95Ms &&
(listSamples.Count == 0 || Percentile(listSamples, 0.95) <= options.MaximumListP95Ms)
};
 await WriteScaleProgressAsync(options, "completed", records.Count, processed, characters, jobCount, 100);
 await WriteAtomicJsonAsync(options.MetricsOutputPath, new
 {
 schema_version = "corpus-m2-scale-metrics-v1",
 generated_at = DateTimeOffset.UtcNow,
 result
 });
 return result;
}

 private static async Task<object> RunFullScaleAsync(Arguments options)
 {
 Require(options.DatabasePath, "--database");
 Require(options.FixturePath, "--fixture");
 Require(options.MetricsOutputPath, "--metrics-output");
 Require(options.ProgressOutputPath, "--progress-output");
 if (options.MinimumLatencySamples < 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumLatencySamples));

 await WriteFullScaleProgressAsync(options, "loading_fixture", 0, 0, 0, 0, 0, 0, 0, null);
 if (File.Exists(options.DatabasePath)) File.Delete(options.DatabasePath);
 var records = new List<ScaleRecord>();
 long characters = 0;
 await foreach (var line in File.ReadLinesAsync(options.FixturePath))
 {
 var record = JsonSerializer.Deserialize<ScaleRecord>(line, JsonOptions)
 ?? throw new InvalidOperationException("Full-scale fixture contains an empty record.");
 records.Add(record);
 characters += record.Text.Length;
 }
 if (characters < options.MinimumCharacters)
 throw new InvalidOperationException($"Full-scale fixture has {characters} characters; expected at least {options.MinimumCharacters}.");

 var store = new SqliteReferenceCorpusAnalysisJobStore(options.DatabasePath);
 await store.EnsureSchemaAsync();
 var seed = await SeedFullScaleFixtureAsync(options.DatabasePath, records);
 await WriteFullScaleProgressAsync(options, "seeding", characters, seed.Sources.Count, seed.LibraryCount,
 seed.SessionLibraryBindings, 0, 0, 0, null);

 var resolver = new FixedPathResolver(options.DatabasePath);
 var scheduler = new SqliteReferenceCorpusAnalysisScheduler(resolver, new FixedSettingsService());
 var jobs = await EnqueueFullScaleJobsAsync(scheduler, seed);
 var expectedWorkItems = jobs.Sum(job => job.TotalWorkItems);
 await WriteFullScaleProgressAsync(options, "queued", characters, seed.Sources.Count, seed.LibraryCount,
 seed.SessionLibraryBindings, jobs.Count, expectedWorkItems, 0, null);

 var listSamples = new List<double>(Math.Max(options.MinimumLatencySamples, jobs.Count));
 var listRequest = new ListReferenceCorpusAnalysisJobsPayload(new(null, 200, "updated_at", "desc", null));
 for (var sample = 0; sample < Math.Max(options.MinimumLatencySamples, jobs.Count); sample++)
 {
 var listedAt = Stopwatch.GetTimestamp();
 await scheduler.ListAsync(listRequest, CancellationToken.None);
 listSamples.Add(Stopwatch.GetElapsedTime(listedAt).TotalMilliseconds);
 }

 var databaseBytesBeforeWorker = new FileInfo(options.DatabasePath).Length;
 var managedBytesBeforeWorker = GC.GetTotalMemory(forceFullCollection: false);
 var claimSamples = new List<double>(jobs.Count);
 var analyzer = new FrozenFeatureAnalyzer();
 var timing = new ReferenceCorpusAnalysisWorkerOptions(
 TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1),
 latency => claimSamples.Add(latency.TotalMilliseconds));
 await using var worker = new ReferenceCorpusAnalysisWorker(
 resolver, analyzer, new UnexpectedTechniqueAnalyzer(), "full-scale-worker",
 TimeSpan.FromMilliseconds(1), timing);
 var run = Stopwatch.StartNew();
 var processedWorkItems = 0;
 foreach (var job in jobs)
 {
 if (!await worker.PumpOnceAsync(CancellationToken.None))
 throw new InvalidOperationException($"Full-scale worker did not claim '{job.JobId}'.");
 processedWorkItems += job.TotalWorkItems;
 await WriteFullScaleProgressAsync(options, "running", characters, seed.Sources.Count, seed.LibraryCount,
 seed.SessionLibraryBindings, jobs.Count, expectedWorkItems, processedWorkItems,
 expectedWorkItems == 0 ? 100 : processedWorkItems * 100d / expectedWorkItems);
 }
 run.Stop();

 var completedJobs = 0;
 foreach (var job in jobs)
 {
 var completed = await scheduler.GetAsync(new(job.JobId), CancellationToken.None)
 ?? throw new InvalidOperationException($"Full-scale job '{job.JobId}' disappeared.");
 if (completed.Status == ReferenceCorpusAnalysisJobStatuses.Completed) completedJobs++;
 }
 var progressSamples = new List<double>(Math.Max(options.MinimumLatencySamples, jobs.Count));
 for (var sample = 0; sample < Math.Max(options.MinimumLatencySamples, jobs.Count); sample++)
 {
 var progressAt = Stopwatch.GetTimestamp();
 var current = await scheduler.GetAsync(new(jobs[sample % jobs.Count].JobId), CancellationToken.None);
 if (current is null) throw new InvalidOperationException("Full-scale job progress read returned no job.");
 progressSamples.Add(Stopwatch.GetElapsedTime(progressAt).TotalMilliseconds);
 }
 var persisted = await ReadFullScalePersistentMetricsAsync(options.DatabasePath);
 var completionRows = await ScalarAsync(options.DatabasePath, "SELECT COUNT(*) FROM reference_analysis_work_item_completions;");
 var duplicateOutputs = await ScalarAsync(options.DatabasePath, "SELECT COUNT(*) - COUNT(DISTINCT completion_key) FROM reference_analysis_work_item_completions;");
 var observationRows = await ScalarAsync(options.DatabasePath, "SELECT COUNT(*) FROM reference_feature_observations;");
 var workItemRows = await ScalarAsync(options.DatabasePath, "SELECT COUNT(*) FROM reference_analysis_work_items;");
 var databaseBytesAfter = new FileInfo(options.DatabasePath).Length;
 var managedBytesAfter = GC.GetTotalMemory(forceFullCollection: false);
 var elapsedSeconds = Math.Max(run.Elapsed.TotalSeconds, double.Epsilon);
 var throughput = expectedWorkItems / elapsedSeconds;
 var passed = seed.Sources.Count >= 2 && seed.LibraryCount >= 2 && seed.SessionLibraryBindings >= 2 &&
 jobs.Count >= 2 && expectedWorkItems > 0 && completedJobs == jobs.Count &&
 persisted.ProcessedWorkItems == expectedWorkItems && persisted.SucceededWorkItems == expectedWorkItems &&
 completionRows == expectedWorkItems && duplicateOutputs == 0 && analyzer.CallCount == expectedWorkItems &&
 persisted.TokensReserved == 0 && persisted.BudgetPenetration == 0 &&
 persisted.ActiveLeases == 0 && claimSamples.Count >= options.MinimumLatencySamples &&
 listSamples.Count >= options.MinimumLatencySamples && progressSamples.Count >= options.MinimumLatencySamples &&
 throughput >= options.MinimumThroughput && Percentile(claimSamples, 0.95) <= options.MaximumClaimP95Ms &&
 Percentile(listSamples, 0.95) <= options.MaximumListP95Ms &&
 Percentile(progressSamples, 0.95) <= options.MaximumProgressP95Ms;
 var result = new
 {
 pipeline = "scheduler_snapshot_builder_worker_fake_analyzer",
 fixture = Path.GetFullPath(options.FixturePath),
 characters,
 anchors = seed.Sources.Count,
 libraries = seed.LibraryCount,
 session_id = seed.SessionId,
 session_library_bindings = seed.SessionLibraryBindings,
 jobs = jobs.Count,
 work_items = expectedWorkItems,
 processed_work_items = persisted.ProcessedWorkItems,
 succeeded_work_items = persisted.SucceededWorkItems,
 completed_jobs = completedJobs,
 completion_rows = completionRows,
 observation_rows = observationRows,
 duplicate_outputs = duplicateOutputs,
 fake_analyzer_calls = analyzer.CallCount,
 elapsed_ms = run.Elapsed.TotalMilliseconds,
 throughput_work_items_per_second = throughput,
 claim_ms = Distribution(claimSamples),
 task_list_ms = Distribution(listSamples),
 job_progress_ms = Distribution(progressSamples),
 tokens = new
 {
 spent = persisted.TokensSpent,
 reserved = persisted.TokensReserved,
 persisted_budget_penetration = persisted.BudgetPenetration
 },
 storage = new
 {
 database_bytes_before_worker = databaseBytesBeforeWorker,
 database_bytes_after = databaseBytesAfter,
 managed_bytes_before_worker = managedBytesBeforeWorker,
 managed_bytes_after = managedBytesAfter,
 analysis_work_item_rows = workItemRows,
 active_leases = persisted.ActiveLeases
 },
 passed
 };
 await WriteFullScaleProgressAsync(options, "completed", characters, seed.Sources.Count, seed.LibraryCount,
 seed.SessionLibraryBindings, jobs.Count, expectedWorkItems, processedWorkItems, 100);
 await WriteAtomicJsonAsync(options.MetricsOutputPath, new
 {
 schema_version = "corpus-m2-full-scale-metrics-v1",
 generated_at = DateTimeOffset.UtcNow,
 result
 });
 return result;
 }

 private static Task WriteScaleProgressAsync(
 Arguments options,
 string status,
 int totalWorkItems,
 int processedWorkItems,
 long characters,
 int jobs,
 double? percent) =>
 WriteAtomicJsonAsync(options.ProgressOutputPath, new
 {
 schema_version = "corpus-m2-scale-progress-v1",
 updated_at = DateTimeOffset.UtcNow,
 status,
 process_id = Environment.ProcessId,
 fixture = Path.GetFullPath(options.FixturePath),
 database = Path.GetFullPath(options.DatabasePath),
 characters,
 jobs,
 total_work_items = totalWorkItems,
 processed_work_items = processedWorkItems,
 percent
 });

 private static async Task WriteAtomicJsonAsync(string outputPath, object value)
 {
 var fullPath = Path.GetFullPath(outputPath);
 var directory = Path.GetDirectoryName(fullPath)
 ?? throw new InvalidOperationException($"Output path '{outputPath}' has no parent directory.");
 Directory.CreateDirectory(directory);
 var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
 try
 {
 await File.WriteAllTextAsync(
 temporaryPath,
 JsonSerializer.Serialize(value, JsonOptions),
 new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
 File.Move(temporaryPath, fullPath, overwrite: true);
 }
 finally
 {
 if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
 }
 }

 private static async ValueTask<ReferenceCorpusAnalysisWorkItemReservation> CreateReservationAsync(
 SqliteReferenceCorpusAnalysisJobStore store, string suffix, DateTimeOffset now, int reservedTokens)
 {
 const string payload = "{\"text\":\"fixture\"}";
 var snapshotId = $"snapshot-{suffix}";
 var jobId = $"job-{suffix}";
 await store.EnqueueAsync(
 new(snapshotId, 101, "stage_2", "sentence", "nodes-hash", "[\"syntax\"]",
 "corpus-analysis-v2", "feature-v2", "fake", "fake-model", 1, 1, now),
 [new(0, "node-1", null, "syntax", "hash-1", payload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(payload))],
 new(jobId, $"run-{suffix}", snapshotId, 7, 101,
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis, "{\"node_type\":\"sentence\"}",
 "input-hash", null, ReferenceCorpusAnalysisPriorityClasses.Normal, 100,
 1, 1, 2500, "stage_2", null, 3, now));
 var claim = await store.ClaimNextAsync(WorkerId, now, TimeSpan.FromSeconds(45))
 ?? throw new InvalidOperationException("Harness job was not claimable.");
 return await store.ReserveNextWorkItemAsync(jobId, WorkerId, claim.LeaseToken, reservedTokens, now.AddMilliseconds(1))
 ?? throw new InvalidOperationException("Harness work item was not reservable.");
 }

 private static async Task EnqueueScaleJobAsync(
 SqliteReferenceCorpusAnalysisJobStore store, string suffix, ScaleRecord[] records, DateTimeOffset now)
 {
 var items = records.Select((record, index) =>
 {
 var payload = JsonSerializer.Serialize(new { text = record.Text }, JsonOptions);
 var nodeId = $"scale-node-{record.SequenceIndex}";
 var nodeHash = TextHash(record.Text);
 return new ReferenceCorpusAnalysisWorkItemSnapshot(index, nodeId, null,
 index % 2 == 0 ? "syntax" : "narrative", nodeHash, payload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(payload));
 }).ToArray();
 var snapshotId = $"snapshot-{suffix}";
 await store.EnqueueAsync(
 new(snapshotId, 101, "stage_2", "mixed", $"nodes-{suffix}", "[\"syntax\",\"narrative\"]",
 "corpus-analysis-v2", "feature-v2", "fake", "fake-model", records.Length, records.Length, now),
 items,
 new($"job-{suffix}", $"run-{suffix}", snapshotId, 7, 101,
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis, "{\"node_type\":\"mixed\"}",
 $"input-{suffix}", null, ReferenceCorpusAnalysisPriorityClasses.Normal, 100,
 records.Length, records.Length, records.Length * 32, "stage_2", null, 3, now));
 }

 private static ReferenceCorpusAnalysisCompletionEnvelope CreateCompletion(
 ReferenceCorpusAnalysisWorkItemReservation reservation, int tokensSpent, DateTimeOffset completedAt)
 {
 var payload = JsonSerializer.Serialize(new { result = "accepted", reservation.NodeId }, JsonOptions);
 return new(
 ReferenceCorpusAnalysisCompletionCodec.CreateKey(
 reservation.InputSnapshotId, reservation.Ordinal, reservation.InvocationNumber),
 reservation.JobId, reservation.RunId, reservation.InputSnapshotId,
 reservation.Ordinal, reservation.InvocationNumber, reservation.AttemptNumber,
 reservation.ReservedTokens, ReferenceCorpusAnalysisCompletionKinds.FeatureObservations,
 payload, ReferenceCorpusAnalysisCompletionCodec.Hash(payload), tokensSpent, "[]", completedAt);
 }

 private static async Task SeedAnchorAndProbeAsync(string databasePath)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT OR IGNORE INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,
 source_file_hash,build_version,status,created_at,updated_at)
 VALUES(101,7,'fixture','fixture','fixture.txt','txt','user_owned',
 'source-hash','v2','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT OR IGNORE INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,
 start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES('node-1',101,NULL,'sentence',0,0,1,0,7,7,'hash-1','fixture','2026-07-10T00:00:00Z');
 CREATE TABLE IF NOT EXISTS harness_outputs(
 completion_key TEXT PRIMARY KEY, created_at TEXT NOT NULL);
 CREATE TABLE IF NOT EXISTS harness_scale_outputs(
 completion_key TEXT PRIMARY KEY, created_at TEXT NOT NULL);
 """;
await command.ExecuteNonQueryAsync();
}

 private static async Task SeedRuntimeCorpusAsync(string databasePath)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,
 source_file_hash,build_version,status,created_at,updated_at)
 VALUES(101,1,'runtime','runtime','runtime.txt','txt','allowed',
 'runtime-hash','v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,
 start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES
 ('runtime-chapter',101,NULL,'chapter',0,0,1,0,100,100,'runtime-chapter-hash','Runtime chapter','2026-07-10T00:00:00Z'),
 ('runtime-sentence',101,'runtime-chapter','sentence',1,1,1,0,17,17,'runtime-sentence-hash','Runtime sentence.','2026-07-10T00:00:00Z');
 """;
 await command.ExecuteNonQueryAsync();
 }

 private static async Task<FullScaleSeed> SeedFullScaleFixtureAsync(
 string databasePath,
 IReadOnlyList<ScaleRecord> records)
 {
 var sources = new List<FullScaleSource>();
 var nextAnchorId = 1_000L;
 foreach (var group in records.GroupBy(record => record.SourceId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
 {
 var libraries = group.Select(record => record.LibraryId).Distinct(StringComparer.Ordinal).ToArray();
 if (libraries.Length != 1)
 throw new InvalidOperationException($"Scale source '{group.Key}' belongs to multiple libraries.");
 sources.Add(new FullScaleSource(nextAnchorId++, group.Key, libraries[0],
 group.OrderBy(record => record.ChapterIndex).ThenBy(record => record.SequenceIndex).ToArray()));
 }
 if (sources.Count == 0) throw new InvalidOperationException("Full-scale fixture contains no sources.");

 const string sessionId = "scale-full-session";
 var librariesById = sources.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
 await using var connection = await OpenAsync(databasePath);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
 await ExecuteFullScaleAsync(connection, transaction, """
 CREATE TABLE IF NOT EXISTS reference_source_segments (
 segment_id TEXT PRIMARY KEY,
 anchor_id INTEGER NOT NULL,
 chapter_index INTEGER NOT NULL,
 chapter_title TEXT NOT NULL,
 segment_type TEXT NOT NULL,
 segment_index INTEGER NOT NULL,
 parent_segment_id TEXT NOT NULL,
 start_offset INTEGER NOT NULL,
 end_offset INTEGER NOT NULL,
 text TEXT NOT NULL,
 text_hash TEXT NOT NULL,
 node_id TEXT
 );
 """);

 foreach (var source in sources)
 {
 await ExecuteFullScaleAsync(connection, transaction, """
 INSERT INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,
 source_file_hash,build_version,status,created_at,updated_at)
 VALUES($anchor_id,7,$title,'harness',$source_path,'fixture','authorized',
 $source_hash,'scale-full-v1','ready',$created_at,$created_at);
 """,
 ("$anchor_id", source.AnchorId),
 ("$title", source.SourceId),
 ("$source_path", $"{source.SourceId}.fixture"),
 ("$source_hash", TextHash(source.SourceId)),
 ("$created_at", DateTimeOffset.UtcNow.ToString("O")));
 }
 foreach (var libraryId in librariesById)
 {
 await ExecuteFullScaleAsync(connection, transaction, """
 INSERT INTO reference_corpus_libraries(library_id,scope,novel_id,name,created_at)
 VALUES($library_id,'project',7,$name,$created_at);
 INSERT INTO reference_session_library_binding(session_id,library_id)
 VALUES($session_id,$library_id);
 """,
 ("$library_id", libraryId),
 ("$name", $"Scale {libraryId}"),
 ("$session_id", sessionId),
 ("$created_at", DateTimeOffset.UtcNow.ToString("O")));
 }
 foreach (var source in sources)
 {
 await ExecuteFullScaleAsync(connection, transaction, """
 INSERT INTO reference_library_members(library_id,anchor_id,enabled,source_quality,dedup_group_id)
 VALUES($library_id,$anchor_id,1,'trusted',$dedup_group_id);
 INSERT INTO reference_source_license(anchor_id,license_state,authorization_evidence,reuse_policy,
 max_verbatim_ratio,cleared_for_insertion,reviewed_at)
 VALUES($anchor_id,'authorized','scale-harness','adapted_only',1.0,1,$reviewed_at);
 """,
 ("$library_id", source.LibraryId),
 ("$anchor_id", source.AnchorId),
 ("$dedup_group_id", $"scale:{source.SourceId}"),
 ("$reviewed_at", DateTimeOffset.UtcNow.ToString("O")));

 foreach (var chapter in source.Records.GroupBy(record => record.ChapterIndex).OrderBy(group => group.Key))
 {
 var chapterRecords = chapter.OrderBy(record => record.SequenceIndex).ToArray();
 var chapterNodeId = $"scale-full:{source.AnchorId}:chapter:{chapter.Key}";
 var passageNodeId = $"scale-full:{source.AnchorId}:passage:{chapter.Key}";
 var passageText = string.Join("\n", chapterRecords.Select(record => record.Text));
 var chapterText = $"Scale chapter {chapter.Key}";
 await InsertFullScaleNodeAsync(connection, transaction, chapterNodeId, source.AnchorId, null,
 "chapter", chapter.Key, 0, chapter.Key, 0, Math.Max(1, passageText.Length), chapterText);

 var offset = 0;
 foreach (var record in chapterRecords)
 {
 var sentenceNodeId = $"scale-full:{source.AnchorId}:sentence:{record.SequenceIndex}";
 await InsertFullScaleNodeAsync(connection, transaction, sentenceNodeId, source.AnchorId, chapterNodeId,
 ReferenceCorpusNodeTypes.Sentence, record.SequenceIndex, 1, chapter.Key, offset,
 offset + record.Text.Length, record.Text);
 offset += record.Text.Length;
 }
 await InsertFullScaleNodeAsync(connection, transaction, passageNodeId, source.AnchorId, chapterNodeId,
 ReferenceCorpusNodeTypes.Passage, chapterRecords[0].SequenceIndex, 1, chapter.Key, 0,
 Math.Max(1, passageText.Length), passageText);
 await ExecuteFullScaleAsync(connection, transaction, """
 INSERT INTO reference_source_segments
 (segment_id,anchor_id,chapter_index,chapter_title,segment_type,segment_index,parent_segment_id,
 start_offset,end_offset,text,text_hash,node_id)
 VALUES($segment_id,$anchor_id,$chapter_index,$chapter_title,'paragraph',$segment_index,'root',
 0,$end_offset,$text,$text_hash,$node_id);
 """,
 ("$segment_id", $"scale-full:segment:{source.AnchorId}:{chapter.Key}"),
 ("$anchor_id", source.AnchorId),
 ("$chapter_index", chapter.Key),
 ("$chapter_title", $"Scale chapter {chapter.Key}"),
 ("$segment_index", chapter.Key),
 ("$end_offset", Math.Max(1, passageText.Length)),
 ("$text", passageText),
 ("$text_hash", TextHash(passageText)),
 ("$node_id", passageNodeId));
 }
 }
 await transaction.CommitAsync();
 return new FullScaleSeed(sources, librariesById.Length, librariesById.Length, sessionId);
 }

 private static Task InsertFullScaleNodeAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string nodeId,
 long anchorId,
 string? parentNodeId,
 string nodeType,
 int sequenceIndex,
 int depth,
 int chapterIndex,
 int startOffset,
 int endOffset,
 string text) =>
 ExecuteFullScaleAsync(connection, transaction, """
 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,
 start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES($node_id,$anchor_id,$parent_node_id,$node_type,$sequence_index,$depth,$chapter_index,
 $start_offset,$end_offset,$char_len,$text_hash,$text,$created_at);
 """,
 ("$node_id", nodeId),
 ("$anchor_id", anchorId),
 ("$parent_node_id", parentNodeId),
 ("$node_type", nodeType),
 ("$sequence_index", sequenceIndex),
 ("$depth", depth),
 ("$chapter_index", chapterIndex),
 ("$start_offset", startOffset),
 ("$end_offset", endOffset),
 ("$char_len", text.Length),
 ("$text_hash", TextHash(text)),
 ("$text", text),
 ("$created_at", DateTimeOffset.UtcNow.ToString("O")));

 private static async Task ExecuteFullScaleAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string sql,
 params (string Name, object? Value)[] parameters)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = sql;
 foreach (var parameter in parameters)
 command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
 await command.ExecuteNonQueryAsync();
 }

 private static async Task<IReadOnlyList<ReferenceCorpusAnalysisJobPayload>> EnqueueFullScaleJobsAsync(
 SqliteReferenceCorpusAnalysisScheduler scheduler,
 FullScaleSeed seed)
 {
 var jobs = new List<ReferenceCorpusAnalysisJobPayload>(seed.Sources.Count * 2);
 foreach (var source in seed.Sources)
 {
 foreach (var scope in new[] { ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusNodeTypes.Passage })
 {
 var nodeCount = scope == ReferenceCorpusNodeTypes.Sentence
 ? source.Records.Count
 : source.Records.Select(record => record.ChapterIndex).Distinct().Count();
 var familyCount = scope == ReferenceCorpusNodeTypes.Sentence
 ? ReferenceCorpusFeatureFamilies.SentenceFamilies.Count
 : ReferenceCorpusFeatureFamilies.PassageFamilies.Count;
 var tokenBudget = checked(nodeCount * familyCount * 16);
 jobs.Add(await scheduler.EnqueueAsync(new(
 $"scale-full-{source.AnchorId}-{scope}", 7, source.AnchorId,
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis, scope,
 ReferenceCorpusAnalysisPriorityClasses.Normal, 100, tokenBudget), CancellationToken.None));
 }
 }
 return jobs;
 }

 private static async Task<FullScalePersistentMetrics> ReadFullScalePersistentMetricsAsync(string databasePath)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT COALESCE(SUM(tokens_spent),0),COALESCE(SUM(tokens_reserved),0),
 COALESCE(SUM(CASE WHEN token_budget IS NOT NULL AND tokens_spent>token_budget
 THEN tokens_spent-token_budget ELSE 0 END),0),
 COALESCE(SUM(processed_work_items),0),COALESCE(SUM(succeeded_work_items),0),
 COALESCE(SUM(CASE WHEN lease_owner IS NOT NULL OR lease_token IS NOT NULL THEN 1 ELSE 0 END),0)
 FROM reference_analysis_jobs;
 """;
 await using var reader = await command.ExecuteReaderAsync();
 if (!await reader.ReadAsync()) throw new InvalidOperationException("Full-scale persistent metrics are unavailable.");
 return new FullScalePersistentMetrics(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
 reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
 }

 private static Task WriteFullScaleProgressAsync(
 Arguments options,
 string status,
 long characters,
 int anchors,
 int libraries,
 int sessionLibraryBindings,
 int jobs,
 int totalWorkItems,
 int processedWorkItems,
 double? percent) =>
 WriteAtomicJsonAsync(options.ProgressOutputPath, new
 {
 schema_version = "corpus-m2-full-scale-progress-v1",
 updated_at = DateTimeOffset.UtcNow,
 status,
 process_id = Environment.ProcessId,
 fixture = Path.GetFullPath(options.FixturePath),
 database = Path.GetFullPath(options.DatabasePath),
 characters,
 anchors,
 libraries,
 session_library_bindings = sessionLibraryBindings,
 jobs,
 total_work_items = totalWorkItems,
 processed_work_items = processedWorkItems,
 percent
 });

 private static async Task<ReferenceCorpusAnalysisJobPayload> WaitForRuntimeJobAsync(
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

 private static async Task<DateTimeOffset> ReadAttemptCompletedAtAsync(
 string databasePath,
 string jobId,
 int attemptNumber)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT completed_at
 FROM reference_analysis_job_attempts
 WHERE job_id=$job_id AND attempt_no=$attempt_no;
 """;
 command.Parameters.AddWithValue("$job_id", jobId);
 command.Parameters.AddWithValue("$attempt_no", attemptNumber);
 var value = await command.ExecuteScalarAsync();
 if (value is not string completedAt) throw new InvalidOperationException("Expired lease attempt has no completion time.");
 return DateTimeOffset.Parse(completedAt, CultureInfo.InvariantCulture);
 }

 private static async Task SeedScaleNodesAsync(string databasePath, IReadOnlyList<ScaleRecord> records)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var transaction = await connection.BeginTransactionAsync();
 await using var command = connection.CreateCommand();
 command.Transaction = (SqliteTransaction)transaction;
 command.CommandText = """
 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,
 start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES($node,101,NULL,$type,$sequence,0,$chapter,$start,$end,$length,$hash,$text,$created);
 """;
 var node = command.Parameters.Add("$node", SqliteType.Text);
 var type = command.Parameters.Add("$type", SqliteType.Text);
 var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
 var chapter = command.Parameters.Add("$chapter", SqliteType.Integer);
 var start = command.Parameters.Add("$start", SqliteType.Integer);
 var end = command.Parameters.Add("$end", SqliteType.Integer);
 var length = command.Parameters.Add("$length", SqliteType.Integer);
 var hash = command.Parameters.Add("$hash", SqliteType.Text);
 var text = command.Parameters.Add("$text", SqliteType.Text);
 var created = command.Parameters.Add("$created", SqliteType.Text);
 var offset = 0;
 foreach (var record in records)
 {
 node.Value = $"scale-node-{record.SequenceIndex}";
 type.Value = record.SequenceIndex % 2 == 0 ? "sentence" : "passage";
 sequence.Value = record.SequenceIndex;
 chapter.Value = record.ChapterIndex;
 start.Value = offset;
 offset += record.Text.Length;
 end.Value = offset;
 length.Value = record.Text.Length;
 hash.Value = TextHash(record.Text);
 text.Value = record.Text;
 created.Value = DateTimeOffset.UtcNow.ToString("O");
 await command.ExecuteNonQueryAsync();
 }
 await transaction.CommitAsync();
 }

 private static async ValueTask InsertProbeAsync(
 SqliteConnection connection, SqliteTransaction transaction, string completionKey, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO harness_outputs(completion_key,created_at) VALUES($key,$time);";
 command.Parameters.AddWithValue("$key", completionKey);
 command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async ValueTask InsertScaleOutputAsync(
 SqliteConnection connection, SqliteTransaction transaction, string completionKey, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO harness_scale_outputs(completion_key,created_at) VALUES($key,$time);";
 command.Parameters.AddWithValue("$key", completionKey);
 command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async Task WriteScenarioAsync(
 Arguments options, ReferenceCorpusAnalysisWorkItemReservation reservation, DateTimeOffset createdAt)
 {
 Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ScenarioPath))!);
 await File.WriteAllTextAsync(options.ScenarioPath,
 JsonSerializer.Serialize(new ScenarioState(options.ScenarioId, options.Point, createdAt, reservation), JsonOptions));
 }

 private static async Task CheckpointAndBlockAsync(Arguments options)
 {
 Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.CheckpointPath))!);
 await File.WriteAllTextAsync(options.CheckpointPath, $"{options.Point}|{Environment.ProcessId}|{DateTimeOffset.UtcNow:O}");
 await Task.Delay(Timeout.InfiniteTimeSpan);
 }

 private static async Task<Audit> ReadAuditAsync(string databasePath, string jobId, string completionKey)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT
 job.tokens_spent,job.tokens_reserved,
 SUM(CASE WHEN work.work_state='succeeded' THEN 1 ELSE 0 END),
 SUM(CASE WHEN work.work_state='pending' THEN 1 ELSE 0 END),
 (SELECT COUNT(*) FROM reference_analysis_work_item_completions WHERE completion_key=$key),
 (SELECT COUNT(*) FROM reference_analysis_work_item_completions WHERE completion_key=$key AND finalized_at IS NOT NULL),
 (SELECT COUNT(*) FROM harness_outputs WHERE completion_key=$key),
 (SELECT COUNT(*) - COUNT(DISTINCT completion_key) FROM harness_outputs)
 FROM reference_analysis_jobs AS job
 JOIN reference_analysis_work_items AS work ON work.input_snapshot_id=job.input_snapshot_id
 WHERE job.job_id=$job GROUP BY job.job_id;
 """;
 command.Parameters.AddWithValue("$job", jobId);
 command.Parameters.AddWithValue("$key", completionKey);
 await using var reader = await command.ExecuteReaderAsync();
 if (!await reader.ReadAsync()) throw new InvalidOperationException("Harness audit row was not found.");
 var tokensSpent = reader.GetInt32(0);
 return new(tokensSpent, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
 reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
 tokensSpent > ActualTokens ? ActualTokens + ReservedTokens : ActualTokens);
 }

private static object Distribution(IReadOnlyList<double> values) => new
 {
 count = values.Count,
 p50 = Percentile(values, 0.50),
 p95 = Percentile(values, 0.95),
 max = values.Count == 0 ? 0 : values.Max()
};

 private static string TextHash(string text) =>
 Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

 private static double Percentile(IReadOnlyList<double> values, double percentile)
 {
 if (values.Count == 0) return 0;
 var sorted = values.Order().ToArray();
 var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
 return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
 }

 private static async Task<long> ScalarAsync(string databasePath, string sql)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = sql;
 return Convert.ToInt64(await command.ExecuteScalarAsync());
 }

 private static async Task<SqliteConnection> OpenAsync(string databasePath)
 {
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
 await connection.OpenAsync();
 await using var pragma = connection.CreateCommand();
 pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
 await pragma.ExecuteNonQueryAsync();
 return connection;
 }

 private static void Require(string value, string name)
 {
 if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
 }

 private sealed record ScenarioState(string ScenarioId, string Point, DateTimeOffset CreatedAt,
 ReferenceCorpusAnalysisWorkItemReservation Reservation);
 private sealed record ScaleRecord(
 [property: JsonPropertyName("source_id")] string SourceId,
 [property: JsonPropertyName("library_id")] string LibraryId,
 [property: JsonPropertyName("chapter_index")] int ChapterIndex,
 [property: JsonPropertyName("sequence_index")] int SequenceIndex,
 [property: JsonPropertyName("text")] string Text,
 [property: JsonPropertyName("license_state")] string LicenseState);
 private sealed record FullScaleSource(
 long AnchorId,
 string SourceId,
 string LibraryId,
 IReadOnlyList<ScaleRecord> Records);
 private sealed record FullScaleSeed(
 IReadOnlyList<FullScaleSource> Sources,
 int LibraryCount,
 int SessionLibraryBindings,
 string SessionId);
 private sealed record FullScalePersistentMetrics(
 long TokensSpent,
 long TokensReserved,
 long BudgetPenetration,
 long ProcessedWorkItems,
 long SucceededWorkItems,
 long ActiveLeases);
 private sealed record RuntimeControlSample(
 string Control,
 int Sample,
 double RequestToSettledMs,
 string Status,
 int ProcessedWorkItems,
 int SucceededWorkItems,
 bool Passed);
 private sealed record RuntimeStaleLeaseSample(
 int Sample,
 double ExpiryToReclaimMs,
 double ExpiryToCompletedMs,
 int AttemptCount,
 long CompletionRows,
 long ObservationRows,
 long AbandonedAttempts,
 bool Passed);
 private sealed record Audit(int TokensSpent, int TokensReserved, int SucceededWorkItems, int PendingWorkItems,
 int CompletionRows, int FinalizedCompletionRows, int OutputRows, int DuplicateOutputRows, int ExpectedTokensSpent);

 private sealed class GateFeatureAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
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
 var evidenceEnd = Math.Min(7, input.NodeText.Length);
 var observations = input.Family == ReferenceCorpusFeatureFamilies.Syntax
 ? $"[{{\"feature_key\":\"sentence_pattern\",\"label\":\"subject_predicate\",\"complexity\":\"simple\",\"confidence\":0.8,\"evidence_start\":0,\"evidence_end\":{evidenceEnd},\"explanation\":\"runtime harness\"}}]"
 : "[]";
 var output = new ReferenceCorpusFeatureFamilyAnalysisOutput(
 $"{{\"schema_version\":\"reference-corpus-feature-family-v1\",\"family\":\"{input.Family}\",\"node_type\":\"{input.NodeType}\",\"observations\":{observations}}}",
 10);
 Returned.TrySetResult();
 return output;
 }
 }

 private sealed class FrozenFeatureAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
 {
 public int CallCount { get; private set; }

 public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusFeatureFamilyAnalysisInput input,
 CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 CallCount++;
 var evidenceEnd = Math.Min(7, input.NodeText.Length);
 var observations = input.Family == ReferenceCorpusFeatureFamilies.Syntax
 ? $"[{{\"feature_key\":\"sentence_pattern\",\"label\":\"subject_predicate\",\"complexity\":\"simple\",\"confidence\":0.8,\"evidence_start\":0,\"evidence_end\":{evidenceEnd},\"explanation\":\"runtime harness\"}}]"
 : "[]";
 return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
 $"{{\"schema_version\":\"reference-corpus-feature-family-v1\",\"family\":\"{input.Family}\",\"node_type\":\"{input.NodeType}\",\"observations\":{observations}}}",
 10));
 }
 }

 private sealed class UnexpectedTechniqueAnalyzer : IReferenceCorpusTechniqueSpecimenAnalyzer
 {
 public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusTechniqueSpecimenAnalysisInput input,
 CancellationToken cancellationToken) =>
 throw new InvalidOperationException("Technique analysis is not part of the runtime harness.");
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

 private sealed class Arguments
 {
 public string Command { get; init; } = string.Empty;
 public string DatabasePath { get; init; } = string.Empty;
 public string CheckpointPath { get; init; } = string.Empty;
 public string ScenarioPath { get; init; } = string.Empty;
public string ModelResultPath { get; init; } = string.Empty;
public string FixturePath { get; init; } = string.Empty;
 public string RootPath { get; init; } = string.Empty;
 public string MetricsOutputPath { get; init; } = string.Empty;
 public string ProgressOutputPath { get; init; } = string.Empty;
 public string Point { get; init; } = string.Empty;
 public string ScenarioId { get; init; } = "default";
 public int MinimumCharacters { get; init; } = 50_000;
 public int Samples { get; init; } = 30;
 public int MinimumLatencySamples { get; init; } = 30;
 public int JobSize { get; init; } = 100;
 public double MinimumThroughput { get; init; } = 20;
 public double MaximumClaimP95Ms { get; init; } = 100;
 public double MaximumListP95Ms { get; init; } = 200;
 public double MaximumProgressP95Ms { get; init; } = 200;

 public static Arguments Parse(string[] args)
 {
 if (args.Length == 0) throw new ArgumentException("A command is required.");
 var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
 for (var index = 1; index < args.Length; index += 2)
 {
 if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
 throw new ArgumentException($"Invalid argument at position {index}.");
 values[args[index][2..]] = args[index + 1];
 }
 string Get(string key, string fallback = "") => values.GetValueOrDefault(key, fallback);
 int GetInt(string key, int fallback) => int.TryParse(Get(key), CultureInfo.InvariantCulture, out var value) ? value : fallback;
 double GetDouble(string key, double fallback) => double.TryParse(Get(key), CultureInfo.InvariantCulture, out var value) ? value : fallback;
 var scenario = Get("scenario");
 return new()
 {
 Command = args[0], DatabasePath = Get("database"), CheckpointPath = Get("checkpoint"),
 ScenarioPath = scenario, ModelResultPath = Get("model-result"), FixturePath = Get("fixture"),
 RootPath = Get("root"),
 MetricsOutputPath = Get("metrics-output"), ProgressOutputPath = Get("progress-output"),
 Point = Get("point"), ScenarioId = Get("scenario-id", "default"),
 MinimumCharacters = GetInt("minimum-characters", 50_000), JobSize = GetInt("job-size", 100),
 Samples = GetInt("samples", 30),
 MinimumLatencySamples = GetInt("minimum-latency-samples", 30),
 MinimumThroughput = GetDouble("minimum-throughput", 20),
 MaximumClaimP95Ms = GetDouble("maximum-claim-p95-ms", 100),
 MaximumListP95Ms = GetDouble("maximum-list-p95-ms", 200),
 MaximumProgressP95Ms = GetDouble("maximum-progress-p95-ms", 200)
 };
 }
 }
}
