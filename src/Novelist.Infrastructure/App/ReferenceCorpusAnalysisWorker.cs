using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

internal sealed record ReferenceCorpusAnalysisWorkerOptions(
 TimeSpan LeaseDuration,
 TimeSpan HeartbeatInterval,
 TimeSpan ReclaimRetryDelay,
 Action<TimeSpan>? ClaimLatencyObserver = null);

public sealed class ReferenceCorpusAnalysisWorker : IAsyncDisposable
{
 private static readonly ReferenceCorpusAnalysisWorkerOptions DefaultOptions = new(
 TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
 private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
 private readonly ReferenceCorpusFeatureWorkItemProcessor _featureProcessor;
 private readonly ReferenceCorpusTechniqueWorkItemProcessor _techniqueProcessor;
 private readonly ReferenceCorpusFeatureObservationPersistence _featurePersistence = new();
 private readonly ReferenceCorpusTechniqueSpecimenPersistence _techniquePersistence = new();
 private readonly ReferenceCorpusAnalysisRetryPolicy _retryPolicy = new();
private readonly string _workerId;
private readonly TimeSpan _idleDelay;
 private readonly TimeSpan _leaseDuration;
 private readonly TimeSpan _heartbeatInterval;
 private readonly TimeSpan _reclaimRetryDelay;
 private readonly Action<TimeSpan>? _claimLatencyObserver;
private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
 private readonly SemaphoreSlim _manualPumpGate = new(1, 1);
private CancellationTokenSource? _loopCancellation;
private Task? _loopTask;
private string? _boundDatabasePath;
 private bool _disposed;

 public ReferenceCorpusAnalysisWorker(
 IReferenceCorpusDatabasePathResolver databasePathResolver,
 IReferenceCorpusFeatureFamilyAnalyzer featureAnalyzer,
 IReferenceCorpusTechniqueSpecimenAnalyzer techniqueAnalyzer,
 string? workerId = null,
 TimeSpan? idleDelay = null)
 : this(databasePathResolver, featureAnalyzer, techniqueAnalyzer, workerId, idleDelay, DefaultOptions)
 {
 }

 internal ReferenceCorpusAnalysisWorker(
 IReferenceCorpusDatabasePathResolver databasePathResolver,
 IReferenceCorpusFeatureFamilyAnalyzer featureAnalyzer,
 IReferenceCorpusTechniqueSpecimenAnalyzer techniqueAnalyzer,
 string? workerId,
 TimeSpan? idleDelay,
 ReferenceCorpusAnalysisWorkerOptions options)
 {
 _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
 _featureProcessor = new(featureAnalyzer ?? throw new ArgumentNullException(nameof(featureAnalyzer)));
_techniqueProcessor = new(techniqueAnalyzer ?? throw new ArgumentNullException(nameof(techniqueAnalyzer)));
 _workerId = string.IsNullOrWhiteSpace(workerId) ? $"analysis-worker:{Environment.ProcessId}:{Guid.NewGuid():N}" : workerId;
 _idleDelay = idleDelay ?? TimeSpan.FromSeconds(1);
 if (_idleDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleDelay));
 var timing = options ?? throw new ArgumentNullException(nameof(options));
 if (timing.LeaseDuration <= TimeSpan.Zero || timing.LeaseDuration > TimeSpan.FromMinutes(10))
 throw new ArgumentOutOfRangeException(nameof(options), "Lease duration must be between zero and ten minutes.");
 if (timing.HeartbeatInterval <= TimeSpan.Zero || timing.HeartbeatInterval > TimeSpan.FromMinutes(10))
 throw new ArgumentOutOfRangeException(nameof(options), "Heartbeat interval must be between zero and ten minutes.");
 if (timing.ReclaimRetryDelay < TimeSpan.Zero || timing.ReclaimRetryDelay > TimeSpan.FromMinutes(10))
 throw new ArgumentOutOfRangeException(nameof(options), "Reclaim retry delay must be between zero and ten minutes.");
 _leaseDuration = timing.LeaseDuration;
 _heartbeatInterval = timing.HeartbeatInterval;
 _reclaimRetryDelay = timing.ReclaimRetryDelay;
 _claimLatencyObserver = timing.ClaimLatencyObserver;
}

 public async ValueTask StartAsync(CancellationToken cancellationToken = default)
 {
await _lifecycleGate.WaitAsync(cancellationToken);
try
{
 ObjectDisposedException.ThrowIf(_disposed, this);
if (_loopTask is { IsCompleted: false }) return;
 var databasePath = Path.GetFullPath(await _databasePathResolver.ResolveAsync(cancellationToken));
 var store = new SqliteReferenceCorpusAnalysisJobStore(databasePath);
 await ReconcileAsync(store, cancellationToken);
_loopCancellation?.Dispose();
_loopCancellation = new CancellationTokenSource();
 _boundDatabasePath = databasePath;
 _loopTask = RunLoopAsync(store, _loopCancellation.Token);
 }
 finally
 {
 _lifecycleGate.Release();
 }
 }

 public async ValueTask StopAsync(CancellationToken cancellationToken = default)
 {
 Task? loopTask;
 await _lifecycleGate.WaitAsync(cancellationToken);
 try
 {
 if (_loopTask is null) return;
 _loopCancellation!.Cancel();
 loopTask = _loopTask;
 }
 finally
 {
 _lifecycleGate.Release();
 }
 await loopTask.WaitAsync(cancellationToken);
 await _lifecycleGate.WaitAsync(cancellationToken);
 try
 {
 if (ReferenceEquals(_loopTask, loopTask))
 {
_loopTask = null;
_loopCancellation?.Dispose();
_loopCancellation = null;
 _boundDatabasePath = null;
 }
 }
 finally
 {
 _lifecycleGate.Release();
 }
 }

 private async Task RunLoopAsync(
 SqliteReferenceCorpusAnalysisJobStore store,
 CancellationToken cancellationToken)
 {
 while (!cancellationToken.IsCancellationRequested)
 {
 try
 {
 if (!await PumpOnceCoreAsync(store, cancellationToken))
 await Task.Delay(_idleDelay, cancellationToken);
 }
 catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
 {
 break;
 }
 catch
 {
 try
 {
 await Task.Delay(_idleDelay, cancellationToken);
 }
 catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
 {
 break;
 }
 }
 }
 }

public async ValueTask DisposeAsync()
{
await StopAsync();
 await _lifecycleGate.WaitAsync();
 try
 {
 if (_disposed) return;
 _disposed = true;
 }
 finally
 {
 _lifecycleGate.Release();
 }

await _manualPumpGate.WaitAsync();
_manualPumpGate.Release();
}

public async ValueTask<bool> PumpOnceAsync(CancellationToken cancellationToken)
{
await _manualPumpGate.WaitAsync(cancellationToken);
try
{
 ObjectDisposedException.ThrowIf(_disposed, this);
var store = new SqliteReferenceCorpusAnalysisJobStore(await _databasePathResolver.ResolveAsync(cancellationToken));
 await ReconcileAsync(store, cancellationToken);
 return await PumpOnceCoreAsync(store, cancellationToken);
 }
 finally
 {
 _manualPumpGate.Release();
 }
}

 private async ValueTask ReconcileAsync(
SqliteReferenceCorpusAnalysisJobStore store,
CancellationToken cancellationToken)
{
await store.EnsureSchemaAsync(cancellationToken);
 while (await store.ReadNextUnfinalizedCompletionAsync(cancellationToken) is { } completion)
 await FinalizeRecordedCompletionAsync(store, completion, cancellationToken);
var now = DateTimeOffset.UtcNow;
await store.RequeueDueRetriesAsync(now, cancellationToken);
await store.ReclaimExpiredLeasesAsync(now, now.Add(_reclaimRetryDelay), cancellationToken);
 }

private async ValueTask<bool> PumpOnceCoreAsync(
SqliteReferenceCorpusAnalysisJobStore store,
CancellationToken cancellationToken)
{
 var recoverableCompletion = await store.ReadNextUnfinalizedCompletionAsync(cancellationToken);
 if (recoverableCompletion is not null)
 {
 await FinalizeRecordedCompletionAsync(store, recoverableCompletion, cancellationToken);
 return true;
 }

var now = DateTimeOffset.UtcNow;
 await store.RequeueDueRetriesAsync(now, cancellationToken);
await store.ReclaimExpiredLeasesAsync(now, now.Add(_reclaimRetryDelay), cancellationToken);
ReferenceCorpusAnalysisJobClaim? claim;
if (_claimLatencyObserver is null)
{
 claim = await store.ClaimNextAsync(_workerId, now, _leaseDuration, cancellationToken);
}
else
{
 var claimStarted = Stopwatch.GetTimestamp();
 claim = await store.ClaimNextAsync(_workerId, now, _leaseDuration, cancellationToken);
 try
 {
 _claimLatencyObserver(Stopwatch.GetElapsedTime(claimStarted));
 }
 catch
 {
 // Harness telemetry must not affect durable worker behavior.
 }
}
 if (claim is null) return false;

 while (true)
 {
 cancellationToken.ThrowIfCancellationRequested();
var current = await store.GetAsync(claim.Job.JobId, cancellationToken)
?? throw new InvalidOperationException("Claimed analysis job disappeared.");

 var pendingCompletion = await store.ReadNextUnfinalizedCompletionAsync(current.JobId, cancellationToken);
 if (pendingCompletion is not null)
 {
 var finalized = await FinalizeRecordedCompletionAsync(store, pendingCompletion, cancellationToken);
 if (finalized.Status != ReferenceCorpusAnalysisJobStatuses.Running) return true;
 continue;
 }

if (current.Status is ReferenceCorpusAnalysisJobStatuses.PauseRequested or ReferenceCorpusAnalysisJobStatuses.CancelRequested)
 {
 await store.AcknowledgeControlBoundaryAsync(current.JobId, _workerId, claim.LeaseToken, DateTimeOffset.UtcNow, cancellationToken);
 return true;
 }
 if (current.Status != ReferenceCorpusAnalysisJobStatuses.Running) return true;

 ReferenceCorpusFrozenTokenPolicy tokenPolicy;
 try
 {
 tokenPolicy = await ReadNextTokenPolicyAsync(store, current, cancellationToken);
 }
 catch (Exception exception) when (exception is JsonException or ReferenceCorpusAnalysisJobConflictException)
 {
 await store.FailClaimAsync(current.JobId, _workerId, claim.LeaseToken,
 "analysis_snapshot_corrupt", Truncate(exception.Message), DateTimeOffset.UtcNow, cancellationToken);
 return true;
 }
 var remaining = current.TokenBudget is { } budget ? budget - current.TokensSpent : tokenPolicy.TokenReservation;
 if (remaining <= 0)
 {
 throw new InvalidOperationException("A running analysis job has no remaining token budget.");
 }
 ReferenceCorpusAnalysisWorkItemReservation? reservation;
 try
 {
 // Keep a fast sequence of work items fenced even when the periodic heartbeat is intentionally slow.
 await store.HeartbeatAsync(current.JobId, _workerId, claim.LeaseToken,
 DateTimeOffset.UtcNow, _leaseDuration, cancellationToken);
 reservation = await store.ReserveNextWorkItemAsync(
current.JobId,
 _workerId,
 claim.LeaseToken,
 Math.Min(tokenPolicy.TokenReservation, remaining),
 DateTimeOffset.UtcNow,
 cancellationToken);
 }
 catch (ReferenceCorpusAnalysisJobConflictException exception) when (
 exception.Message.StartsWith("analysis_snapshot_", StringComparison.Ordinal) ||
 exception.Message.StartsWith("legacy_snapshot_", StringComparison.Ordinal))
 {
 var separator = exception.Message.IndexOf(':');
 var code = separator > 0 ? exception.Message[..separator] : "analysis_snapshot_corrupt";
 await store.FailClaimAsync(current.JobId, _workerId, claim.LeaseToken,
 code, Truncate(exception.Message), DateTimeOffset.UtcNow, cancellationToken);
 return true;
 }
 catch (ReferenceCorpusAnalysisJobConflictException)
 {
 // A concurrent worker reclaimed this lease; the fencing boundary prevents a stale commit.
 return true;
 }
 if (reservation is null) return true;

using var heartbeatCancellation = new CancellationTokenSource();
var heartbeat = RunHeartbeatAsync(store, reservation.JobId, reservation.LeaseToken, heartbeatCancellation.Token);
 ReferenceCorpusAnalysisCompletionEnvelope? recordedCompletion = null;
 try
 {
 try
 {
 var execution = await ExecuteAsync(reservation, cancellationToken);
 if (execution.Status == WorkItemExecutionStatuses.BudgetExhausted)
 {
 await store.SettleWorkItemAsync(new(
 reservation, ReferenceCorpusAnalysisWorkItemSettlementKind.BudgetExhausted,
 execution.TokensSpent, null, null, null, DateTimeOffset.UtcNow), cancellationToken);
 return true;
 }
 if (execution.Status == WorkItemExecutionStatuses.ValidationFailed)
 {
 await store.SettleWorkItemAsync(new(
 reservation, ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure,
 execution.TokensSpent, "analysis_output_invalid", execution.Diagnostics, null, DateTimeOffset.UtcNow), cancellationToken);
 return true;
 }

 var completion = CreateCompletion(reservation, execution, DateTimeOffset.UtcNow);
 using var durableCommit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
 await store.RecordCompletionAsync(
 reservation, completion, DateTimeOffset.UtcNow, durableCommit.Token);
 recordedCompletion = completion;
 }
catch (Exception)
{
using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var persisted = await store.ReadNextUnfinalizedCompletionAsync(
reservation.JobId, probe.Token);
if (persisted != completion) throw;
recordedCompletion = persisted;
}

 using var durableFinalize = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var committed = await FinalizeRecordedCompletionAsync(
 store, recordedCompletion, durableFinalize.Token);
if (committed.Status != ReferenceCorpusAnalysisJobStatuses.Running) return true;
 }
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
 if (recordedCompletion is not null) throw;
using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
 try
 {
 await store.AbandonReservationAsync(
 reservation,
 reservation.ReservedTokens,
 "worker_shutdown",
 "Worker shutdown interrupted the reserved model invocation.",
 DateTimeOffset.UtcNow,
 DateTimeOffset.UtcNow.AddSeconds(1),
 cleanup.Token);
 }
 catch (ReferenceCorpusAnalysisJobConflictException)
 {
 }
 throw;
 }
 catch (HttpRequestException exception)
 {
 var failedAt = DateTimeOffset.UtcNow;
 var decision = _retryPolicy.Decide(new(
ReferenceCorpusAnalysisRetryCategories.ProviderTransient,
reservation.AttemptNumber,
failedAt,
 RetryAfter: null));
 var shouldRetry = decision.ShouldRetry && reservation.AttemptNumber < current.MaxAttempts;
 await store.SettleWorkItemAsync(new(
 reservation,
 shouldRetry ? ReferenceCorpusAnalysisWorkItemSettlementKind.RetryableFailure : ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure,
 reservation.ReservedTokens,
 shouldRetry ? "provider_transient" : "provider_retry_exhausted",
 Truncate(exception.Message),
 shouldRetry ? decision.NextAttemptAt : null,
 failedAt), cancellationToken);
 return true;
 }
 catch (BridgeRequestException exception) when (exception.Retryable)
 {
 var failedAt = DateTimeOffset.UtcNow;
 var decision = _retryPolicy.Decide(new(
 ReferenceCorpusAnalysisRetryCategories.ProviderTransient,
 reservation.AttemptNumber,
 failedAt,
 RetryAfter: ReadRetryAfter(exception.Details)));
 var shouldRetry = decision.ShouldRetry && reservation.AttemptNumber < current.MaxAttempts;
 await store.SettleWorkItemAsync(new(
 reservation,
 shouldRetry ? ReferenceCorpusAnalysisWorkItemSettlementKind.RetryableFailure : ReferenceCorpusAnalysisWorkItemSettlementKind.PermanentFailure,
 reservation.ReservedTokens,
 shouldRetry ? exception.Code : "provider_retry_exhausted",
 Truncate(exception.Message),
 shouldRetry ? decision.NextAttemptAt : null,
 failedAt), cancellationToken);
 return true;
}
}
finally
 {
 heartbeatCancellation.Cancel();
 try { await heartbeat; } catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested) { }
 }
}
}

 private static TimeSpan? ReadRetryAfter(object? details)
 {
 if (details is null) return null;
 try
 {
 var element = details is JsonElement json
 ? json
 : JsonSerializer.SerializeToElement(details);
 if (element.ValueKind != JsonValueKind.Object) return null;
 if (element.TryGetProperty("retry_after_ms", out var milliseconds) &&
 milliseconds.TryGetDouble(out var millisecondsValue) && millisecondsValue >= 0)
 return TimeSpan.FromMilliseconds(millisecondsValue);
 if (element.TryGetProperty("retry_after_seconds", out var seconds) &&
 seconds.TryGetDouble(out var secondsValue) && secondsValue >= 0)
 return TimeSpan.FromSeconds(secondsValue);
 }
 catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
 {
 }
 return null;
 }

private async ValueTask<ReferenceCorpusAnalysisJob> FinalizeRecordedCompletionAsync(
 SqliteReferenceCorpusAnalysisJobStore store,
 ReferenceCorpusAnalysisCompletionEnvelope completion,
 CancellationToken cancellationToken)
 {
 return await store.FinalizeCompletionAsync(
 completion,
 DateTimeOffset.UtcNow,
 async (connection, transaction, persisted, token) =>
 {
 switch (persisted.OutputKind)
 {
 case ReferenceCorpusAnalysisCompletionKinds.FeatureObservations:
 var feature = ReferenceCorpusAnalysisCompletionCodec.Deserialize<ReferenceCorpusFeatureCompletionPayload>(
 persisted.OutputPayloadJson);
 await _featurePersistence.PersistAsync(connection, transaction, new(
 feature.RunId, feature.AnchorId, feature.NodeId, feature.NodeType,
 feature.CreatedAt, feature.Observations), token);
 break;
 case ReferenceCorpusAnalysisCompletionKinds.TechniqueSpecimen:
 var technique = ReferenceCorpusAnalysisCompletionCodec.Deserialize<ReferenceCorpusTechniqueCompletionPayload>(
 persisted.OutputPayloadJson);
 await _techniquePersistence.PersistAsync(connection, transaction, new(
 technique.RunId, technique.AnchorId, technique.CreatedAt, technique.Candidate), token);
 break;
 default:
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"analysis_completion_corrupt: unsupported output kind '{persisted.OutputKind}'.");
 }
 },
 cancellationToken);
 }

 private static ReferenceCorpusAnalysisCompletionEnvelope CreateCompletion(
 ReferenceCorpusAnalysisWorkItemReservation reservation,
 WorkItemExecution execution,
 DateTimeOffset modelCompletedAt)
 {
 if (execution.OutputKind is null || execution.OutputPayloadJson is null)
 throw new InvalidOperationException("A successful work item has no durable completion payload.");
 return new(
 ReferenceCorpusAnalysisCompletionCodec.CreateKey(
 reservation.InputSnapshotId, reservation.Ordinal, reservation.InvocationNumber),
 reservation.JobId,
 reservation.RunId,
 reservation.InputSnapshotId,
 reservation.Ordinal,
 reservation.InvocationNumber,
 reservation.AttemptNumber,
 reservation.ReservedTokens,
 execution.OutputKind,
 execution.OutputPayloadJson,
 ReferenceCorpusAnalysisCompletionCodec.Hash(execution.OutputPayloadJson),
 execution.TokensSpent,
 JsonSerializer.Serialize(new[] { execution.Diagnostics }),
 modelCompletedAt);
 }

private async ValueTask<WorkItemExecution> ExecuteAsync(
 ReferenceCorpusAnalysisWorkItemReservation reservation,
 CancellationToken cancellationToken)
 {
 if (string.Equals(reservation.FeatureFamily, "technique_specimen", StringComparison.Ordinal))
 {
 var payload = ReferenceCorpusAnalysisFrozenInputCodec.Deserialize<ReferenceCorpusFrozenTechniqueWorkItem>(reservation.InputPayloadJson, reservation.InputPayloadHash);
 ValidateTechniquePayload(reservation, payload);
 var result = await _techniqueProcessor.ProcessAsync(new(
 payload.RunId, payload.AnchorId, payload.NodeId, payload.NodeType, payload.NodeText,
 payload.Observations, payload.Model, payload.TokenPolicy.MaxValidationAttempts,
 new(reservation.ReservedTokens, Math.Min(payload.TokenPolicy.MaximumOutputTokensPerCall, reservation.ReservedTokens), Math.Min(payload.TokenPolicy.UnknownUsageCharge, reservation.ReservedTokens))), cancellationToken);
 return result.Status switch
 {
ReferenceCorpusTechniqueWorkItemStatuses.Succeeded => new(
WorkItemExecutionStatuses.Succeeded, result.TokensSpent, string.Join(" | ", result.Diagnostics),
 ReferenceCorpusAnalysisCompletionKinds.TechniqueSpecimen,
 ReferenceCorpusAnalysisCompletionCodec.Serialize(new ReferenceCorpusTechniqueCompletionPayload(
 payload.RunId, payload.AnchorId, DateTimeOffset.UtcNow, result.Candidate!))),
 ReferenceCorpusTechniqueWorkItemStatuses.BudgetExhausted => new(WorkItemExecutionStatuses.BudgetExhausted, result.TokensSpent, string.Join(" | ", result.Diagnostics), null, null),
 _ => new(WorkItemExecutionStatuses.ValidationFailed, result.TokensSpent, string.Join(" | ", result.Diagnostics), null, null)
 };
 }

 var feature = ReferenceCorpusAnalysisFrozenInputCodec.Deserialize<ReferenceCorpusFrozenFeatureWorkItem>(reservation.InputPayloadJson, reservation.InputPayloadHash);
 ValidateFeaturePayload(reservation, feature);
 var featureResult = await _featureProcessor.ProcessAsync(new(
 feature.RunId, feature.AnchorId, feature.NodeId, feature.NodeText, feature.NodeType,
 feature.FeatureFamily, feature.Context, feature.Model, feature.TokenPolicy.MaxValidationAttempts,
 new(reservation.ReservedTokens, Math.Min(feature.TokenPolicy.MaximumOutputTokensPerCall, reservation.ReservedTokens), Math.Min(feature.TokenPolicy.UnknownUsageCharge, reservation.ReservedTokens))), cancellationToken);
 return featureResult.Status switch
 {
ReferenceCorpusFeatureWorkItemStatuses.Succeeded => new(
WorkItemExecutionStatuses.Succeeded, featureResult.TokensSpent, string.Join(" | ", featureResult.Diagnostics),
 ReferenceCorpusAnalysisCompletionKinds.FeatureObservations,
 ReferenceCorpusAnalysisCompletionCodec.Serialize(new ReferenceCorpusFeatureCompletionPayload(
 feature.RunId, feature.AnchorId, feature.NodeId, feature.NodeType, DateTimeOffset.UtcNow,
 featureResult.AcceptedObservations))),
 ReferenceCorpusFeatureWorkItemStatuses.BudgetExhausted => new(WorkItemExecutionStatuses.BudgetExhausted, featureResult.TokensSpent, string.Join(" | ", featureResult.Diagnostics), null, null),
 _ => new(WorkItemExecutionStatuses.ValidationFailed, featureResult.TokensSpent, string.Join(" | ", featureResult.Diagnostics), null, null)
 };
 }

 private async Task RunHeartbeatAsync(
 SqliteReferenceCorpusAnalysisJobStore store,
 string jobId,
 string leaseToken,
 CancellationToken cancellationToken)
 {
 using var timer = new PeriodicTimer(_heartbeatInterval);
 while (await timer.WaitForNextTickAsync(cancellationToken))
 await store.HeartbeatAsync(jobId, _workerId, leaseToken, DateTimeOffset.UtcNow, _leaseDuration, cancellationToken);
 }

 private static void ValidateFeaturePayload(ReferenceCorpusAnalysisWorkItemReservation reservation, ReferenceCorpusFrozenFeatureWorkItem payload)
 {
 if (payload.SchemaVersion != ReferenceCorpusAnalysisFrozenInputVersions.FeatureV1 || payload.RunId != reservation.RunId ||
 payload.NodeId != reservation.NodeId || payload.NodeTextHash != reservation.NodeTextHash || payload.FeatureFamily != reservation.FeatureFamily)
 throw new ReferenceCorpusAnalysisJobConflictException("analysis_snapshot_corrupt: feature payload identity does not match reservation.");
 }

 private static void ValidateTechniquePayload(ReferenceCorpusAnalysisWorkItemReservation reservation, ReferenceCorpusFrozenTechniqueWorkItem payload)
 {
 if (payload.SchemaVersion != ReferenceCorpusAnalysisFrozenInputVersions.TechniqueV1 || payload.RunId != reservation.RunId ||
 payload.NodeId != reservation.NodeId || payload.NodeTextHash != reservation.NodeTextHash ||
 payload.EvidenceSetHash != ReferenceCorpusAnalysisFrozenInputCodec.ComputeEvidenceSetHash(payload.Observations))
 throw new ReferenceCorpusAnalysisJobConflictException("analysis_snapshot_corrupt: technique payload identity or evidence hash does not match reservation.");
 }

 private static string Truncate(string value) => value.Length <= 1200 ? value : value[..1200];

 private sealed record WorkItemExecution(
 string Status,
 int TokensSpent,
 string Diagnostics,
 string? OutputKind,
 string? OutputPayloadJson);
 private static class WorkItemExecutionStatuses
 {
 public const string Succeeded="succeeded";
 public const string BudgetExhausted="budget_exhausted";
 public const string ValidationFailed="validation_failed";
 }
 private static ValueTask<ReferenceCorpusFrozenTokenPolicy> ReadNextTokenPolicyAsync(
 SqliteReferenceCorpusAnalysisJobStore store,
 ReferenceCorpusAnalysisJob job,
 CancellationToken cancellationToken)
 {
 _ = store;
 cancellationToken.ThrowIfCancellationRequested();
 using var document = JsonDocument.Parse(job.InputJson);
 if (!document.RootElement.TryGetProperty("tokenPolicy", out var tokenPolicyElement))
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"legacy_snapshot_not_executable: analysis job '{job.JobId}' has no frozen token policy.");
 var policy = tokenPolicyElement.Deserialize<ReferenceCorpusFrozenTokenPolicy>(
 new JsonSerializerOptions(JsonSerializerDefaults.Web))
 ?? throw new ReferenceCorpusAnalysisJobConflictException(
 $"analysis_snapshot_corrupt: analysis job '{job.JobId}' has an invalid frozen token policy.");
 if (policy.MaxValidationAttempts is < 1 or > 4 ||
 policy.MaximumOutputTokensPerCall <= 0 ||
 policy.UnknownUsageCharge <= 0 ||
 policy.TokenReservation <= 0 ||
 policy.MaximumOutputTokensPerCall > policy.TokenReservation ||
 policy.UnknownUsageCharge > policy.TokenReservation)
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"analysis_snapshot_corrupt: analysis job '{job.JobId}' frozen token policy is outside supported bounds.");
return ValueTask.FromResult(policy);
}
}
