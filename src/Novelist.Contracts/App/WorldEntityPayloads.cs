using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record CharacterPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("personality")] string Personality,
    [property: JsonPropertyName("abilities")] string Abilities,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record CharacterRelationPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("source_character_id")] long SourceCharacterId,
    [property: JsonPropertyName("target_character_id")] long TargetCharacterId,
    [property: JsonPropertyName("relation_describe")] string RelationDescribe,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("chapter_id")] long ChapterId,
    [property: JsonPropertyName("is_current")] bool IsCurrent,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record CreateCharacterPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("personality")] string? Personality,
    [property: JsonPropertyName("abilities")] string? Abilities);

public sealed record UpdateCharacterPayload(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("personality")] string? Personality,
    [property: JsonPropertyName("abilities")] string? Abilities);

public sealed record LocationPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("location_type")] string LocationType,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("detail_json")] string DetailJson,
    [property: JsonPropertyName("parent_location_id")] long? ParentLocationId,
    [property: JsonPropertyName("tags")] string Tags,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record LocationRelationPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("location_a_id")] long LocationAId,
    [property: JsonPropertyName("location_b_id")] long LocationBId,
    [property: JsonPropertyName("relation_type")] string RelationType,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record CreateLocationPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("location_type")] string? LocationType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("detail_json")] string? DetailJson,
    [property: JsonPropertyName("parent_location_id")] long? ParentLocationId,
    [property: JsonPropertyName("tags")] string? Tags);

public sealed record UpdateLocationPayload(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("location_type")] string? LocationType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("detail_json")] string? DetailJson,
    [property: JsonPropertyName("parent_location_id")] long? ParentLocationId,
    [property: JsonPropertyName("tags")] string? Tags,
    [property: JsonPropertyName("clear_parent")] bool ClearParent);
