using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
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

    [Fact]
    public async Task CreateRunCanRetryWhenItsFirstSchemaInitializationFails()
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
        var store = new SqliteReferenceMaterializationRunStore(
            new FailFirstCorpusDatabasePathResolver(new ReferenceCorpusDatabasePathResolver(options)));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None));

        var created = await store.CreateAsync(
            CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5),
            CancellationToken.None);

        Assert.Equal(ReferenceMaterializationRunStates.Queued, created.Status);
    }

    [Fact]
    public async Task BuildCandidatesPersistsOnlyChapterLocalNodeEvidenceAndIsIdempotent()
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
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);

        var first = await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        var second = await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);

        Assert.True(first.CandidateCount > 0);
        Assert.Equal(first.CandidateCount, second.CandidateCount);
        var chapterOne = Assert.Single(progress.Items, item => item.ChapterIndex == 1);
        Assert.Equal(ReferenceMaterializationChapterStates.LlmQualifying, chapterOne.Status);
        Assert.Equal(first.CandidateCount, chapterOne.CandidateCount);
        Assert.Equal(first.CandidateCount, await CountCandidatesForChapterAsync(options, run.RunId, chapterIndex: 1));
        Assert.Equal(0, await CountCandidateLinksOutsideChapterAsync(options, run.RunId, chapterIndex: 1));
    }

    [Fact]
    public async Task QualificationPersistsOnlyACompleteGroundedDecisionSetAndAdvancesTheChapterToEmbedding()
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
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex: 1, CancellationToken.None);

        var work = await store.ReadQualificationWorkItemAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        var result = new ReferenceMaterializationQualificationResult(
            work.Request.Candidates.Select((candidate, index) => new ReferenceMaterializationCandidateQualification(
                candidate.CandidateId,
                index == 0
                    ? ReferenceMaterializationCandidateDecisions.Accepted
                    : ReferenceMaterializationCandidateDecisions.Rejected,
                [new ReferenceMaterializationQualificationSpan(candidate.SourceNodes[0].NodeId, 0, candidate.SourceNodes[0].Text.Length)],
                new ReferenceMaterializationQualityScores(0.9, 0.8, 0.7, 0.6, 0.5, 0.4),
                new ReferenceMaterializationQualificationTags(["reveal"], [], ["close_third"], ["subtext"]),
                0.85,
                ["complete_exchange"])).ToArray());

        var applied = await store.PersistQualificationAsync(run.RunId, chapterIndex: 1, result, CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);
        var chapter = Assert.Single(progress.Items, item => item.ChapterIndex == 1);

        Assert.Equal(work.Request.Model.ProviderName, work.Model.ProviderName);
        Assert.Equal(work.Request.Model.ModelId, work.Model.ModelId);
        Assert.Equal(work.Request.Candidates.Count, applied.DecidedCount);
        Assert.Equal(1, applied.AcceptedCount);
        Assert.Equal(work.Request.Candidates.Count - 1, applied.RejectedCount);
        Assert.Equal(ReferenceMaterializationChapterStates.Embedding, chapter.Status);
        Assert.Equal(applied.DecidedCount, chapter.DecidedCount);
        Assert.Equal(applied.AcceptedCount, chapter.AcceptedCount);
        Assert.Equal(applied.RejectedCount, chapter.RejectedCount);
        Assert.Equal(0, await CountPendingCandidatesForChapterAsync(options, run.RunId, chapterIndex: 1));
    }

    [Fact]
    public async Task QualificationRejectsPartialDecisionSetsWithoutChangingPendingCandidates()
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
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex: 1, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.PersistQualificationAsync(
                run.RunId,
                chapterIndex: 1,
                new ReferenceMaterializationQualificationResult([]),
                CancellationToken.None));

        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);
        var chapter = Assert.Single(progress.Items, item => item.ChapterIndex == 1);
        Assert.Equal(ReferenceMaterializationChapterStates.LlmQualifying, chapter.Status);
        Assert.Equal(chapter.CandidateCount, await CountPendingCandidatesForChapterAsync(options, run.RunId, chapterIndex: 1));
    }

    [Fact]
    public async Task EmbeddingPersistsOneFiniteFrozenVectorForEveryAcceptedCandidate()
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
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        var qualificationWork = await store.ReadQualificationWorkItemAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        await store.PersistQualificationAsync(
            run.RunId,
            chapterIndex: 1,
            new ReferenceMaterializationQualificationResult(
                qualificationWork.Request.Candidates.Select(candidate => AcceptedDecision(candidate)).ToArray()),
            CancellationToken.None);

        var embeddingWork = await store.ReadEmbeddingWorkItemAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        var embedder = new ReferenceMaterializationEmbeddingProcessor(
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "embedding-provider", "https://example.invalid", "key", "embedding-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 8));
        var result = await embedder.EmbedAsync(embeddingWork.Request, CancellationToken.None);
        var persisted = await store.PersistEmbeddingsAsync(run.RunId, chapterIndex: 1, result, CancellationToken.None);

        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);
        var chapter = Assert.Single(progress.Items, item => item.ChapterIndex == 1);
        Assert.Equal(embeddingWork.Request.Items.Count, persisted.VectorCount);
        Assert.Equal(embeddingWork.Request.Items.Count, chapter.VectorCount);
        Assert.Equal(ReferenceMaterializationChapterStates.Indexing, chapter.Status);
        Assert.Equal(embeddingWork.Request.Items.Count, await CountEmbeddingsForChapterAsync(options, run.RunId, chapterIndex: 1));
    }

    [Fact]
    public async Task EmbeddingRejectsMismatchedProviderResponseBeforeAnyPersistence()
    {
        var embedder = new ReferenceMaterializationEmbeddingProcessor(
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "embedding-provider", "https://example.invalid", "key", "embedding-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 7));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await embedder.EmbedAsync(
                new ReferenceMaterializationEmbeddingRequest(
                    new ReferenceMaterializationEmbeddingModel("embedding-provider", "embedding-model", 8),
                    [new ReferenceMaterializationEmbeddingItem("candidate-1", "完整的证据文本。")]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task GenerationVectorIndexCompletesOnlyWhenTheWholeCurrentBatchHasCompleteVectors()
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
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var embedder = new ReferenceMaterializationEmbeddingProcessor(
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "embedding-provider", "https://example.invalid", "key", "embedding-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 8));

        foreach (var chapterIndex in new[] { 1, 2 })
        {
            await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex, CancellationToken.None);
            var qualification = await store.ReadQualificationWorkItemAsync(run.RunId, chapterIndex, CancellationToken.None);
            await store.PersistQualificationAsync(
                run.RunId,
                chapterIndex,
                new ReferenceMaterializationQualificationResult(
                    qualification.Request.Candidates.Select(candidate => AcceptedDecision(candidate)).ToArray()),
                CancellationToken.None);
            var embedding = await store.ReadEmbeddingWorkItemAsync(run.RunId, chapterIndex, CancellationToken.None);
            await store.PersistEmbeddingsAsync(
                run.RunId,
                chapterIndex,
                await embedder.EmbedAsync(embedding.Request, CancellationToken.None),
                CancellationToken.None);
        }

        var vec = new RecordingVecProvisioner();
        var indexer = new ReferenceMaterializationVectorIndexer(resolver, vec);
        var indexed = await indexer.IndexCurrentBatchAsync(run.RunId, CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);
        var status = await store.GetAsync(run.RunId, CancellationToken.None);

        Assert.Equal(0, indexed.BatchIndex);
        Assert.True(indexed.VectorCount > 0);
        Assert.Equal(indexed.VectorCount, vec.LastRequest?.Vectors.Count);
        Assert.Contains("vec_reference_materialization_", vec.LastRequest?.TableName, StringComparison.Ordinal);
        Assert.All(progress.Items, item => Assert.Equal(ReferenceMaterializationChapterStates.Completed, item.Status));
        Assert.Equal(2, status?.ProcessedChapters);
        Assert.Equal(1, status?.CompletedChapterBatches);
    }

    [Fact]
    public async Task WorkerProcessesAllChaptersInTheFrozenBatchConcurrentlyBeforeAdvancing()
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
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var qualifier = new ConcurrentAcceptingQualifier();
        var indexer = new ReferenceMaterializationVectorIndexer(resolver, new RecordingVecProvisioner());
        var worker = new ReferenceMaterializationWorker(
            resolver,
            qualifier,
            new AcceptingEmbedder(),
            indexer,
            workerId: "test-materialization-worker");

        var before = await store.GetAsync(run.RunId, CancellationToken.None);
        Assert.Equal(ReferenceMaterializationRunStates.Queued, before?.Status);
        Assert.Equal(0, before?.CurrentBatchIndex);

        var processed = await worker.ProcessRunOnceAsync(run.RunId, CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);
        var status = await store.GetAsync(run.RunId, CancellationToken.None);

        Assert.True(processed);
        Assert.Equal(2, qualifier.InvocationCount);
        Assert.True(qualifier.MaximumConcurrency >= 2);
        Assert.All(progress.Items, item => Assert.Equal(ReferenceMaterializationChapterStates.Completed, item.Status));
        Assert.Equal(2, status?.ProcessedChapters);
        Assert.Equal(1, status?.CompletedChapterBatches);
        Assert.Null(status?.CurrentBatchIndex);
        Assert.True(
            status?.Status == ReferenceMaterializationRunStates.Completed,
            $"{status?.LastErrorCode}: {status?.LastErrorMessage}");
        Assert.True(status?.VectorIndexHealthy);
        Assert.Equal(status?.AcceptedCount, await CountPromotedMaterialsAsync(options, run.RunId));
        Assert.Equal(status?.GenerationId, await ReadActiveGenerationAsync(options, anchor.AnchorId));
        var materializationService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var listed = await materializationService.ListActiveMaterialsAsync(
            new ListActiveReferenceMaterializationMaterialsPayload(anchor.NovelId, anchor.AnchorId, 1, 20),
            CancellationToken.None);
        Assert.Equal(status!.AcceptedCount, checked((int)listed.Total));
        Assert.All(listed.Items, item => Assert.Equal(status.GenerationId, item.GenerationId));
        Assert.All(listed.Items, item =>
        {
            Assert.Equal(["turn_beat"], item.Tags.SceneBeatRoles);
            Assert.Equal(["mistrust"], item.Tags.CharacterRelations);
            Assert.Equal(["reveal"], item.Tags.CausalInformationRoles);
        });
    }

    [Fact]
    public async Task WorkerCompletesRuleRejectedOnlyChaptersWithoutCallingModels()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(
            options,
            chapterCount: 2,
            sourceOverride: "# 第一章\n\n嗯。\n\n# 第二章\n\n他点头。\n");
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var worker = new ReferenceMaterializationWorker(
            resolver,
            new FailingQualifier(),
            new AcceptingEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, new RecordingVecProvisioner()),
            workerId: "rule-rejection-worker");

        Assert.True(await worker.ProcessRunOnceAsync(run.RunId, CancellationToken.None));
        var status = await store.GetAsync(run.RunId, CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);
        var rejectedCandidates = await splitService.ListMaterializationCandidatesAsync(
            new ListReferenceMaterializationCandidatesPayload(
                anchor.NovelId,
                anchor.AnchorId,
                run.RunId,
                ReferenceMaterializationCandidateDecisions.Rejected),
            CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, status.Status);
        Assert.Equal(0, status.AcceptedCount);
        Assert.Equal(0, status.VectorCount);
        Assert.Equal(2, status.RejectedCount);
        Assert.Equal(2, rejectedCandidates.Items.Count);
        Assert.All(rejectedCandidates.Items, candidate => Assert.Equal("deterministic_triage", candidate.DecisionOrigin));
        Assert.Contains(rejectedCandidates.Items, candidate => candidate.ReasonCodes.SequenceEqual(["fragment"]));
        Assert.Contains(rejectedCandidates.Items, candidate => candidate.ReasonCodes.SequenceEqual(["generic_action"]));
        Assert.All(progress.Items, item =>
        {
            Assert.Equal(ReferenceMaterializationChapterStates.Completed, item.Status);
            Assert.Equal(1, item.RejectedCount);
            Assert.Equal(0, item.AcceptedCount);
            Assert.Equal(0, item.VectorCount);
        });
    }

    [Fact]
    public async Task ConfirmedReviewCandidateRequalifiesAndReindexesOnlyItsCompletedChapter()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options, chapterCount: 2);
        var materialization = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await materialization.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await materialization.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var provisioner = new RecordingVecProvisioner();
        var initialWorker = new ReferenceMaterializationWorker(
            resolver,
            new SingleReviewQualifier(),
            new AcceptingEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, provisioner),
            workerId: "test-review-initial-worker");

        Assert.True(await initialWorker.ProcessRunOnceAsync(run.RunId, CancellationToken.None));
        var initial = await store.GetAsync(run.RunId, CancellationToken.None);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, initial?.Status);
        Assert.Equal(1, initial?.ReviewCount);
        var reviewCandidate = Assert.Single((await materialization.ListMaterializationCandidatesAsync(
            new ListReferenceMaterializationCandidatesPayload(
                anchor.NovelId,
                anchor.AnchorId,
                run.RunId,
                ReferenceMaterializationCandidateDecisions.ReviewRequired),
            CancellationToken.None)).Items);
        Assert.NotEmpty(reviewCandidate.SourceSpans);

        var reviewed = await materialization.ReviewMaterializationCandidateAsync(
            new ReviewReferenceMaterializationCandidatePayload(
                anchor.NovelId,
                anchor.AnchorId,
                run.RunId,
                reviewCandidate.CandidateId,
                ReferenceMaterializationCandidateReviewActions.Confirm,
                reviewCandidate.RowVersion),
            CancellationToken.None);

        Assert.True(reviewed.RequalificationQueued);
        Assert.Equal(ReferenceMaterializationCandidateDecisions.Pending, reviewed.Decision);
        Assert.Equal(ReferenceMaterializationRunStates.Running, reviewed.Status.Status);
        Assert.Equal(0, reviewed.Status.CompletedChapterBatches);
        var conflict = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await materialization.ReviewMaterializationCandidateAsync(
                new ReviewReferenceMaterializationCandidatePayload(
                    anchor.NovelId,
                    anchor.AnchorId,
                    run.RunId,
                    reviewCandidate.CandidateId,
                    ReferenceMaterializationCandidateReviewActions.Confirm,
                    reviewCandidate.RowVersion),
                CancellationToken.None));
        Assert.Equal(ReferenceMaterializationErrorCodes.CandidateReviewConflict, conflict.ErrorCode);
        var reopened = await store.ListChapterProgressAsync(run.RunId, 1, 10, CancellationToken.None);
        Assert.Equal(ReferenceMaterializationChapterStates.LlmQualifying, Assert.Single(reopened.Items, item => item.ChapterIndex == reviewCandidate.ChapterIndex).Status);
        Assert.All(
            reopened.Items.Where(item => item.ChapterIndex != reviewCandidate.ChapterIndex),
            item => Assert.Equal(ReferenceMaterializationChapterStates.Completed, item.Status));

        var resumedWorker = new ReferenceMaterializationWorker(
            resolver,
            new ConcurrentAcceptingQualifier(),
            new AcceptingEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, provisioner),
            workerId: "test-review-resumed-worker");
        Assert.True(await resumedWorker.ProcessRunOnceAsync(run.RunId, CancellationToken.None));
        var completed = await store.GetAsync(run.RunId, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, completed.Status);
        Assert.Equal(initial!.GenerationId, completed.GenerationId);
        Assert.Equal(completed.AcceptedCount, completed.VectorCount);
        Assert.Equal(0, completed.ReviewCount);
        Assert.Equal(completed.GenerationId, await ReadActiveGenerationAsync(options, anchor.AnchorId));
        var acceptedCandidates = await materialization.ListMaterializationCandidatesAsync(
            new ListReferenceMaterializationCandidatesPayload(
                anchor.NovelId,
                anchor.AnchorId,
                run.RunId,
                ReferenceMaterializationCandidateDecisions.Accepted),
            CancellationToken.None);
        Assert.Contains(acceptedCandidates.Items, candidate => candidate.CandidateId == reviewCandidate.CandidateId);
    }

    [Fact]
    public async Task SemanticSearchUsesOnlyTheActiveGenerationVectorIndexWithoutLexicalPrefilter()
    {
        var options = CreateOptions();
        var (anchor, status, vec) = await CreateCompletedGenerationAsync(options);
        var query = "绝不重复的检索意图";
        var search = new SqliteReferenceMaterializationSemanticSearch(
            options,
            new ReferenceCorpusDatabasePathResolver(options),
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "embedding-provider", "https://example.invalid", "key", "embedding-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 8),
            vec);

        var results = await search.SearchAsync(anchor.AnchorId, query, maxResults: 10, CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.All(results, result =>
        {
            Assert.Equal(status.GenerationId, result.Material.GenerationId);
            Assert.DoesNotContain(query, result.Material.Text, StringComparison.Ordinal);
            Assert.InRange(result.VectorScore, 0, 1);
        });
        Assert.NotNull(vec.LastSearchRequest);
        Assert.Equal(
            SqliteVecTableProvisioner.BuildReferenceMaterializationVectorTableName(status.GenerationId, 8),
            vec.LastSearchRequest!.TableName);
        Assert.Equal(8, vec.LastSearchRequest.Dimensions);
    }

    [Fact]
    public async Task SemanticSearchFusesIndependentLexicalStructuredTechniqueAndVectorRoutes()
    {
        var options = CreateOptions();
        var (anchor, status, vec) = await CreateCompletedGenerationAsync(
            options,
            "# 第一章\n\nlexicaltoken sets the scene.\n\n# 第二章\n\nlexicaltoken turns the pressure.");
        var search = new SqliteReferenceMaterializationSemanticSearch(
            options,
            new ReferenceCorpusDatabasePathResolver(options),
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "embedding-provider", "https://example.invalid", "key", "embedding-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 8),
            vec);

        var results = await search.SearchAsync(anchor.AnchorId, "lexicaltoken reveal subtext", maxResults: 10, CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.All(results, result =>
        {
            Assert.Equal(status.GenerationId, result.Material.GenerationId);
            Assert.NotNull(result.ScoreComponents);
            Assert.True(result.ScoreComponents!.ContainsKey("semantic"));
            Assert.True(result.ScoreComponents.ContainsKey("lexical"));
            Assert.True(result.ScoreComponents.ContainsKey("structured"));
            Assert.True(result.ScoreComponents.ContainsKey("technique"));
            Assert.True(result.ScoreComponents.ContainsKey("quality"));
            Assert.True(result.ScoreComponents.ContainsKey("fused"));
        });
        Assert.Contains(results, result => result.ScoreComponents!["lexical"] > 0);
        Assert.Contains(results, result => result.ScoreComponents!["structured"] > 0);
        Assert.Contains(results, result => result.ScoreComponents!["technique"] > 0);
    }

    [Fact]
    public async Task SemanticSearchUsesTheLexicalRouteForShortChineseTerms()
    {
        var options = CreateOptions();
        var (anchor, _, vec) = await CreateCompletedGenerationAsync(
            options,
            "# 第一章\n\n雨声压住窗沿，门外响起第三次敲门。\n\n# 第二章\n\n雨声停下后，屋里更安静了。");
        var search = new SqliteReferenceMaterializationSemanticSearch(
            options,
            new ReferenceCorpusDatabasePathResolver(options),
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "embedding-provider", "https://example.invalid", "key", "embedding-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 8),
            vec);

        var results = await search.SearchAsync(anchor.AnchorId, "雨声", maxResults: 10, CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.Contains(results, result => result.ScoreComponents!["lexical"] > 0);
    }

    [Fact]
    public async Task SemanticSearchRejectsAnActiveEmbeddingConfigurationThatDriftedFromTheGeneration()
    {
        var options = CreateOptions();
        var (anchor, _, vec) = await CreateCompletedGenerationAsync(options);
        var search = new SqliteReferenceMaterializationSemanticSearch(
            options,
            new ReferenceCorpusDatabasePathResolver(options),
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "different-provider", "https://example.invalid", "key", "different-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 8),
            vec);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await search.SearchAsync(anchor.AnchorId, "检索意图", maxResults: 10, CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed, exception.ErrorCode);
        Assert.Null(vec.LastSearchRequest);
    }

    [Fact]
    public async Task SemanticSearchReportsVectorIndexFailuresInsteadOfReturningFallbackResults()
    {
        var options = CreateOptions();
        var (anchor, _, vec) = await CreateCompletedGenerationAsync(options);
        vec.SearchException = new InvalidOperationException("sqlite-vec is unavailable");
        var search = new SqliteReferenceMaterializationSemanticSearch(
            options,
            new ReferenceCorpusDatabasePathResolver(options),
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "embedding-provider", "https://example.invalid", "key", "embedding-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 8),
            vec);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await search.SearchAsync(anchor.AnchorId, "检索意图", maxResults: 10, CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.VectorIndexFailed, exception.ErrorCode);
        Assert.NotNull(vec.LastSearchRequest);
    }

    [Fact]
    public async Task ActiveMaterialQueriesRejectAGenerationFromThePreviousQualifierSchema()
    {
        var options = CreateOptions();
        var (anchor, status, vec) = await CreateCompletedGenerationAsync(options);
        await SetQualifierVersionAsync(options, status.RunId, "material-qualifier-v1");
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var search = new SqliteReferenceMaterializationSemanticSearch(
            options,
            new ReferenceCorpusDatabasePathResolver(options),
            new FixedEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "embedding-provider", "https://example.invalid", "key", "embedding-model", 8, null)),
            new FixedEmbeddingClient(dimensions: 8),
            vec);

        var listed = await service.ListActiveMaterialsAsync(
            new ListActiveReferenceMaterializationMaterialsPayload(anchor.NovelId, anchor.AnchorId, 1, 20),
            CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await search.SearchAsync(anchor.AnchorId, "检索意图", maxResults: 10, CancellationToken.None));

        Assert.Empty(listed.Items);
        Assert.Equal(0, listed.Total);
        Assert.Equal(ReferenceMaterializationErrorCodes.GenerationIncomplete, exception.ErrorCode);
    }

    [Fact]
    public async Task WorkerFailsTheCurrentBatchAndLeavesLaterBatchesUnclaimedWhenOneChapterFails()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options, chapterCount: 6);
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var worker = new ReferenceMaterializationWorker(
            resolver,
            new FailingQualifier(),
            new AcceptingEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, new RecordingVecProvisioner()),
            workerId: "test-materialization-failing-worker");

        var processed = await worker.ProcessRunOnceAsync(run.RunId, CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);
        var status = await store.GetAsync(run.RunId, CancellationToken.None);

        Assert.True(processed);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, status?.Status);
        Assert.Equal(ReferenceMaterializationErrorCodes.LlmRequestFailed, status?.LastErrorCode);
        Assert.All(progress.Items.Where(item => item.BatchIndex == 0), item =>
            Assert.Equal(ReferenceMaterializationChapterStates.Failed, item.Status));
        var later = Assert.Single(progress.Items, item => item.BatchIndex == 1);
        Assert.Equal(ReferenceMaterializationChapterStates.Pending, later.Status);
        Assert.Equal(0, later.CandidateCount);
    }

    [Fact]
    public async Task ClaimReclaimsAnExpiredLeaseAndResetsOnlyTheCurrentIncompleteBatch()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options, chapterCount: 6);
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var firstClaim = await store.ClaimCurrentBatchAsync(
            run.RunId,
            "expired-owner",
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.NotNull(firstClaim);
        await store.BuildCandidatesForChapterAsync(run.RunId, firstClaim!.ChapterIndexes[0], CancellationToken.None);
        await MarkLeaseExpiredAsync(options, run.RunId);

        var reclaimed = await store.ClaimCurrentBatchAsync(
            run.RunId,
            "recovery-owner",
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);
        var pendingCandidates = await splitService.ListMaterializationCandidatesAsync(
            new ListReferenceMaterializationCandidatesPayload(
                anchor.NovelId,
                anchor.AnchorId,
                run.RunId,
                ReferenceMaterializationCandidateDecisions.Pending),
            CancellationToken.None);

        Assert.NotNull(reclaimed);
        Assert.Equal([1, 2, 3, 4, 5], reclaimed!.ChapterIndexes);
        Assert.NotEmpty(pendingCandidates.Items);
        Assert.All(pendingCandidates.Items, item =>
        {
            Assert.Empty(item.Tags.NarrativeFunctions);
            Assert.Empty(item.Tags.Techniques);
        });
        Assert.All(progress.Items.Where(item => item.BatchIndex == 0), item =>
        {
            Assert.Equal(ReferenceMaterializationChapterStates.Pending, item.Status);
            Assert.Equal(0, item.CandidateCount);
            Assert.Equal(0, item.DecidedCount);
            Assert.Equal(0, item.VectorCount);
        });
        var later = Assert.Single(progress.Items, item => item.BatchIndex == 1);
        Assert.Equal(ReferenceMaterializationChapterStates.Pending, later.Status);
        await store.ReleaseBatchLeaseAsync(reclaimed, CancellationToken.None);
    }

    [Fact]
    public async Task WorkerHeartbeatKeepsALongRunningBatchLeaseOwnedUntilTheBatchCompletes()
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
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var qualifier = new BlockingQualifier();
        await using var worker = new ReferenceMaterializationWorker(
            resolver,
            qualifier,
            new AcceptingEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, new RecordingVecProvisioner()),
            workerId: "heartbeat-owner",
            leaseDuration: TimeSpan.FromMilliseconds(600));

        var processing = worker.ProcessRunOnceAsync(run.RunId, CancellationToken.None).AsTask();
        await qualifier.Started.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(900));
        var contender = await store.ClaimCurrentBatchAsync(
            run.RunId,
            "contending-owner",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Null(contender);
        qualifier.Release();
        Assert.True(await processing);
    }

    [Fact]
    public async Task ExplicitRetryResetsOnlyTheFailedCurrentBatchAndKeepsTheFrozenGeneration()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options, chapterCount: 6);
        var preflight = new RecordingPreflight(new ReferenceMaterializationModelPreflightResult(
            new ReferenceMaterializationModelIdentityPayload("provider", "model"),
            new ReferenceMaterializationModelIdentityPayload("embedding-provider", "embedding-model", 8)));
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer(), modelPreflight: preflight);
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var claim = await store.ClaimCurrentBatchAsync(run.RunId, "failing-owner", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(claim);
        await store.BuildCandidatesForChapterAsync(run.RunId, claim!.ChapterIndexes[0], CancellationToken.None);
        await store.FailCurrentBatchAsync(
            claim,
            ReferenceMaterializationErrorCodes.LlmRequestFailed,
            "Provider timed out.",
            CancellationToken.None);

        var retried = await service.RetryMaterializationAsync(
            new RetryReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, run.RunId),
            CancellationToken.None);
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);

        Assert.Equal(1, preflight.CallCount);
        Assert.Equal(ReferenceMaterializationRunStates.Running, retried.Status);
        Assert.Equal(run.GenerationId, retried.GenerationId);
        Assert.All(progress.Items.Where(item => item.BatchIndex == 0), item =>
            Assert.Equal(ReferenceMaterializationChapterStates.Pending, item.Status));
        var later = Assert.Single(progress.Items, item => item.BatchIndex == 1);
        Assert.Equal(ReferenceMaterializationChapterStates.Pending, later.Status);
        Assert.Equal(0, later.CandidateCount);
    }

    [Fact]
    public async Task WorkerSplitsLargeChapterQualificationIntoBoundedModelBatches()
    {
        var options = CreateOptions();
        var repeated = string.Join(
            "\n\n",
            Enumerable.Range(1, 41).Select(index => $"“第{index}次别开门。”她把钥匙攥进掌心，门外响起第三次敲门，她仍没有回答。"));
        var anchor = await CreateAnchorAsync(
            options,
            chapterCount: 2,
            sourceOverride: $"# 第一章\n\n{repeated}\n\n# 第二章\n\n她终于说出了真相。\n");
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var qualifier = new ConcurrentAcceptingQualifier();
        var worker = new ReferenceMaterializationWorker(
            resolver,
            qualifier,
            new AcceptingEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, new RecordingVecProvisioner()),
            workerId: "test-materialization-batch-worker");

        Assert.True(await worker.ProcessRunOnceAsync(run.RunId, CancellationToken.None));
        var progress = await store.ListChapterProgressAsync(run.RunId, page: 1, size: 10, CancellationToken.None);

        Assert.True(qualifier.InvocationCount >= 3);
        Assert.InRange(qualifier.MaximumCandidateBatchSize, 1, ReferenceMaterializationChatCompletionQualifier.MaxCandidatesPerRequest);
        Assert.All(progress.Items, item => Assert.Equal(ReferenceMaterializationChapterStates.Completed, item.Status));
    }

    [Fact]
    public async Task QualificationWorkItemDoesNotReadCandidatesOutsideTheFrozenModelBatch()
    {
        var options = CreateOptions();
        var repeated = string.Join(
            "\n\n",
            Enumerable.Range(1, 12).Select(index => $"第{index}段的冲突在门外持续升级，她把钥匙攥进掌心，没有立刻回答。"));
        var anchor = await CreateAnchorAsync(
            options,
            chapterCount: 2,
            sourceOverride: $"# 第一章\n\n{repeated}\n\n# 第二章\n\n她终于说出了真相。\n");
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        await CorruptSixthPendingCandidateEvidenceAsync(options, run.RunId, chapterIndex: 1);

        var work = await store.ReadQualificationWorkItemAsync(run.RunId, chapterIndex: 1, CancellationToken.None);

        Assert.Equal(ReferenceMaterializationChatCompletionQualifier.MaxCandidatesPerRequest, work.Request.Candidates.Count);
    }

    [Fact]
    public async Task QualificationPersistenceDoesNotReadCandidatesOutsideTheFrozenModelBatch()
    {
        var options = CreateOptions();
        var repeated = string.Join(
            "\n\n",
            Enumerable.Range(1, 12).Select(index => $"第{index}段的冲突在门外持续升级，她把钥匙攥进掌心，没有立刻回答。"));
        var anchor = await CreateAnchorAsync(
            options,
            chapterCount: 2,
            sourceOverride: $"# 第一章\n\n{repeated}\n\n# 第二章\n\n她终于说出了真相。\n");
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        var work = await store.ReadQualificationWorkItemAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        await CorruptSixthPendingCandidateEvidenceAsync(options, run.RunId, chapterIndex: 1);

        var persisted = await store.PersistQualificationAsync(
            run.RunId,
            chapterIndex: 1,
            new ReferenceMaterializationQualificationResult(work.Request.Candidates.Select(AcceptedDecision).ToArray()),
            CancellationToken.None);

        Assert.Equal(work.Request.Candidates.Count, persisted.DecidedCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
        AppInitializationOptions options,
        int chapterCount,
        string? sourceOverride = null)
    {
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("运行仓库", "", ""), CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "run-store.md");
        var source = sourceOverride ?? string.Join(
            "\n\n",
            Enumerable.Range(1, chapterCount).Select(index => $"# 第{index}章\n\n第 {index} 章正文。"));
        await File.WriteAllTextAsync(sourcePath, source);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        return await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "运行仓库来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
    }

    private async ValueTask<(ReferenceAnchorPayload Anchor, ReferenceMaterializationStatusPayload Status, SearchableVecProvisioner Vec)> CreateCompletedGenerationAsync(
        AppInitializationOptions options,
        string? sourceOverride = null)
    {
        var anchor = await CreateAnchorAsync(options, chapterCount: 2, sourceOverride);
        var splitService = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await splitService.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await splitService.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);

        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId, chapterBatchSize: 5), CancellationToken.None);
        var vec = new SearchableVecProvisioner();
        var worker = new ReferenceMaterializationWorker(
            resolver,
            new ConcurrentAcceptingQualifier(),
            new AcceptingEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, vec),
            workerId: "test-materialization-semantic-search-worker");

        Assert.True(await worker.ProcessRunOnceAsync(run.RunId, CancellationToken.None));
        var status = await store.GetAsync(run.RunId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, status.Status);
        Assert.Equal(status.AcceptedCount, vec.LastProvisionRequest?.Vectors.Count);
        return (anchor, status, vec);
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
            QualifierVersion: ReferenceMaterializationChatCompletionQualifier.SchemaVersion,
            Llm: new ReferenceMaterializationModelIdentityPayload("provider", "model"),
            Embedding: new ReferenceMaterializationModelIdentityPayload("embedding-provider", "embedding-model", 8),
            ChapterBatchSize: chapterBatchSize,
            StartedAt: DateTimeOffset.UtcNow);
    }

    private static async ValueTask<int> CountCandidatesForChapterAsync(AppInitializationOptions options, string runId, int chapterIndex)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(DISTINCT candidate.candidate_id)
            FROM reference_material_candidates candidate
            JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            WHERE candidate.run_id = $run_id
              AND node.chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask SetQualifierVersionAsync(
        AppInitializationOptions options,
        string runId,
        string qualifierVersion)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_materialization_runs
            SET qualifier_version = $qualifier_version
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$qualifier_version", qualifierVersion);
        command.Parameters.AddWithValue("$run_id", runId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private static async ValueTask<int> CountCandidateLinksOutsideChapterAsync(AppInitializationOptions options, string runId, int chapterIndex)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_material_candidates candidate
            JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            WHERE candidate.run_id = $run_id
              AND node.chapter_index <> $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask<int> CountPendingCandidatesForChapterAsync(AppInitializationOptions options, string runId, int chapterIndex)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(DISTINCT candidate.candidate_id)
            FROM reference_material_candidates candidate
            JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            WHERE candidate.run_id = $run_id
              AND node.chapter_index = $chapter_index
              AND candidate.decision = $decision;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        command.Parameters.AddWithValue("$decision", ReferenceMaterializationCandidateDecisions.Pending);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask CorruptSixthPendingCandidateEvidenceAsync(
        AppInitializationOptions options,
        string runId,
        int chapterIndex)
    {
        await using var connection = await OpenConnectionAsync(options);
        string? candidateId;
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT candidate.candidate_id
                FROM reference_material_candidates candidate
                JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
                JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
                WHERE candidate.run_id = $run_id
                  AND candidate.decision = $decision
                  AND node.chapter_index = $chapter_index
                GROUP BY candidate.candidate_id
                ORDER BY MIN(node.start_offset), MIN(node.end_offset), candidate.candidate_id
                LIMIT 1 OFFSET 5;
                """;
            select.Parameters.AddWithValue("$run_id", runId);
            select.Parameters.AddWithValue("$decision", ReferenceMaterializationCandidateDecisions.Pending);
            select.Parameters.AddWithValue("$chapter_index", chapterIndex);
            candidateId = (string?)await select.ExecuteScalarAsync(CancellationToken.None);
        }

        Assert.False(string.IsNullOrWhiteSpace(candidateId));
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE reference_material_candidate_nodes
            SET evidence_end = 2147483647
            WHERE candidate_id = $candidate_id;
            """;
        update.Parameters.AddWithValue("$candidate_id", candidateId);
        Assert.True(await update.ExecuteNonQueryAsync(CancellationToken.None) > 0);
    }

    private static ReferenceMaterializationCandidateQualification AcceptedDecision(
        ReferenceMaterializationQualificationCandidate candidate)
    {
        return new ReferenceMaterializationCandidateQualification(
            candidate.CandidateId,
            ReferenceMaterializationCandidateDecisions.Accepted,
            candidate.SourceNodes.Select(node => new ReferenceMaterializationQualificationSpan(node.NodeId, 0, node.Text.Length)).ToArray(),
            new ReferenceMaterializationQualityScores(0.9, 0.8, 0.7, 0.6, 0.5, 0.4),
            new ReferenceMaterializationQualificationTags(["reveal"], [], ["close_third"], ["subtext"])
            {
                SceneBeatRoles = ["turn_beat"],
                CharacterRelations = ["mistrust"],
                CausalInformationRoles = ["reveal"]
            },
            0.85,
            ["complete_exchange"]);
    }

    private static ReferenceMaterializationCandidateQualification ReviewRequiredDecision(
        ReferenceMaterializationQualificationCandidate candidate) => new(
        candidate.CandidateId,
        ReferenceMaterializationCandidateDecisions.ReviewRequired,
        candidate.SourceNodes.Select(node => new ReferenceMaterializationQualificationSpan(node.NodeId, 0, node.Text.Length)).ToArray(),
        new ReferenceMaterializationQualityScores(0.6, 0.6, 0.6, 0.6, 0.6, 0.6),
        new ReferenceMaterializationQualificationTags(["reveal"], [], ["close_third"], ["subtext"])
        {
            SceneBeatRoles = ["turn_beat"],
            CharacterRelations = ["mistrust"],
            CausalInformationRoles = ["reveal"]
        },
        0.5,
        ["requires_review"]);

    private static async ValueTask<int> CountEmbeddingsForChapterAsync(AppInitializationOptions options, string runId, int chapterIndex)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materialization_candidate_embeddings embedding
            JOIN reference_material_candidates candidate ON candidate.candidate_id = embedding.candidate_id
            JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            WHERE embedding.run_id = $run_id
              AND node.chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask<int> CountPromotedMaterialsAsync(AppInitializationOptions options, string runId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_materialization_materials WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask<string?> ReadActiveGenerationAsync(AppInitializationOptions options, long anchorId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT active_generation_id FROM reference_anchor_materialization_state WHERE anchor_id = $anchor_id;";
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        return (string?)await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async ValueTask MarkLeaseExpiredAsync(AppInitializationOptions options, string runId)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_materialization_run_leases
            SET lease_expires_at = $lease_expires_at
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$lease_expires_at", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        command.Parameters.AddWithValue("$run_id", runId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(AppInitializationOptions options)
    {
        var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        return connection;
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

    private sealed class FailFirstCorpusDatabasePathResolver(IReferenceCorpusDatabasePathResolver inner)
        : IReferenceCorpusDatabasePathResolver
    {
        private int _remainingFailures = 1;

        public ValueTask<string> ResolveAsync(CancellationToken cancellationToken) =>
            Interlocked.Exchange(ref _remainingFailures, 0) == 1
                ? ValueTask.FromException<string>(new InvalidOperationException("Transient database-path resolution failure."))
                : inner.ResolveAsync(cancellationToken);
    }

    private sealed class FixedEmbeddingConfigurationService(EmbeddingRequestOptions options) : IEmbeddingConfigurationService
    {
        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<EmbeddingRequestOptions?>(options);
        }
    }

    private sealed class FixedEmbeddingClient(int dimensions) : IEmbeddingClient
    {
        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = inputs.Select((_, index) => new EmbeddingItemResult(
                index,
                Enumerable.Range(0, dimensions).Select(value => (float)(index + value + 1)).ToArray())).ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(options.ModelId, dimensions, items, new EmbeddingUsage(0, 0)));
        }
    }

    private sealed class RecordingVecProvisioner : ISqliteVecTableProvisioner
    {
        public SqliteVecProvisionRequest? LastRequest { get; private set; }

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SearchableVecProvisioner : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
    {
        public SqliteVecProvisionRequest? LastProvisionRequest { get; private set; }

        public SqliteVecSearchRequest? LastSearchRequest { get; private set; }

        public Exception? SearchException { get; set; }

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastProvisionRequest = request;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
            string databasePath,
            SqliteVecSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSearchRequest = request;
            if (SearchException is not null)
            {
                throw SearchException;
            }
            var records = (LastProvisionRequest?.Vectors ?? [])
                .Take(request.TopK)
                .Select((vector, index) => new SqliteVecSearchRecord(vector.RowId, 0.1 + index * 0.1))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<SqliteVecSearchRecord>>(records);
        }
    }

    private sealed class RecordingPreflight(ReferenceMaterializationModelPreflightResult result) : IReferenceMaterializationModelPreflight
    {
        public int CallCount { get; private set; }

        public ValueTask<ReferenceMaterializationModelPreflightResult> VerifyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingQualifier : IReferenceMaterializationQualifier
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async ValueTask<ReferenceMaterializationQualificationResult> QualifyAsync(
            ReferenceMaterializationQualificationRequest input,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new ReferenceMaterializationQualificationResult(
                input.Candidates.Select(candidate => AcceptedDecision(candidate)).ToArray());
        }
    }

    private sealed class ConcurrentAcceptingQualifier : IReferenceMaterializationQualifier
    {
        private int _active;
        private int _maximumConcurrency;
        private int _maximumCandidateBatchSize;
        private int _invocationCount;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public int MaximumCandidateBatchSize => Volatile.Read(ref _maximumCandidateBatchSize);
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async ValueTask<ReferenceMaterializationQualificationResult> QualifyAsync(
            ReferenceMaterializationQualificationRequest input,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            UpdateMaximum(ref _maximumCandidateBatchSize, input.Candidates.Count);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maximumConcurrency, active);

            try
            {
                await Task.Delay(80, cancellationToken);
                return new ReferenceMaterializationQualificationResult(
                    input.Candidates.Select(candidate => AcceptedDecision(candidate)).ToArray());
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void UpdateMaximum(ref int destination, int value)
        {
            while (true)
            {
                var observed = Volatile.Read(ref destination);
                if (observed >= value || Interlocked.CompareExchange(ref destination, value, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class SingleReviewQualifier : IReferenceMaterializationQualifier
    {
        private int _reviewed;

        public ValueTask<ReferenceMaterializationQualificationResult> QualifyAsync(
            ReferenceMaterializationQualificationRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceMaterializationQualificationResult(
                input.Candidates.Select(candidate => Interlocked.CompareExchange(ref _reviewed, 1, 0) == 0
                    ? ReviewRequiredDecision(candidate)
                    : AcceptedDecision(candidate)).ToArray()));
        }
    }

    private sealed class AcceptingEmbedder : IReferenceMaterializationEmbedder
    {
        public ValueTask<ReferenceMaterializationEmbeddingResult> EmbedAsync(
            ReferenceMaterializationEmbeddingRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceMaterializationEmbeddingResult(
                input.Items.Select(item => new ReferenceMaterializationCandidateEmbedding(
                    item.CandidateId,
                    Enumerable.Range(1, input.Model.Dimensions).Select(value => (float)value).ToArray())).ToArray()));
        }
    }

    private sealed class FailingQualifier : IReferenceMaterializationQualifier
    {
        public ValueTask<ReferenceMaterializationQualificationResult> QualifyAsync(
            ReferenceMaterializationQualificationRequest input,
            CancellationToken cancellationToken)
        {
            _ = input;
            _ = cancellationToken;
            throw new InvalidOperationException("provider rejected the qualification request");
        }
    }
}
