using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceMaterializationBlueprintPreviewStatuses
{
    public const string Active = "active";
    public const string Stale = "stale";
}

public static class ReferenceMaterializationBlueprintPreviewNextActions
{
    public const string None = "none";
    public const string Rebuild = "rebuild";
}

public sealed record GenerateReferenceMaterializationBlueprintPreviewPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("requested_count")] int RequestedCount = 3);

public sealed record GetReferenceMaterializationBlueprintPreviewPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("session_id")] string SessionId);

public sealed record ReferenceMaterializationBlueprintPreviewSourcePayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("material_count")] long MaterialCount);

public sealed record ReferenceMaterializationBlueprintPreviewMaterialLinkPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("material_type")] string MaterialType,
    [property: JsonPropertyName("text_preview")] string TextPreview,
    [property: JsonPropertyName("quality_score")] double QualityScore,
    [property: JsonPropertyName("vector_score")] double VectorScore,
    [property: JsonPropertyName("fit_explanation")] string FitExplanation);

public sealed record ReferenceMaterializationBlueprintPreviewBeatPayload(
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("beat_index")] int BeatIndex,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("narrative_function")] string NarrativeFunction,
    [property: JsonPropertyName("materials")] IReadOnlyList<ReferenceMaterializationBlueprintPreviewMaterialLinkPayload> Materials);

public sealed record ReferenceMaterializationBlueprintPreviewCandidatePayload(
    [property: JsonPropertyName("blueprint_id")] string BlueprintId,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("beats")] IReadOnlyList<ReferenceMaterializationBlueprintPreviewBeatPayload> Beats);

public sealed record ReferenceMaterializationBlueprintPreviewPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("next_action")] string NextAction,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("sources")] IReadOnlyList<ReferenceMaterializationBlueprintPreviewSourcePayload> Sources,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ReferenceMaterializationBlueprintPreviewCandidatePayload> Candidates,
    [property: JsonPropertyName("stale_anchor_ids")] IReadOnlyList<long> StaleAnchorIds,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);
