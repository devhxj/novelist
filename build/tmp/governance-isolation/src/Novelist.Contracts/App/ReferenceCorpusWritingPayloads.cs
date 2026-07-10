using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record GenerateReferenceCorpusInsertionDraftPayload(
    [property: JsonPropertyName("natural_language_goal")] string NaturalLanguageGoal,
    [property: JsonPropertyName("chapter_context")] CurrentChapterContextPayload ChapterContext,
    [property: JsonPropertyName("scope")] ReferenceCorpusScopePayload Scope,
    [property: JsonPropertyName("slot_values")] IReadOnlyDictionary<string, string> SlotValues,
    [property: JsonPropertyName("selected_blueprint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReferenceCorpusInsertionBlueprintPayload? SelectedBlueprint = null);

public sealed record GenerateReferenceCorpusInsertionDraftCandidatesPayload(
    [property: JsonPropertyName("natural_language_goal")] string NaturalLanguageGoal,
    [property: JsonPropertyName("chapter_context")] CurrentChapterContextPayload ChapterContext,
    [property: JsonPropertyName("scope")] ReferenceCorpusScopePayload Scope,
    [property: JsonPropertyName("slot_values")] IReadOnlyDictionary<string, string> SlotValues,
    [property: JsonPropertyName("selected_blueprint")] ReferenceCorpusInsertionBlueprintPayload SelectedBlueprint,
    [property: JsonPropertyName("requested_count")] int RequestedCount,
    [property: JsonPropertyName("slot_value_variants")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReferenceCorpusDraftSlotValueVariantPayload>? SlotValueVariants = null);

public sealed record ReferenceCorpusDraftSlotValueVariantPayload(
    [property: JsonPropertyName("variant_id")] string VariantId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("slot_values")] IReadOnlyDictionary<string, string> SlotValues);

public sealed record GenerateReferenceCorpusBlueprintCandidatesPayload(
    [property: JsonPropertyName("natural_language_goal")] string NaturalLanguageGoal,
    [property: JsonPropertyName("chapter_context")] CurrentChapterContextPayload ChapterContext,
    [property: JsonPropertyName("scope")] ReferenceCorpusScopePayload Scope,
    [property: JsonPropertyName("requested_count")] int RequestedCount,
    [property: JsonPropertyName("feedback")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReferenceCorpusBlueprintFeedbackPayload? Feedback = null);

public sealed record ReferenceCorpusBlueprintFeedbackPayload(
    [property: JsonPropertyName("rejected_blueprint_ids")] IReadOnlyList<string> RejectedBlueprintIds,
    [property: JsonPropertyName("rejected_node_ids")] IReadOnlyList<string> RejectedNodeIds,
    [property: JsonPropertyName("avoid_library_ids")] IReadOnlyList<string> AvoidLibraryIds,
    [property: JsonPropertyName("avoid_anchor_ids")] IReadOnlyList<long> AvoidAnchorIds,
    [property: JsonPropertyName("problem_tags")] IReadOnlyList<string> ProblemTags,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record ReferenceCorpusInsertionBlueprintPayload(
    [property: JsonPropertyName("blueprint_id")] string BlueprintId,
    [property: JsonPropertyName("query_context_hash")] string QueryContextHash,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("beats")] IReadOnlyList<ReferenceCorpusInsertionBlueprintBeatPayload> Beats);

public sealed record ReferenceCorpusInsertionBlueprintBeatPayload(
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("beat_index")] int BeatIndex,
    [property: JsonPropertyName("role_in_beat")] string RoleInBeat,
    [property: JsonPropertyName("narrative_function")] string NarrativeFunction,
    [property: JsonPropertyName("node_ids")] IReadOnlyList<string> NodeIds);

public sealed record ReferenceCorpusBlueprintSourcePayload(
    [property: JsonPropertyName("library_id")] string LibraryId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("node_count")] int NodeCount);

public sealed record ReferenceCorpusBlueprintGapPositionPayload(
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("beat_index")] int BeatIndex,
    [property: JsonPropertyName("role_in_beat")] string RoleInBeat,
    [property: JsonPropertyName("narrative_function")] string NarrativeFunction,
    [property: JsonPropertyName("node_ids")] IReadOnlyList<string> NodeIds,
    [property: JsonPropertyName("covered_dimensions")] IReadOnlyList<string> CoveredDimensions,
    [property: JsonPropertyName("missing_dimensions")] IReadOnlyList<string> MissingDimensions,
    [property: JsonPropertyName("gap_reasons")] IReadOnlyList<string> GapReasons);

public sealed record ReferenceCorpusBlueprintCandidatePayload
{
    public ReferenceCorpusBlueprintCandidatePayload(
        ReferenceCorpusInsertionBlueprintPayload Blueprint,
        IReadOnlyList<ReferenceCorpusBlueprintSourcePayload> SourceDistribution,
        double CoverageScore,
        IReadOnlyList<string> GapReasons,
        string FeedbackReason)
        : this(Blueprint, SourceDistribution, CoverageScore, GapReasons, FeedbackReason, [])
    {
    }

    [JsonConstructor]
    public ReferenceCorpusBlueprintCandidatePayload(
        ReferenceCorpusInsertionBlueprintPayload Blueprint,
        IReadOnlyList<ReferenceCorpusBlueprintSourcePayload> SourceDistribution,
        double CoverageScore,
        IReadOnlyList<string> GapReasons,
        string FeedbackReason,
        IReadOnlyList<ReferenceCorpusBlueprintGapPositionPayload>? GapPositions)
    {
        this.Blueprint = Blueprint;
        this.SourceDistribution = SourceDistribution ?? [];
        this.CoverageScore = CoverageScore;
        this.GapReasons = GapReasons ?? [];
        this.FeedbackReason = FeedbackReason;
        this.GapPositions = GapPositions ?? [];
    }

    [JsonPropertyName("blueprint")]
    public ReferenceCorpusInsertionBlueprintPayload Blueprint { get; init; }

    [JsonPropertyName("source_distribution")]
    public IReadOnlyList<ReferenceCorpusBlueprintSourcePayload> SourceDistribution { get; init; }

    [JsonPropertyName("coverage_score")]
    public double CoverageScore { get; init; }

    [JsonPropertyName("gap_reasons")]
    public IReadOnlyList<string> GapReasons { get; init; }

    [JsonPropertyName("feedback_reason")]
    public string FeedbackReason { get; init; }

    [JsonPropertyName("gap_positions")]
    public IReadOnlyList<ReferenceCorpusBlueprintGapPositionPayload> GapPositions { get; init; }
}

public sealed record ReferenceCorpusBlueprintCandidatesPayload(
    [property: JsonPropertyName("query_context")] ReferenceCorpusQueryContextPayload QueryContext,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload> Candidates,
    [property: JsonPropertyName("feedback_applied")] bool FeedbackApplied,
    [property: JsonPropertyName("feedback_summary")] string FeedbackSummary);

public sealed record ReferenceCorpusSlotReplacementPayload(
    [property: JsonPropertyName("slot_name")] string SlotName,
    [property: JsonPropertyName("source_value")] string SourceValue,
    [property: JsonPropertyName("replacement_value")] string ReplacementValue,
    [property: JsonPropertyName("source_start")] int SourceStart,
    [property: JsonPropertyName("source_end")] int SourceEnd,
    [property: JsonPropertyName("output_start")] int OutputStart,
    [property: JsonPropertyName("output_end")] int OutputEnd);

public sealed record ReferenceCorpusPreservedSpanPayload(
    [property: JsonPropertyName("span_id")] string SpanId,
    [property: JsonPropertyName("source_start")] int SourceStart,
    [property: JsonPropertyName("source_end")] int SourceEnd,
    [property: JsonPropertyName("output_start")] int OutputStart,
    [property: JsonPropertyName("output_end")] int OutputEnd,
    [property: JsonPropertyName("source_text_hash")] string SourceTextHash,
    [property: JsonPropertyName("output_text_hash")] string OutputTextHash,
    [property: JsonPropertyName("matches")] bool Matches);

public sealed record ReferenceCorpusLockedSpanPayload(
    [property: JsonPropertyName("span_id")] string SpanId,
    [property: JsonPropertyName("source_start")] int SourceStart,
    [property: JsonPropertyName("source_end")] int SourceEnd,
    [property: JsonPropertyName("output_start")] int OutputStart,
    [property: JsonPropertyName("output_end")] int OutputEnd,
    [property: JsonPropertyName("source_text_hash")] string SourceTextHash,
    [property: JsonPropertyName("output_text_hash")] string OutputTextHash,
    [property: JsonPropertyName("matches")] bool Matches,
    [property: JsonPropertyName("reason")] string Reason);

public static class ReferenceCorpusTransitionDecisions
{
    public const string DirectJoin = "direct_join";
    public const string InsertTransition = "insert_transition";
    public const string ReplacePiece = "replace_piece";
}

public sealed record ReferenceCorpusTransitionPayload(
    [property: JsonPropertyName("transition_id")] string TransitionId,
    [property: JsonPropertyName("gap_id")] string GapId,
    [property: JsonPropertyName("after_piece_id")] string AfterPieceId,
    [property: JsonPropertyName("before_piece_id")] string BeforePieceId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("text_hash")] string TextHash,
    [property: JsonPropertyName("output_start")] int OutputStart,
    [property: JsonPropertyName("output_end")] int OutputEnd,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("replacement_piece_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplacementPieceId = null,
    [property: JsonPropertyName("replacement_node_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplacementNodeId = null);

public sealed record ReferenceCorpusInsertionPiecePayload(
    [property: JsonPropertyName("piece_id")] string PieceId,
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("library_id")] string LibraryId,
    [property: JsonPropertyName("text_hash")] string SourceTextHash,
    [property: JsonPropertyName("reuse_policy")] string ReusePolicy,
    [property: JsonPropertyName("license_state")] string LicenseState,
    [property: JsonPropertyName("output_text")] string OutputText,
    [property: JsonPropertyName("preserved_text_hash")] string PreservedTextHash,
    [property: JsonPropertyName("preserved_hash_matches")] bool PreservedHashMatches,
    [property: JsonPropertyName("preserved_spans")] IReadOnlyList<ReferenceCorpusPreservedSpanPayload> PreservedSpans,
    [property: JsonPropertyName("locked_spans")] IReadOnlyList<ReferenceCorpusLockedSpanPayload> LockedSpans,
    [property: JsonPropertyName("slot_replacements")] IReadOnlyList<ReferenceCorpusSlotReplacementPayload> SlotReplacements);

public sealed record ReferenceCorpusInsertionGateViolationPayload(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("actual")] double Actual,
    [property: JsonPropertyName("threshold")] double Threshold);

public sealed record ReferenceCorpusInsertionGatePiecePayload(
    [property: JsonPropertyName("piece_id")] string PieceId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("should_block")] bool ShouldBlock,
    [property: JsonPropertyName("four_gram_containment_ratio")] double FourGramContainmentRatio,
    [property: JsonPropertyName("longest_common_substring_ratio")] double LongestCommonSubstringRatio,
    [property: JsonPropertyName("violations")] IReadOnlyList<ReferenceCorpusInsertionGateViolationPayload> Violations);

public sealed record ReferenceCorpusInsertionGatePayload(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("pieces")] IReadOnlyList<ReferenceCorpusInsertionGatePiecePayload> Pieces);

public sealed record ReferenceCorpusDraftAuditViolationPayload(
    [property: JsonPropertyName("violation_id")] string ViolationId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("piece_id")] string PieceId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("span_id")] string? SpanId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("transition_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransitionId = null);

public sealed record ReferenceCorpusDraftAuditPiecePayload(
    [property: JsonPropertyName("piece_id")] string PieceId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("preserved_span_count")] int PreservedSpanCount,
    [property: JsonPropertyName("mismatched_span_count")] int MismatchedSpanCount,
    [property: JsonPropertyName("violations")] IReadOnlyList<ReferenceCorpusDraftAuditViolationPayload> Violations);

public sealed record ReferenceCorpusDraftAuditTransitionPayload(
    [property: JsonPropertyName("transition_id")] string TransitionId,
    [property: JsonPropertyName("gap_id")] string GapId,
    [property: JsonPropertyName("after_piece_id")] string AfterPieceId,
    [property: JsonPropertyName("before_piece_id")] string BeforePieceId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("violations")] IReadOnlyList<ReferenceCorpusDraftAuditViolationPayload> Violations);

public sealed record ReferenceCorpusDraftAuditPayload(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("pieces")] IReadOnlyList<ReferenceCorpusDraftAuditPiecePayload> Pieces,
    [property: JsonPropertyName("transitions")] IReadOnlyList<ReferenceCorpusDraftAuditTransitionPayload> Transitions);

public sealed record ReferenceCorpusInsertionDraftPayload(
    [property: JsonPropertyName("query_context")] ReferenceCorpusQueryContextPayload QueryContext,
    [property: JsonPropertyName("blueprint")] ReferenceCorpusInsertionBlueprintPayload Blueprint,
    [property: JsonPropertyName("pieces")] IReadOnlyList<ReferenceCorpusInsertionPiecePayload> Pieces,
    [property: JsonPropertyName("slot_replacements")] IReadOnlyList<ReferenceCorpusSlotReplacementPayload> SlotReplacements,
    [property: JsonPropertyName("transitions")] IReadOnlyList<ReferenceCorpusTransitionPayload> Transitions,
    [property: JsonPropertyName("assembled_text")] string AssembledText,
    [property: JsonPropertyName("chapter_text_after_insertion")] string ChapterTextAfterInsertion,
    [property: JsonPropertyName("ready_for_insertion")] bool ReadyForInsertion,
    [property: JsonPropertyName("gate")] ReferenceCorpusInsertionGatePayload Gate,
    [property: JsonPropertyName("audit")] ReferenceCorpusDraftAuditPayload Audit);

public static class ReferenceCorpusDraftCandidateNextActions
{
    public const string RegenerateBlueprint = "regenerate_blueprint";
}

public sealed record ReferenceCorpusDraftCandidateNextActionPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("transition_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransitionId,
    [property: JsonPropertyName("rejected_piece_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedPieceId,
    [property: JsonPropertyName("rejected_node_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedNodeId,
    [property: JsonPropertyName("replacement_node_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplacementNodeId,
    [property: JsonPropertyName("feedback")] ReferenceCorpusBlueprintFeedbackPayload Feedback);

public sealed record ReferenceCorpusInsertionDraftCandidatePayload(
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("draft")] ReferenceCorpusInsertionDraftPayload Draft,
    [property: JsonPropertyName("next_action")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReferenceCorpusDraftCandidateNextActionPayload? NextAction = null);

public sealed record ReferenceCorpusInsertionDraftCandidatesPayload(
    [property: JsonPropertyName("query_context")] ReferenceCorpusQueryContextPayload QueryContext,
    [property: JsonPropertyName("selected_blueprint")] ReferenceCorpusInsertionBlueprintPayload SelectedBlueprint,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ReferenceCorpusInsertionDraftCandidatePayload> Candidates);
