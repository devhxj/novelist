using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

// 高级素材抽取的中间产物：分析器（LLM 分趟）产出的候选观测/机理草稿，
// 由摄入层按冻结词表与证据边界校验后写壳。校验不通过即拒收，不进库。
public sealed record AdvancedMaterialEvidenceDraft(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("start_offset")] int StartOffset,
    [property: JsonPropertyName("end_offset")] int EndOffset);

public sealed record AdvancedMaterialObservationDraft(
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("explanation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Explanation,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence")] IReadOnlyList<AdvancedMaterialEvidenceDraft> Evidence);

public sealed record AdvancedMaterialSpecimenDraft(
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("abstract")] string Abstract,
    [property: JsonPropertyName("trigger_context")] string TriggerContext,
    [property: JsonPropertyName("why_it_works")] IReadOnlyList<string> WhyItWorks,
    [property: JsonPropertyName("effect_on_reader")] string EffectOnReader,
    [property: JsonPropertyName("transfer_template")] string TransferTemplate,
    [property: JsonPropertyName("transfer_slots")] IReadOnlyDictionary<string, string> TransferSlots,
    [property: JsonPropertyName("world_context_dependencies")] IReadOnlyList<string> WorldContextDependencies,
    [property: JsonPropertyName("failure_modes")] IReadOnlyList<string> FailureModes,
    [property: JsonPropertyName("anti_patterns")] IReadOnlyList<string> AntiPatterns,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence")] IReadOnlyList<AdvancedMaterialEvidenceDraft> Evidence);

public sealed record AdvancedMaterialIngestionResult(
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("rejected")] int Rejected);

public sealed record StartReferenceAdvancedMaterialAnalysisPayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("run_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null);

public sealed record ReferenceAdvancedMaterialPipelinePayload(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("observation_accepted")] int ObservationAccepted,
    [property: JsonPropertyName("observation_rejected")] int ObservationRejected,
    [property: JsonPropertyName("specimen_accepted")] int SpecimenAccepted,
    [property: JsonPropertyName("specimen_rejected")] int SpecimenRejected,
    [property: JsonPropertyName("strategy_groups")] int StrategyGroups);
