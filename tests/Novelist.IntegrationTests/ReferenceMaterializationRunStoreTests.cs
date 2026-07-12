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

    private static ReferenceMaterializationCandidateQualification AcceptedDecision(
        ReferenceMaterializationQualificationCandidate candidate)
    {
        return new ReferenceMaterializationCandidateQualification(
            candidate.CandidateId,
            ReferenceMaterializationCandidateDecisions.Accepted,
            candidate.SourceNodes.Select(node => new ReferenceMaterializationQualificationSpan(node.NodeId, 0, node.Text.Length)).ToArray(),
            new ReferenceMaterializationQualityScores(0.9, 0.8, 0.7, 0.6, 0.5, 0.4),
            new ReferenceMaterializationQualificationTags(["reveal"], [], ["close_third"], ["subtext"]),
            0.85,
            ["complete_exchange"]);
    }

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
}
