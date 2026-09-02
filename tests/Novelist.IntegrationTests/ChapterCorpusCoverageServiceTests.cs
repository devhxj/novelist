using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public class ChapterCorpusCoverageServiceTests
{
    [Fact]
    public void SplitBeatsStripsBulletsAndBlankLines()
    {
        var beats = ChapterCorpusCoverageService.SplitBeats("- 林岚在雨夜门口发现水痕\n\n* 审问守门人\n无冲突过渡\n");
        Assert.Equal(new[] { "林岚在雨夜门口发现水痕", "审问守门人", "无冲突过渡" }, beats);
    }

    [Fact]
    public async Task ComputeCoverageMarksBeatsCoveredByRetrievalHits()
    {
        var planning = new StubPlanningService(
        [
            new ChapterPlanPayload(42, "next", "- 雨夜门口对峙\n- 旧城门追查线索\n- 无关的日常过渡"),
        ]);
        var anchors = new StubAnchorService(
        [
            new ReferenceAnchorPayload(
                101, 42, "全局雨夜参考", "作者", "D:\\books\\a.md", "markdown", "user_provided",
                "hash", "v1", ReferenceAnchorBuildStates.Ready,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "private", "user_verified", []),
        ],
        query => query.Contains("雨夜") || query.Contains("线索"));

        var service = new ChapterCorpusCoverageService(anchors, planning);
        var coverage = await service.ComputeCoverageAsync(new GetChapterCorpusCoveragePayload(42, 1), CancellationToken.None);

        Assert.Equal("next", coverage.Scope);
        Assert.Equal(3, coverage.TotalCount);
        Assert.Equal(2, coverage.CoveredCount);
        Assert.Equal(2d / 3d, coverage.CoverageRatio, 4);
        Assert.True(coverage.Sufficient);
        Assert.Contains(coverage.Beats, beat => beat.Covered && beat.AnchorTitle == "全局雨夜参考");
        var uncovered = Assert.Single(coverage.Beats, beat => !beat.Covered);
        Assert.Equal("无关的日常过渡", uncovered.Beat);
        Assert.Null(uncovered.AnchorTitle);
    }

    [Fact]
    public async Task ComputeCoverageReportsInsufficientBelowHalfAndEmptyWithoutPlan()
    {
        var service = new ChapterCorpusCoverageService(
            new StubAnchorService([], _ => false),
            new StubPlanningService([new ChapterPlanPayload(42, "next", "- 只有这一个 beat")]));

        var low = await service.ComputeCoverageAsync(new GetChapterCorpusCoveragePayload(42), CancellationToken.None);
        Assert.Equal(1, low.TotalCount);
        Assert.Equal(0, low.CoveredCount);
        Assert.False(low.Sufficient);

        var empty = await new ChapterCorpusCoverageService(
            new StubAnchorService([], _ => false),
            new StubPlanningService([]))
            .ComputeCoverageAsync(new GetChapterCorpusCoveragePayload(42), CancellationToken.None);
        Assert.Equal(0, empty.TotalCount);
        Assert.Empty(empty.Beats);
        Assert.False(empty.Sufficient);
    }

    [Fact]
    public async Task ComputeCoverageSearchesAllBeatsInOneBatchAndMarksTruncation()
    {
        var beatLines = Enumerable.Range(1, 45).Select(index => $"- 第{index}个节拍").ToArray();
        var planning = new StubPlanningService(
        [
            new ChapterPlanPayload(42, "next", string.Join('\n', beatLines)),
        ]);
        var anchors = new StubAnchorService(
        [
            new ReferenceAnchorPayload(
                101, 42, "全局雨夜参考", "作者", "D:\\books\\a.md", "markdown", "user_provided",
                "hash", "v1", ReferenceAnchorBuildStates.Ready,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "private", "user_verified", []),
        ],
        _ => true);
        var service = new ChapterCorpusCoverageService(anchors, planning);

        var coverage = await service.ComputeCoverageAsync(new GetChapterCorpusCoveragePayload(42, 1), CancellationToken.None);

        // 性能约束：N 个 beat 只触发一次批量检索（一次材料全量读取），而非 N 次独立检索。
        Assert.Equal(1, anchors.BatchCallCount);
        Assert.Equal(40, anchors.BatchedQueryCount);
        Assert.Equal(40, coverage.TotalCount);
        Assert.Equal(40, coverage.CoveredCount);
        Assert.True(coverage.Sufficient);
        Assert.True(coverage.Truncated);
    }

    [Fact]
    public async Task ComputeCoverageIgnoresMaterialsFromNonReadyAnchors()
    {
        var planning = new StubPlanningService(
        [
            new ChapterPlanPayload(42, "next", "- 雨夜门口对峙"),
        ]);
        var building = new ReferenceAnchorPayload(
            101, 42, "全局雨夜参考", "作者", "D:\\books\\a.md", "markdown", "user_provided",
            "hash", "v1", "building",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "private", "user_verified", []);
        var anchors = new StubAnchorService([building], _ => true);
        var service = new ChapterCorpusCoverageService(anchors, planning);

        var coverage = await service.ComputeCoverageAsync(new GetChapterCorpusCoveragePayload(42, 1, Refresh: true), CancellationToken.None);

        // 覆盖口径与注入一致：非 Ready 锚点的材料不会被实际注入，不能计入覆盖。
        Assert.Equal(1, coverage.TotalCount);
        Assert.Equal(0, coverage.CoveredCount);
        Assert.False(coverage.Sufficient);
    }

    private sealed class StubPlanningService(IReadOnlyList<ChapterPlanPayload> plans) : IPlanningService
    {
        public ValueTask<IReadOnlyList<ChapterPlanPayload>> GetChapterPlansAsync(long novelId, CancellationToken cancellationToken)
            => ValueTask.FromResult(plans);

        public ValueTask UpdateChapterPlanAsync( long novelId, UpdateChapterPlanPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<TimelineEntryPayload>> GetTimelineEntriesAsync( long novelId, int fromChapter, int toChapter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<TimelineEntryPayload> CreateTimelineEntryAsync( long novelId, CreateTimelineEntryPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateTimelineEntryAsync( long novelId, long entryId, UpdateTimelineEntryPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteTimelineEntryAsync( long novelId, long entryId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<StoryArcPayload>> GetStoryArcsAsync( long novelId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<StoryArcPayload> CreateStoryArcAsync( long novelId, CreateStoryArcPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateStoryArcAsync( long novelId, long arcId, UpdateStoryArcPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteStoryArcAsync( long novelId, long arcId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ArcNodePayload>> GetArcNodesAsync( long novelId, int fromChapter, int toChapter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArcNodePayload> CreateArcNodeAsync( long novelId, CreateArcNodePayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateArcNodeAsync( long novelId, long nodeId, UpdateArcNodePayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteArcNodeAsync( long novelId, long nodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ReaderPerspectivePayload>> GetReaderPerspectivesAsync( long novelId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReaderPerspectivePayload> CreateReaderPerspectiveAsync( long novelId, CreateReaderPerspectivePayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateReaderPerspectiveAsync( long novelId, long perspectiveId, UpdateReaderPerspectivePayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteReaderPerspectiveAsync( long novelId, long perspectiveId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubAnchorService(
        IReadOnlyList<ReferenceAnchorPayload> anchors,
        Func<string, bool> matchQuery) : IReferenceAnchorService
    {
        public int BatchCallCount { get; private set; }

        public int BatchedQueryCount { get; private set; }

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(long novelId, CancellationToken cancellationToken)
            => ValueTask.FromResult(anchors);

        public async ValueTask<IReadOnlyList<PageResultPayload<ReferenceMaterialPayload>>> SearchMaterialsBatchAsync(
            IReadOnlyList<SearchReferenceMaterialsPayload> inputs,
            CancellationToken cancellationToken)
        {
            BatchCallCount++;
            BatchedQueryCount += inputs.Count;
            var results = new List<PageResultPayload<ReferenceMaterialPayload>>(inputs.Count);
            foreach (var input in inputs)
            {
                results.Add(await SearchMaterialsAsync(input, cancellationToken));
            }

            return results;
        }

        public ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(SearchReferenceMaterialsPayload input, CancellationToken cancellationToken)
        {
            // 与真实服务一致：ReadyOnly 只统计就绪锚点，非 Ready 书的材料不参与覆盖判定。
            if (input.ReadyOnly == true && anchors.All(anchor => !string.Equals(anchor.Status, ReferenceAnchorBuildStates.Ready, StringComparison.Ordinal)))
            {
                return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialPayload>([], 0, 1, 1, 0));
            }

            if (matchQuery(input.Query))
            {
                var material = new ReferenceMaterialPayload(
                    "mat-1", 101, "seg-1", "sentence",
                    "environment", "restrained", "rain_threshold", "close", "delayed_reaction",
                    0.9, 0.88, 0.9,
                    "雨声压低了整条街的呼吸。",
                    "hash", "test", true, DateTimeOffset.UtcNow);
                return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialPayload>([material], 1, 1, 1, 1));
            }

            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialPayload>([], 0, 1, 1, 0));
        }

        public ValueTask<ReferenceAnchorPayload> CreateAnchorAsync( CreateReferenceAnchorPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceAnchorPayload> RegisterMaterializationSourceAsync( CreateReferenceAnchorPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> CreateAnchorsAsync( CreateReferenceAnchorsPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CreateReferenceAnchorsResultPayload> CreateAnchorsWithResultAsync( CreateReferenceAnchorsPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorAsync( long novelId, long anchorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceAnchorBuildStatusPayload?> GetBuildStatusAsync( long novelId, long anchorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceMaterialEmbeddingBackfillPayload> BackfillMaterialEmbeddingsAsync( BackfillReferenceMaterialEmbeddingsPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceMaterialCoveragePayload> GetMaterialCoverageAsync( GetReferenceMaterialCoveragePayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PageResultPayload<ReferenceMaterialTagReviewItemPayload>> GetMaterialTagReviewQueueAsync( GetReferenceMaterialTagReviewQueuePayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceMaterialDetailPayload?> GetMaterialDetailAsync( GetReferenceMaterialDetailPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceSourceSegmentDetailPayload?> GetSourceSegmentDetailAsync( GetReferenceSourceSegmentDetailPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceSourceProcessingDetailPayload?> GetSourceProcessingDetailAsync( GetReferenceSourceProcessingDetailPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceMaterialPayload> UpdateMaterialTagsAsync( UpdateReferenceMaterialTagsPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ReferenceMaterialPayload>> UpdateMaterialsTagsAsync( UpdateReferenceMaterialsTagsPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceUserFeedbackPayload> RecordUserFeedbackAsync( RecordReferenceUserFeedbackPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ReferenceUserFeedbackPayload>> GetUserFeedbackAsync( GetReferenceUserFeedbackPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteAnchorAsync( long novelId, long anchorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteAnchorsAsync( DeleteReferenceAnchorsPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteMaterialsAsync( DeleteReferenceMaterialsPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RestoreMaterialsAsync( RestoreReferenceMaterialsPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceAnchorPayload> PromoteAnchorToWorkspaceCorpusAsync( PromoteReferenceAnchorToWorkspaceCorpusPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> PromoteAnchorsToWorkspaceCorpusAsync( PromoteReferenceAnchorsToWorkspaceCorpusPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync( UpdateReferenceAnchorMetadataPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
