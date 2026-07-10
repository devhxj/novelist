using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record EnqueueReferenceCorpusAnalysisJobPayload(
 [property: JsonPropertyName("run_id")] string RunId,
 [property: JsonPropertyName("novel_id")] long NovelId,
 [property: JsonPropertyName("anchor_id")] long AnchorId,
 [property: JsonPropertyName("job_kind")] string JobKind,
 [property: JsonPropertyName("scope")] string Scope,
 [property: JsonPropertyName("priority_class")] string PriorityClass,
 [property: JsonPropertyName("priority_value")] int PriorityValue,
 [property: JsonPropertyName("token_budget")] int? TokenBudget = null,
[property: JsonPropertyName("max_attempts")] int MaxAttempts = 3,
 [property: JsonPropertyName("dependency_job_id")] string? DependencyJobId = null,
 [property: JsonPropertyName("min_observation_confidence")] double MinObservationConfidence = 0.70);

public sealed record GetReferenceCorpusAnalysisJobPayload([property: JsonPropertyName("job_id")] string JobId);
public sealed record ListReferenceCorpusAnalysisJobsPayload([property: JsonPropertyName("page_request")] PageRequestPayload PageRequest);
public sealed record PauseReferenceCorpusAnalysisJobPayload([property: JsonPropertyName("job_id")] string JobId, [property: JsonPropertyName("expected_version")] long ExpectedVersion);
public sealed record ResumeReferenceCorpusAnalysisJobPayload([property: JsonPropertyName("job_id")] string JobId, [property: JsonPropertyName("expected_version")] long ExpectedVersion, [property: JsonPropertyName("new_token_budget")] int? NewTokenBudget = null);
public sealed record CancelReferenceCorpusAnalysisJobPayload([property: JsonPropertyName("job_id")] string JobId, [property: JsonPropertyName("expected_version")] long ExpectedVersion);
public sealed record ReprioritizeReferenceCorpusAnalysisJobPayload([property: JsonPropertyName("job_id")] string JobId, [property: JsonPropertyName("expected_version")] long ExpectedVersion, [property: JsonPropertyName("priority_class")] string PriorityClass, [property: JsonPropertyName("priority_value")] int PriorityValue);

public sealed record ReferenceCorpusAnalysisJobDependencyPayload(
 [property: JsonPropertyName("job_id")] string JobId,
 [property: JsonPropertyName("required_status")] string RequiredStatus,
 [property: JsonPropertyName("satisfied")] bool Satisfied);

public sealed record ReferenceCorpusAnalysisJobPayload(
 [property: JsonPropertyName("job_id")] string JobId,
 [property: JsonPropertyName("run_id")] string RunId,
 [property: JsonPropertyName("novel_id")] long NovelId,
 [property: JsonPropertyName("anchor_id")] long AnchorId,
 [property: JsonPropertyName("job_kind")] string JobKind,
 [property: JsonPropertyName("scope")] string Scope,
 [property: JsonPropertyName("status")] string Status,
 [property: JsonPropertyName("version")] long Version,
 [property: JsonPropertyName("priority_class")] string PriorityClass,
 [property: JsonPropertyName("priority_value")] int PriorityValue,
 [property: JsonPropertyName("total_nodes")] int TotalNodes,
 [property: JsonPropertyName("total_work_items")] int TotalWorkItems,
 [property: JsonPropertyName("processed_work_items")] int ProcessedWorkItems,
 [property: JsonPropertyName("succeeded_work_items")] int SucceededWorkItems,
 [property: JsonPropertyName("skipped_work_items")] int SkippedWorkItems,
 [property: JsonPropertyName("failed_work_items")] int FailedWorkItems,
 [property: JsonPropertyName("retrying_work_items")] int RetryingWorkItems,
 [property: JsonPropertyName("token_budget")] int? TokenBudget,
 [property: JsonPropertyName("tokens_spent")] int TokensSpent,
 [property: JsonPropertyName("resume_cursor")] string? ResumeCursor,
[property: JsonPropertyName("attempt_count")] int AttemptCount,
 [property: JsonPropertyName("failure_attempt_count")] int FailureAttemptCount,
[property: JsonPropertyName("max_attempts")] int MaxAttempts,
 [property: JsonPropertyName("next_attempt_at")] DateTimeOffset? NextAttemptAt,
 [property: JsonPropertyName("lease_heartbeat_at")] DateTimeOffset? LeaseHeartbeatAt,
 [property: JsonPropertyName("lease_expires_at")] DateTimeOffset? LeaseExpiresAt,
 [property: JsonPropertyName("dependency")] ReferenceCorpusAnalysisJobDependencyPayload? Dependency,
 [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
 [property: JsonPropertyName("queued_at")] DateTimeOffset QueuedAt,
 [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
 [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
[property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
[property: JsonPropertyName("error_code")] string? ErrorCode,
[property: JsonPropertyName("error_message")] string? ErrorMessage,
[property: JsonPropertyName("current_chapter")] int? CurrentChapter = null,
[property: JsonPropertyName("allowed_actions")] IReadOnlyList<string>? AllowedActions = null,
[property: JsonPropertyName("safe_diagnostics")] IReadOnlyList<string>? SafeDiagnostics = null,
 [property: JsonPropertyName("tokens_reserved")] int TokensReserved = 0,
 [property: JsonPropertyName("processed_nodes")] int ProcessedNodes = 0);
