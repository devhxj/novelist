using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ListReferenceMaterialsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("page")] int Page = 1,
    [property: JsonPropertyName("size")] int Size = 20);

public sealed record SearchReferenceMaterialsPayload(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("max_results")] int MaxResults = 20,
    [property: JsonPropertyName("novel_id")] long? NovelId = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("library_ids")] IReadOnlyList<string>? LibraryIds = null,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long>? AnchorIds = null);

public sealed record ReferenceMaterialListItemPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("metadata")] ReferenceMaterialMetadataPayload Metadata,
    [property: JsonPropertyName("text_hash")] string TextHash);

public sealed record ReferenceMaterialSearchHitPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("metadata")] ReferenceMaterialMetadataPayload Metadata,
    [property: JsonPropertyName("text_hash")] string TextHash,
    [property: JsonPropertyName("vector_distance")] double VectorDistance);

public sealed record ReferenceMaterialEntityPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record ReferenceMaterialSourceSpanPayload(
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine);

public sealed record ReferenceMaterialSettingPayload(
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("time")] string? Time,
    [property: JsonPropertyName("environment")] string? Environment);

public sealed record ReferenceMaterialPerspectivePayload(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("focus_entity")] string? FocusEntity);

public sealed record ReferenceMaterialFactPayload(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("subject")] string? Subject);

public sealed record ReferenceMaterialCausalityPayload(
    [property: JsonPropertyName("cause")] string? Cause,
    [property: JsonPropertyName("consequence")] string? Consequence);

public sealed record ReferenceMaterialStateChangePayload(
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("before")] string Before,
    [property: JsonPropertyName("after")] string After);

public sealed record ReferenceMaterialConflictPayload(
    [property: JsonPropertyName("pressure")] string? Pressure,
    [property: JsonPropertyName("cost")] string? Cost);

public sealed record ReferenceMaterialInformationPayload(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);

public sealed record ReferenceMaterialEmotionPayload(
    [property: JsonPropertyName("tone")] string? Tone,
    [property: JsonPropertyName("subtext")] string? Subtext);

public sealed record ReferenceMaterialForeshadowingPayload(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("target")] string Target);

public sealed record ReferenceMaterialMetadataPayload(
    [property: JsonPropertyName("source_span")] ReferenceMaterialSourceSpanPayload SourceSpan,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("entities")] IReadOnlyList<ReferenceMaterialEntityPayload> Entities,
    [property: JsonPropertyName("setting")] ReferenceMaterialSettingPayload? Setting,
    [property: JsonPropertyName("perspective")] ReferenceMaterialPerspectivePayload? Perspective,
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("facts")] IReadOnlyList<ReferenceMaterialFactPayload> Facts,
    [property: JsonPropertyName("causality")] ReferenceMaterialCausalityPayload? Causality,
    [property: JsonPropertyName("state_changes")] IReadOnlyList<ReferenceMaterialStateChangePayload> StateChanges,
    [property: JsonPropertyName("character_dynamics")] string? CharacterDynamics,
    [property: JsonPropertyName("conflict")] ReferenceMaterialConflictPayload? Conflict,
    [property: JsonPropertyName("information")] ReferenceMaterialInformationPayload? Information,
    [property: JsonPropertyName("emotion")] ReferenceMaterialEmotionPayload? Emotion,
    [property: JsonPropertyName("narrative_functions")] IReadOnlyList<string> NarrativeFunctions,
    [property: JsonPropertyName("foreshadowing")] IReadOnlyList<ReferenceMaterialForeshadowingPayload> Foreshadowing,
    [property: JsonPropertyName("motifs")] IReadOnlyList<string> Motifs,
    [property: JsonPropertyName("expression_techniques")] IReadOnlyList<string> ExpressionTechniques,
    [property: JsonPropertyName("reuse_hint")] string ReuseHint);
