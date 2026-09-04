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
    [property: JsonPropertyName("content")] string Content,
    // U1：调用方读盘时见到的那份内容的基线令牌。携带时不匹配即拒绝保存（CONTENT_CONFLICT），
    // 由前端既有冲突条接管；缺省保持旧的最后写入者胜语义（Agent 直写、导入等路径）。
    [property: JsonPropertyName("baseline_hash")] string? BaselineHash = null);

public sealed record DeleteChapterPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_id")] long ChapterId);
