using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceStyleProfileStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";

    public static IReadOnlyList<string> All { get; } = [Active, Archived];
}

public static class ReferenceStyleProfileBuildStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Running, Completed, Failed, Cancelled];
}

public static class ReferenceStyleProfileBuildStages
{
    public const string Queued = "queued";
    public const string Validating = "validating";
    public const string ReadingSources = "reading_sources";
    public const string ReadingMaterials = "reading_materials";
    public const string PersistingProfile = "persisting_profile";
    public const string DeterministicBaseline = "deterministic_baseline";
    public const string LlmAnalysis = "llm_analysis";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
    [
        Queued,
        Validating,
        ReadingSources,
        ReadingMaterials,
        PersistingProfile,
        DeterministicBaseline,
        LlmAnalysis,
        Completed,
        Failed,
        Cancelled
    ];
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

public static class ReferenceStyleTaxonomyVersions
{
    public const string V1 = "reference-style-taxonomy-v1";
}

public static class ReferenceStyleTaxonomyCategories
{
    public const string Narration = "narration";
    public const string ProseRhythm = "prose_rhythm";
    public const string DialogueAndSubtext = "dialogue_and_subtext";
    public const string ImageryAndSensation = "imagery_and_sensation";
    public const string TensionAndStructure = "tension_and_structure";
    public const string WebFictionMechanics = "web_fiction_mechanics";

    public static IReadOnlyList<string> All { get; } =
    [
        Narration,
        ProseRhythm,
        DialogueAndSubtext,
        ImageryAndSensation,
        TensionAndStructure,
        WebFictionMechanics
    ];
}

public sealed record ReferenceStyleTaxonomyFeaturePayload(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("compatible_beat_duties")] IReadOnlyList<string> CompatibleBeatDuties);

public static class ReferenceStyleTaxonomy
{
    public const string Version = ReferenceStyleTaxonomyVersions.V1;

    public static IReadOnlyList<ReferenceStyleTaxonomyFeaturePayload> Features { get; } =
    [
        Feature(
            "narration_distance",
            ReferenceStyleTaxonomyCategories.Narration,
            "How close the narration stays to viewpoint perception and interiority.",
            ["close_limited", "mid_summary", "distant_omniscient"],
            ["interiority", "pov_control", "narration_distance"]),
        Feature(
            "pov_control",
            ReferenceStyleTaxonomyCategories.Narration,
            "How consistently viewpoint knowledge, perception, and camera access are controlled.",
            ["tight_internal", "limited_external", "head_hopping_risk"],
            ["pov_control", "viewpoint_boundary", "interiority"]),
        Feature(
            "rhythm",
            ReferenceStyleTaxonomyCategories.ProseRhythm,
            "The local tempo and pressure created by sentence movement.",
            ["staccato", "balanced", "rolling_periodic"],
            ["rhythm", "pacing", "pressure"]),
        Feature(
            "sentence_shape",
            ReferenceStyleTaxonomyCategories.ProseRhythm,
            "The dominant clause and sentence construction pattern.",
            ["short_direct", "layered_clause", "fragment_pressure"],
            ["sentence_shape", "rhythm", "anti_screenplay"]),
        Feature(
            "paragraph_cadence",
            ReferenceStyleTaxonomyCategories.ProseRhythm,
            "How paragraphs bundle action, perception, reflection, and turn endings.",
            ["single_beat", "braided_motion_reflection", "long_wave"],
            ["paragraph_cadence", "rhythm", "scene_flow"]),
        Feature(
            "dialogue_mechanics",
            ReferenceStyleTaxonomyCategories.DialogueAndSubtext,
            "How dialogue turns, tags, interruptions, and beats carry scene pressure.",
            ["short_turns", "interrupted_exchange", "subtext_reply"],
            ["dialogue", "subtext", "external_evidence"]),
        Feature(
            "subtext",
            ReferenceStyleTaxonomyCategories.DialogueAndSubtext,
            "How the prose implies unstated motive, conflict, and withheld response.",
            ["withheld_answer", "displaced_topic", "implication_gap"],
            ["subtext", "dialogue", "emotion_pressure"]),
        Feature(
            "externalized_emotion",
            ReferenceStyleTaxonomyCategories.DialogueAndSubtext,
            "How emotion is shown through visible behavior instead of explanation.",
            ["body_afterbeat", "object_handling", "silence_response"],
            ["external_evidence", "physical_afterbeat", "interiority"]),
        Feature(
            "sensory_image",
            ReferenceStyleTaxonomyCategories.ImageryAndSensation,
            "Which concrete sensory channel carries place, pressure, and mood.",
            ["tactile_grounding", "auditory_pressure", "visual_anchor"],
            ["sensory_anchor", "environment", "source_backed_detail"]),
        Feature(
            "metaphor_system",
            ReferenceStyleTaxonomyCategories.ImageryAndSensation,
            "How figurative language is grounded, repeated, or abstracted.",
            ["concrete_vehicle", "recurring_symbol", "abstract_risk"],
            ["image_system", "sensory_anchor", "anti_ai_prose"]),
        Feature(
            "image_system",
            ReferenceStyleTaxonomyCategories.ImageryAndSensation,
            "How repeated images or motifs organize scene tone and promise.",
            ["weather_motif", "object_motif", "light_shadow_motif"],
            ["image_system", "sensory_anchor", "theme_motif"]),
        Feature(
            "tension_pressure",
            ReferenceStyleTaxonomyCategories.TensionAndStructure,
            "How conflict pressure narrows choices and escalates reader attention.",
            ["narrowing_options", "ticking_clock", "interpersonal_threat"],
            ["pressure", "conflict", "escalation"]),
        Feature(
            "hook_pattern",
            ReferenceStyleTaxonomyCategories.TensionAndStructure,
            "How a local passage ending opens a question, reversal, or threat.",
            ["question_tail", "reversal_tail", "threat_arrival"],
            ["hook", "reader_question", "pressure"]),
        Feature(
            "payoff_pattern",
            ReferenceStyleTaxonomyCategories.TensionAndStructure,
            "How the prose resolves a planted question or emotional setup.",
            ["answer_reveal", "emotional_release", "promise_fulfilled"],
            ["payoff", "reveal", "emotion_turn"]),
        Feature(
            "transition_pattern",
            ReferenceStyleTaxonomyCategories.TensionAndStructure,
            "How the prose moves between beats, time, scene, and causality.",
            ["time_jump", "causal_bridge", "scene_cut"],
            ["transition", "causality", "scene_flow"]),
        Feature(
            "exposition_handling",
            ReferenceStyleTaxonomyCategories.TensionAndStructure,
            "How necessary information is embedded, delayed, or over-explained.",
            ["embedded_in_action", "dialogue_exposition", "infodump_risk"],
            ["exposition", "source_backed_detail", "anti_ai_prose"]),
        Feature(
            "action_clarity",
            ReferenceStyleTaxonomyCategories.TensionAndStructure,
            "How clearly physical movement, blocking, and sequence are legible.",
            ["clean_blocking", "ambiguous_blocking", "sequential_motion"],
            ["action", "blocking", "external_evidence"]),
        Feature(
            "anti_screenplay_prose",
            ReferenceStyleTaxonomyCategories.ProseRhythm,
            "Whether action is novelistic prose instead of camera-direction staging.",
            ["interiorized_action", "camera_direction_risk", "prose_afterbeat"],
            ["anti_screenplay", "interiority", "physical_afterbeat"]),
        Feature(
            "chapter_hook",
            ReferenceStyleTaxonomyCategories.WebFictionMechanics,
            "How a chapter ending renews reader desire to continue.",
            ["cliffhanger_question", "new_threat", "promise_open"],
            ["hook", "chapter_hook", "reader_promise"]),
        Feature(
            "escalation_beat",
            ReferenceStyleTaxonomyCategories.WebFictionMechanics,
            "How a serial beat increases cost, complication, or opposition.",
            ["complication", "cost_increase", "pressure_turn"],
            ["escalation", "pressure", "conflict"]),
        Feature(
            "payoff_beat",
            ReferenceStyleTaxonomyCategories.WebFictionMechanics,
            "How a beat delivers a concrete reveal, release, or tactical gain.",
            ["reveal_payoff", "emotional_payoff", "tactical_payoff"],
            ["payoff", "reveal", "pleasure_point"]),
        Feature(
            "compression_expansion",
            ReferenceStyleTaxonomyCategories.WebFictionMechanics,
            "How the prose compresses connective tissue or expands high-value moments.",
            ["compressed_summary", "expanded_moment", "balanced_scene"],
            ["pacing", "rhythm", "scene_focus"]),
        Feature(
            "pleasure_point_delivery",
            ReferenceStyleTaxonomyCategories.WebFictionMechanics,
            "How web-fiction satisfaction points are staged and released.",
            ["power_reversal", "competence_display", "emotional_catharsis"],
            ["pleasure_point", "payoff", "reader_promise"]),
        Feature(
            "cliffhanger_type",
            ReferenceStyleTaxonomyCategories.WebFictionMechanics,
            "The specific unresolved tension used to carry the reader forward.",
            ["danger_cut", "secret_reveal", "choice_suspended"],
            ["hook", "cliffhanger", "reader_question"]),
        Feature(
            "information_withholding",
            ReferenceStyleTaxonomyCategories.WebFictionMechanics,
            "How the text withholds information fairly without confusing causality.",
            ["fair_gap", "delayed_identity", "hidden_motive"],
            ["reader_question", "mystery", "causality"]),
        Feature(
            "reader_promise_tracking",
            ReferenceStyleTaxonomyCategories.WebFictionMechanics,
            "How the passage plants, renews, or pays off serial reader promises.",
            ["promise_planted", "promise_renewed", "promise_paid_off"],
            ["reader_promise", "hook", "payoff"])
    ];

    public static IReadOnlyList<string> FeatureKeys { get; } = Features
        .Select(feature => feature.FeatureKey)
        .ToArray();

    private static readonly IReadOnlyDictionary<string, ReferenceStyleTaxonomyFeaturePayload> FeaturesByKey = Features
        .ToDictionary(feature => feature.FeatureKey, StringComparer.Ordinal);

    public static bool IsSupportedFeatureKey(string featureKey)
    {
        return FeaturesByKey.ContainsKey(featureKey);
    }

    public static bool IsSupportedLabel(string featureKey, string label)
    {
        return FeaturesByKey.TryGetValue(featureKey, out var feature) &&
            feature.Labels.Contains(label, StringComparer.Ordinal);
    }

    public static ReferenceStyleTaxonomyFeaturePayload GetFeature(string featureKey)
    {
        return FeaturesByKey.TryGetValue(featureKey, out var feature)
            ? feature
            : throw new ArgumentException($"Unsupported style taxonomy feature key: {featureKey}.", nameof(featureKey));
    }

    private static ReferenceStyleTaxonomyFeaturePayload Feature(
        string featureKey,
        string category,
        string description,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> compatibleBeatDuties)
    {
        return new ReferenceStyleTaxonomyFeaturePayload(featureKey, category, description, labels, compatibleBeatDuties);
    }
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
    [property: JsonPropertyName("allowed_source_trust_levels")] IReadOnlyList<string> AllowedSourceTrustLevels,
    [property: JsonPropertyName("build_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? BuildId = null);

public sealed record GetReferenceStyleProfileBuildStatusPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("build_id")] string BuildId);

public sealed record CancelReferenceStyleProfileBuildPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("build_id")] string BuildId);

public sealed record GetReferenceStyleProfilesPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("include_archived")] bool IncludeArchived = false);

public sealed record ArchiveReferenceStyleProfilePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("profile_id")] long ProfileId);

public sealed record RestoreReferenceStyleProfilePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("profile_id")] long ProfileId);

public sealed record CompareReferenceStyleProfilesPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("left_profile_id")] long LeftProfileId,
    [property: JsonPropertyName("right_profile_id")] long RightProfileId);

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

public sealed record ReferenceStyleProfileBuildStatusPayload(
    [property: JsonPropertyName("build_id")] string BuildId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("profile_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ProfileId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("progress_completed")] int ProgressCompleted,
    [property: JsonPropertyName("progress_total")] int ProgressTotal,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("source_hashes")] IReadOnlyList<string> SourceHashes,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode,
    [property: JsonPropertyName("error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("completed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("cancelled_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CancelledAt);

public sealed record ReferenceStyleProfileComparisonPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("left_profile")] ReferenceStyleProfileSummaryPayload LeftProfile,
    [property: JsonPropertyName("right_profile")] ReferenceStyleProfileSummaryPayload RightProfile,
    [property: JsonPropertyName("numeric_differences")] IReadOnlyList<ReferenceStyleNumericFeatureDifferencePayload> NumericDifferences,
    [property: JsonPropertyName("distribution_differences")] IReadOnlyList<ReferenceStyleDistributionFeatureDifferencePayload> DistributionDifferences,
    [property: JsonPropertyName("categorical_differences")] IReadOnlyList<ReferenceStyleCategoricalFeatureDifferencePayload> CategoricalDifferences,
    [property: JsonPropertyName("compared_at")] DateTimeOffset ComparedAt);

public sealed record ReferenceStyleNumericFeatureDifferencePayload(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("left_value")] double? LeftValue,
    [property: JsonPropertyName("right_value")] double? RightValue,
    [property: JsonPropertyName("absolute_delta")] double? AbsoluteDelta,
    [property: JsonPropertyName("relative_delta")] double? RelativeDelta,
    [property: JsonPropertyName("left_confidence")] double? LeftConfidence,
    [property: JsonPropertyName("right_confidence")] double? RightConfidence);

public sealed record ReferenceStyleDistributionFeatureDifferencePayload(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("buckets")] IReadOnlyList<ReferenceStyleDistributionBucketDifferencePayload> Buckets,
    [property: JsonPropertyName("left_confidence")] double? LeftConfidence,
    [property: JsonPropertyName("right_confidence")] double? RightConfidence);

public sealed record ReferenceStyleDistributionBucketDifferencePayload(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("left_min")] double? LeftMin,
    [property: JsonPropertyName("left_max")] double? LeftMax,
    [property: JsonPropertyName("left_weight")] double? LeftWeight,
    [property: JsonPropertyName("right_min")] double? RightMin,
    [property: JsonPropertyName("right_max")] double? RightMax,
    [property: JsonPropertyName("right_weight")] double? RightWeight,
    [property: JsonPropertyName("absolute_delta")] double? AbsoluteDelta);

public sealed record ReferenceStyleCategoricalFeatureDifferencePayload(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("left_weight")] double? LeftWeight,
    [property: JsonPropertyName("right_weight")] double? RightWeight,
    [property: JsonPropertyName("absolute_delta")] double? AbsoluteDelta,
    [property: JsonPropertyName("left_confidence")] double? LeftConfidence,
    [property: JsonPropertyName("right_confidence")] double? RightConfidence);

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
