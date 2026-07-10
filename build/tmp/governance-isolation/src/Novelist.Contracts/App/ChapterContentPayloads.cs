using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ChapterPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("word_count")] int WordCount,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("file_path")] string FilePath);

public sealed record CreateChapterPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title);

public sealed record SaveContentPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);
