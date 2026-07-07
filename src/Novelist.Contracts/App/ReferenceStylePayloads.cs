using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceStyleProfileStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";

    public static IReadOnlyList<string> All { get; } = [Active, Archived];
}

public static class ReferenceStyleAnalyzerSources
{
    public const string DeterministicBaseline = "deterministic_baseline";
    public const string LlmAssisted = "llm_assisted";

    public static IReadOnlyList<string> All { get; } = [DeterministicBaseline, LlmAssisted];
}

public static class ReferenceStyleFeatureSchemaVersions
{
    public const string V1 = "style-profile-v1";
}

public static class ReferenceStyleAnalyzerVersions
{
    public const string DeterministicV1 = "reference-style-deterministic-v1";
    public const string LlmAssistedV1 = "reference-style-llm-assisted-v1";
}

public static class ReferenceStyleLlmAnalysisSchemaVersions
{
    public const string V1 = "reference-style-llm-analysis-v1";
}

public static class ReferenceStyleLlmAnalysisValidationStatuses
{
    public const string Passed = "passed";
    public const string Partial = "partial";
    public const string Rejected = "rejected";
    public const string InvalidJson = "invalid_json";
    public const string InvalidSchema = "invalid_schema";
}

public static class ReferenceStyleImitationIntensities
{
    public const string DiagnosticOnly = "diagnostic_only";
    public const string Loose = "loose";
    public const string Moderate = "moderate";
    public const string Strong = "strong";

    public static IReadOnlyList<string> All { get; } = [DiagnosticOnly, Loose, Moderate, Strong];
}

public sealed record BuildReferenceStyleProfilePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("allowed_license_statuses")] IReadOnlyList<string> AllowedLicenseStatuses,
    [property: JsonPropertyName("allowed_source_trust_levels")] IReadOnlyList<string> AllowedSourceTrustLevels);

public sealed record GetReferenceStyleProfilesPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("include_archived")] bool IncludeArchived = false);

public sealed record ReferenceStyleProfileSummaryPayload(
    [property: JsonPropertyName("profile_id")] long ProfileId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("analyzer_version")] string AnalyzerVersion,
    [property: JsonPropertyName("feature_schema_version")] string FeatureSchemaVersion,
    [property: JsonPropertyName("analyzer_source")] string AnalyzerSource,
    [property: JsonPropertyName("source_anchor_ids")] IReadOnlyList<long> SourceAnchorIds,
    [property: JsonPropertyName("source_hashes")] IReadOnlyList<string> SourceHashes,
    [property: JsonPropertyName("aggregate_confidence")] double AggregateConfidence,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("archived_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ArchivedAt);

public sealed record ReferenceStyleProfilePayload(
    [property: JsonPropertyName("profile_id")] long ProfileId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("analyzer_version")] string AnalyzerVersion,
    [property: JsonPropertyName("feature_schema_version")] string FeatureSchemaVersion,
    [property: JsonPropertyName("analyzer_source")] string AnalyzerSource,
    [property: JsonPropertyName("source_anchor_ids")] IReadOnlyList<long> SourceAnchorIds,
    [property: JsonPropertyName("source_hashes")] IReadOnlyList<string> SourceHashes,
    [property: JsonPropertyName("allowed_license_statuses")] IReadOnlyList<string> AllowedLicenseStatuses,
    [property: JsonPropertyName("allowed_source_trust_levels")] IReadOnlyList<string> AllowedSourceTrustLevels,
    [property: JsonPropertyName("aggregate_confidence")] double AggregateConfidence,
    [property: JsonPropertyName("features")] ReferenceStyleFeatureVectorPayload Features,
    [property: JsonPropertyName("evidence_spans")] IReadOnlyList<ReferenceStyleEvidenceSpanPayload> EvidenceSpans,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("archived_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ArchivedAt);

public sealed record ReferenceStyleFeatureVectorPayload(
    [property: JsonPropertyName("numeric_features")] IReadOnlyList<ReferenceStyleNumericFeaturePayload> NumericFeatures,
    [property: JsonPropertyName("distribution_features")] IReadOnlyList<ReferenceStyleDistributionFeaturePayload> DistributionFeatures,
    [property: JsonPropertyName("categorical_features")] IReadOnlyList<ReferenceStyleCategoricalFeaturePayload> CategoricalFeatures);

public sealed record ReferenceStyleNumericFeaturePayload(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds);

public sealed record ReferenceStyleDistributionFeaturePayload(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("buckets")] IReadOnlyList<ReferenceStyleDistributionBucketPayload> Buckets,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds);

public sealed record ReferenceStyleDistributionBucketPayload(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max,
    [property: JsonPropertyName("weight")] double Weight);

public sealed record ReferenceStyleCategoricalFeaturePayload(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("weight")] double Weight,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds);

public sealed record ReferenceStyleEvidenceSpanPayload(
    [property: JsonPropertyName("evidence_id")] string EvidenceId,
    [property: JsonPropertyName("profile_id")] long ProfileId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_segment_id")] string SourceSegmentId,
    [property: JsonPropertyName("material_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? MaterialId,
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("start_offset")] int StartOffset,
    [property: JsonPropertyName("end_offset")] int EndOffset,
    [property: JsonPropertyName("text_hash")] string TextHash,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("analyzer_source")] string AnalyzerSource);

public sealed record ReferenceStyleLlmAnalysisRequestPayload(
    [property: JsonPropertyName("profile_id")] long ProfileId,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("requested_feature_keys")] IReadOnlyList<string> RequestedFeatureKeys,
    [property: JsonPropertyName("windows")] IReadOnlyList<ReferenceStyleAnalysisWindowPayload> Windows);

public sealed record ReferenceStyleAnalysisWindowPayload(
    [property: JsonPropertyName("window_id")] string WindowId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_segment_id")] string SourceSegmentId,
    [property: JsonPropertyName("material_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? MaterialId,
    [property: JsonPropertyName("start_offset")] int StartOffset,
    [property: JsonPropertyName("end_offset")] int EndOffset,
    [property: JsonPropertyName("text_hash")] string TextHash,
    [property: JsonPropertyName("text")] string Text);

public sealed record ReferenceStyleLlmAnalysisRejectedLabelPayload(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record ReferenceStyleLlmAnalysisValidationResultPayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("evidence_spans")] IReadOnlyList<ReferenceStyleEvidenceSpanPayload> EvidenceSpans,
    [property: JsonPropertyName("rejected_labels")] IReadOnlyList<ReferenceStyleLlmAnalysisRejectedLabelPayload> RejectedLabels,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);
