using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record PreferenceItemPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("is_global")] bool IsGlobal,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record PreferenceResultPayload(
    [property: JsonPropertyName("global")] IReadOnlyList<PreferenceItemPayload> Global,
    [property: JsonPropertyName("novel")] IReadOnlyList<PreferenceItemPayload> Novel);

public sealed record CreatePreferencePayload(
    [property: JsonPropertyName("is_global")] bool IsGlobal,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("content")] string Content);

public sealed record UpdatePreferencePayload(
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("is_global")] bool? IsGlobal);
