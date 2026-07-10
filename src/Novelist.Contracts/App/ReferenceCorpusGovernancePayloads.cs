using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceCorpusReviewStates
{
 public const string Unverified = "unverified";
 public const string LowConfidence = "low_confidence";
 public const string Confirmed = "confirmed";
 public const string Rejected = "rejected";
 public const string Conflicted = "conflicted";
 public static IReadOnlyList<string> All { get; } = [Unverified, LowConfidence, Confirmed, Rejected, Conflicted];
}

public static class ReferenceCorpusValidityStates
{
 public const string Active = "active";
 public const string Superseded = "superseded";
}

public sealed record ReferenceCorpusGovernanceMemberPayload(
 [property: JsonPropertyName("anchor_id")] long AnchorId,
 [property: JsonPropertyName("title")] string Title,
 [property: JsonPropertyName("enabled")] bool Enabled,
 [property: JsonPropertyName("source_quality")] string? SourceQuality,
 [property: JsonPropertyName("disabled_reason")] string? DisabledReason,
 [property: JsonPropertyName("dedup_group_id")] string? DedupGroupId,
 [property: JsonPropertyName("license_state")] string LicenseState,
 [property: JsonPropertyName("reuse_policy")] string ReusePolicy,
 [property: JsonPropertyName("max_verbatim_ratio")] double? MaxVerbatimRatio,
 [property: JsonPropertyName("cleared_for_insertion")] bool ClearedForInsertion);

public sealed record ReferenceCorpusGovernanceLibraryPayload(
 [property: JsonPropertyName("library_id")] string LibraryId,
 [property: JsonPropertyName("scope")] string Scope,
 [property: JsonPropertyName("novel_id")] long? NovelId,
 [property: JsonPropertyName("name")] string Name,
 [property: JsonPropertyName("bound_to_session")] bool BoundToSession,
 [property: JsonPropertyName("members")] IReadOnlyList<ReferenceCorpusGovernanceMemberPayload> Members);

public sealed record GetReferenceCorpusGovernancePayload([property: JsonPropertyName("session_id")] string? SessionId);
public sealed record ReferenceCorpusGovernancePayload(
 [property: JsonPropertyName("session_id")] string? SessionId,
 [property: JsonPropertyName("libraries")] IReadOnlyList<ReferenceCorpusGovernanceLibraryPayload> Libraries,
 [property: JsonPropertyName("pending_review_count")] int PendingReviewCount,
 [property: JsonPropertyName("stale_aggregate_count")] int StaleAggregateCount,
 [property: JsonPropertyName("insertion_audit_count")] int InsertionAuditCount);
public sealed record SetReferenceCorpusSessionLibraryBindingPayload(
 [property: JsonPropertyName("session_id")] string SessionId,
 [property: JsonPropertyName("library_id")] string LibraryId,
 [property: JsonPropertyName("enabled")] bool Enabled);
public sealed record UpdateReferenceCorpusLibraryMemberPayload(
 [property: JsonPropertyName("library_id")] string LibraryId,
 [property: JsonPropertyName("anchor_id")] long AnchorId,
 [property: JsonPropertyName("enabled")] bool Enabled,
 [property: JsonPropertyName("source_quality")] string? SourceQuality,
 [property: JsonPropertyName("disabled_reason")] string? DisabledReason);
public sealed record UpdateReferenceCorpusLicensePayload(
 [property: JsonPropertyName("anchor_id")] long AnchorId,
 [property: JsonPropertyName("license_state")] string LicenseState,
 [property: JsonPropertyName("authorization_evidence")] string? AuthorizationEvidence,
 [property: JsonPropertyName("reuse_policy")] string ReusePolicy,
 [property: JsonPropertyName("max_verbatim_ratio")] double? MaxVerbatimRatio,
 [property: JsonPropertyName("cleared_for_insertion")] bool ClearedForInsertion);
public sealed record RebuildReferenceCorpusDedupGroupsPayload([property: JsonPropertyName("library_id")] string? LibraryId);
public sealed record ReferenceCorpusDedupResultPayload(
[property: JsonPropertyName("members_scanned")] int MembersScanned,
[property: JsonPropertyName("groups_assigned")] int GroupsAssigned);
public sealed record RecordReferenceCorpusInsertionAuditPayload(
 [property: JsonPropertyName("audit_id")] string AuditId,
 [property: JsonPropertyName("session_id")] string SessionId,
 [property: JsonPropertyName("novel_id")] long NovelId,
 [property: JsonPropertyName("chapter_number")] int ChapterNumber,
 [property: JsonPropertyName("candidate_id")] string CandidateId,
 [property: JsonPropertyName("draft")] ReferenceCorpusInsertionDraftPayload Draft);

public sealed record BuildReferenceCorpusAggregatesPayload(
 [property: JsonPropertyName("library_ids")] IReadOnlyList<string> LibraryIds,
 [property: JsonPropertyName("run_id")] string? RunId);
public sealed record ListReferenceCorpusAggregatesPayload([property: JsonPropertyName("aggregate_type")] string? AggregateType);
public sealed record ReferenceCorpusAggregatePayload(
 [property: JsonPropertyName("aggregate_id")] string AggregateId,
 [property: JsonPropertyName("aggregate_type")] string AggregateType,
 [property: JsonPropertyName("name")] string Name,
 [property: JsonPropertyName("summary")] string Summary,
 [property: JsonPropertyName("sample_count")] int SampleCount,
 [property: JsonPropertyName("validity_state")] string ValidityState,
 [property: JsonPropertyName("library_ids")] IReadOnlyList<string> LibraryIds,
 [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
 [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record RefreshReferenceCorpusReviewQueuePayload([property: JsonPropertyName("confidence_threshold")] double ConfidenceThreshold);
public sealed record ListReferenceCorpusReviewQueuePayload([property: JsonPropertyName("page_request")] PageRequestPayload PageRequest);
public sealed record ReviewReferenceCorpusItemsPayload(
 [property: JsonPropertyName("queue_ids")] IReadOnlyList<string> QueueIds,
 [property: JsonPropertyName("review_state")] string ReviewState);
public sealed record ReferenceCorpusReviewQueueItemPayload(
 [property: JsonPropertyName("queue_id")] string QueueId,
 [property: JsonPropertyName("item_type")] string ItemType,
 [property: JsonPropertyName("item_id")] string ItemId,
 [property: JsonPropertyName("anchor_id")] long AnchorId,
 [property: JsonPropertyName("node_id")] string NodeId,
 [property: JsonPropertyName("reason")] string Reason,
 [property: JsonPropertyName("review_state")] string ReviewState,
[property: JsonPropertyName("confidence")] double Confidence,
[property: JsonPropertyName("feature_family")] string? FeatureFamily,
 [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
 [property: JsonPropertyName("anchor_title")] string? AnchorTitle = null,
 [property: JsonPropertyName("evidence_start")] int? EvidenceStart = null,
 [property: JsonPropertyName("evidence_end")] int? EvidenceEnd = null,
 [property: JsonPropertyName("evidence_preview")] string? EvidencePreview = null);
public sealed record ReconcileReferenceCorpusRunPayload(
 [property: JsonPropertyName("anchor_id")] long AnchorId,
 [property: JsonPropertyName("new_run_id")] string NewRunId);
public sealed record ReferenceCorpusReconcileResultPayload(
 [property: JsonPropertyName("superseded_observations")] int SupersededObservations,
 [property: JsonPropertyName("superseded_specimens")] int SupersededSpecimens,
 [property: JsonPropertyName("conflicts_queued")] int ConflictsQueued,
 [property: JsonPropertyName("aggregates_marked_stale")] int AggregatesMarkedStale);
