using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface INovelImportRunService
{
    ValueTask<NovelImportRunPayload> StartRunAsync(
        StartNovelImportPayload input,
        CancellationToken cancellationToken);

    ValueTask<NovelImportRunPayload> CancelRunAsync(
        CancelNovelImportPayload input,
        CancellationToken cancellationToken);

    ValueTask<NovelImportRunPayload?> GetRunAsync(
        GetNovelImportRunPayload input,
        CancellationToken cancellationToken);

    ValueTask<NovelImportRecoveryStatusPayload> GetRecoveryStatusAsync(CancellationToken cancellationToken);

    ValueTask<NovelImportRunPayload> UpdateRunAsync(
        NovelImportRunUpdate update,
        CancellationToken cancellationToken);
}

public sealed record NovelImportRunUpdate(
    string TaskId,
    string State,
    string Stage,
    long? CreatedNovelId,
    IReadOnlyList<string>? CreatedFileRoots,
    IReadOnlyList<NovelImportSkippedChapterPayload>? SkippedChapters,
    IReadOnlyList<NovelImportDiagnosticPayload>? Diagnostics,
    IReadOnlyList<NovelImportWarningPayload>? Warnings,
    CopyableDiagnosticPayload? Error);
