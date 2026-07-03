using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record NovelPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record CreateNovelPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("genre")] string? Genre);

public sealed record UpdateNovelPayload(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("genre")] string? Genre);

public sealed record SetActiveNovelPayload(
    [property: JsonPropertyName("novel_id")] long NovelId);
