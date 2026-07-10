namespace Novelist.Core.App;

public sealed record ReferenceCorpusAnalysisJob(
 string JobId,
 string RunId,
 string InputSnapshotId,
 long NovelId,
 long AnchorId,
 string JobKind,
 string InputJson,
 string InputHash,
 string? DependencyJobId,
 string PriorityClass,
 int PriorityValue,
 string Status,
 int TotalNodes,
 int TotalWorkItems,
 int ProcessedWorkItems,
 int SucceededWorkItems,
 int SkippedWorkItems,
 int FailedWorkItems,
 int RetryingWorkItems,
 int? TokenBudget,
 int TokensSpent,
 string? ResumeCursor,
 string CurrentStage,
 int? CurrentChapter,
 int AttemptCount,
 int MaxAttempts,
 DateTimeOffset? NextAttemptAt,
 string? LeaseOwner,
 string? LeaseToken,
 DateTimeOffset? LeaseAcquiredAt,
 DateTimeOffset? LeaseExpiresAt,
 DateTimeOffset? HeartbeatAt,
 DateTimeOffset? PauseRequestedAt,
 DateTimeOffset? CancelRequestedAt,
 DateTimeOffset QueuedAt,
 DateTimeOffset? StartedAt,
 DateTimeOffset? CompletedAt,
 DateTimeOffset UpdatedAt,
 string? LastErrorCode,
string? LastErrorMessage,
long Version,
int FailureAttemptCount = 0,
 int TokensReserved = 0,
 int ProcessedNodes = 0);

public sealed record ReferenceCorpusAnalysisJobEnqueue(
 string JobId,
 string RunId,
 string InputSnapshotId,
 long NovelId,
 long AnchorId,
 string JobKind,
 string InputJson,
 string InputHash,
 string? DependencyJobId,
 string PriorityClass,
 int PriorityValue,
 int TotalNodes,
 int TotalWorkItems,
 int? TokenBudget,
 string CurrentStage,
 int? CurrentChapter,
int MaxAttempts,
DateTimeOffset QueuedAt);

public sealed record ReferenceCorpusAnalysisInputSnapshot(
 string InputSnapshotId,
 long AnchorId,
 string AnalysisStage,
 string Scope,
 string NodeSetHash,
 string FamilySetJson,
 string SchemaVersion,
 string AnalyzerVersion,
 string ModelProvider,
 string ModelId,
 int TotalNodes,
 int TotalWorkItems,
 DateTimeOffset CreatedAt);

public sealed record ReferenceCorpusAnalysisWorkItemSnapshot(
int Ordinal,
string NodeId,
string? ChapterNodeId,
string FeatureFamily,
 string NodeTextHash,
 string InputPayloadJson,
 string InputPayloadHash);

public sealed record ReferenceCorpusAnalysisJobListRequest(
long? NovelId,
long? AnchorId,
string? Status,
int Offset,
 int Limit,
 DateTimeOffset? UpdatedBefore = null,
 string? JobIdAfter = null);

public sealed record ReferenceCorpusAnalysisJobClaim(
ReferenceCorpusAnalysisJob Job,
string LeaseToken);

public sealed record ReferenceCorpusAnalysisJobLease(
string JobId,
string WorkerId,
string LeaseToken,
int AttemptNumber,
DateTimeOffset LeaseExpiresAt);

public sealed record ReferenceCorpusAnalysisWorkItemReservation(
 string JobId,
 string RunId,
 string InputSnapshotId,
 int Ordinal,
 string NodeId,
 string? ChapterNodeId,
string FeatureFamily,
string NodeTextHash,
string InputPayloadJson,
 string InputPayloadHash,
int AttemptNumber,
 int InvocationNumber,
 int ReservedTokens,
string WorkerId,
string LeaseToken);

public enum ReferenceCorpusAnalysisWorkItemSettlementKind
{
 RetryableFailure,
 PermanentFailure,
 BudgetExhausted,
 PauseBoundary,
 CancelBoundary
}

public sealed record ReferenceCorpusAnalysisWorkItemSettlementRequest(
 ReferenceCorpusAnalysisWorkItemReservation Reservation,
 ReferenceCorpusAnalysisWorkItemSettlementKind Kind,
 int TokensSpent,
 string? ErrorCode,
 string? ErrorMessage,
 DateTimeOffset? NextAttemptAt,
 DateTimeOffset SettledAt);

public sealed record ReferenceCorpusAnalysisWorkItemSettlementResult(
ReferenceCorpusAnalysisJob Job,
string WorkItemState,
string AttemptOutcome);

public enum ReferenceCorpusAnalysisCommitDisposition
{
 Continue,
 Complete,
 PauseAfterCommit,
 CancelAfterCommit,
 BudgetExhaustedAfterCommit
}

public sealed class ReferenceCorpusAnalysisJobConflictException : InvalidOperationException
{
 public ReferenceCorpusAnalysisJobConflictException(string message) : base(message)
 {
 }
}

public static class ReferenceCorpusAnalysisJobKinds
{
 public const string FeatureAnalysis = "feature_analysis";
 public const string TechniqueSpecimen = "technique_specimen";

 public static IReadOnlyList<string> All { get; } = [FeatureAnalysis, TechniqueSpecimen];
}

public static class ReferenceCorpusAnalysisJobStatuses
{
 public const string Queued = "queued";
 public const string Running = "running";
 public const string PauseRequested = "pause_requested";
 public const string Paused = "paused";
 public const string CancelRequested = "cancel_requested";
 public const string Cancelled = "cancelled";
 public const string RetryWait = "retry_wait";
 public const string BudgetExhausted = "budget_exhausted";
 public const string Completed = "completed";
 public const string Failed = "failed";

 public static IReadOnlyList<string> All { get; } =
 [
 Queued, Running, PauseRequested, Paused, CancelRequested,
 Cancelled, RetryWait, BudgetExhausted, Completed, Failed
 ];
}

public static class ReferenceCorpusAnalysisPriorityClasses
{
 public const string CurrentChapter = "current_chapter";
 public const string AdjacentChapter = "adjacent_chapter";
 public const string Normal = "normal";
 public const string Maintenance = "maintenance";

 public static IReadOnlyList<string> All { get; } = [CurrentChapter, AdjacentChapter, Normal, Maintenance];
}
