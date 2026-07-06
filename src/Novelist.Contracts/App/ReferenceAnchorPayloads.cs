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

    public static IReadOnlyList<string> All { get; } = [Chapter, Paragraph, Sentence, Passage];
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
    IReadOnlyList<string>? EmotionTransitions = null);

public sealed record ReferenceSlotValuePayload(
    [property: JsonPropertyName("slot_name")] string SlotName,
    [property: JsonPropertyName("value")] string Value);

public sealed record AdaptReferenceMaterialPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("slot_values")] IReadOnlyList<ReferenceSlotValuePayload> SlotValues,
    [property: JsonPropertyName("max_rewrite_level")] string MaxRewriteLevel,
    [property: JsonPropertyName("scene_facts")] IReadOnlyList<string> SceneFacts);

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
