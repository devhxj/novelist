using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record CreateStyleSamplePayload(
    [property: JsonPropertyName("novel_id")]
    long? NovelId,
    [property: JsonPropertyName("is_global")] bool IsGlobal,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("source_metadata")]
    StyleSampleSourceMetadataPayload? SourceMetadata);

public sealed record UpdateStyleSamplePayload(
    [property: JsonPropertyName("sample_id")] long SampleId,
    [property: JsonPropertyName("novel_id")]
    long? NovelId,
    [property: JsonPropertyName("is_global")] bool IsGlobal,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("source_metadata")]
    StyleSampleSourceMetadataPayload? SourceMetadata);

public sealed record DeleteStyleSamplePayload(
    [property: JsonPropertyName("sample_id")] long SampleId);

public sealed record GetStyleSamplePayload(
    [property: JsonPropertyName("sample_id")] long SampleId);

public sealed record SearchStyleSamplesPayload(
    [property: JsonPropertyName("novel_id")]
    long? NovelId,
    [property: JsonPropertyName("include_global")] bool IncludeGlobal,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("size")] int Size);

public sealed record StyleSamplePayload(
    [property: JsonPropertyName("sample_id")] long SampleId,
    [property: JsonPropertyName("novel_id")]
    long? NovelId,
    [property: JsonPropertyName("is_global")] bool IsGlobal,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("preview")] string Preview,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("stats_schema_version")] string StatsSchemaVersion,
    [property: JsonPropertyName("stats")] StyleSampleStatsPayload Stats,
    [property: JsonPropertyName("source_metadata")]
    StyleSampleSourceMetadataPayload? SourceMetadata,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record StyleSampleDetailPayload(
    [property: JsonPropertyName("sample_id")] long SampleId,
    [property: JsonPropertyName("novel_id")]
    long? NovelId,
    [property: JsonPropertyName("is_global")] bool IsGlobal,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("stats_schema_version")] string StatsSchemaVersion,
    [property: JsonPropertyName("stats")] StyleSampleStatsPayload Stats,
    [property: JsonPropertyName("source_metadata")]
    StyleSampleSourceMetadataPayload? SourceMetadata,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record StyleSampleStatsPayload(
    [property: JsonPropertyName("character_count")] int CharacterCount,
    [property: JsonPropertyName("sentence_count")] int SentenceCount,
    [property: JsonPropertyName("average_sentence_chars")] double AverageSentenceChars,
    [property: JsonPropertyName("dialogue_ratio")] double DialogueRatio,
    [property: JsonPropertyName("interiority_ratio")] double InteriorityRatio,
    [property: JsonPropertyName("sensory_ratio")] double SensoryRatio,
    [property: JsonPropertyName("punctuation_per_100_chars")] double PunctuationPer100Chars);

public sealed record StyleSampleSourceMetadataPayload(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source_hash")] string SourceHash);

public sealed record StartStyleSkillExtractionPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("novel_id")]
    long? NovelId,
    [property: JsonPropertyName("sample_ids")] IReadOnlyList<long> SampleIds,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("reasoning_effort")] string ReasoningEffort,
    [property: JsonPropertyName("skill_name")] string SkillName);

public sealed record CancelStyleSkillExtractionPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record StyleSkillExtractionRunPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("progress_completed")] int ProgressCompleted,
    [property: JsonPropertyName("progress_total")] int ProgressTotal,
    [property: JsonPropertyName("sample_ids")] IReadOnlyList<long> SampleIds,
    [property: JsonPropertyName("skill_name")] string SkillName,
    [property: JsonPropertyName("skill_preview")] string SkillPreview,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CopyableDiagnosticPayload> Diagnostics,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("completed_at")]
    DateTimeOffset? CompletedAt);

public sealed record StyleSkillExtractionProgressPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("progress_completed")] int ProgressCompleted,
    [property: JsonPropertyName("progress_total")] int ProgressTotal,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);
