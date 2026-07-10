using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceCorpusAnalysisScheduler : IReferenceCorpusAnalysisScheduler
{
private const int MaxPageSize = 200;
private static readonly ReferenceCorpusFrozenTokenPolicy DefaultTokenPolicy = new(4, 512, 512, 4096);
 private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
 private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
 private readonly IAppSettingsService _settings;
 private readonly ReferenceCorpusAnalysisInputSnapshotBuilder _snapshotBuilder = new();

 public SqliteReferenceCorpusAnalysisScheduler(
 IReferenceCorpusDatabasePathResolver databasePathResolver,
 IAppSettingsService settings)
 {
 _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
 _settings = settings ?? throw new ArgumentNullException(nameof(settings));
 }

 public async ValueTask<ReferenceCorpusAnalysisJobPayload> EnqueueAsync(
 EnqueueReferenceCorpusAnalysisJobPayload input,
 CancellationToken cancellationToken)
 {
 ValidateEnqueue(input);
 var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
 var store = new SqliteReferenceCorpusAnalysisJobStore(databasePath);
 await store.EnsureSchemaAsync(cancellationToken);
 var model = await ResolveFrozenModelAsync(cancellationToken);
 var now = DateTimeOffset.UtcNow;
var snapshotId = $"analysis-snapshot:{Guid.NewGuid():N}";
var jobId = $"analysis-job:{Guid.NewGuid():N}";
 var techniqueDependency = input.JobKind == ReferenceCorpusAnalysisJobKinds.TechniqueSpecimen
 ? await GetRequiredTechniqueDependencyAsync(store, input, cancellationToken)
 : null;

await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
 var built = input.JobKind switch
 {
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis => await _snapshotBuilder.BuildFeatureAsync(
 connection,
 new(snapshotId, input.RunId, input.AnchorId, input.Scope, "reference-feature-analyzer-v1",
 model.ProviderName, model.ModelId, model.ReasoningEffort, DefaultTokenPolicy, now),
 cancellationToken),
 ReferenceCorpusAnalysisJobKinds.TechniqueSpecimen => await _snapshotBuilder.BuildTechniqueAsync(
 connection,
new(snapshotId, input.RunId, input.AnchorId, input.Scope, input.MinObservationConfidence,
 "reference-technique-analyzer-v1", model.ProviderName, model.ModelId, model.ReasoningEffort, DefaultTokenPolicy, now,
 techniqueDependency!.JobId, techniqueDependency.RunId, techniqueDependency.InputSnapshotId),
 cancellationToken),
 _ => throw new ArgumentOutOfRangeException(nameof(input), input.JobKind, "Unknown analysis job kind.")
 };

 var inputJson = JsonSerializer.Serialize(new
 {
 input.RunId,
 input.NovelId,
 input.AnchorId,
 input.JobKind,
 input.Scope,
 input.PriorityClass,
 input.PriorityValue,
 input.TokenBudget,
 input.MaxAttempts,
 input.DependencyJobId,
input.MinObservationConfidence,
 TokenPolicy = DefaultTokenPolicy,
model
 }, JsonOptions);
 var job = await store.EnqueueAsync(
 built.Snapshot,
 built.WorkItems,
 new(
 jobId,
 input.RunId,
 snapshotId,
 input.NovelId,
 input.AnchorId,
 input.JobKind,
 inputJson,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(inputJson),
 input.DependencyJobId,
 input.PriorityClass,
 input.PriorityValue,
 built.Snapshot.TotalNodes,
 built.Snapshot.TotalWorkItems,
 input.TokenBudget,
 input.JobKind,
 CurrentChapter: null,
 input.MaxAttempts,
 now),
 cancellationToken);
 return ToPayload(job);
 }

 public async ValueTask<ReferenceCorpusAnalysisJobPayload?> GetAsync(
 GetReferenceCorpusAnalysisJobPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 var store = await CreateStoreAsync(cancellationToken);
 var job = await store.GetAsync(input.JobId, cancellationToken);
 return job is null ? null : ToPayload(job);
 }

 public async ValueTask<PageResultPayload<ReferenceCorpusAnalysisJobPayload>> ListAsync(
 ListReferenceCorpusAnalysisJobsPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 ArgumentNullException.ThrowIfNull(input.PageRequest);
 var size = input.PageRequest.PageSize;
 if (size is < 1 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(input), "Page size must be between 1 and 200.");
var filters = input.PageRequest.Filters ?? new Dictionary<string, string>();
ValidateFilters(filters);
 var novelId = ParseOptionalLong(filters, "novel_id");
 var anchorId = ParseOptionalLong(filters, "anchor_id");
filters.TryGetValue("status", out var status);
 var fingerprint = ListFingerprint(novelId, anchorId, status, input.PageRequest.SortBy, input.PageRequest.SortDir);
 var cursor = DecodeListCursor(input.PageRequest.Cursor, fingerprint);
var store = await CreateStoreAsync(cancellationToken);
 var jobs = await store.ListAsync(new(novelId, anchorId, status, 0, size + 1, cursor?.UpdatedAt, cursor?.JobId), cancellationToken);
var total = await store.CountAsync(novelId, anchorId, status, cancellationToken);
 var hasMore = jobs.Count > size;
 var pageItems = jobs.Take(size).ToArray();
 var nextCursor = hasMore && pageItems.Length > 0
 ? EncodeListCursor(new(fingerprint, pageItems[^1].UpdatedAt, pageItems[^1].JobId))
 : null;
return new(
 pageItems.Select(ToPayload).ToArray(),
total,
 1,
size,
total == 0 ? 0 : (int)Math.Ceiling(total / (double)size),
 nextCursor,
 hasMore,
 total > int.MaxValue ? int.MaxValue : (int)total);
 }

 public async ValueTask<ReferenceCorpusAnalysisJobPayload> PauseAsync(PauseReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken)
 {
 var store = await CreateStoreAsync(cancellationToken);
 return ToPayload(await store.RequestPauseAsync(input.JobId, input.ExpectedVersion, DateTimeOffset.UtcNow, cancellationToken));
 }

 public async ValueTask<ReferenceCorpusAnalysisJobPayload> ResumeAsync(ResumeReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken)
 {
 var store = await CreateStoreAsync(cancellationToken);
 return ToPayload(await store.ResumeAsync(input.JobId, input.ExpectedVersion, input.NewTokenBudget, DateTimeOffset.UtcNow, cancellationToken));
 }

 public async ValueTask<ReferenceCorpusAnalysisJobPayload> CancelAsync(CancelReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken)
 {
 var store = await CreateStoreAsync(cancellationToken);
 return ToPayload(await store.RequestCancelAsync(input.JobId, input.ExpectedVersion, DateTimeOffset.UtcNow, cancellationToken));
 }

 public async ValueTask<ReferenceCorpusAnalysisJobPayload> ReprioritizeAsync(ReprioritizeReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken)
 {
 var store = await CreateStoreAsync(cancellationToken);
 return ToPayload(await store.ReprioritizeAsync(input.JobId, input.ExpectedVersion, input.PriorityClass, input.PriorityValue, DateTimeOffset.UtcNow, cancellationToken));
 }

 private async ValueTask<SqliteReferenceCorpusAnalysisJobStore> CreateStoreAsync(CancellationToken cancellationToken)
 {
 var store = new SqliteReferenceCorpusAnalysisJobStore(await _databasePathResolver.ResolveAsync(cancellationToken));
 await store.EnsureSchemaAsync(cancellationToken);
 return store;
 }

 private async ValueTask<ReferenceCorpusFrozenModelSelection> ResolveFrozenModelAsync(CancellationToken cancellationToken)
 {
 var settings = await _settings.GetSettingsAsync(cancellationToken);
 var parts = settings.SelectedModelKey.Split('/', 2, StringSplitOptions.TrimEntries);
 if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
 throw new InvalidOperationException("Reference corpus background analysis requires a selected model.");
 return new(parts[0].ToLowerInvariant(), parts[1], settings.ReasoningEffort ?? string.Empty);
 }

 private static ReferenceCorpusAnalysisJobPayload ToPayload(ReferenceCorpusAnalysisJob job) => new(
 job.JobId, job.RunId, job.NovelId, job.AnchorId, job.JobKind, ReadScope(job.InputJson), job.Status,
 job.Version, job.PriorityClass, job.PriorityValue, job.TotalNodes, job.TotalWorkItems,
 job.ProcessedWorkItems, job.SucceededWorkItems, job.SkippedWorkItems, job.FailedWorkItems,
 job.RetryingWorkItems, job.TokenBudget, job.TokensSpent, job.ResumeCursor, job.AttemptCount,
 job.FailureAttemptCount, job.MaxAttempts, job.NextAttemptAt, job.HeartbeatAt, job.LeaseExpiresAt, Dependency: null,
 job.QueuedAt, job.QueuedAt, job.StartedAt, job.UpdatedAt, job.CompletedAt,
job.LastErrorCode, job.LastErrorMessage, job.CurrentChapter,
 AllowedActions(job.Status), SafeDiagnostics(job), job.TokensReserved, job.ProcessedNodes);

 private static IReadOnlyList<string> AllowedActions(string status) => status switch
 {
 ReferenceCorpusAnalysisJobStatuses.Queued => ["pause", "cancel", "reprioritize"],
 ReferenceCorpusAnalysisJobStatuses.Running => ["pause", "cancel", "reprioritize"],
 ReferenceCorpusAnalysisJobStatuses.PauseRequested => ["cancel"],
 ReferenceCorpusAnalysisJobStatuses.Paused => ["resume", "cancel", "reprioritize"],
 ReferenceCorpusAnalysisJobStatuses.RetryWait => ["resume", "cancel", "reprioritize"],
 ReferenceCorpusAnalysisJobStatuses.BudgetExhausted => ["resume", "cancel"],
 _ => []
 };

 private static IReadOnlyList<string> SafeDiagnostics(ReferenceCorpusAnalysisJob job)
 {
 var result = new List<string>(2);
 if (!string.IsNullOrWhiteSpace(job.LastErrorCode)) result.Add(job.LastErrorCode);
 if (job.DependencyJobId is not null) result.Add("dependency:" + job.DependencyJobId);
 return result;
 }

 private static string ReadScope(string inputJson)
 {
 using var document = JsonDocument.Parse(inputJson);
 var found = document.RootElement.TryGetProperty("scope", out var scope) ||
 document.RootElement.TryGetProperty("Scope", out scope);
 return found && scope.ValueKind == JsonValueKind.String
 ? scope.GetString() ?? string.Empty
 : string.Empty;
 }

 private static async ValueTask<SqliteConnection> OpenConnectionAsync(string path, CancellationToken cancellationToken)
 {
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
 await connection.OpenAsync(cancellationToken);
 await using var pragma = connection.CreateCommand();
 pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
 await pragma.ExecuteNonQueryAsync(cancellationToken);
 return connection;
 }

 private static void ValidateEnqueue(EnqueueReferenceCorpusAnalysisJobPayload input)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.NovelId <= 0 || input.AnchorId <= 0 || string.IsNullOrWhiteSpace(input.RunId))
 throw new ArgumentException("Run, novel, and anchor are required.", nameof(input));
 if (!ReferenceCorpusAnalysisJobKinds.All.Contains(input.JobKind, StringComparer.Ordinal))
 throw new ArgumentOutOfRangeException(nameof(input), input.JobKind, "Unknown job kind.");
 if (!ReferenceCorpusAnalysisPriorityClasses.All.Contains(input.PriorityClass, StringComparer.Ordinal))
 throw new ArgumentOutOfRangeException(nameof(input), input.PriorityClass, "Unknown priority class.");
if (input.Scope is not ReferenceCorpusNodeTypes.Sentence and not ReferenceCorpusNodeTypes.Passage ||
input.TokenBudget is < 0 || input.MaxAttempts is < 1 or > 20 || input.MinObservationConfidence is < 0 or > 1)
throw new ArgumentOutOfRangeException(nameof(input), "Analysis enqueue values are outside supported bounds.");
 if (input.JobKind == ReferenceCorpusAnalysisJobKinds.TechniqueSpecimen && string.IsNullOrWhiteSpace(input.DependencyJobId))
 throw new ArgumentException("Technique specimen jobs require a completed feature-analysis dependency.", nameof(input));
}

 private static async ValueTask<ReferenceCorpusAnalysisJob> GetRequiredTechniqueDependencyAsync(
 SqliteReferenceCorpusAnalysisJobStore store,
 EnqueueReferenceCorpusAnalysisJobPayload input,
 CancellationToken cancellationToken)
 {
 var dependency = await store.GetAsync(input.DependencyJobId!, cancellationToken);
 if (dependency is null)
 throw new ArgumentException("Technique specimen dependency job was not found.", nameof(input));
 if (dependency.JobKind != ReferenceCorpusAnalysisJobKinds.FeatureAnalysis ||
 dependency.Status != ReferenceCorpusAnalysisJobStatuses.Completed ||
 dependency.NovelId != input.NovelId || dependency.AnchorId != input.AnchorId ||
 !string.Equals(ReadScope(dependency.InputJson), input.Scope, StringComparison.Ordinal))
 throw new ArgumentException("Technique specimen dependency must be a completed feature-analysis job for the same novel, anchor, and scope.", nameof(input));
 return dependency;
 }

 private static string ListFingerprint(long? novelId, long? anchorId, string? status, string sortBy, string sortDir)
 {
 var value = string.Join('\u001f', novelId, anchorId, status ?? string.Empty, sortBy, sortDir);
 return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
 }

 private static string EncodeListCursor(AnalysisJobListCursor cursor) =>
 Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions))
 .TrimEnd('=')
 .Replace('+', '-')
 .Replace('/', '_');

 private static AnalysisJobListCursor? DecodeListCursor(string? value, string fingerprint)
 {
 if (string.IsNullOrWhiteSpace(value)) return null;
 try
 {
 var normalized = value.Replace('-', '+').Replace('_', '/');
 normalized += new string('=', (4 - normalized.Length % 4) % 4);
 var cursor = JsonSerializer.Deserialize<AnalysisJobListCursor>(Convert.FromBase64String(normalized), JsonOptions);
 if (cursor is null || !string.Equals(cursor.Fingerprint, fingerprint, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(cursor.JobId))
 throw new FormatException();
 return cursor;
 }
 catch (Exception exception) when (exception is FormatException or JsonException)
 {
 throw new PageRequestValidationException(PageRequestErrorCodes.InvalidCursor, "cursor is invalid or does not match the query.");
 }
 }

 private sealed record AnalysisJobListCursor(string Fingerprint, DateTimeOffset UpdatedAt, string JobId);

 private static void ValidateFilters(IReadOnlyDictionary<string, string> filters)
 {
 var allowed = new HashSet<string>(["novel_id", "anchor_id", "status"], StringComparer.Ordinal);
 foreach (var key in filters.Keys)
 if (!allowed.Contains(key)) throw new ArgumentException($"Unsupported analysis job filter '{key}'.", nameof(filters));
 }

 private static long? ParseOptionalLong(IReadOnlyDictionary<string, string> filters, string key) =>
 filters.TryGetValue(key, out var value)
 ? long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
 ? parsed
 : throw new ArgumentException($"Filter '{key}' must be a positive integer.", nameof(filters))
 : null;
}
