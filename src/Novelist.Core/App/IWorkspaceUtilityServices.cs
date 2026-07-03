using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface ISkillCatalogService
{
    ValueTask<IReadOnlyList<SkillMetaPayload>> ListSkillsAsync(
        ListSkillsPayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteSkillAsync(DeleteSkillPayload input, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SlashCommandPayload>> ListSlashCommandsAsync(
        ListSlashCommandsPayload input,
        CancellationToken cancellationToken);

    ValueTask<ExtractStyleResultPayload> ExtractStyleAsync(
        ExtractStylePayload input,
        CancellationToken cancellationToken);
}

public interface IWorkspaceSearchService
{
    ValueTask<IReadOnlyList<SearchResultPayload>> SearchAllAsync(
        long novelId,
        string query,
        CancellationToken cancellationToken);

    ValueTask RebuildNovelIndexAsync(long novelId, CancellationToken cancellationToken);
}

public interface IStoryMemorySearchService
{
    ValueTask<SearchStoryMemoryResultPayload> SearchAsync(
        SearchStoryMemoryPayload input,
        CancellationToken cancellationToken);
}

public interface INovelExportService
{
    ValueTask ExportNovelAsync(long novelId, string format, CancellationToken cancellationToken);
}

public interface INovelExportDestinationPicker
{
    ValueTask<string?> PickSaveFileAsync(
        NovelExportDestinationRequest request,
        CancellationToken cancellationToken);
}

public sealed record NovelExportDestinationRequest(
    string DefaultFileName,
    string Format,
    IReadOnlyList<NovelExportFileFilter> Filters);

public sealed record NovelExportFileFilter(
    string DisplayName,
    string Pattern);

public interface IWritingStatisticsService
{
    ValueTask<IReadOnlyList<DailyActivityPayload>> GetWritingActivityAsync(
        int months,
        CancellationToken cancellationToken);

    ValueTask<WritingStatsPayload> GetWritingStatsAsync(CancellationToken cancellationToken);
}

public interface IWritingDeltaRecorder
{
    ValueTask RecordWordDeltaAsync(
        long novelId,
        long chapterId,
        int wordDelta,
        CancellationToken cancellationToken);
}
