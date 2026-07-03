using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IPlanningService
{
    ValueTask<IReadOnlyList<ChapterPlanPayload>> GetChapterPlansAsync(
        long novelId,
        CancellationToken cancellationToken);

    ValueTask UpdateChapterPlanAsync(
        long novelId,
        UpdateChapterPlanPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<TimelineEntryPayload>> GetTimelineEntriesAsync(
        long novelId,
        int fromChapter,
        int toChapter,
        CancellationToken cancellationToken);

    ValueTask<TimelineEntryPayload> CreateTimelineEntryAsync(
        long novelId,
        CreateTimelineEntryPayload input,
        CancellationToken cancellationToken);

    ValueTask UpdateTimelineEntryAsync(
        long novelId,
        long entryId,
        UpdateTimelineEntryPayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteTimelineEntryAsync(
        long novelId,
        long entryId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<StoryArcPayload>> GetStoryArcsAsync(
        long novelId,
        CancellationToken cancellationToken);

    ValueTask<StoryArcPayload> CreateStoryArcAsync(
        long novelId,
        CreateStoryArcPayload input,
        CancellationToken cancellationToken);

    ValueTask UpdateStoryArcAsync(
        long novelId,
        long arcId,
        UpdateStoryArcPayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteStoryArcAsync(
        long novelId,
        long arcId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ArcNodePayload>> GetArcNodesAsync(
        long novelId,
        int fromChapter,
        int toChapter,
        CancellationToken cancellationToken);

    ValueTask<ArcNodePayload> CreateArcNodeAsync(
        long novelId,
        CreateArcNodePayload input,
        CancellationToken cancellationToken);

    ValueTask UpdateArcNodeAsync(
        long novelId,
        long nodeId,
        UpdateArcNodePayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteArcNodeAsync(
        long novelId,
        long nodeId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReaderPerspectivePayload>> GetReaderPerspectivesAsync(
        long novelId,
        CancellationToken cancellationToken);

    ValueTask<ReaderPerspectivePayload> CreateReaderPerspectiveAsync(
        long novelId,
        CreateReaderPerspectivePayload input,
        CancellationToken cancellationToken);

    ValueTask UpdateReaderPerspectiveAsync(
        long novelId,
        long perspectiveId,
        UpdateReaderPerspectivePayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteReaderPerspectiveAsync(
        long novelId,
        long perspectiveId,
        CancellationToken cancellationToken);
}
