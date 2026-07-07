using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceBlueprintStates
{
    public const string Draft = "draft";
    public const string Normalized = "normalized";
    public const string ReviewFailed = "review_failed";
    public const string ReviewPassed = "review_passed";
    public const string Approved = "approved";
    public const string Stale = "stale";
    public const string MaterialBound = "material_bound";
    public const string UsedForCandidate = "used_for_candidate";
    public const string Superseded = "superseded";

    public static IReadOnlyList<string> All { get; } =
    [
        Draft,
        Normalized,
        ReviewFailed,
        ReviewPassed,
        Approved,
        Stale,
        MaterialBound,
        UsedForCandidate,
        Superseded
    ];
}

public static class ReferenceBlueprintBeatTypes
{
    public const string Action = "action";
    public const string Reaction = "reaction";
    public const string Interiority = "interiority";
    public const string Environment = "environment";
    public const string Transition = "transition";
    public const string InformationReveal = "information_reveal";
    public const string Hook = "hook";
    public const string DialogueExchange = "dialogue_exchange";

    public static IReadOnlyList<string> All { get; } =
    [
        Action,
        Reaction,
        Interiority,
        Environment,
        Transition,
        InformationReveal,
        Hook,
        DialogueExchange
    ];
}

public static class ReferenceBlueprintReviewStatuses
{
    public const string Passed = "passed";
    public const string Failed = "failed";

    public static IReadOnlyList<string> All { get; } = [Passed, Failed];
}

public static class ReferenceOrchestrationRunStatuses
{
    public const string Running = "running";
    public const string WaitingForUser = "waiting_for_user";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
    [
        Running,
        WaitingForUser,
        Completed,
        Failed,
        Cancelled
    ];
}

public static class ReferenceOrchestrationStages
{
    public const string SourceConfirmation = "source_confirmation";
    public const string BlueprintGeneration = "blueprint_generation";
    public const string BlueprintReview = "blueprint_review";
    public const string BlueprintApproval = "blueprint_approval";
    public const string MaterialBinding = "material_binding";
    public const string DraftGeneration = "draft_generation";
    public const string DraftAudit = "draft_audit";
    public const string FinalInsertion = "final_insertion";

    public static IReadOnlyList<string> All { get; } =
    [
        SourceConfirmation,
        BlueprintGeneration,
        BlueprintReview,
        BlueprintApproval,
        MaterialBinding,
        DraftGeneration,
        DraftAudit,
        FinalInsertion
    ];
}

public static class ReferenceOrchestrationDecisionTypes
{
    public const string ConfirmSourceAndFacts = "confirm_source_and_facts";
    public const string ApplyBlueprintRevision = "apply_blueprint_revision";
    public const string ApproveBlueprint = "approve_blueprint";
    public const string ResolveHighRiskStop = "resolve_high_risk_stop";
    public const string ApproveFinalInsertion = "approve_final_insertion";

    public static IReadOnlyList<string> All { get; } =
    [
        ConfirmSourceAndFacts,
        ApplyBlueprintRevision,
        ApproveBlueprint,
        ResolveHighRiskStop,
        ApproveFinalInsertion
    ];
}

public static class ReferenceOrchestrationStopReasons
{
    public const string SourceConfirmationRequired = "source_confirmation_required";
    public const string FactBoundaryApprovalRequired = "fact_boundary_approval_required";
    public const string BlueprintApprovalRequired = "blueprint_approval_required";
    public const string BlueprintRevisionApprovalRequired = "blueprint_revision_approval_required";
    public const string HighRiskGateBlocked = "high_risk_gate_blocked";
    public const string DraftAuditFailed = "draft_audit_failed";
    public const string FinalInsertionRequired = "final_insertion_required";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
    [
        SourceConfirmationRequired,
        FactBoundaryApprovalRequired,
        BlueprintApprovalRequired,
        BlueprintRevisionApprovalRequired,
        HighRiskGateBlocked,
        DraftAuditFailed,
        FinalInsertionRequired,
        Cancelled
    ];
}

public sealed record GenerateReferenceChapterBlueprintPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("chapter_goal")] string? ChapterGoal,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("known_facts")] IReadOnlyList<string> KnownFacts,
    [property: JsonPropertyName("forbidden_facts")] IReadOnlyList<string> ForbiddenFacts);

public sealed record ReferenceChapterBlueprintAnalysisTrackPayload(
    [property: JsonPropertyName("track")] string Track,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("points")] IReadOnlyList<string> Points);

public sealed record ReferenceChapterBlueprintExecutionTrackPayload(
    [property: JsonPropertyName("track")] string Track,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("paragraph_intentions")] IReadOnlyList<string> ParagraphIntentions,
    [property: JsonPropertyName("execution_modes")] IReadOnlyList<string> ExecutionModes,
    [property: JsonPropertyName("anti_screenplay_duties")] IReadOnlyList<string> AntiScreenplayDuties,
    [property: JsonPropertyName("source_backed_detail_targets")] IReadOnlyList<string> SourceBackedDetailTargets,
    [property: JsonPropertyName("candidate_rejection_rules")] IReadOnlyList<string> CandidateRejectionRules);

public sealed record ReferenceChapterBlueprintSummaryPayload(
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source_plan_hash")] string SourcePlanHash,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ReferenceChapterBlueprintPayload(
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source_plan_scope")] string SourcePlanScope,
    [property: JsonPropertyName("source_plan_hash")] string SourcePlanHash,
    [property: JsonPropertyName("context_hash")] string ContextHash,
    [property: JsonPropertyName("analysis_contract_hash")] string AnalysisContractHash,
    [property: JsonPropertyName("blueprint_version")] int BlueprintVersion,
    [property: JsonPropertyName("parent_blueprint_id")] long ParentBlueprintId,
    [property: JsonPropertyName("primary_anchor_id")] long PrimaryAnchorId,
    [property: JsonPropertyName("chapter_function")] string ChapterFunction,
    [property: JsonPropertyName("logic_analysis")] ReferenceChapterBlueprintAnalysisTrackPayload LogicAnalysis,
    [property: JsonPropertyName("emotion_analysis")] ReferenceChapterBlueprintAnalysisTrackPayload EmotionAnalysis,
    [property: JsonPropertyName("narration_analysis")] ReferenceChapterBlueprintAnalysisTrackPayload NarrationAnalysis,
    [property: JsonPropertyName("character_analysis")] ReferenceChapterBlueprintAnalysisTrackPayload CharacterAnalysis,
    [property: JsonPropertyName("reference_analysis")] ReferenceChapterBlueprintAnalysisTrackPayload ReferenceAnalysis,
    [property: JsonPropertyName("transition_plan")] ReferenceChapterBlueprintAnalysisTrackPayload TransitionPlan,
    [property: JsonPropertyName("execution_contract")] ReferenceChapterBlueprintExecutionTrackPayload ExecutionContract,
    [property: JsonPropertyName("previous_state")] string PreviousState,
    [property: JsonPropertyName("final_state")] string FinalState,
    [property: JsonPropertyName("final_hook")] string FinalHook,
    [property: JsonPropertyName("global_pov")] string GlobalPov,
    [property: JsonPropertyName("global_narrative_distance")] string GlobalNarrativeDistance,
    [property: JsonPropertyName("known_facts")] IReadOnlyList<string> KnownFacts,
    [property: JsonPropertyName("forbidden_facts")] IReadOnlyList<string> ForbiddenFacts,
    [property: JsonPropertyName("risk_flags")] IReadOnlyList<string> RiskFlags,
    [property: JsonPropertyName("beats")] IReadOnlyList<ReferenceChapterBlueprintBeatPayload> Beats,
    [property: JsonPropertyName("latest_review")] ReferenceChapterBlueprintReviewPayload? LatestReview,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt)
{
    [JsonPropertyName("build_version")]
    public string BuildVersion { get; init; } = string.Empty;
}

public sealed record ReferenceBlueprintStyleContractPayload(
    [property: JsonPropertyName("style_profile_ids")] IReadOnlyList<long> StyleProfileIds,
    [property: JsonPropertyName("style_dimensions")] IReadOnlyList<string> StyleDimensions,
    [property: JsonPropertyName("imitation_intensity")] string ImitationIntensity,
    [property: JsonPropertyName("min_style_fit")] double MinStyleFit,
    [property: JsonPropertyName("allowed_closeness")] string AllowedCloseness,
    [property: JsonPropertyName("required_evidence_types")] IReadOnlyList<string> RequiredEvidenceTypes,
    [property: JsonPropertyName("forbidden_style_risks")] IReadOnlyList<string> ForbiddenStyleRisks);

public static class ReferenceStyleAttemptStatuses
{
    public const string NotApplicable = "not_applicable";
    public const string Attempted = "attempted";
    public const string DiagnosticOnly = "diagnostic_only";
    public const string RetrievalGap = "retrieval_gap";

    public static IReadOnlyList<string> All { get; } = [NotApplicable, Attempted, DiagnosticOnly, RetrievalGap];
}

public sealed record ReferenceChapterBlueprintBeatPayload(
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("beat_index")] int BeatIndex,
    [property: JsonPropertyName("scene_index")] int SceneIndex,
    [property: JsonPropertyName("beat_type")] string BeatType,
    [property: JsonPropertyName("narrative_function")] string NarrativeFunction,
    [property: JsonPropertyName("logic_premise")] string LogicPremise,
    [property: JsonPropertyName("conflict_pressure")] string ConflictPressure,
    [property: JsonPropertyName("causality_in")] string CausalityIn,
    [property: JsonPropertyName("causality_out")] string CausalityOut,
    [property: JsonPropertyName("transition_in")] string TransitionIn,
    [property: JsonPropertyName("transition_out")] string TransitionOut,
    [property: JsonPropertyName("pov_character")] string PovCharacter,
    [property: JsonPropertyName("narrative_distance")] string NarrativeDistance,
    [property: JsonPropertyName("viewpoint_allowed_knowledge")] IReadOnlyList<string> ViewpointAllowedKnowledge,
    [property: JsonPropertyName("viewpoint_forbidden_knowledge")] IReadOnlyList<string> ViewpointForbiddenKnowledge,
    [property: JsonPropertyName("character_states_before")] IReadOnlyList<string> CharacterStatesBefore,
    [property: JsonPropertyName("character_states_after")] IReadOnlyList<string> CharacterStatesAfter,
    [property: JsonPropertyName("character_goals")] IReadOnlyList<string> CharacterGoals,
    [property: JsonPropertyName("character_misbeliefs")] IReadOnlyList<string> CharacterMisbeliefs,
    [property: JsonPropertyName("relationship_pressure")] IReadOnlyList<string> RelationshipPressure,
    [property: JsonPropertyName("emotion_trigger")] string EmotionTrigger,
    [property: JsonPropertyName("emotion_before")] string EmotionBefore,
    [property: JsonPropertyName("emotion_after")] string EmotionAfter,
    [property: JsonPropertyName("suppressed_reaction")] string SuppressedReaction,
    [property: JsonPropertyName("external_evidence")] string ExternalEvidence,
    [property: JsonPropertyName("narration_strategy")] string NarrationStrategy,
    [property: JsonPropertyName("rhythm_strategy")] string RhythmStrategy,
    [property: JsonPropertyName("paragraph_intention")] string ParagraphIntention,
    [property: JsonPropertyName("execution_mode")] string ExecutionMode,
    [property: JsonPropertyName("anti_screenplay_duty")] string AntiScreenplayDuty,
    [property: JsonPropertyName("sensory_anchor_target")] string SensoryAnchorTarget,
    [property: JsonPropertyName("subtext_plan")] string SubtextPlan,
    [property: JsonPropertyName("source_backed_detail_target")] string SourceBackedDetailTarget,
    [property: JsonPropertyName("candidate_rejection_rule")] string CandidateRejectionRule,
    [property: JsonPropertyName("scene_facts")] IReadOnlyList<string> SceneFacts,
    [property: JsonPropertyName("forbidden_facts")] IReadOnlyList<string> ForbiddenFacts,
    [property: JsonPropertyName("reference_query")] ReferenceMaterialQueryPayload ReferenceQuery,
    [property: JsonPropertyName("required_material_types")] IReadOnlyList<string> RequiredMaterialTypes,
    [property: JsonPropertyName("max_rewrite_level")] string MaxRewriteLevel,
    [property: JsonPropertyName("slot_plan")] IReadOnlyList<ReferenceSlotValuePayload> SlotPlan,
    [property: JsonPropertyName("locked_phrase_policy")] string LockedPhrasePolicy,
    [property: JsonPropertyName("no_reuse_reason")] string NoReuseReason,
    [property: JsonPropertyName("prose_duties")] IReadOnlyList<string> ProseDuties,
    [property: JsonPropertyName("risk_flags")] IReadOnlyList<string> RiskFlags,
    [property: JsonPropertyName("style_contract")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReferenceBlueprintStyleContractPayload? StyleContract = null);

public sealed record ReviewReferenceChapterBlueprintPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId);

public sealed record ReferenceBlueprintRevisionChangePayload(
    [property: JsonPropertyName("field_path")] string FieldPath,
    [property: JsonPropertyName("new_value")] string NewValue);

public sealed record ReviseReferenceChapterBlueprintPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("changes")] IReadOnlyList<ReferenceBlueprintRevisionChangePayload> Changes,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("revision_reason")] string RevisionReason);

public sealed record ReferenceChapterBlueprintReviewDefectPayload(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("field_path")] string FieldPath,
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("required_fix")] string RequiredFix);

public sealed record ReferenceChapterBlueprintReviewPayload(
    [property: JsonPropertyName("review_id")] string ReviewId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("context_hash")] string ContextHash,
    [property: JsonPropertyName("source_plan_hash")] string SourcePlanHash,
    [property: JsonPropertyName("analysis_contract_hash")] string AnalysisContractHash,
    [property: JsonPropertyName("review_version")] int ReviewVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("logic_errors")] IReadOnlyList<string> LogicErrors,
    [property: JsonPropertyName("causality_errors")] IReadOnlyList<string> CausalityErrors,
    [property: JsonPropertyName("emotion_errors")] IReadOnlyList<string> EmotionErrors,
    [property: JsonPropertyName("narration_errors")] IReadOnlyList<string> NarrationErrors,
    [property: JsonPropertyName("execution_errors")] IReadOnlyList<string> ExecutionErrors,
    [property: JsonPropertyName("character_state_errors")] IReadOnlyList<string> CharacterStateErrors,
    [property: JsonPropertyName("pov_errors")] IReadOnlyList<string> PovErrors,
    [property: JsonPropertyName("continuity_errors")] IReadOnlyList<string> ContinuityErrors,
    [property: JsonPropertyName("transition_errors")] IReadOnlyList<string> TransitionErrors,
    [property: JsonPropertyName("forbidden_fact_errors")] IReadOnlyList<string> ForbiddenFactErrors,
    [property: JsonPropertyName("reference_binding_errors")] IReadOnlyList<string> ReferenceBindingErrors,
    [property: JsonPropertyName("material_fit_errors")] IReadOnlyList<string> MaterialFitErrors,
    [property: JsonPropertyName("screenplay_drift_risks")] IReadOnlyList<string> ScreenplayDriftRisks,
    [property: JsonPropertyName("ai_prose_risks")] IReadOnlyList<string> AiProseRisks,
    [property: JsonPropertyName("novelistic_narration_errors")] IReadOnlyList<string> NovelisticNarrationErrors,
    [property: JsonPropertyName("required_fixes")] IReadOnlyList<string> RequiredFixes,
    [property: JsonPropertyName("defects")] IReadOnlyList<ReferenceChapterBlueprintReviewDefectPayload> Defects,
    [property: JsonPropertyName("reviewed_at")] DateTimeOffset ReviewedAt);

public sealed record ApproveReferenceChapterBlueprintPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("review_id")] string ReviewId,
    [property: JsonPropertyName("approver_origin")] string ApproverOrigin = "user");

public sealed record BindReferenceBlueprintMaterialsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("max_results_per_beat")] int MaxResultsPerBeat,
    [property: JsonPropertyName("select_top_candidate")] bool SelectTopCandidate = false);

public sealed record ReferenceBlueprintMaterialLinkPayload(
    [property: JsonPropertyName("link_id")] string LinkId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("intended_use")] string IntendedUse,
    [property: JsonPropertyName("max_rewrite_level")] string MaxRewriteLevel,
    [property: JsonPropertyName("selected")] bool Selected,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("score_components")] IReadOnlyDictionary<string, double> ScoreComponents,
    [property: JsonPropertyName("fit_explanation")] string FitExplanation,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record ReferenceBlueprintMaterialBindingResultPayload(
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("links")] IReadOnlyList<ReferenceBlueprintMaterialLinkPayload> Links);

public sealed record GenerateReferenceAnchoredDraftPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("beat_ids")] IReadOnlyList<string> BeatIds,
    [property: JsonPropertyName("style_intensities")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? StyleIntensities = null,
    [property: JsonPropertyName("candidates_per_beat")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int CandidatesPerBeat = 0);

public sealed record ReferenceAnchoredDraftPayload(
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ReferenceDraftParagraphCandidatePayload> Candidates,
    [property: JsonPropertyName("audit")] ReferenceAnchoredDraftAuditPayload? Audit);

public sealed record ReferenceDraftParagraphCandidatePayload(
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("rewrite_level")] string RewriteLevel,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("changed_slots")] IReadOnlyList<ReferenceSlotValuePayload> ChangedSlots,
    [property: JsonPropertyName("non_slot_edits")] IReadOnlyList<string> NonSlotEdits,
    [property: JsonPropertyName("audit_status")] string AuditStatus,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("style_attempts")] IReadOnlyList<ReferenceDraftStyleAttemptPayload>? StyleAttempts = null);

public sealed record ReferenceDraftStyleAttemptPayload(
    [property: JsonPropertyName("style_profile_ids")] IReadOnlyList<long> StyleProfileIds,
    [property: JsonPropertyName("style_dimensions")] IReadOnlyList<string> StyleDimensions,
    [property: JsonPropertyName("imitation_intensity")] string ImitationIntensity,
    [property: JsonPropertyName("min_style_fit")] double MinStyleFit,
    [property: JsonPropertyName("allowed_closeness")] string AllowedCloseness,
    [property: JsonPropertyName("required_evidence_types")] IReadOnlyList<string> RequiredEvidenceTypes,
    [property: JsonPropertyName("forbidden_style_risks")] IReadOnlyList<string> ForbiddenStyleRisks,
    [property: JsonPropertyName("selected_material_style_fit")] double? SelectedMaterialStyleFit,
    [property: JsonPropertyName("selected_material_low_confidence")] bool SelectedMaterialLowConfidence,
    [property: JsonPropertyName("status")] string Status);

public sealed record AuditReferenceAnchoredDraftPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("candidate_ids")] IReadOnlyList<string> CandidateIds);

public sealed record GetReferenceAnchoredDraftAuditsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("candidate_ids")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? CandidateIds = null,
    [property: JsonPropertyName("limit")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int Limit = 0);

public sealed record GetReferenceStyleAuditFindingsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("candidate_ids")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? CandidateIds = null,
    [property: JsonPropertyName("risk_types")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? RiskTypes = null,
    [property: JsonPropertyName("limit")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int Limit = 0);

public sealed record ReferenceStyleAuditFindingPayload(
    [property: JsonPropertyName("audit_id")] string AuditId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("rewrite_level")] string RewriteLevel,
    [property: JsonPropertyName("candidate_ids")] IReadOnlyList<string> CandidateIds,
    [property: JsonPropertyName("risk_type")] string RiskType,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("required_action")] string RequiredAction,
    [property: JsonPropertyName("audited_at")] DateTimeOffset AuditedAt);

public sealed record ReferenceAnchoredDraftAuditPayload(
    [property: JsonPropertyName("audit_id")] string AuditId,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("rewrite_level")] string RewriteLevel,
    [property: JsonPropertyName("provenance_errors")] IReadOnlyList<string> ProvenanceErrors,
    [property: JsonPropertyName("blueprint_errors")] IReadOnlyList<string> BlueprintErrors,
    [property: JsonPropertyName("unsupported_fact_errors")] IReadOnlyList<string> UnsupportedFactErrors,
    [property: JsonPropertyName("pov_errors")] IReadOnlyList<string> PovErrors,
    [property: JsonPropertyName("ai_prose_risks")] IReadOnlyList<string> AiProseRisks,
    [property: JsonPropertyName("required_fixes")] IReadOnlyList<string> RequiredFixes,
    [property: JsonPropertyName("audited_at")] DateTimeOffset AuditedAt,
    [property: JsonPropertyName("candidate_ids")] IReadOnlyList<string>? CandidateIds = null,
    [property: JsonPropertyName("readable_report")] ReferenceDraftAuditReadableReportPayload? ReadableReport = null);

public sealed record ReferenceDraftAuditReadableReportPayload(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("candidate_ids")] IReadOnlyList<string> CandidateIds,
    [property: JsonPropertyName("findings")] IReadOnlyList<ReferenceDraftAuditReadableFindingPayload> Findings);

public sealed record ReferenceDraftAuditReadableFindingPayload(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("candidate_ids")] IReadOnlyList<string> CandidateIds,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("required_action")] string RequiredAction);

public sealed record ReferenceCorpusSearchPolicyPayload(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("max_results_per_beat")] int MaxResultsPerBeat,
    [property: JsonPropertyName("license_statuses")] IReadOnlyList<string> LicenseStatuses,
    [property: JsonPropertyName("include_anchor_ids")] IReadOnlyList<long> IncludeAnchorIds,
    [property: JsonPropertyName("exclude_anchor_ids")] IReadOnlyList<long> ExcludeAnchorIds);

public sealed record StartReferenceOrchestrationRunPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("chapter_goal")] string? ChapterGoal,
    [property: JsonPropertyName("known_facts")] IReadOnlyList<string> KnownFacts,
    [property: JsonPropertyName("forbidden_facts")] IReadOnlyList<string> ForbiddenFacts,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long>? AnchorIds,
    [property: JsonPropertyName("corpus_search_policy")] ReferenceCorpusSearchPolicyPayload CorpusSearchPolicy,
    [property: JsonPropertyName("source_confirmed")] bool SourceConfirmed = false);

public sealed record ReferenceOrchestrationApprovalSummaryPayload(
    [property: JsonPropertyName("chapter_function")] string ChapterFunction,
    [property: JsonPropertyName("pov")] string Pov,
    [property: JsonPropertyName("fact_boundary_changes")] IReadOnlyList<string> FactBoundaryChanges,
    [property: JsonPropertyName("emotional_trajectory")] string EmotionalTrajectory,
    [property: JsonPropertyName("material_use_plan")] string MaterialUsePlan,
    [property: JsonPropertyName("rewrite_budget")] string RewriteBudget,
    [property: JsonPropertyName("high_risk_findings")] IReadOnlyList<string> HighRiskFindings);

public sealed record ReferenceOrchestrationRequiredDecisionPayload(
    [property: JsonPropertyName("decision_type")] string DecisionType,
    [property: JsonPropertyName("stop_reason")] string StopReason,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("required_actions")] IReadOnlyList<string> RequiredActions,
    [property: JsonPropertyName("approval_summary")] ReferenceOrchestrationApprovalSummaryPayload ApprovalSummary,
    [property: JsonPropertyName("proposed_blueprint_revision")] ReferenceOrchestrationBlueprintRevisionProposalPayload? ProposedBlueprintRevision = null);

public sealed record ReferenceOrchestrationBlueprintRevisionProposalPayload(
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("review_id")] string ReviewId,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("revision_reason")] string RevisionReason,
    [property: JsonPropertyName("changes")] IReadOnlyList<ReferenceBlueprintRevisionChangePayload> Changes);

public sealed record ReferenceOrchestrationRunPayload(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("chapter_goal")] string ChapterGoal,
    [property: JsonPropertyName("known_facts")] IReadOnlyList<string> KnownFacts,
    [property: JsonPropertyName("forbidden_facts")] IReadOnlyList<string> ForbiddenFacts,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("corpus_search_policy")] ReferenceCorpusSearchPolicyPayload CorpusSearchPolicy,
    [property: JsonPropertyName("blueprint_id")] long BlueprintId,
    [property: JsonPropertyName("review_id")] string ReviewId,
    [property: JsonPropertyName("candidate_ids")] IReadOnlyList<string> CandidateIds,
    [property: JsonPropertyName("current_decision")] ReferenceOrchestrationRequiredDecisionPayload? CurrentDecision,
    [property: JsonPropertyName("last_stop_reason")] string LastStopReason,
    [property: JsonPropertyName("error_message")] string ErrorMessage,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ReferenceOrchestrationRunEventPayload(
    [property: JsonPropertyName("event_id")] long EventId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stop_reason")] string StopReason,
    [property: JsonPropertyName("decision_type")] string DecisionType,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record ResumeReferenceOrchestrationRunPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("decision_type")] string DecisionType,
    [property: JsonPropertyName("decision_payload")] string DecisionPayload);

public sealed record CancelReferenceOrchestrationRunPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("reason")] string Reason);
