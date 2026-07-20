using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ListReferenceMaterialsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("page")] int Page = 1,
    [property: JsonPropertyName("size")] int Size = 20);

public sealed record ReferenceMaterialListItemPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("material_type")] string MaterialType,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("text_hash")] string TextHash);
