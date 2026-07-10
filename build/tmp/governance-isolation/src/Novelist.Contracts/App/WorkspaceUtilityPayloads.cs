using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ListSkillsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId);

public sealed record DeleteSkillPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source);

public sealed record SkillMetaPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("source")] string Source);

public sealed record ListSlashCommandsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId);

public sealed record SlashCommandPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("type")] string Type);

public sealed record ExtractStylePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("sample")] string Sample,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("reasoning_effort")] string ReasoningEffort);

public sealed record ExtractStyleResultPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("raw_content")] string RawContent,
    [property: JsonPropertyName("file_path")] string FilePath);

public sealed record SearchResultPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string Subtitle,
    [property: JsonPropertyName("chapter_num")] int ChapterNum,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("match_prefix")] string MatchPrefix,
    [property: JsonPropertyName("match_hit")] string MatchHit,
    [property: JsonPropertyName("match_suffix")] string MatchSuffix,
    [property: JsonPropertyName("match_position")] int MatchPosition,
    [property: JsonPropertyName("match_len")] int MatchLen,
    [property: JsonPropertyName("relevance")] double Relevance,
    [property: JsonPropertyName("panel_id")] string PanelId);

public sealed record SearchStoryMemoryPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("top_k")] int TopK,
    [property: JsonPropertyName("min_relevance")] double MinRelevance,
    [property: JsonPropertyName("chapter_numbers")] IReadOnlyList<int> ChapterNumbers,
    [property: JsonPropertyName("chunk_types")] IReadOnlyList<string> ChunkTypes);

public sealed record StoryMemoryHitPayload(
    [property: JsonPropertyName("chunk_id")] string ChunkId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("chapter_title")] string ChapterTitle,
    [property: JsonPropertyName("chunk_type")] string ChunkType,
    [property: JsonPropertyName("relevance")] double Relevance,
    [property: JsonPropertyName("content")] string Content);

public sealed record SearchStoryMemoryResultPayload(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("max_relevance")] string MaxRelevance,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("results")] IReadOnlyList<StoryMemoryHitPayload> Results);

public sealed record DailyActivityPayload(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("words")] int Words);

public sealed record WritingStatsPayload(
    [property: JsonPropertyName("total_words")] int TotalWords,
    [property: JsonPropertyName("total_days_active")] int TotalDaysActive,
    [property: JsonPropertyName("current_streak")] int CurrentStreak,
    [property: JsonPropertyName("longest_streak")] int LongestStreak,
    [property: JsonPropertyName("total_novels")] long TotalNovels,
    [property: JsonPropertyName("total_chapters")] long TotalChapters);
