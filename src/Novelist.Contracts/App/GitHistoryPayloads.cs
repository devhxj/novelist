using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record GetGitCommitsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("size")] int Size);

public sealed record GetGitCommitFilesPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("commit_id")] string CommitId);

public sealed record GetGitFileDiffPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("path")] string Path);

public sealed record GitCommitSummaryPayload(
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("short_commit_id")] string ShortCommitId,
    [property: JsonPropertyName("author_name")] string AuthorName,
    [property: JsonPropertyName("author_email")] string AuthorEmail,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("committed_at")] DateTimeOffset CommittedAt,
    [property: JsonPropertyName("changed_file_count")] int ChangedFileCount);

public sealed record GitCommitFilePayload(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("old_path")]
    string? OldPath,
    [property: JsonPropertyName("change_type")] string ChangeType,
    [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions,
    [property: JsonPropertyName("binary")] bool Binary);

public sealed record GitFileDiffPayload(
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("old_path")]
    string? OldPath,
    [property: JsonPropertyName("change_type")] string ChangeType,
    [property: JsonPropertyName("diff_text")] string DiffText,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("binary")] bool Binary);

public sealed record GitAuthorSettingsPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("scope")] string Scope);

public sealed record SaveGitAuthorSettingsPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email);
