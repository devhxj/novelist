using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record StartReferenceCorpusFeatureAnalysisPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("token_budget")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TokenBudget = null,
    [property: JsonPropertyName("resume")] bool Resume = false,
    [property: JsonPropertyName("run_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RunId = null);

public sealed record GetReferenceCorpusFeatureAnalysisRunPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("run_id")] string RunId);

public sealed record StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_node_type")] string SourceNodeType,
    [property: JsonPropertyName("min_observation_confidence")] double MinObservationConfidence = 0.70,
    [property: JsonPropertyName("run_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RunId = null,
    [property: JsonPropertyName("token_budget")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TokenBudget = null,
    [property: JsonPropertyName("resume")] bool Resume = false);

public sealed record GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("run_id")] string RunId);

public sealed record ListReferenceCorpusFeatureObservationsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("node_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NodeId,
    [property: JsonPropertyName("page_request")] PageRequestPayload PageRequest);

public sealed record ListReferenceCorpusTechniqueSpecimensPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_node_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceNodeId,
    [property: JsonPropertyName("page_request")] PageRequestPayload PageRequest);

public sealed record ReferenceCorpusFeatureAnalysisRunPayload(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("families")] IReadOnlyList<string> Families,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("token_budget")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TokenBudget,
    [property: JsonPropertyName("tokens_spent")] int TokensSpent,
    [property: JsonPropertyName("resume_cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResumeCursor,
    [property: JsonPropertyName("observation_count")] int ObservationCount,
    [property: JsonPropertyName("processed_work_items")] int ProcessedWorkItems,
    [property: JsonPropertyName("analyzer_version")] string AnalyzerVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("model_provider")] string ModelProvider,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);

public sealed record ReferenceCorpusFeatureObservationPayload(
    [property: JsonPropertyName("observation_id")] string ObservationId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("node_type")] string NodeType,
    [property: JsonPropertyName("text_hash")] string TextHash,
    [property: JsonPropertyName("feature_family")] string FeatureFamily,
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("value_kind")] string ValueKind,
    [property: JsonPropertyName("value_preview")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ValuePreview,
    [property: JsonPropertyName("value_text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ValueText,
    [property: JsonPropertyName("value_num")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? ValueNum,
    [property: JsonPropertyName("value_bool")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ValueBool,
    [property: JsonPropertyName("intensity")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Intensity,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence_start")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? EvidenceStart,
    [property: JsonPropertyName("evidence_end")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? EvidenceEnd,
    [property: JsonPropertyName("evidence_preview")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EvidencePreview,
    [property: JsonPropertyName("explanation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Explanation,
    [property: JsonPropertyName("review_state")] string ReviewState,
    [property: JsonPropertyName("validity_state")] string ValidityState,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record ReferenceCorpusTechniqueSpecimenEvidencePayload(
    [property: JsonPropertyName("observation_id")] string ObservationId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("node_type")] string NodeType,
    [property: JsonPropertyName("text_hash")] string TextHash,
    [property: JsonPropertyName("feature_family")] string FeatureFamily,
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence_start")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? EvidenceStart,
    [property: JsonPropertyName("evidence_end")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? EvidenceEnd,
    [property: JsonPropertyName("evidence_preview")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EvidencePreview,
    [property: JsonPropertyName("value_preview")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ValuePreview,
    [property: JsonPropertyName("explanation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Explanation);

public sealed record ReferenceCorpusTechniqueTransferSlotPayload(
    [property: JsonPropertyName("slot_name")] string SlotName,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("constraints")] string Constraints);

public sealed record ReferenceCorpusTechniqueWhyFactorPayload(
    [property: JsonPropertyName("factor")] string Factor,
    [property: JsonPropertyName("observation_ids")] IReadOnlyList<string> ObservationIds,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("evidence")] IReadOnlyList<ReferenceCorpusTechniqueSpecimenEvidencePayload> Evidence);

public sealed record ReferenceCorpusTechniqueWhyItWorksPayload(
    [property: JsonPropertyName("contributing_factors")] IReadOnlyList<ReferenceCorpusTechniqueWhyFactorPayload> ContributingFactors,
    [property: JsonPropertyName("trace_complete")] bool TraceComplete);

public sealed record ReferenceCorpusTechniqueSpecimenPayload(
    [property: JsonPropertyName("specimen_id")] string SpecimenId,
    [property: JsonPropertyName("source_node_id")] string SourceNodeId,
    [property: JsonPropertyName("source_anchor_id")] long SourceAnchorId,
    [property: JsonPropertyName("analysis_run_id")] string AnalysisRunId,
    [property: JsonPropertyName("technique_family")] string TechniqueFamily,
    [property: JsonPropertyName("technique_abstract")] string TechniqueAbstract,
    [property: JsonPropertyName("trigger_context")] string TriggerContext,
    [property: JsonPropertyName("transfer_template")] string TransferTemplate,
    [property: JsonPropertyName("transfer_slots")] IReadOnlyList<ReferenceCorpusTechniqueTransferSlotPayload> TransferSlots,
    [property: JsonPropertyName("effect_on_reader")] string EffectOnReader,
    [property: JsonPropertyName("applicability_conditions")] IReadOnlyList<string> ApplicabilityConditions,
    [property: JsonPropertyName("failure_modes")] IReadOnlyList<string> FailureModes,
    [property: JsonPropertyName("anti_patterns")] IReadOnlyList<string> AntiPatterns,
    [property: JsonPropertyName("world_context_dependencies")] IReadOnlyList<string> WorldContextDependencies,
    [property: JsonPropertyName("why_it_works")] ReferenceCorpusTechniqueWhyItWorksPayload WhyItWorks,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("review_state")] string ReviewState,
    [property: JsonPropertyName("validity_state")] string ValidityState,
    [property: JsonPropertyName("mastery_notes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? MasteryNotes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("evidence")] IReadOnlyList<ReferenceCorpusTechniqueSpecimenEvidencePayload> Evidence);

public sealed record ReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("token_budget")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TokenBudget,
    [property: JsonPropertyName("tokens_spent")] int TokensSpent,
    [property: JsonPropertyName("resume_cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResumeCursor,
    [property: JsonPropertyName("specimen_count")] int SpecimenCount,
    [property: JsonPropertyName("processed_nodes")] int ProcessedNodes,
    [property: JsonPropertyName("analyzer_version")] string AnalyzerVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("model_provider")] string ModelProvider,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);
