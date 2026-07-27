using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceWholeChapterMaterializationTests : IDisposable
{
    private const string MultiParagraphDialogue = "\u201c\u4f60\u8fd8\u8981\u8d70\uff1f\u201d\n\u5979\u6ca1\u6709\u56de\u7b54\u3002\n\n\u201c\u90a3\u6211\u7b49\u4f60\u3002\u201d";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "novelist-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WholeChapterMaterializationUsesOneModelCallPerChapterAndActivatesExactSourceMaterials()
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(
            new CreateNovelPayload("\u6574\u7ae0\u6750\u6599\u5316", "", ""),
            CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "whole-chapter.txt");
        await File.WriteAllTextAsync(
            sourcePath,
            $"\u7b2c1\u7ae0 \u7b49\u5f85\n\n{MultiParagraphDialogue}\n\n\u7b2c2\u7ae0 \u56de\u58f0\n\n\u96e8\u505c\u4e86\uff0c\u95e8\u5916\u7684\u4eba\u5374\u6ca1\u6709\u8d70\u3002\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.RegisterMaterializationSourceAsync(
            new RegisterReferenceMaterializationSourcePayload(
                novel.Id,
                "\u6574\u7ae0\u6765\u6e90",
                null,
                sourcePath,
                "text",
                "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(
            options,
            new UnusedChapterSplitAnalyzer(),
            modelPreflight: new FixedPreflight());
        var split = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(
                novel.Id,
                anchor.AnchorId,
                "\u7b2c{number}\u7ae0 {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, split.SplitProfileId),
            CancellationToken.None);
        var run = await service.EnqueueMaterializationAsync(
            new EnqueueReferenceMaterializationPayload(novel.Id, anchor.AnchorId, split.SplitProfileId),
            CancellationToken.None);
        var extractor = new RecordingExtractor();
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var vectors = new RecordingVecProvisioner();
        await using var worker = new ReferenceMaterializationWorker(
            resolver,
            extractor,
            new FixedEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, vectors));

        for (var chapter = 0; chapter < split.ChapterCount; chapter++)
        {
            Assert.True(await worker.ProcessRunOnceAsync(run.RunId, CancellationToken.None));
        }

        var completed = await service.GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(novel.Id, anchor.AnchorId, run.RunId),
            CancellationToken.None);
        var search = new SqliteReferenceMaterialSearch(
            options,
            resolver,
            new FixedEmbeddingConfiguration(),
            new FixedEmbeddingClient(),
            vectors);
        var materials = await search.ListAsync(
            new ReferenceMaterialListRequest(novel.Id, anchor.AnchorId, 1, 20),
            CancellationToken.None);
        var firstMaterialPage = await search.ListAsync(
            new ReferenceMaterialListRequest(novel.Id, anchor.AnchorId, 1, 1),
            CancellationToken.None);
        var secondMaterialPage = await search.ListAsync(
            new ReferenceMaterialListRequest(novel.Id, anchor.AnchorId, 2, 1),
            CancellationToken.None);
        var hits = await search.SearchAsync(
            new ReferenceMaterialSearchRequest(
                "Find the waiting dialogue.",
                10,
                AnchorIds: [anchor.AnchorId]),
            CancellationToken.None);

        Assert.NotNull(completed);
        Assert.True(
            completed.Status == ReferenceMaterializationRunStates.Completed,
            $"{completed.LastErrorCode}: {completed.LastErrorMessage}");
        Assert.True(completed.VectorIndexHealthy);
        Assert.Equal(2, completed.MaterialCount);
        Assert.Equal(2, completed.VectorCount);
        Assert.Equal(2, completed.ModelCallCount);
        var chapterProgress = await service.ListMaterializationChapterProgressAsync(
            new ListReferenceMaterializationChapterProgressPayload(
                novel.Id,
                anchor.AnchorId,
                run.RunId,
                1,
                20),
            CancellationToken.None);
        Assert.All(chapterProgress.Items, chapter =>
        {
            Assert.Equal(1, chapter.MaterialCount);
            Assert.Equal(1, chapter.VectorCount);
            Assert.Equal(1, chapter.ModelCallCount);
        });
        var firstChapterMaterials = await service.ListMaterializationChapterMaterialsAsync(
            new ListReferenceMaterializationChapterMaterialsPayload(
                novel.Id,
                anchor.AnchorId,
                run.RunId,
                1,
                1,
                20),
            CancellationToken.None);
        Assert.Equal(1, firstChapterMaterials.Total);
        Assert.Equal(MultiParagraphDialogue, firstChapterMaterials.Items.Single().Text);
        Assert.Equal(1, firstChapterMaterials.Items.Single().ChapterIndex);
        Assert.Equal(1, firstChapterMaterials.Items.Single().Metadata.SourceSpan.StartLine);
        Assert.Equal(4, firstChapterMaterials.Items.Single().Metadata.SourceSpan.EndLine);
        Assert.Collection(
            extractor.Requests.OrderBy(request => request.ChapterIndex),
            first =>
            {
                Assert.Equal(1, first.ChapterIndex);
                Assert.Equal("\u7b49\u5f85", first.ChapterTitle);
                Assert.Equal(MultiParagraphDialogue, first.ChapterText);
            },
            second =>
            {
                Assert.Equal(2, second.ChapterIndex);
                Assert.Equal("\u56de\u58f0", second.ChapterTitle);
                Assert.Equal("\u96e8\u505c\u4e86\uff0c\u95e8\u5916\u7684\u4eba\u5374\u6ca1\u6709\u8d70\u3002", second.ChapterText);
            });
        Assert.Equal(2, materials.Total);
        Assert.Contains(materials.Items, material => material.Text == MultiParagraphDialogue);
        Assert.All(materials.Items, material => Assert.Equal(run.GenerationId, material.GenerationId));
        Assert.Equal(2, firstMaterialPage.TotalPages);
        Assert.Equal(1, firstMaterialPage.Items.Single().ChapterIndex);
        Assert.Equal(MultiParagraphDialogue, firstMaterialPage.Items.Single().Text);
        Assert.Equal(2, secondMaterialPage.Items.Single().ChapterIndex);
        Assert.Contains(hits, hit => hit.Text == MultiParagraphDialogue);
        Assert.All(hits, hit => Assert.Equal(run.GenerationId, hit.GenerationId));
    }

    [Fact]
    public async Task RegisterMaterializationSourceOnlyRegistersSourceAndLeavesMaterializationPending()
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(
            new CreateNovelPayload("登记来源", "", ""),
            CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "pending.txt");
        await File.WriteAllTextAsync(sourcePath, "Chapter 1: Pending\n\nSource text.");

        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.RegisterMaterializationSourceAsync(
            new RegisterReferenceMaterializationSourcePayload(
                novel.Id,
                "待处理来源",
                null,
                sourcePath,
                "text",
                "user_provided"),
            CancellationToken.None);

        Assert.Equal("pending_split", anchor.Status);
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_materials WHERE anchor_id = $anchor_id;";
        command.Parameters.AddWithValue("$anchor_id", anchor.AnchorId);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task EmptyExtractionFailsWithoutPersistingOrActivatingMaterials()
    {
        var logs = new ConcurrentQueue<string>();
        var scenario = await CreateQueuedScenarioAsync(
            new EmptyExtractor(),
            new FixedEmbedder(),
            writeLog: (message, exception) => logs.Enqueue($"{message} {exception?.Message}"));
        await using var worker = scenario.Worker;

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var failed = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.Equal(ReferenceMaterializationErrorCodes.NoMaterials, failed.LastErrorCode);
        Assert.Equal(0, await CountRowsAsync("reference_materials"));
        Assert.Equal(0, await CountRowsAsync("reference_material_embeddings"));
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
        Assert.Contains(logs, message => message.Contains("chapter processing started", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("chapter started", StringComparison.Ordinal));
        Assert.Contains(logs, message =>
            message.Contains(ReferenceMaterializationErrorCodes.NoMaterials, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerExtractsChaptersSequentially()
    {
        const string source = """
            Chapter 1: One

            Content one.
            Chapter 2: Two

            Content two.
            Chapter 3: Three

            Content three.
            Chapter 4: Four

            Content four.
            Chapter 5: Five

            Content five.
            """;
        var extractor = new ConcurrentTrackingExtractor();
        var scenario = await CreateQueuedScenarioAsync(extractor, new FixedEmbedder(), source);
        await using var worker = scenario.Worker;

        var completed = await DrainRunAsync(scenario);

        Assert.Equal(1, extractor.MaximumConcurrency);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, completed.Status);
        Assert.Equal(5, completed.ProcessedChapters);
    }

    [Fact]
    public async Task EachWorkerPassCommitsExactlyOneChapter()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using var worker = scenario.Worker;

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var afterFirstChapter = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Extracting, afterFirstChapter.Status);
        Assert.Equal(1, afterFirstChapter.ProcessedChapters);
        Assert.Equal(1, afterFirstChapter.MaterialCount);
        Assert.Equal(1, afterFirstChapter.VectorCount);

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var completed = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, completed.Status);
        Assert.Equal(2, completed.ProcessedChapters);
    }

    [Fact]
    public async Task ReleasedLeaseLetsTheNextWorkerRestartAnOrphanedChapter()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using var worker = scenario.Worker;
        var store = new SqliteReferenceMaterializationRunStore(
            new ReferenceCorpusDatabasePathResolver(scenario.Options));
        var abandoned = await store.ClaimCurrentChapterAsync(
            scenario.Run.RunId,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.NotNull(abandoned);
        await store.ReadChapterWorkItemAsync(abandoned!, CancellationToken.None);
        await store.ReleaseChapterLeaseAsync(abandoned, CancellationToken.None);

        var recovered = await store.ClaimCurrentChapterAsync(
            scenario.Run.RunId,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.True(recovered!.RequiresProcessing);
        await store.ReleaseChapterLeaseAsync(recovered, CancellationToken.None);
    }

    [Fact]
    public async Task PrepareMaterialsRejectsADeclaredSourceSpanThatDoesNotMatchTheMaterialText()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using var worker = scenario.Worker;
        var store = new SqliteReferenceMaterializationRunStore(
            new ReferenceCorpusDatabasePathResolver(scenario.Options));
        var claim = await store.ClaimCurrentChapterAsync(
            scenario.Run.RunId,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.NotNull(claim);
        var workItem = await store.ReadChapterWorkItemAsync(claim!, CancellationToken.None);

        var exception = Assert.Throws<ReferenceMaterializationException>(() =>
            SqliteReferenceMaterializationRunStore.PrepareMaterials(
                workItem,
                new ReferenceChapterMaterialExtractionResult(
                [
                    new ExtractedReferenceMaterial(
                        workItem.ChapterText,
                        new ReferenceMaterialMetadata(
                            new ReferenceMaterialSourceSpan(1, 2), "叙述", [], null, null, null, [], null, [], null,
                            null, null, null, [], [], [], [], "错误坐标。"))
                ])));

        Assert.Equal(ReferenceMaterializationErrorCodes.SourceTextMismatch, exception.ErrorCode);
        await store.ReleaseChapterLeaseAsync(claim, CancellationToken.None);
    }

    [Fact]
    public async Task ExpiredLeaseCannotPersistOverTheReplacementWorker()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using var worker = scenario.Worker;
        var store = new SqliteReferenceMaterializationRunStore(
            new ReferenceCorpusDatabasePathResolver(scenario.Options));
        var expired = await store.ClaimCurrentChapterAsync(
            scenario.Run.RunId,
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);
        Assert.NotNull(expired);
        var staleWorkItem = await store.ReadChapterWorkItemAsync(expired!, CancellationToken.None);
        await store.MarkChapterEmbeddingAsync(expired, staleWorkItem, CancellationToken.None);
        var materials = SqliteReferenceMaterializationRunStore.PrepareMaterials(
            staleWorkItem,
            new ReferenceChapterMaterialExtractionResult(
            [
                new ExtractedReferenceMaterial(
                    staleWorkItem.ChapterText,
                    MaterialMetadata("Stale material.", staleWorkItem.ChapterText))
            ]));
        var embeddings = new ReferenceMaterializationEmbeddingResult(
            materials.Select(material => new ReferenceMaterializationMaterialEmbedding(
                material.MaterialId,
                [1f, 2f, 3f])).ToArray());
        await Task.Delay(60);

        var replacement = await store.ClaimCurrentChapterAsync(
            scenario.Run.RunId,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.NotNull(replacement);
        var replacementWorkItem = await store.ReadChapterWorkItemAsync(replacement!, CancellationToken.None);
        await store.MarkChapterEmbeddingAsync(replacement, replacementWorkItem, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistChapterAsync(
            expired,
            staleWorkItem,
            materials,
            embeddings,
            CancellationToken.None).AsTask());
        Assert.Equal(0, await CountRowsAsync("reference_materials"));
        Assert.Equal(0, await CountRowsAsync("reference_material_embeddings"));
        await store.ReleaseChapterLeaseAsync(replacement, CancellationToken.None);
    }

    [Fact]
    public async Task ExpiredLeaseCannotCompleteVectorIndexOrAdvanceTheChapter()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using var worker = scenario.Worker;
        var store = new SqliteReferenceMaterializationRunStore(
            new ReferenceCorpusDatabasePathResolver(scenario.Options));
        var expired = await store.ClaimCurrentChapterAsync(
            scenario.Run.RunId,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.NotNull(expired);
        var workItem = await store.ReadChapterWorkItemAsync(expired!, CancellationToken.None);
        await store.MarkChapterEmbeddingAsync(expired, workItem, CancellationToken.None);
        var materials = SqliteReferenceMaterializationRunStore.PrepareMaterials(
            workItem,
            new ReferenceChapterMaterialExtractionResult(
            [
                new ExtractedReferenceMaterial(
                    workItem.ChapterText,
                    MaterialMetadata("Indexed material.", workItem.ChapterText))
            ]));
        var embeddings = new ReferenceMaterializationEmbeddingResult(
            materials.Select(material => new ReferenceMaterializationMaterialEmbedding(
                material.MaterialId,
                [1f, 2f, 3f])).ToArray());
        await store.PersistChapterAsync(expired, workItem, materials, embeddings, CancellationToken.None);
        await store.MarkCurrentChapterEmbeddingAsync(expired, CancellationToken.None);
        var indexWorkItem = await store.ReadCurrentChapterVectorIndexWorkItemAsync(expired, CancellationToken.None);
        await Task.Delay(1_100);
        var replacement = await store.ClaimCurrentChapterAsync(
            scenario.Run.RunId,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.NotNull(replacement);
        Assert.False(replacement!.RequiresProcessing);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteCurrentChapterIndexAsync(
            expired,
            indexWorkItem,
            CancellationToken.None).AsTask());
        var status = await ReadStatusAsync(scenario);
        Assert.Equal(0, status.ProcessedChapters);
        Assert.Equal(1, status.CurrentChapterIndex);
        await store.ReleaseChapterLeaseAsync(replacement, CancellationToken.None);
    }

    [Fact]
    public async Task VectorIndexFailureMarksTheCurrentChapterFailedBeforeAdvancing()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using var worker = scenario.Worker;
        scenario.Vectors.ProvisionException = new InvalidOperationException("Injected vector index failure.");

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var failed = await ReadStatusAsync(scenario);
        var progress = await scenario.Service.ListMaterializationChapterProgressAsync(
            new ListReferenceMaterializationChapterProgressPayload(
                scenario.Anchor.NovelId,
                scenario.Anchor.AnchorId,
                scenario.Run.RunId,
                1,
                20),
            CancellationToken.None);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.Equal(ReferenceMaterializationErrorCodes.VectorIndexFailed, failed.LastErrorCode);
        Assert.False(failed.VectorIndexHealthy);
        Assert.Equal(0, failed.ProcessedChapters);
        Assert.Equal(ReferenceMaterializationChapterStates.Failed, progress.Items[0].Status);
        Assert.Equal(ReferenceMaterializationChapterStates.Pending, progress.Items[1].Status);
        Assert.Equal(0, await CountRowsAsync("reference_materials"));
        Assert.Equal(0, await CountRowsAsync("reference_material_embeddings"));
    }

    [Fact]
    public async Task InvalidEmbeddingFailsWithoutPartiallyPersistingTheChapter()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new InvalidEmbedder());
        await using var worker = scenario.Worker;

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var failed = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingInvalid, failed.LastErrorCode);
        Assert.Equal(0, await CountRowsAsync("reference_materials"));
        Assert.Equal(0, await CountRowsAsync("reference_material_embeddings"));
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
    }

    [Fact]
    public async Task UnexpectedEmbedderFailureIsReportedAsAnEmbeddingRequestFailure()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new ThrowingEmbedder());
        await using var worker = scenario.Worker;

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var failed = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingRequestFailed, failed.LastErrorCode);
    }

    [Fact]
    public async Task SourceChangeAfterEnqueueFailsBeforeAnyModelCall()
    {
        var extractor = new RecordingExtractor();
        var scenario = await CreateQueuedScenarioAsync(extractor, new FixedEmbedder());
        await File.AppendAllTextAsync(scenario.SourcePath, "changed");
        await using var worker = scenario.Worker;

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var failed = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.Equal(ReferenceMaterializationErrorCodes.SourceChanged, failed.LastErrorCode);
        Assert.Empty(extractor.Requests);
        Assert.Equal(0, await CountRowsAsync("reference_materials"));
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
    }

    [Fact]
    public async Task FailedChapterStopsBeforeTheNextChapter()
    {
        const string source = """
            Chapter 1: One

            Content one.
            Chapter 2: Two

            Content two.
            Chapter 3: Three

            Content three.
            Chapter 4: Four

            Content four.
            Chapter 5: Five

            Content five.
            Chapter 6: Six

            Content six.
            """;
        var extractor = new FailingChapterExtractor(3);
        var scenario = await CreateQueuedScenarioAsync(
            extractor,
            new FixedEmbedder(),
            source);
        await using var worker = scenario.Worker;

        var failed = await DrainRunAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.Equal([1, 2, 3], extractor.ChapterIndexes);
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
        Assert.Equal(2, await CountRowsAsync("reference_materials"));
        Assert.Equal(2, await CountRowsAsync("reference_material_embeddings"));
    }

    [Fact]
    public async Task RunAllResumesTheFailedRunAndSkipsCompletedChapters()
    {
        const string source = """
            Chapter 1: One

            Content one.
            Chapter 2: Two

            Content two.
            Chapter 3: Three

            Content three.
            Chapter 4: Four

            Content four.
            """;
        var extractor = new FailOnceChapterExtractor(3);
        var scenario = await CreateQueuedScenarioAsync(extractor, new FixedEmbedder(), source);
        await using var worker = scenario.Worker;

        var failed = await DrainRunAsync(scenario);

        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.Equal(2, failed.ProcessedChapters);
        Assert.Equal(2, await CountRowsAsync("reference_materials"));
        var resumed = await scenario.Service.EnqueueMaterializationAsync(
            new EnqueueReferenceMaterializationPayload(
                scenario.Anchor.NovelId,
                scenario.Anchor.AnchorId,
                scenario.Run.SplitProfileId,
                scenario.Run.RunId),
            CancellationToken.None);

        Assert.Equal(scenario.Run.RunId, resumed.RunId);
        Assert.Equal(scenario.Run.GenerationId, resumed.GenerationId);
        var completed = await DrainRunAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, completed.Status);
        Assert.Equal([1, 2, 3, 3, 4], extractor.ChapterIndexes);
        Assert.Equal(4, await CountRowsAsync("reference_materials"));
    }

    [Fact]
    public async Task RunChapterForcesOnlyTheSelectedChapterThenStops()
    {
        const string source = """
            Chapter 1: One

            Content one.
            Chapter 2: Two

            Content two.
            Chapter 3: Three

            Content three.
            Chapter 4: Four

            Content four.
            """;
        var extractor = new FailOnceChapterExtractor(3);
        var scenario = await CreateQueuedScenarioAsync(extractor, new FixedEmbedder(), source);
        await using var worker = scenario.Worker;
        Assert.Equal(ReferenceMaterializationRunStates.Failed, (await DrainRunAsync(scenario)).Status);

        await scenario.Service.RunMaterializationChapterAsync(
            new RunReferenceMaterializationChapterPayload(
                scenario.Anchor.NovelId,
                scenario.Anchor.AnchorId,
                scenario.Run.RunId,
                1),
            CancellationToken.None);
        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var afterChapterOne = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Paused, afterChapterOne.Status);
        Assert.False(afterChapterOne.VectorIndexHealthy);
        Assert.Equal([1, 2, 3, 1], extractor.ChapterIndexes);
        Assert.False(await worker.PumpOnceAsync(CancellationToken.None));

        await scenario.Service.RunMaterializationChapterAsync(
            new RunReferenceMaterializationChapterPayload(
                scenario.Anchor.NovelId,
                scenario.Anchor.AnchorId,
                scenario.Run.RunId,
                3),
            CancellationToken.None);
        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var afterChapterThree = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Paused, afterChapterThree.Status);
        Assert.Equal(3, afterChapterThree.ProcessedChapters);
        Assert.Equal([1, 2, 3, 1, 3], extractor.ChapterIndexes);

        await scenario.Service.EnqueueMaterializationAsync(
            new EnqueueReferenceMaterializationPayload(
                scenario.Anchor.NovelId,
                scenario.Anchor.AnchorId,
                scenario.Run.SplitProfileId,
                scenario.Run.RunId),
            CancellationToken.None);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, (await DrainRunAsync(scenario)).Status);
        Assert.Equal([1, 2, 3, 1, 3, 4], extractor.ChapterIndexes);
    }

    [Fact]
    public async Task RunChapterCanReplaceAChapterInACompletedGeneration()
    {
        var extractor = new FailingChapterExtractor(int.MaxValue);
        var scenario = await CreateQueuedScenarioAsync(extractor, new FixedEmbedder());
        await using var worker = scenario.Worker;
        var completed = await DrainRunAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, completed.Status);
        Assert.Equal(scenario.Run.GenerationId, await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));

        var scheduled = await scenario.Service.RunMaterializationChapterAsync(
            new RunReferenceMaterializationChapterPayload(
                scenario.Anchor.NovelId,
                scenario.Anchor.AnchorId,
                scenario.Run.RunId,
                1),
            CancellationToken.None);

        Assert.Equal(ReferenceMaterializationRunStates.Queued, scheduled.Status);
        Assert.Equal(1, scheduled.CurrentChapterIndex);
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var replaced = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Completed, replaced.Status);
        Assert.Equal(scenario.Run.GenerationId, replaced.GenerationId);
        Assert.Equal(scenario.Run.GenerationId, await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
        Assert.Equal([1, 2, 1], extractor.ChapterIndexes);
        Assert.Equal(2, await CountRowsAsync("reference_materials"));
        Assert.Equal(2, await CountRowsAsync("reference_material_embeddings"));
    }

    [Fact]
    public async Task RunAllRejectsACompletedChapterWhoseCommittedDataIsMissing()
    {
        var extractor = new FailOnceChapterExtractor(2);
        var scenario = await CreateQueuedScenarioAsync(extractor, new FixedEmbedder());
        await using var worker = scenario.Worker;
        Assert.Equal(ReferenceMaterializationRunStates.Failed, (await DrainRunAsync(scenario)).Status);
        await DeleteChapterResultAsync(scenario.Run.RunId, 1);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await scenario.Service.EnqueueMaterializationAsync(
                new EnqueueReferenceMaterializationPayload(
                    scenario.Anchor.NovelId,
                    scenario.Anchor.AnchorId,
                    scenario.Run.SplitProfileId,
                    scenario.Run.RunId),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.GenerationIncomplete, exception.ErrorCode);
    }

    [Fact]
    public async Task FailedReplacementRunDoesNotChangeTheActiveGeneration()
    {
        var first = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using (first.Worker)
        {
            Assert.Equal(ReferenceMaterializationRunStates.Completed, (await DrainRunAsync(first)).Status);
        }
        var activeGeneration = await ReadActiveGenerationAsync(first.Anchor.AnchorId);
        Assert.Equal(first.Run.GenerationId, activeGeneration);
        var replacement = await first.Service.EnqueueMaterializationAsync(
            new EnqueueReferenceMaterializationPayload(
                first.Anchor.NovelId,
                first.Anchor.AnchorId,
                first.Run.SplitProfileId),
            CancellationToken.None);
        var resolver = new ReferenceCorpusDatabasePathResolver(first.Options);
        await using var replacementWorker = new ReferenceMaterializationWorker(
            resolver,
            new EmptyExtractor(),
            new FixedEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, new RecordingVecProvisioner()));

        Assert.True(await replacementWorker.ProcessRunOnceAsync(replacement.RunId, CancellationToken.None));

        var failed = await first.Service.GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(
                first.Anchor.NovelId,
                first.Anchor.AnchorId,
                replacement.RunId),
            CancellationToken.None);
        var materials = await CreateSearch(first).ListAsync(
            new ReferenceMaterialListRequest(
                first.Anchor.NovelId,
                first.Anchor.AnchorId,
                1,
                20),
            CancellationToken.None);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed?.Status);
        Assert.Equal(activeGeneration, await ReadActiveGenerationAsync(first.Anchor.AnchorId));
        Assert.NotEmpty(materials.Items);
        Assert.All(materials.Items, material => Assert.Equal(activeGeneration, material.GenerationId));
    }

    [Fact]
    public async Task MaterialSearchResolvesSessionLibraryScope()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using (scenario.Worker)
        {
            Assert.Equal(ReferenceMaterializationRunStates.Completed, (await DrainRunAsync(scenario)).Status);
        }
        var search = CreateSearch(scenario);

        var hits = await search.SearchAsync(
            new ReferenceMaterialSearchRequest(
                "Find chapter material.",
                10,
                SessionId: $"project:{scenario.Anchor.NovelId}:default"),
            CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Equal(scenario.Anchor.AnchorId, hit.AnchorId));
    }

    [Fact]
    public async Task MaterialSearchFiltersForbiddenSourcesBeforeVectorQuery()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using (scenario.Worker)
        {
            Assert.Equal(ReferenceMaterializationRunStates.Completed, (await DrainRunAsync(scenario)).Status);
        }
        await using (var connection = await OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE reference_source_license
                SET license_state = $forbidden,
                    reuse_policy = $policy
                WHERE anchor_id = $anchor_id;
                """;
            command.Parameters.AddWithValue("$forbidden", ReferenceCorpusLicenseStates.Forbidden);
            command.Parameters.AddWithValue("$policy", ReferenceCorpusReusePolicies.Forbidden);
            command.Parameters.AddWithValue("$anchor_id", scenario.Anchor.AnchorId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var search = CreateSearch(scenario);

        var listException = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await search.ListAsync(
                new ReferenceMaterialListRequest(
                    scenario.Anchor.NovelId,
                    scenario.Anchor.AnchorId,
                    1,
                    20),
                CancellationToken.None));
        var hits = await search.SearchAsync(
            new ReferenceMaterialSearchRequest(
                "Find chapter material.",
                10,
                AnchorIds: [scenario.Anchor.AnchorId]),
            CancellationToken.None);

        Assert.Equal(ReferenceMaterializationErrorCodes.GenerationIncomplete, listException.ErrorCode);
        Assert.Empty(hits);
        Assert.Equal(0, scenario.Vectors.SearchCallCount);
    }

    [Fact]
    public async Task MaterialSearchReportsVectorFailureWithoutFallback()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using (scenario.Worker)
        {
            Assert.Equal(ReferenceMaterializationRunStates.Completed, (await DrainRunAsync(scenario)).Status);
        }
        scenario.Vectors.SearchException = new InvalidOperationException("Injected vector failure.");
        var search = CreateSearch(scenario);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await search.SearchAsync(
                new ReferenceMaterialSearchRequest(
                    "Find chapter material.",
                    10,
                    AnchorIds: [scenario.Anchor.AnchorId]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.VectorIndexFailed, exception.ErrorCode);
        Assert.Equal(1, scenario.Vectors.SearchCallCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions() => new()
    {
        ConfigDirectory = Path.Combine(_root, "config"),
        DefaultDataDirectory = Path.Combine(_root, "data"),
        EnableLegacyMigration = false
    };

    private async ValueTask<QueuedScenario> CreateQueuedScenarioAsync(
        IReferenceChapterMaterialExtractor extractor,
        IReferenceMaterializationEmbedder embedder,
        string source = "Chapter 1: One\n\nFirst complete chapter.\n\nChapter 2: Two\n\nSecond complete chapter.\n",
        Action<string, Exception?>? writeLog = null)
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(
            new CreateNovelPayload("Failure case", "", ""),
            CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "failure-case.txt");
        await File.WriteAllTextAsync(sourcePath, source);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.RegisterMaterializationSourceAsync(
            new RegisterReferenceMaterializationSourcePayload(
                novel.Id,
                "Failure source",
                null,
                sourcePath,
                "text",
                "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(
            options,
            new UnusedChapterSplitAnalyzer(),
            modelPreflight: new FixedPreflight());
        var split = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(
                novel.Id,
                anchor.AnchorId,
                "Chapter {number}: {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, split.SplitProfileId),
            CancellationToken.None);
        var run = await service.EnqueueMaterializationAsync(
            new EnqueueReferenceMaterializationPayload(novel.Id, anchor.AnchorId, split.SplitProfileId),
            CancellationToken.None);
        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var vectors = new RecordingVecProvisioner();
        var worker = new ReferenceMaterializationWorker(
            resolver,
            extractor,
            embedder,
            new ReferenceMaterializationVectorIndexer(resolver, vectors),
            writeLog: writeLog);
        return new QueuedScenario(options, anchor, service, run, worker, vectors, sourcePath);
    }

    private static SqliteReferenceMaterialSearch CreateSearch(QueuedScenario scenario) =>
        new(
            scenario.Options,
            new ReferenceCorpusDatabasePathResolver(scenario.Options),
            new FixedEmbeddingConfiguration(),
            new FixedEmbeddingClient(),
            scenario.Vectors);

    private static async ValueTask<ReferenceMaterializationStatusPayload> ReadStatusAsync(QueuedScenario scenario) =>
        await scenario.Service.GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(
                scenario.Anchor.NovelId,
                scenario.Anchor.AnchorId,
                scenario.Run.RunId),
            CancellationToken.None)
        ?? throw new InvalidOperationException("Materialization run disappeared.");

    private static async ValueTask<ReferenceMaterializationStatusPayload> DrainRunAsync(QueuedScenario scenario)
    {
        for (var attempt = 0; attempt <= scenario.Run.TotalChapters; attempt++)
        {
            var status = await ReadStatusAsync(scenario);
            if (status.Status is ReferenceMaterializationRunStates.Completed or ReferenceMaterializationRunStates.Failed)
            {
                return status;
            }

            Assert.True(await scenario.Worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));
        }

        throw new TimeoutException("Materialization run did not settle one chapter at a time.");
    }

    private async ValueTask<int> CountRowsAsync(string tableName)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "reference_materials",
            "reference_material_embeddings"
        };
        if (!allowed.Contains(tableName))
        {
            throw new ArgumentException("Unsupported test table.", nameof(tableName));
        }

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private async ValueTask DeleteChapterResultAsync(string runId, int chapterIndex)
    {
        await using var connection = await OpenConnectionAsync();
        await using (var embeddings = connection.CreateCommand())
        {
            embeddings.CommandText = """
                DELETE FROM reference_material_embeddings
                WHERE material_id IN (
                  SELECT material_id
                  FROM reference_materials
                  WHERE run_id = $run_id
                    AND chapter_index = $chapter_index);
                """;
            embeddings.Parameters.AddWithValue("$run_id", runId);
            embeddings.Parameters.AddWithValue("$chapter_index", chapterIndex);
            await embeddings.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var materials = connection.CreateCommand();
        materials.CommandText = "DELETE FROM reference_materials WHERE run_id = $run_id AND chapter_index = $chapter_index;";
        materials.Parameters.AddWithValue("$run_id", runId);
        materials.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await materials.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async ValueTask<string?> ReadActiveGenerationAsync(long anchorId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT active_generation_id
            FROM reference_anchor_materialization_state
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? null : (string)value;
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync()
    {
        var databasePath = Path.Combine(_root, "data", "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private sealed class UnusedChapterSplitAnalyzer : IReferenceChapterSplitAnalyzer
    {
        public ValueTask<ReferenceChapterSplitModelResult> AnalyzeAsync(
            ReferenceChapterSplitModelRequest input,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Manual chapter splitting must not call the model analyzer.");
    }

    private static ReferenceMaterialMetadata MaterialMetadata(string reuseHint, string sourceText) =>
        new(
            new ReferenceMaterialSourceSpan(1, sourceText.Count(character => character == '\n') + 1), "叙述", [], null, null, null, [], null, [], null,
            null, null, null, [], [], [], [], reuseHint);

    private sealed class FixedPreflight : IReferenceMaterializationModelPreflight
    {
        public ValueTask<ReferenceMaterializationModelPreflightResult> VerifyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceMaterializationModelPreflightResult(
                new ReferenceMaterializationModelIdentityPayload("test-llm", "whole-chapter-model"),
                new ReferenceMaterializationModelIdentityPayload("test-embedding", "embedding-model", 3)));
        }
    }

    private sealed class RecordingExtractor : IReferenceChapterMaterialExtractor
    {
        private readonly object _gate = new();
        private readonly List<ReferenceChapterMaterialExtractionRequest> _requests = [];

        public IReadOnlyList<ReferenceChapterMaterialExtractionRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public ValueTask<ReferenceChapterMaterialExtractionResult> ExtractAsync(
            ReferenceChapterMaterialExtractionRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _requests.Add(input);
            }

            return ValueTask.FromResult(new ReferenceChapterMaterialExtractionResult(
            [
                new ExtractedReferenceMaterial(
                    input.ChapterText,
                    MaterialMetadata("Reusable chapter material.", input.ChapterText))
            ]));
        }
    }

    private sealed class EmptyExtractor : IReferenceChapterMaterialExtractor
    {
        public ValueTask<ReferenceChapterMaterialExtractionResult> ExtractAsync(
            ReferenceChapterMaterialExtractionRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceChapterMaterialExtractionResult([]));
        }
    }

    private sealed class ThrowingEmbedder : IReferenceMaterializationEmbedder
    {
        public ValueTask<ReferenceMaterializationEmbeddingResult> EmbedAsync(
            ReferenceMaterializationEmbeddingRequest input,
            CancellationToken cancellationToken) =>
            throw new ArgumentException("Injected embedding input failure.", nameof(input));
    }

    private sealed class FailingChapterExtractor(int failingChapter) : IReferenceChapterMaterialExtractor
    {
        private readonly object _gate = new();
        private readonly List<int> _chapterIndexes = [];

        public IReadOnlyList<int> ChapterIndexes
        {
            get
            {
                lock (_gate)
                {
                    return _chapterIndexes.ToArray();
                }
            }
        }

        public ValueTask<ReferenceChapterMaterialExtractionResult> ExtractAsync(
            ReferenceChapterMaterialExtractionRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _chapterIndexes.Add(input.ChapterIndex);
            }

            if (input.ChapterIndex == failingChapter)
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.LlmRequestFailed,
                    "Injected chapter failure.");
            }

            return ValueTask.FromResult(new ReferenceChapterMaterialExtractionResult(
            [
                new ExtractedReferenceMaterial(
                    input.ChapterText,
                    MaterialMetadata("Reusable chapter material.", input.ChapterText))
            ]));
        }
    }

    private sealed class FailOnceChapterExtractor(int failingChapter) : IReferenceChapterMaterialExtractor
    {
        private readonly List<int> _chapterIndexes = [];
        private bool _failed;

        public IReadOnlyList<int> ChapterIndexes => _chapterIndexes;

        public ValueTask<ReferenceChapterMaterialExtractionResult> ExtractAsync(
            ReferenceChapterMaterialExtractionRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _chapterIndexes.Add(input.ChapterIndex);
            if (input.ChapterIndex == failingChapter && !_failed)
            {
                _failed = true;
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.LlmRequestFailed,
                    "Injected one-time chapter failure.");
            }

            return ValueTask.FromResult(new ReferenceChapterMaterialExtractionResult(
            [
                new ExtractedReferenceMaterial(
                    input.ChapterText,
                    MaterialMetadata("Reusable chapter material.", input.ChapterText))
            ]));
        }
    }

    private sealed class ConcurrentTrackingExtractor : IReferenceChapterMaterialExtractor
    {
        private int _active;
        private int _maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async ValueTask<ReferenceChapterMaterialExtractionResult> ExtractAsync(
            ReferenceChapterMaterialExtractionRequest input,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(50, cancellationToken);
                return new ReferenceChapterMaterialExtractionResult(
                [
                    new ExtractedReferenceMaterial(
                        input.ChapterText,
                        MaterialMetadata("Complete chapter.", input.ChapterText))
                ]);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref _maximumConcurrency, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class FixedEmbedder : IReferenceMaterializationEmbedder
    {
        public ValueTask<ReferenceMaterializationEmbeddingResult> EmbedAsync(
            ReferenceMaterializationEmbeddingRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceMaterializationEmbeddingResult(
                input.Items.Select((item, index) => new ReferenceMaterializationMaterialEmbedding(
                    item.MaterialId,
                    [1f, index + 2f, index + 3f])).ToArray()));
        }
    }

    private sealed class InvalidEmbedder : IReferenceMaterializationEmbedder
    {
        public ValueTask<ReferenceMaterializationEmbeddingResult> EmbedAsync(
            ReferenceMaterializationEmbeddingRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceMaterializationEmbeddingResult(
                input.Items.Select(item => new ReferenceMaterializationMaterialEmbedding(
                    item.MaterialId,
                    [1f, 2f])).ToArray()));
        }
    }

    private sealed class RecordingVecProvisioner : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
    {
        private readonly Dictionary<string, IReadOnlyList<SqliteVecVectorRecord>> _vectors = new(StringComparer.Ordinal);

        public int SearchCallCount { get; private set; }

        public Exception? SearchException { get; set; }

        public Exception? ProvisionException { get; set; }

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProvisionException is not null)
            {
                throw ProvisionException;
            }

            Assert.NotEmpty(request.Vectors);
            _vectors[request.TableName] = request.Vectors.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
            string databasePath,
            SqliteVecSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCallCount++;
            if (SearchException is not null)
            {
                throw SearchException;
            }

            var results = _vectors.TryGetValue(request.TableName, out var vectors)
                ? vectors.Take(request.TopK)
                    .Select((vector, index) => new SqliteVecSearchRecord(vector.RowId, index * 0.01))
                    .ToArray()
                : [];
            return ValueTask.FromResult<IReadOnlyList<SqliteVecSearchRecord>>(results);
        }
    }

    private sealed class FixedEmbeddingConfiguration : IEmbeddingConfigurationService
    {
        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<EmbeddingRequestOptions?>(new EmbeddingRequestOptions(
                "test-embedding",
                "https://example.invalid",
                "key",
                "embedding-model",
                3,
                null));
        }
    }

    private sealed class FixedEmbeddingClient : IEmbeddingClient
    {
        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                3,
                [new EmbeddingItemResult(0, [1f, 2f, 3f])],
                new EmbeddingUsage(0, 0)));
        }
    }

    private sealed record QueuedScenario(
        AppInitializationOptions Options,
        ReferenceAnchorPayload Anchor,
        SqliteReferenceMaterializationService Service,
        ReferenceMaterializationStatusPayload Run,
        ReferenceMaterializationWorker Worker,
        RecordingVecProvisioner Vectors,
        string SourcePath);
}
