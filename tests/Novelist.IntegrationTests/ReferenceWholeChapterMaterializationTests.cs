using Microsoft.Data.Sqlite;
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
            new CreateReferenceAnchorPayload(
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
            new ReferenceMaterializationVectorIndexer(resolver, vectors),
            workerId: "whole-chapter-test-worker");

        Assert.True(await worker.ProcessRunOnceAsync(run.RunId, CancellationToken.None));

        var completed = await service.GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(novel.Id, anchor.AnchorId, run.RunId),
            CancellationToken.None);
        var materials = await service.ListActiveMaterialsAsync(
            new ListActiveReferenceMaterializationMaterialsPayload(novel.Id, anchor.AnchorId, 1, 20),
            CancellationToken.None);
        var search = new SqliteReferenceMaterialSearch(
            options,
            resolver,
            new FixedEmbeddingConfiguration(),
            new FixedEmbeddingClient(),
            vectors);
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
        Assert.Contains(hits, hit => hit.Text == MultiParagraphDialogue);
        Assert.All(hits, hit => Assert.Equal(run.GenerationId, hit.GenerationId));
    }

    [Fact]
    public async Task EmptyExtractionFailsWithoutPersistingOrActivatingMaterials()
    {
        var scenario = await CreateQueuedScenarioAsync(new EmptyExtractor(), new FixedEmbedder());
        await using var worker = scenario.Worker;

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var failed = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.Equal(ReferenceMaterializationErrorCodes.NoMaterials, failed.LastErrorCode);
        Assert.Equal(0, await CountRowsAsync("reference_materialization_materials"));
        Assert.Equal(0, await CountRowsAsync("reference_materialization_material_embeddings"));
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
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
        Assert.Equal(0, await CountRowsAsync("reference_materialization_materials"));
        Assert.Equal(0, await CountRowsAsync("reference_materialization_material_embeddings"));
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
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
        Assert.Equal(0, await CountRowsAsync("reference_materialization_materials"));
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
    }

    [Fact]
    public async Task FailedChapterStopsBeforeTheNextOrderedBatch()
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

        Assert.True(await worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));

        var failed = await ReadStatusAsync(scenario);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed.Status);
        Assert.DoesNotContain(6, extractor.ChapterIndexes);
        Assert.All(extractor.ChapterIndexes, chapterIndex => Assert.InRange(chapterIndex, 1, 5));
        Assert.Null(await ReadActiveGenerationAsync(scenario.Anchor.AnchorId));
    }

    [Fact]
    public async Task FailedReplacementRunDoesNotChangeTheActiveGeneration()
    {
        var first = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using (first.Worker)
        {
            Assert.True(await first.Worker.ProcessRunOnceAsync(first.Run.RunId, CancellationToken.None));
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
            new ReferenceMaterializationVectorIndexer(resolver, new RecordingVecProvisioner()),
            workerId: "failed-replacement-worker");

        Assert.True(await replacementWorker.ProcessRunOnceAsync(replacement.RunId, CancellationToken.None));

        var failed = await first.Service.GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(
                first.Anchor.NovelId,
                first.Anchor.AnchorId,
                replacement.RunId),
            CancellationToken.None);
        var materials = await first.Service.ListActiveMaterialsAsync(
            new ListActiveReferenceMaterializationMaterialsPayload(
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
            Assert.True(await scenario.Worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));
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
            Assert.True(await scenario.Worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));
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

        var hits = await search.SearchAsync(
            new ReferenceMaterialSearchRequest(
                "Find chapter material.",
                10,
                AnchorIds: [scenario.Anchor.AnchorId]),
            CancellationToken.None);

        Assert.Empty(hits);
        Assert.Equal(0, scenario.Vectors.SearchCallCount);
    }

    [Fact]
    public async Task MaterialSearchReportsVectorFailureWithoutFallback()
    {
        var scenario = await CreateQueuedScenarioAsync(new RecordingExtractor(), new FixedEmbedder());
        await using (scenario.Worker)
        {
            Assert.True(await scenario.Worker.ProcessRunOnceAsync(scenario.Run.RunId, CancellationToken.None));
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
        string source = "Chapter 1: One\n\nFirst complete chapter.\n\nChapter 2: Two\n\nSecond complete chapter.\n")
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
            new CreateReferenceAnchorPayload(
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
            workerId: "whole-chapter-failure-worker");
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

    private async ValueTask<int> CountRowsAsync(string tableName)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "reference_materialization_materials",
            "reference_materialization_material_embeddings"
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
                    input.ChapterIndex == 1 ? "dialogue" : "atmosphere",
                    input.ChapterText,
                    "Reusable chapter material.",
                    input.ChapterIndex == 1 ? ["dialogue"] : ["suspense"])
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
                    "passage",
                    input.ChapterText,
                    "Reusable chapter material.",
                    ["passage"])
            ]));
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
                input.Items.Select((item, index) => new ReferenceMaterializationCandidateEmbedding(
                    item.CandidateId,
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
                input.Items.Select(item => new ReferenceMaterializationCandidateEmbedding(
                    item.CandidateId,
                    [1f, 2f])).ToArray()));
        }
    }

    private sealed class RecordingVecProvisioner : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
    {
        private readonly Dictionary<string, IReadOnlyList<SqliteVecVectorRecord>> _vectors = new(StringComparer.Ordinal);

        public int SearchCallCount { get; private set; }

        public Exception? SearchException { get; set; }

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
