using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceChapterSplitModes
{
    public const string Auto = "auto";
    public const string Manual = "manual";

    public static IReadOnlyList<string> All { get; } = [Auto, Manual];
}

public static class ReferenceChapterSplitProfileStates
{
    public const string Draft = "draft";
    public const string Validated = "validated";
    public const string Confirmed = "confirmed";
    public const string Stale = "stale";

    public static IReadOnlyList<string> All { get; } = [Draft, Validated, Confirmed, Stale];
}

public static class ReferenceMaterializationBatchSizes
{
    public const int Default = 5;
    public static IReadOnlyList<int> All { get; } = [5, 10];

    public static void Validate(int value)
    {
        if (!All.Contains(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Chapter batch size must be 5 or 10.");
        }
    }
}

public static class ReferenceMaterializationRunStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Failed = "failed";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Queued, Running, Failed, Completed, Cancelled];
}

public static class ReferenceMaterializationChapterStates
{
    public const string Pending = "pending";
    public const string BuildingCandidates = "building_candidates";
    public const string LlmQualifying = "llm_qualifying";
    public const string Embedding = "embedding";
    public const string Indexing = "indexing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
    [
        Pending,
        BuildingCandidates,
        LlmQualifying,
        Embedding,
        Indexing,
        Completed,
        Failed,
        Cancelled
    ];
}

public static class ReferenceMaterializationErrorCodes
{
    public const string LlmNotConfigured = "materialization_llm_not_configured";
    public const string LlmHealthCheckFailed = "materialization_llm_health_check_failed";
    public const string LlmRequestFailed = "materialization_llm_request_failed";
    public const string LlmOutputInvalid = "materialization_llm_output_invalid";
    public const string EmbeddingNotConfigured = "materialization_embedding_not_configured";
    public const string EmbeddingHealthCheckFailed = "materialization_embedding_health_check_failed";
    public const string EmbeddingRequestFailed = "materialization_embedding_request_failed";
    public const string EmbeddingInvalid = "materialization_embedding_invalid";
    public const string VectorIndexFailed = "materialization_vector_index_failed";
    public const string GenerationIncomplete = "materialization_generation_incomplete";
}

public sealed record AnalyzeReferenceChapterSplitPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId);

public sealed record PreviewReferenceChapterSplitPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("delimiter_template")] string DelimiterTemplate);

public sealed record ConfirmReferenceChapterSplitPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("split_profile_id")] string SplitProfileId);

public sealed record EnqueueReferenceMaterializationPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("split_profile_id")] string SplitProfileId,
    [property: JsonPropertyName("chapter_batch_size")] int ChapterBatchSize = ReferenceMaterializationBatchSizes.Default);

public sealed record GetReferenceMaterializationStatusPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("run_id")] string RunId);

public sealed record ListReferenceMaterializationChapterProgressPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("size")] int Size);

public sealed record ReferenceChapterSplitBoundaryPayload(
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("heading_start")] int HeadingStart,
    [property: JsonPropertyName("content_start")] int ContentStart,
    [property: JsonPropertyName("content_end")] int ContentEnd,
    [property: JsonPropertyName("text_hash")] string TextHash);

public sealed record ReferenceChapterSplitProfilePayload(
    [property: JsonPropertyName("split_profile_id")] string SplitProfileId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_hash")] string SourceHash,
    [property: JsonPropertyName("split_mode")] string SplitMode,
    [property: JsonPropertyName("pattern_kind")] string PatternKind,
    [property: JsonPropertyName("delimiter_template")] string DelimiterTemplate,
    [property: JsonPropertyName("sample_char_count")] int SampleCharCount,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("chapter_count")] int ChapterCount,
    [property: JsonPropertyName("boundaries")] IReadOnlyList<ReferenceChapterSplitBoundaryPayload> Boundaries,
    [property: JsonPropertyName("model_provider")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ModelProvider = null,
    [property: JsonPropertyName("model_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ModelId = null,
    [property: JsonPropertyName("confidence")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Confidence = null);

public sealed record ReferenceMaterializationModelIdentityPayload(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("dimensions")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Dimensions = null);

public sealed record ReferenceMaterializationChapterProgressPayload(
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("batch_index")] int BatchIndex,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("current_stage")] string CurrentStage,
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("decided_count")] int DecidedCount,
    [property: JsonPropertyName("accepted_count")] int AcceptedCount,
    [property: JsonPropertyName("rejected_count")] int RejectedCount,
    [property: JsonPropertyName("review_count")] int ReviewCount,
    [property: JsonPropertyName("vector_count")] int VectorCount,
    [property: JsonPropertyName("model_call_count")] int ModelCallCount,
    [property: JsonPropertyName("started_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("last_error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LastErrorCode,
    [property: JsonPropertyName("last_error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LastErrorMessage,
    [property: JsonPropertyName("row_version")] long RowVersion);

public sealed record ReferenceMaterializationStatusPayload(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("split_profile_id")] string SplitProfileId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("chapter_batch_size")] int ChapterBatchSize,
    [property: JsonPropertyName("total_chapters")] int TotalChapters,
    [property: JsonPropertyName("processed_chapters")] int ProcessedChapters,
    [property: JsonPropertyName("total_chapter_batches")] int TotalChapterBatches,
    [property: JsonPropertyName("completed_chapter_batches")] int CompletedChapterBatches,
    [property: JsonPropertyName("current_batch_index")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CurrentBatchIndex,
    [property: JsonPropertyName("current_batch_start_chapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CurrentBatchStartChapter,
    [property: JsonPropertyName("current_batch_end_chapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CurrentBatchEndChapter,
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("accepted_count")] int AcceptedCount,
    [property: JsonPropertyName("rejected_count")] int RejectedCount,
    [property: JsonPropertyName("review_count")] int ReviewCount,
    [property: JsonPropertyName("vector_count")] int VectorCount,
    [property: JsonPropertyName("llm")] ReferenceMaterializationModelIdentityPayload Llm,
    [property: JsonPropertyName("embedding")] ReferenceMaterializationModelIdentityPayload Embedding,
    [property: JsonPropertyName("last_error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LastErrorCode,
    [property: JsonPropertyName("last_error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LastErrorMessage,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("vector_index_healthy")] bool VectorIndexHealthy,
    [property: JsonPropertyName("next_action")] string NextAction);
