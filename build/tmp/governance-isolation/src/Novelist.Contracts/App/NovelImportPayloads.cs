using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class NovelImportKinds
{
    public const string Epub = "epub";
    public const string Txt = "txt";
    public const string Markdown = "markdown";
}

public static class NovelImportRunStates
{
    public const string Created = "created";
    public const string Parsing = "parsing";
    public const string CreatingNovel = "creating_novel";
    public const string WritingFiles = "writing_files";
    public const string SavingMetadata = "saving_metadata";
    public const string Indexing = "indexing";
    public const string GitCommit = "git_commit";
    public const string Completed = "completed";
    public const string CompletedWithWarning = "completed_with_warning";
    public const string CleanupPending = "cleanup_pending";
    public const string CleanupCompleted = "cleanup_completed";
    public const string CleanupBlocked = "cleanup_blocked";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record StartNovelImportPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("source_display_name")] string SourceDisplayName,
    [property: JsonPropertyName("import_kind")] string ImportKind,
    [property: JsonPropertyName("requested_title")]
    string? RequestedTitle,
    [property: JsonPropertyName("commit_message")]
    string? CommitMessage);

public sealed record CancelNovelImportPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record GetNovelImportRunPayload(
    [property: JsonPropertyName("task_id")] string TaskId);

public sealed record NovelImportProgressPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("progress_completed")] int ProgressCompleted,
    [property: JsonPropertyName("progress_total")] int ProgressTotal,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("created_novel_id")]
    long? CreatedNovelId,
    [property: JsonPropertyName("current_chapter_index")]
    int? CurrentChapterIndex,
    [property: JsonPropertyName("current_chapter_title")]
    string? CurrentChapterTitle,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record NovelImportRunPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("source_display_name")] string SourceDisplayName,
    [property: JsonPropertyName("source_path_hash")] string SourcePathHash,
    [property: JsonPropertyName("parser_type")] string ParserType,
    [property: JsonPropertyName("created_novel_id")]
    long? CreatedNovelId,
    [property: JsonPropertyName("created_file_roots")] IReadOnlyList<string> CreatedFileRoots,
    [property: JsonPropertyName("skipped_chapters")] IReadOnlyList<NovelImportSkippedChapterPayload> SkippedChapters,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics,
    [property: JsonPropertyName("warnings")] IReadOnlyList<NovelImportWarningPayload> Warnings,
    [property: JsonPropertyName("error")]
    CopyableDiagnosticPayload? Error,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("completed_at")]
    DateTimeOffset? CompletedAt);

public sealed record NovelImportSkippedChapterPayload(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record NovelImportDiagnosticPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("severity")] string Severity);

public sealed record NovelImportWarningPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record NovelImportEncodingDiagnosticPayload(
    [property: JsonPropertyName("encoding_name")] string EncodingName,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("detection_source")] string DetectionSource,
    [property: JsonPropertyName("replacement_character_count")] int ReplacementCharacterCount,
    [property: JsonPropertyName("binary_like")] bool BinaryLike,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics);

public sealed record NovelImportSizeLimitErrorPayload(
    [property: JsonPropertyName("source_display_name")] string SourceDisplayName,
    [property: JsonPropertyName("limit_kind")] string LimitKind,
    [property: JsonPropertyName("observed_bytes")] long ObservedBytes,
    [property: JsonPropertyName("limit_bytes")] long LimitBytes);

public sealed record NovelImportRecoveryStatusPayload(
    [property: JsonPropertyName("pending_runs")] IReadOnlyList<NovelImportRunPayload> PendingRuns,
    [property: JsonPropertyName("blocked_runs")] IReadOnlyList<NovelImportRunPayload> BlockedRuns,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt);

public sealed record NovelImportReconciliationResultPayload(
    [property: JsonPropertyName("reconciled_runs")] IReadOnlyList<NovelImportRunPayload> ReconciledRuns,
    [property: JsonPropertyName("blocked_runs")] IReadOnlyList<NovelImportRunPayload> BlockedRuns,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics,
    [property: JsonPropertyName("reconciled_at")] DateTimeOffset ReconciledAt);
