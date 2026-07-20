using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record GenerateReferenceMaterializationBlueprintPreviewPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("requested_count")] int RequestedCount = 3);

public sealed record ReferenceMaterializationBlueprintPreviewSourcePayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("material_count")] int MaterialCount);

public sealed record ReferenceMaterializationBlueprintPreviewMaterialLinkPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("material_type")] string MaterialType,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("vector_distance")] double VectorDistance,
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
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("sources")] IReadOnlyList<ReferenceMaterializationBlueprintPreviewSourcePayload> Sources,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ReferenceMaterializationBlueprintPreviewCandidatePayload> Candidates);
