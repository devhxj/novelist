using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IVersionControlService
{
    ValueTask EnsureRepositoryAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<VersionControlCommitResult> CommitIfChangedAsync(
        long novelId,
        string message,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<VersionControlCommitInfo>> GetLogAsync(
        long novelId,
        string? relativePath,
        int count,
        CancellationToken cancellationToken);

    ValueTask<PageResultPayload<GitCommitSummaryPayload>> GetCommitSummariesAsync(
        GetGitCommitsPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GitCommitFilePayload>> GetCommitFilesAsync(
        GetGitCommitFilesPayload input,
        CancellationToken cancellationToken);

    ValueTask<GitFileDiffPayload> GetFileDiffAsync(
        GetGitFileDiffPayload input,
        CancellationToken cancellationToken);
}

public sealed record VersionControlCommitResult(
    bool Committed,
    string CommitHash);

public sealed record VersionControlCommitInfo(
    string Hash,
    string Message,
    DateTimeOffset Time);

public sealed class VersionControlException : Exception
{
    public VersionControlException(string message)
        : base(message)
    {
    }

    public VersionControlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
