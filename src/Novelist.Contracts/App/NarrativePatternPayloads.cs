using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ChapterRangePayload(
    [property: JsonPropertyName("start_chapter")] int StartChapter,
    [property: JsonPropertyName("end_chapter")] int EndChapter);

public sealed record StartNarrativePatternExtractionPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_ranges")] IReadOnlyList<ChapterRangePayload> ChapterRanges,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("reasoning_effort")] string ReasoningEffort,
    [property: JsonPropertyName("skill_name")] string SkillName,
    [property: JsonPropertyName("selected_chapter_ids")]
    IReadOnlyList<long>? SelectedChapterIds = null);

public sealed record CancelNarrativePatternExtractionPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record GetNarrativePatternRunPayload(
    [property: JsonPropertyName("task_id")] string TaskId);

public sealed record NarrativePatternRunPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("progress_completed")] int ProgressCompleted,
    [property: JsonPropertyName("progress_total")] int ProgressTotal,
    [property: JsonPropertyName("chapter_ranges")] IReadOnlyList<ChapterRangePayload> ChapterRanges,
    [property: JsonPropertyName("selected_chapter_ids")] IReadOnlyList<long> SelectedChapterIds,
    [property: JsonPropertyName("skill_name")] string SkillName,
    [property: JsonPropertyName("skill_preview")] string SkillPreview,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CopyableDiagnosticPayload> Diagnostics,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("completed_at")]
    DateTimeOffset? CompletedAt);

public sealed record NarrativePatternProgressPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("progress_completed")] int ProgressCompleted,
    [property: JsonPropertyName("progress_total")] int ProgressTotal,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("llm_status")] string LlmStatus = "",
    [property: JsonPropertyName("round")] int? Round = null,
    [property: JsonPropertyName("batch_index")] int? BatchIndex = null,
    [property: JsonPropertyName("batch_total")] int? BatchTotal = null,
    [property: JsonPropertyName("token_estimate")] int? TokenEstimate = null,
    [property: JsonPropertyName("boundary_count")] int? BoundaryCount = null,
    [property: JsonPropertyName("summary_count")] int? SummaryCount = null,
    [property: JsonPropertyName("phase_count")] int? PhaseCount = null);

public sealed record NarrativePatternTracePayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("entries")] IReadOnlyList<NarrativePatternTraceEntryPayload> Entries);

public sealed record NarrativePatternTraceEntryPayload(
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("input_hash")] string InputHash,
    [property: JsonPropertyName("output_hash")] string OutputHash,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CopyableDiagnosticPayload> Diagnostics,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
