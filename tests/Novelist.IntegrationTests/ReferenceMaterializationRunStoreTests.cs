using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateRunRequiresConfirmedSplitAndPersistsAllChapterProgressInFrozenBatches()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options, chapterCount: 12);
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None));

        var confirmed = await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var created = await store.CreateAsync(CreateSeed(anchor.AnchorId, confirmed.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(created.RunId, page: 1, size: 20, CancellationToken.None);

        Assert.Equal(ReferenceMaterializationRunStates.Queued, created.Status);
        Assert.Equal(12, created.TotalChapters);
        Assert.Equal(3, created.TotalChapterBatches);
        Assert.Equal(0, created.CurrentBatchIndex);
        Assert.Equal(1, created.CurrentBatchStartChapter);
        Assert.Equal(5, created.CurrentBatchEndChapter);
        Assert.Equal(12, progress.Total);
        Assert.All(progress.Items, item => Assert.Equal(ReferenceMaterializationChapterStates.Pending, item.Status));
        Assert.Equal([0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2], progress.Items.Select(item => item.BatchIndex).ToArray());
    }

    [Fact]
    public async Task CreateRunRejectsAnyBatchSizeOtherThanFiveOrTen()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options, chapterCount: 2);
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 7), CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(AppInitializationOptions options, int chapterCount)
    {
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("运行仓库", "", ""), CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "run-store.md");
        var source = string.Join(
            "\n\n",
            Enumerable.Range(1, chapterCount).Select(index => $"# 第{index}章\n\n第 {index} 章正文。"));
        await File.WriteAllTextAsync(sourcePath, source);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        return await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "运行仓库来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
    }

    private static ReferenceMaterializationRunSeed CreateSeed(long anchorId, string profileId, int chapterBatchSize)
    {
        return new ReferenceMaterializationRunSeed(
            RunId: Guid.NewGuid().ToString("N"),
            AnchorId: anchorId,
            SplitProfileId: profileId,
            GenerationId: Guid.NewGuid().ToString("N"),
            PolicyVersion: "policy-v1",
            CandidateVersion: "candidate-v1",
            QualifierVersion: "qualifier-v1",
            Llm: new ReferenceMaterializationModelIdentityPayload("provider", "model"),
            Embedding: new ReferenceMaterializationModelIdentityPayload("embedding-provider", "embedding-model", 8),
            ChapterBatchSize: chapterBatchSize,
            StartedAt: DateTimeOffset.UtcNow);
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data"),
            EnableLegacyMigration = false
        };
    }

    private sealed class EmptyChapterSplitAnalyzer : Novelist.Core.App.IReferenceChapterSplitAnalyzer
    {
        public ValueTask<Novelist.Core.App.ReferenceChapterSplitModelResult> AnalyzeAsync(
            Novelist.Core.App.ReferenceChapterSplitModelRequest input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Novelist.Core.App.ReferenceChapterSplitModelResult.Empty);
        }
    }
}
