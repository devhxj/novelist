using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ChapterPlanPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("content")] string Content);

public sealed record UpdateChapterPlanPayload(
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("content")] string? Content = null);

public sealed record TimelineEntryPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("detail_json")] string DetailJson,
    [property: JsonPropertyName("target_chapter")] int TargetChapter,
    [property: JsonPropertyName("importance")] int Importance,
    [property: JsonPropertyName("source_chapter_id")] long SourceChapterId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("resolved_chapter_id")] long ResolvedChapterId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record CreateTimelineEntryPayload(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("detail_json")] string? DetailJson = null,
    [property: JsonPropertyName("target_chapter")] int TargetChapter = 0,
    [property: JsonPropertyName("importance")] int? Importance = null,
    [property: JsonPropertyName("source_chapter_id")] long? SourceChapterId = null,
    [property: JsonPropertyName("source")] string? Source = null);

public sealed record UpdateTimelineEntryPayload(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("detail_json")] string? DetailJson = null,
    [property: JsonPropertyName("target_chapter")] int? TargetChapter = null,
    [property: JsonPropertyName("importance")] int? Importance = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("resolved_chapter_id")] long? ResolvedChapterId = null);

public sealed record StoryArcPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("arc_type")] string ArcType,
    [property: JsonPropertyName("importance")] int Importance,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reactivate_at")] string ReactivateAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record CreateStoryArcPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arc_type")] string ArcType,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("importance")] int? Importance = null);

public sealed record UpdateStoryArcPayload(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("arc_type")] string? ArcType = null,
    [property: JsonPropertyName("importance")] int? Importance = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("reactivate_at")] string? ReactivateAt = null);

public sealed record ArcNodePayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("story_arc_id")] long StoryArcId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("target_chapter")] int TargetChapter,
    [property: JsonPropertyName("actual_chapter")] int ActualChapter,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record CreateArcNodePayload(
    [property: JsonPropertyName("story_arc_id")] long StoryArcId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("target_chapter")] int TargetChapter = 0);

public sealed record UpdateArcNodePayload(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("target_chapter")] int? TargetChapter = null,
    [property: JsonPropertyName("actual_chapter")] int? ActualChapter = null,
    [property: JsonPropertyName("status")] string? Status = null);

public sealed record ReaderPerspectivePayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("related_truth")] string RelatedTruth,
    [property: JsonPropertyName("planted_chapter")] int PlantedChapter,
    [property: JsonPropertyName("revealed_chapter")] int RevealedChapter,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record CreateReaderPerspectivePayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("planted_chapter")] int PlantedChapter = 0,
    [property: JsonPropertyName("related_truth")] string? RelatedTruth = null,
    [property: JsonPropertyName("revealed_chapter")] int? RevealedChapter = null);

public sealed record UpdateReaderPerspectivePayload(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("planted_chapter")] int? PlantedChapter = null,
    [property: JsonPropertyName("related_truth")] string? RelatedTruth = null,
    [property: JsonPropertyName("revealed_chapter")] int? RevealedChapter = null);
