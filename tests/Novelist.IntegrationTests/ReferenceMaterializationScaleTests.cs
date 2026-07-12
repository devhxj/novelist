using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationScaleTests : IDisposable
{
    private const int RequiredCharacters = 50_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [MaterializationScaleFact]
    public async Task FakeModelsProcessFiftyKAcrossTwoSourcesWithFrozenFiveAndTenChapterBatches()
    {
        var options = CreateOptions();
        var scaleCharacters = ResolveScaleCharacters();
        var sources = await CreateSourcesAsync(options, scaleCharacters);
        Assert.True(sources.Sum(source => source.CharacterCount) >= scaleCharacters);

        var resolver = new ReferenceCorpusDatabasePathResolver(options);
        var store = new SqliteReferenceMaterializationRunStore(resolver);
        var vec = new ScaleVecProvider();
        var worker = new ReferenceMaterializationWorker(
            resolver,
            new ScaleQualifier(),
            new ScaleEmbedder(),
            new ReferenceMaterializationVectorIndexer(resolver, vec),
            workerId: "materialization-50k-worker");
        var runs = new List<ReferenceMaterializationStatusPayload>();
        var stopwatch = Stopwatch.StartNew();

        var schedule = new[]
        {
            (Source: sources[0], BatchSize: 5),
            (Source: sources[1], BatchSize: 5),
            (Source: sources[0], BatchSize: 5),
            (Source: sources[1], BatchSize: 10)
        };
        foreach (var work in schedule)
        {
            var run = await store.CreateAsync(
                CreateSeed(work.Source.Anchor.AnchorId, work.Source.Profile.SplitProfileId, work.BatchSize),
                CancellationToken.None);
            runs.Add(await DrainRunAsync(worker, store, run.RunId));
        }

        stopwatch.Stop();
        var processedCandidates = runs.Sum(run => run.CandidateCount);
        var throughput = processedCandidates / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        Assert.Equal(4, runs.Count);
        Assert.Equal(3, runs.Count(run => run.ChapterBatchSize == 5));
        Assert.Equal(1, runs.Count(run => run.ChapterBatchSize == 10));
        Assert.All(runs, run =>
        {
            Assert.Equal(ReferenceMaterializationRunStates.Completed, run.Status);
            Assert.Equal(run.AcceptedCount, run.VectorCount);
            Assert.True(run.VectorIndexHealthy);
            Assert.Equal(run.TotalChapters, run.ProcessedChapters);
        });
        Assert.True(processedCandidates > 0);
        Assert.True(throughput >= 20, $"Fake materialization throughput was {throughput:F2} candidates/s.");
        Assert.Equal(0, await CountActiveLeasesAsync(options));
        Assert.Equal(0, await CountDuplicateEmbeddingsAsync(options));

        var search = new SqliteReferenceMaterializationSemanticSearch(
            options,
            resolver,
            new ScaleEmbeddingConfiguration(),
            new ScaleEmbeddingClient(),
            vec);
        foreach (var source in sources)
        {
            var results = await search.SearchAsync(source.Anchor.AnchorId, "无字面重合的预演检索", 10, CancellationToken.None);
            var activeGeneration = await ReadActiveGenerationAsync(options, source.Anchor.AnchorId);
            Assert.NotEmpty(results);
            Assert.All(results, result => Assert.Equal(activeGeneration, result.Material.GenerationId));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async ValueTask<IReadOnlyList<ScaleSource>> CreateSourcesAsync(AppInitializationOptions options, int characterCount)
    {
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料化规模门", "", ""), CancellationToken.None);
        var sourcesDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourcesDirectory);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var result = new List<ScaleSource>();
        for (var index = 0; index < 2; index++)
        {
            var source = BuildMarkdownSource(characterCount / 2, index + 1);
            var sourcePath = Path.Combine(sourcesDirectory, $"materialization-scale-{index + 1}.md");
            await File.WriteAllTextAsync(sourcePath, source);
            var anchor = await anchors.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(
                    novel.Id,
                    $"规模来源 {index + 1}",
                    null,
                    sourcePath,
                    "markdown",
                    "user_provided"),
                CancellationToken.None);
            var splitter = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
            var profile = await splitter.PreviewChapterSplitAsync(
                new PreviewReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, "# {title}"),
                CancellationToken.None);
            profile = await splitter.ConfirmChapterSplitAsync(
                new ConfirmReferenceChapterSplitPayload(novel.Id, anchor.AnchorId, profile.SplitProfileId),
                CancellationToken.None);
            result.Add(new ScaleSource(anchor, profile, source.Length));
        }

        return result;
    }

    private static string BuildMarkdownSource(int minimumCharacters, int sourceNumber)
    {
        var paragraph = $"人物{sourceNumber}望着窗外" + new string('叙', 820) + "。";
        var builder = new System.Text.StringBuilder();
        var chapter = 1;
        while (builder.Length < minimumCharacters)
        {
            builder.Append("# 第").Append(chapter).Append("章\n\n");
            builder.Append(paragraph).Append("\n\n");
            builder.Append(paragraph).Append("\n\n");
            chapter++;
        }

        return builder.ToString();
    }

    private static ReferenceMaterializationRunSeed CreateSeed(long anchorId, string profileId, int batchSize) =>
        new(
            Guid.NewGuid().ToString("N"),
            anchorId,
            profileId,
            Guid.NewGuid().ToString("N"),
            "materialization-policy-v1",
            "candidate-window-v1",
            ReferenceMaterializationChatCompletionQualifier.SchemaVersion,
            new ReferenceMaterializationModelIdentityPayload("scale-llm", "scale-llm-model"),
            new ReferenceMaterializationModelIdentityPayload("scale-embedding", "scale-embedding-model", 8),
            batchSize,
            DateTimeOffset.UtcNow);

    private static async ValueTask<ReferenceMaterializationStatusPayload> DrainRunAsync(
        ReferenceMaterializationWorker worker,
        SqliteReferenceMaterializationRunStore store,
        string runId)
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            var status = await store.GetAsync(runId, CancellationToken.None)
                ?? throw new InvalidOperationException("Materialization scale run disappeared.");
            if (status.Status is ReferenceMaterializationRunStates.Completed or ReferenceMaterializationRunStates.Failed)
            {
                return status;
            }

            Assert.True(await worker.ProcessRunOnceAsync(runId, CancellationToken.None));
        }

        throw new TimeoutException("Materialization scale run did not settle.");
    }

    private static async ValueTask<int> CountActiveLeasesAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_materialization_run_leases WHERE lease_expires_at > $now;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask<int> CountDuplicateEmbeddingsAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM (
              SELECT candidate_id, provider, model_id, dimensions, COUNT(*) AS count
              FROM reference_materialization_candidate_embeddings
              GROUP BY candidate_id, provider, model_id, dimensions
              HAVING count > 1
            );
            """;
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

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(AppInitializationOptions options)
    {
        var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private AppInitializationOptions CreateOptions() => new()
    {
        ConfigDirectory = Path.Combine(_root, "config"),
        DefaultDataDirectory = Path.Combine(_root, "data"),
        EnableLegacyMigration = false
    };

    private static int ResolveScaleCharacters()
    {
        var configured = Environment.GetEnvironmentVariable("NOVELIST_MATERIALIZATION_SCALE_CHARACTERS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return RequiredCharacters;
        }

        if (!int.TryParse(configured, out var characters) || characters < RequiredCharacters)
        {
            throw new InvalidOperationException($"Materialization scale characters must be at least {RequiredCharacters}.");
        }

        return characters;
    }

    private sealed class EmptyChapterSplitAnalyzer : IReferenceChapterSplitAnalyzer
    {
        public ValueTask<ReferenceChapterSplitModelResult> AnalyzeAsync(
            ReferenceChapterSplitModelRequest input,
            CancellationToken cancellationToken) => ValueTask.FromResult(ReferenceChapterSplitModelResult.Empty);
    }

    private sealed class ScaleQualifier : IReferenceMaterializationQualifier
    {
        public ValueTask<ReferenceMaterializationQualificationResult> QualifyAsync(
            ReferenceMaterializationQualificationRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceMaterializationQualificationResult(
                input.Candidates.Select(candidate => new ReferenceMaterializationCandidateQualification(
                    candidate.CandidateId,
                    ReferenceMaterializationCandidateDecisions.Accepted,
                    candidate.SourceNodes.Select(node => new ReferenceMaterializationQualificationSpan(node.NodeId, 0, node.Text.Length)).ToArray(),
                    new ReferenceMaterializationQualityScores(0.9, 0.8, 0.8, 0.7, 0.7, 0.8),
                    new ReferenceMaterializationQualificationTags(["reveal"], ["contained_tension"], ["close_third"], ["subtext"]),
                    0.9,
                    ["scale_complete"])).ToArray()));
        }
    }

    private sealed class ScaleEmbedder : IReferenceMaterializationEmbedder
    {
        public ValueTask<ReferenceMaterializationEmbeddingResult> EmbedAsync(
            ReferenceMaterializationEmbeddingRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceMaterializationEmbeddingResult(
                input.Items.Select((item, index) => new ReferenceMaterializationCandidateEmbedding(
                    item.CandidateId,
                    Enumerable.Range(1, input.Model.Dimensions).Select(value => (float)(value + index)).ToArray())).ToArray()));
        }
    }

    private sealed class ScaleEmbeddingConfiguration : IEmbeddingConfigurationService
    {
        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<EmbeddingRequestOptions?>(new EmbeddingRequestOptions(
                "scale-embedding", "https://example.invalid", "key", "scale-embedding-model", 8, null));
    }

    private sealed class ScaleEmbeddingClient : IEmbeddingClient
    {
        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                8,
                inputs.Select((_, index) => new EmbeddingItemResult(index, Enumerable.Range(1, 8).Select(value => (float)value).ToArray())).ToArray(),
                new EmbeddingUsage(0, 0)));
        }
    }

    private sealed class ScaleVecProvider : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
    {
        private readonly Dictionary<string, IReadOnlyList<SqliteVecVectorRecord>> _vectorsByTable = new(StringComparer.Ordinal);

        public ValueTask ProvisionAsync(string databasePath, SqliteVecProvisionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _vectorsByTable[request.TableName] = request.Vectors.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
            string databasePath,
            SqliteVecSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = _vectorsByTable.TryGetValue(request.TableName, out var vectors)
                ? vectors.Take(request.TopK).Select((vector, index) => new SqliteVecSearchRecord(vector.RowId, index * 0.01)).ToArray()
                : [];
            return ValueTask.FromResult<IReadOnlyList<SqliteVecSearchRecord>>(results);
        }
    }

    private sealed record ScaleSource(
        ReferenceAnchorPayload Anchor,
        ReferenceChapterSplitProfilePayload Profile,
        int CharacterCount);
}
