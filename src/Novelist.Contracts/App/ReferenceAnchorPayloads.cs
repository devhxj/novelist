using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceRewriteLevels
{
    public const string L0 = "L0";
    public const string L1 = "L1";
    public const string L2 = "L2";
    public const string L3 = "L3";
    public const string L4 = "L4";

    public static IReadOnlyList<string> All { get; } = [L0, L1, L2, L3, L4];
}

public static class ReferenceAnchorBuildStates
{
    public const string Created = "created";
    public const string Importing = "importing";
    public const string SourceImported = "source_imported";
    public const string Segmenting = "segmenting";
    public const string SegmentsBuilt = "segments_built";
    public const string ExtractingMaterials = "extracting_materials";
    public const string MaterialsExtracted = "materials_extracted";
    public const string DetectingSlots = "detecting_slots";
    public const string SlotsDetected = "slots_detected";
    public const string Embedding = "embedding";
    public const string Ready = "ready";
    public const string FailedImport = "failed_import";
    public const string FailedSegmenting = "failed_segmenting";
    public const string FailedExtraction = "failed_extraction";
    public const string FailedSlotting = "failed_slotting";
    public const string FailedEmbedding = "failed_embedding";
    public const string Cancelled = "cancelled";
    public const string Stale = "stale";

    public static IReadOnlyList<string> All { get; } =
    [
        Created,
        Importing,
        SourceImported,
        Segmenting,
        SegmentsBuilt,
        ExtractingMaterials,
        MaterialsExtracted,
        DetectingSlots,
        SlotsDetected,
        Embedding,
        Ready,
        FailedImport,
        FailedSegmenting,
        FailedExtraction,
        FailedSlotting,
        FailedEmbedding,
        Cancelled,
        Stale
    ];
}

public static class ReferenceMaterialTypes
{
    public const string Chapter = "chapter";
    public const string Paragraph = "paragraph";
    public const string Sentence = "sentence";
    public const string Passage = "passage";
    public const string Scene = "scene";
    public const string Beat = "beat";
    public const string DialogueExchange = "dialogue_exchange";
    public const string ActionAfterbeat = "action_afterbeat";
    public const string ImageMotif = "image_motif";
    public const string Hook = "hook";
    public const string Payoff = "payoff";
    public const string Transition = "transition";

    public static IReadOnlyList<string> All { get; } =
    [
        Chapter,
        Paragraph,
        Sentence,
        Passage,
        Scene,
        Beat,
        DialogueExchange,
        ActionAfterbeat,
        ImageMotif,
        Hook,
        Payoff,
        Transition
    ];
}

public static class ReferenceMaterialArchiveFilters
{
    public const string Active = "active";
    public const string Archived = "archived";
    public const string All = "all";

    public static IReadOnlyList<string> Allowed { get; } = [Active, Archived, All];
}

public static class ReferenceFeedbackDecisions
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Edited = "edited";

    public static IReadOnlyList<string> All { get; } = [Accepted, Rejected, Edited];
}

public static class ReferenceFeedbackTargetTypes
{
    public const string Material = "material";
    public const string ReuseCandidate = "reuse_candidate";
    public const string DraftCandidate = "draft_candidate";
    public const string Blueprint = "blueprint";
    public const string BlueprintBeat = "blueprint_beat";
    public const string MaterialLink = "material_link";

    public static IReadOnlyList<string> All { get; } =
    [
        Material,
        ReuseCandidate,
        DraftCandidate,
        Blueprint,
        BlueprintBeat,
        MaterialLink
    ];
}

public static class ReferenceCorpusVisibilities
{
    public const string Private = "private";
    public const string Workspace = "workspace";
    public const string Restricted = "restricted";

    public static IReadOnlyList<string> All { get; } = [Private, Workspace, Restricted];
}

public static class ReferenceSourceTrustLevels
{
    public const string UserVerified = "user_verified";
    public const string Imported = "imported";
    public const string Unverified = "unverified";

    public static IReadOnlyList<string> All { get; } = [UserVerified, Imported, Unverified];
}

public static class ReferenceAnchorOwnerScopes
{
    public const string Novel = "novel";
    public const string WorkspaceCorpus = "workspace_corpus";
}

public sealed record CreateReferenceAnchorPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("license_status")] string LicenseStatus,
    [property: JsonPropertyName("visibility")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Visibility = null,
    [property: JsonPropertyName("source_trust")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceTrust = null,
    [property: JsonPropertyName("user_tags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? UserTags = null);

public sealed record CreateReferenceAnchorsPayload(
    [property: JsonPropertyName("anchors")] IReadOnlyList<CreateReferenceAnchorPayload> Anchors);

public sealed record CreateReferenceAnchorFailurePayload(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("source_identity")] string SourceIdentity,
    [property: JsonPropertyName("diagnostic")] string Diagnostic,
    [property: JsonPropertyName("retry_available")] bool RetryAvailable);

public sealed record CreateReferenceAnchorsResultPayload(
    [property: JsonPropertyName("succeeded")] IReadOnlyList<ReferenceAnchorPayload> Succeeded,
    [property: JsonPropertyName("failed")] IReadOnlyList<CreateReferenceAnchorFailurePayload> Failed,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("succeeded_count")] int SucceededCount,
    [property: JsonPropertyName("failed_count")] int FailedCount);

public sealed record PromoteReferenceAnchorToWorkspaceCorpusPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_trust")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceTrust = null,
    [property: JsonPropertyName("user_tags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? UserTags = null);

public sealed record PromoteReferenceAnchorsToWorkspaceCorpusPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("source_trust")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceTrust = null,
    [property: JsonPropertyName("user_tags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? UserTags = null);

public sealed record DeleteReferenceAnchorsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds);

public sealed record DeleteReferenceMaterialsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("material_ids")] IReadOnlyList<string> MaterialIds);

public sealed record RestoreReferenceMaterialsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("material_ids")] IReadOnlyList<string> MaterialIds);

public sealed record UpdateReferenceAnchorMetadataPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("license_status")] string LicenseStatus,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("source_trust")] string SourceTrust,
[property: JsonPropertyName("user_tags")] IReadOnlyList<string> UserTags);

public sealed record BackfillReferenceMaterialEmbeddingsPayload(
 [property: JsonPropertyName("novel_id")] long NovelId,
 [property: JsonPropertyName("anchor_ids")]
 [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<long>? AnchorIds = null);

public sealed record ReferenceMaterialEmbeddingInspectionPayload(
 [property: JsonPropertyName("material_id")] string MaterialId,
 [property: JsonPropertyName("anchor_id")] long AnchorId,
 [property: JsonPropertyName("node_id")] string NodeId,
 [property: JsonPropertyName("provider_key")] string ProviderKey,
 [property: JsonPropertyName("model_id")] string ModelId,
 [property: JsonPropertyName("dimensions")] int Dimensions,
 [property: JsonPropertyName("material_hash")] string MaterialHash,
 [property: JsonPropertyName("node_text_hash")] string NodeTextHash,
 [property: JsonPropertyName("embedding_hash")] string EmbeddingHash,
 [property: JsonPropertyName("status")] string Status,
 [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ReferenceMaterialEmbeddingBackfillPayload(
 [property: JsonPropertyName("provider_key")] string ProviderKey,
 [property: JsonPropertyName("model_id")] string ModelId,
 [property: JsonPropertyName("dimensions")] int Dimensions,
 [property: JsonPropertyName("material_count")] int MaterialCount,
 [property: JsonPropertyName("built_count")] int BuiltCount,
 [property: JsonPropertyName("reused_count")] int ReusedCount,
 [property: JsonPropertyName("aligned_source_segment_count")] int AlignedSourceSegmentCount,
 [property: JsonPropertyName("aligned_material_count")] int AlignedMaterialCount,
 [property: JsonPropertyName("projection_count")] int ProjectionCount,
 [property: JsonPropertyName("items")] IReadOnlyList<ReferenceMaterialEmbeddingInspectionPayload> Items);

public sealed record ReferenceAnchorPayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("license_status")] string LicenseStatus,
    [property: JsonPropertyName("source_file_hash")] string SourceFileHash,
    [property: JsonPropertyName("build_version")] string BuildVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("source_trust")] string SourceTrust,
    [property: JsonPropertyName("user_tags")] IReadOnlyList<string> UserTags)
{
    [JsonPropertyName("owner_scope")]
    public string OwnerScope { get; init; } = NovelId == 0
        ? ReferenceAnchorOwnerScopes.WorkspaceCorpus
        : ReferenceAnchorOwnerScopes.Novel;

    [JsonPropertyName("owner_novel_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OwnerNovelId { get; init; } = NovelId == 0 ? null : NovelId;

    public ReferenceAnchorPayload(
        long anchorId,
        long novelId,
        string title,
        string author,
        string sourcePath,
        string sourceKind,
        string licenseStatus,
        string sourceFileHash,
        string buildVersion,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
        : this(
            anchorId,
            novelId,
            title,
            author,
            sourcePath,
            sourceKind,
            licenseStatus,
            sourceFileHash,
            buildVersion,
            status,
            createdAt,
            updatedAt,
            ReferenceCorpusVisibilities.Private,
            ReferenceSourceTrustLevels.UserVerified,
            [])
    {
    }
}

public sealed record ReferenceAnchorBuildStatusPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("source_segment_count")] int SourceSegmentCount,
    [property: JsonPropertyName("material_count")] int MaterialCount,
    [property: JsonPropertyName("slot_count")] int SlotCount,
    [property: JsonPropertyName("vector_count")] int VectorCount,
    [property: JsonPropertyName("last_error")] string LastError,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ReferenceMaterialPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_segment_id")] string SourceSegmentId,
    [property: JsonPropertyName("material_type")] string MaterialType,
    [property: JsonPropertyName("function_tag")] string FunctionTag,
    [property: JsonPropertyName("emotion_tag")] string EmotionTag,
    [property: JsonPropertyName("scene_tag")] string SceneTag,
    [property: JsonPropertyName("pov_tag")] string PovTag,
    [property: JsonPropertyName("technique_tag")] string TechniqueTag,
    [property: JsonPropertyName("function_confidence")] double FunctionConfidence,
    [property: JsonPropertyName("emotion_confidence")] double EmotionConfidence,
    [property: JsonPropertyName("pov_confidence")] double PovConfidence,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("source_hash")] string SourceHash,
    [property: JsonPropertyName("extractor_version")] string ExtractorVersion,
    [property: JsonPropertyName("user_verified")] bool UserVerified,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("score_components")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, double>? ScoreComponents = null);

public sealed record GetReferenceMaterialDetailPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("material_id")] string MaterialId);

public static class ReferenceMaterialTagReviewIssueCodes
{
    public const string Unverified = "unverified";
    public const string LowConfidence = "low_confidence";
    public const string UnknownTag = "unknown_tag";
}

public sealed record ReferenceMaterialSummaryPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_segment_id")] string SourceSegmentId,
    [property: JsonPropertyName("material_type")] string MaterialType,
    [property: JsonPropertyName("function_tag")] string FunctionTag,
    [property: JsonPropertyName("emotion_tag")] string EmotionTag,
    [property: JsonPropertyName("scene_tag")] string SceneTag,
    [property: JsonPropertyName("pov_tag")] string PovTag,
    [property: JsonPropertyName("technique_tag")] string TechniqueTag,
    [property: JsonPropertyName("function_confidence")] double FunctionConfidence,
    [property: JsonPropertyName("emotion_confidence")] double EmotionConfidence,
    [property: JsonPropertyName("pov_confidence")] double PovConfidence,
    [property: JsonPropertyName("text_preview")] string TextPreview,
    [property: JsonPropertyName("text_truncated")] bool TextTruncated,
    [property: JsonPropertyName("source_hash")] string SourceHash,
    [property: JsonPropertyName("extractor_version")] string ExtractorVersion,
    [property: JsonPropertyName("user_verified")] bool UserVerified,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("archive_state")] string ArchiveState = ReferenceMaterialArchiveFilters.Active,
    [property: JsonPropertyName("archived_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ArchivedAt = null,
    [property: JsonPropertyName("score_components")] IReadOnlyDictionary<string, double>? ScoreComponents = null);

public sealed record GetReferenceMaterialTagReviewQueuePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("archive_filter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ArchiveFilter = null);

public sealed record ReferenceMaterialTagReviewIssuePayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("severity")] string Severity);

public sealed record ReferenceMaterialTagReviewItemPayload(
    [property: JsonPropertyName("material")] ReferenceMaterialSummaryPayload Material,
    [property: JsonPropertyName("issues")] IReadOnlyList<ReferenceMaterialTagReviewIssuePayload> Issues);

public sealed record ReferenceMaterialSourceSummaryPayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("license_status")] string LicenseStatus,
    [property: JsonPropertyName("source_file_hash")] string SourceFileHash,
    [property: JsonPropertyName("build_version")] string BuildVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("source_trust")] string SourceTrust,
    [property: JsonPropertyName("user_tags")] IReadOnlyList<string> UserTags,
    [property: JsonPropertyName("owner_scope")] string OwnerScope = ReferenceAnchorOwnerScopes.Novel,
    [property: JsonPropertyName("owner_novel_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? OwnerNovelId = null);

public sealed record ReferenceMaterialSegmentPreviewPayload(
    [property: JsonPropertyName("segment_id")] string SegmentId,
    [property: JsonPropertyName("segment_type")] string SegmentType,
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("chapter_title")] string ChapterTitle,
    [property: JsonPropertyName("segment_index")] int SegmentIndex,
    [property: JsonPropertyName("text_preview")] string TextPreview,
    [property: JsonPropertyName("text_truncated")] bool TextTruncated,
    [property: JsonPropertyName("text_hash")] string TextHash);

public sealed record ReferenceMaterialSlotPreviewPayload(
    [property: JsonPropertyName("slot_name")] string SlotName,
    [property: JsonPropertyName("placeholder")] string Placeholder,
    [property: JsonPropertyName("start_offset")] int StartOffset,
    [property: JsonPropertyName("end_offset")] int EndOffset);

public sealed record ReferenceMaterialProcessingNotePayload(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("source_segment_count")] int SourceSegmentCount = 0,
    [property: JsonPropertyName("material_count")] int MaterialCount = 0,
    [property: JsonPropertyName("slot_count")] int SlotCount = 0,
    [property: JsonPropertyName("vector_count")] int VectorCount = 0,
    [property: JsonPropertyName("affected_source_id")] string AffectedSourceId = "",
    [property: JsonPropertyName("affected_material_id")] string AffectedMaterialId = "",
    [property: JsonPropertyName("affected_segment_id")] string AffectedSegmentId = "",
    [property: JsonPropertyName("affected_slot_id")] string AffectedSlotId = "");

public sealed record ReferenceMaterialDetailPayload(
    [property: JsonPropertyName("material")] ReferenceMaterialSummaryPayload Material,
    [property: JsonPropertyName("source")] ReferenceMaterialSourceSummaryPayload Source,
    [property: JsonPropertyName("segments")] IReadOnlyList<ReferenceMaterialSegmentPreviewPayload> Segments,
    [property: JsonPropertyName("slots")] IReadOnlyList<ReferenceMaterialSlotPreviewPayload> Slots,
    [property: JsonPropertyName("processing_notes")] IReadOnlyList<ReferenceMaterialProcessingNotePayload> ProcessingNotes);

public sealed record GetReferenceSourceSegmentDetailPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("segment_id")] string SegmentId);

public sealed record ReferenceSourceSegmentPreviewPayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("segment_id")] string SegmentId,
    [property: JsonPropertyName("segment_type")] string SegmentType,
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("chapter_title")] string ChapterTitle,
    [property: JsonPropertyName("segment_index")] int SegmentIndex,
    [property: JsonPropertyName("parent_segment_id")] string ParentSegmentId,
    [property: JsonPropertyName("start_offset")] int StartOffset,
    [property: JsonPropertyName("end_offset")] int EndOffset,
    [property: JsonPropertyName("text_preview")] string TextPreview,
    [property: JsonPropertyName("text_truncated")] bool TextTruncated,
    [property: JsonPropertyName("text_hash")] string TextHash);

public sealed record ReferenceSourceSegmentDetailPayload(
    [property: JsonPropertyName("source")] ReferenceMaterialSourceSummaryPayload Source,
    [property: JsonPropertyName("segment")] ReferenceSourceSegmentPreviewPayload Segment,
    [property: JsonPropertyName("processing_notes")] IReadOnlyList<ReferenceMaterialProcessingNotePayload> ProcessingNotes);

public sealed record GetReferenceSourceProcessingDetailPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId);

public sealed record ReferenceSourceProcessingStatusPayload(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("diagnostic")] string Diagnostic,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("source_segment_count")] int SourceSegmentCount,
    [property: JsonPropertyName("material_count")] int MaterialCount,
    [property: JsonPropertyName("slot_count")] int SlotCount,
    [property: JsonPropertyName("vector_count")] int VectorCount);

public sealed record ReferenceSourceProcessingEventPayload(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("source_segment_count")] int SourceSegmentCount,
    [property: JsonPropertyName("material_count")] int MaterialCount,
    [property: JsonPropertyName("slot_count")] int SlotCount,
    [property: JsonPropertyName("vector_count")] int VectorCount,
    [property: JsonPropertyName("affected_source_id")] string AffectedSourceId,
    [property: JsonPropertyName("affected_material_id")] string AffectedMaterialId,
    [property: JsonPropertyName("affected_segment_id")] string AffectedSegmentId,
    [property: JsonPropertyName("affected_slot_id")] string AffectedSlotId);

public sealed record ReferenceSourceProcessingAttemptPayload(
    [property: JsonPropertyName("attempt_id")] string AttemptId,
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("build_id")] string BuildId,
    [property: JsonPropertyName("build_version")] string BuildVersion,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("event_count")] int EventCount,
    [property: JsonPropertyName("source_segment_count")] int SourceSegmentCount,
    [property: JsonPropertyName("material_count")] int MaterialCount,
    [property: JsonPropertyName("slot_count")] int SlotCount,
    [property: JsonPropertyName("vector_count")] int VectorCount,
    [property: JsonPropertyName("recovered_from_attempt_id")] string RecoveredFromAttemptId,
    [property: JsonPropertyName("recovered_from_build_id")] string RecoveredFromBuildId,
    [property: JsonPropertyName("blocked_reason")] string BlockedReason);

public sealed record ReferenceSourceProcessingDetailPayload(
    [property: JsonPropertyName("source")] ReferenceMaterialSourceSummaryPayload Source,
    [property: JsonPropertyName("current_status")] ReferenceSourceProcessingStatusPayload? CurrentStatus,
    [property: JsonPropertyName("events")] IReadOnlyList<ReferenceSourceProcessingEventPayload> Events,
    [property: JsonPropertyName("retry_available")] bool RetryAvailable,
    [property: JsonPropertyName("rebuild_available")] bool RebuildAvailable,
    [property: JsonPropertyName("attempt_count")] int AttemptCount = 0,
    [property: JsonPropertyName("current_attempt")] ReferenceSourceProcessingAttemptPayload? CurrentAttempt = null,
    [property: JsonPropertyName("prior_attempts")] IReadOnlyList<ReferenceSourceProcessingAttemptPayload>? PriorAttempts = null,
    [property: JsonPropertyName("recovered_from_attempt_id")] string RecoveredFromAttemptId = "",
    [property: JsonPropertyName("recovered_from_build_id")] string RecoveredFromBuildId = "",
    [property: JsonPropertyName("blocked_reason")] string BlockedReason = "");

public sealed record UpdateReferenceMaterialTagsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("function_tag")] string? FunctionTag,
    [property: JsonPropertyName("emotion_tag")] string? EmotionTag,
    [property: JsonPropertyName("scene_tag")] string? SceneTag,
    [property: JsonPropertyName("pov_tag")] string? PovTag,
    [property: JsonPropertyName("technique_tag")] string? TechniqueTag,
    [property: JsonPropertyName("origin")] string? Origin,
    [property: JsonPropertyName("note")] string? Note);

public sealed record UpdateReferenceMaterialsTagsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("material_ids")] IReadOnlyList<string> MaterialIds,
    [property: JsonPropertyName("function_tag")] string? FunctionTag,
    [property: JsonPropertyName("emotion_tag")] string? EmotionTag,
    [property: JsonPropertyName("scene_tag")] string? SceneTag,
    [property: JsonPropertyName("pov_tag")] string? PovTag,
    [property: JsonPropertyName("technique_tag")] string? TechniqueTag,
    [property: JsonPropertyName("origin")] string? Origin,
    [property: JsonPropertyName("note")] string? Note);

public sealed record ReferenceMaterialQueryPayload(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("material_types")] IReadOnlyList<string> MaterialTypes,
    [property: JsonPropertyName("emotion_tags")] IReadOnlyList<string> EmotionTags,
    [property: JsonPropertyName("function_tags")] IReadOnlyList<string> FunctionTags,
    [property: JsonPropertyName("pov_tags")] IReadOnlyList<string> PovTags,
    [property: JsonPropertyName("technique_tags")] IReadOnlyList<string> TechniqueTags,
    [property: JsonPropertyName("max_results")] int MaxResults);

public sealed record SearchReferenceMaterialsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("material_types")] IReadOnlyList<string> MaterialTypes,
    [property: JsonPropertyName("emotion_tags")] IReadOnlyList<string> EmotionTags,
    [property: JsonPropertyName("function_tags")] IReadOnlyList<string> FunctionTags,
    [property: JsonPropertyName("pov_tags")] IReadOnlyList<string> PovTags,
    [property: JsonPropertyName("technique_tags")] IReadOnlyList<string> TechniqueTags,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("narrative_duties")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? NarrativeDuties = null,
    [property: JsonPropertyName("emotion_transitions")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? EmotionTransitions = null,
    [property: JsonPropertyName("prose_duties")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ProseDuties = null,
    [property: JsonPropertyName("archive_filter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ArchiveFilter = null,
    [property: JsonPropertyName("style_profile_ids")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<long>? StyleProfileIds = null,
    [property: JsonPropertyName("style_dimensions")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? StyleDimensions = null,
    [property: JsonPropertyName("imitation_intensity")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ImitationIntensity = null);

public sealed record ReferenceSlotValuePayload(
    [property: JsonPropertyName("slot_name")] string SlotName,
    [property: JsonPropertyName("value")] string Value);

public sealed record AdaptReferenceMaterialPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("slot_values")] IReadOnlyList<ReferenceSlotValuePayload> SlotValues,
    [property: JsonPropertyName("max_rewrite_level")] string MaxRewriteLevel,
    [property: JsonPropertyName("scene_facts")] IReadOnlyList<string> SceneFacts,
    [property: JsonPropertyName("style_context")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReferenceDraftStyleAttemptPayload? StyleContext = null);

public sealed record AdaptReferenceMaterialResultPayload(
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("rewrite_level")] string RewriteLevel,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("changed_slots")] IReadOnlyList<ReferenceSlotValuePayload> ChangedSlots,
    [property: JsonPropertyName("non_slot_edits")] IReadOnlyList<string> NonSlotEdits,
    [property: JsonPropertyName("audit")] ReferenceReuseAuditPayload Audit);

public sealed record AuditReferenceReusePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("candidate_text")] string CandidateText,
    [property: JsonPropertyName("max_rewrite_level")] string MaxRewriteLevel,
    [property: JsonPropertyName("scene_facts")] IReadOnlyList<string> SceneFacts);

public sealed record ReferenceReuseAuditPayload(
    [property: JsonPropertyName("audit_id")] string AuditId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("rewrite_level")] string RewriteLevel,
    [property: JsonPropertyName("provenance_errors")] IReadOnlyList<string> ProvenanceErrors,
    [property: JsonPropertyName("unsupported_fact_errors")] IReadOnlyList<string> UnsupportedFactErrors,
    [property: JsonPropertyName("ai_prose_risks")] IReadOnlyList<string> AiProseRisks,
    [property: JsonPropertyName("non_slot_edits")] IReadOnlyList<string> NonSlotEdits,
    [property: JsonPropertyName("required_fixes")] IReadOnlyList<string> RequiredFixes,
    [property: JsonPropertyName("audited_at")] DateTimeOffset AuditedAt);

public sealed record RecordReferenceUserFeedbackPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("feedback_tags")] IReadOnlyList<string> FeedbackTags,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("edited_text")] string EditedText,
    [property: JsonPropertyName("origin")] string Origin);

public sealed record GetReferenceUserFeedbackPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("limit")] int Limit);

public sealed record ReferenceUserFeedbackPayload(
    [property: JsonPropertyName("feedback_id")] string FeedbackId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("feedback_tags")] IReadOnlyList<string> FeedbackTags,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("edited_text_hash")] string EditedTextHash,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
