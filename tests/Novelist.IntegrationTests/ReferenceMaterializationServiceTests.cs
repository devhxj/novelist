using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnqueueRequiresConfirmedSplitRunsModelPreflightAndFreezesBothModelIdentities()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var preflight = new RecordingPreflight(new ReferenceMaterializationModelPreflightResult(
            new ReferenceMaterializationModelIdentityPayload("llm-provider", "llm-model"),
            new ReferenceMaterializationModelIdentityPayload("embedding-provider", "embedding-model", 16)));
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer(), modelPreflight: preflight);
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);

        var created = await service.EnqueueMaterializationAsync(
            new EnqueueReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var status = await service.GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(anchor.NovelId, anchor.AnchorId, created.RunId),
            CancellationToken.None);
        var progress = await service.ListMaterializationChapterProgressAsync(
            new ListReferenceMaterializationChapterProgressPayload(anchor.NovelId, anchor.AnchorId, created.RunId, 1, 20),
            CancellationToken.None);

        Assert.Equal(1, preflight.CallCount);
        Assert.Equal(ReferenceMaterializationRunStates.Queued, created.Status);
        Assert.Equal(1, created.ChapterBatchSize);
        Assert.Equal("llm-provider", created.Llm.Provider);
        Assert.Equal("embedding-model", created.Embedding.ModelId);
        Assert.NotNull(status);
        Assert.Equal(created.GenerationId, status!.GenerationId);
        Assert.Equal(2, progress.Total);
    }

    [Fact]
    public async Task EnqueueBuildsCandidateSourceNodesForRegisteredMaterializationSources()
    {
        var options = CreateOptions();
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("零候选回归", "", ""), CancellationToken.None);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var content = "第1章 开端\n他推门而入，屋里安静得能听见雨声。\n雨声压住窗沿，他想起昨夜的电话。\n\n第2章 结束\n门外响起第三次敲门。\n他终于开口，说出了那个藏了很久的真相。\n";
        var anchor = await anchors.RegisterMaterializationSourceFromContentAsync(
            new CreateReferenceAnchorFromContentPayload(
                novel.Id,
                "零候选回归来源",
                null,
                "regression.txt",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(content))),
            CancellationToken.None);
        var service = new SqliteReferenceMaterializationService(
            options,
            new EmptyChapterSplitAnalyzer(),
            modelPreflight: new RecordingPreflight(new ReferenceMaterializationModelPreflightResult(
                new ReferenceMaterializationModelIdentityPayload("llm", "model"),
                new ReferenceMaterializationModelIdentityPayload("embedding", "model", 8))));
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "第{number}章 {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);

        // 回归：材料化来源登记按设计跳过旧导出管线的分段，入队时必须补建段落/句子
        // 文本节点，否则 worker 构建不出候选窗口，整轮材料化"完成"但产出全 0。
        var created = await service.EnqueueMaterializationAsync(
            new EnqueueReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);

        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));
        var built = await store.BuildCandidatesForChapterAsync(created.RunId, chapterIndex: 1, CancellationToken.None);
        Assert.True(built.CandidateCount > 0, "chapter 1 must produce candidate windows from the enqueued source nodes");
        var workItem = await store.ReadQualificationWorkItemAsync(created.RunId, built.ChapterIndex, CancellationToken.None);
        Assert.NotEmpty(workItem.Request.Candidates);
        Assert.All(workItem.Request.Candidates, candidate => Assert.NotEmpty(candidate.SourceNodes));

        // 章节级直接提取：worker 用 Begin/Persist 完成打分；逐字校验丢弃不在原文中的幻觉摘录。
        var work = await store.BeginChapterExtractionAsync(created.RunId, chapterIndex: 1, CancellationToken.None);
        Assert.NotNull(work);
        Assert.Contains("他推门而入", work!.ChapterText, StringComparison.Ordinal);
        var materials = new List<ReferenceChapterExtractedMaterial>
        {
            new(
                "他推门而入，屋里安静得能听见雨声。",
                ReferenceMaterializationCandidateTypes.Passage,
                new ReferenceMaterializationQualificationTags(["worldbuilding"], [], [], []),
                new ReferenceMaterializationQualityScores(0.9, 0.7, 0.8, 0.6, 0.7, 0.5),
                0.9,
                ["worldbuilding"]),
            new(
                "这句摘录在原文中并不存在。",
                ReferenceMaterializationCandidateTypes.Hook,
                new ReferenceMaterializationQualificationTags([], [], [], []),
                new ReferenceMaterializationQualityScores(0.5, 0.5, 0.5, 0.5, 0.5, 0.5),
                0.9,
                []),
        };
        var persisted = await store.PersistChapterExtractionAsync(created.RunId, chapterIndex: 1, new ReferenceChapterExtractionResult(materials), CancellationToken.None);
        Assert.Equal(1, persisted.CandidateCount);
        Assert.Equal(1, persisted.AcceptedCount);
        Assert.Equal(1, persisted.SkippedCount);

        var embeddingWork = await store.ReadEmbeddingWorkItemAsync(created.RunId, chapterIndex: 1, CancellationToken.None);
        Assert.NotEmpty(embeddingWork.Request.Items);
    }

    [Fact]
    public async Task GetMaterializationStatusWithoutRunIdReturnsTheLatestRunForTheAnchor()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(
            options,
            new EmptyChapterSplitAnalyzer(),
            modelPreflight: new RecordingPreflight(new ReferenceMaterializationModelPreflightResult(
                new ReferenceMaterializationModelIdentityPayload("llm", "model"),
                new ReferenceMaterializationModelIdentityPayload("embedding", "model", 16))));
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var created = await service.EnqueueMaterializationAsync(
            new EnqueueReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);

        var recovered = await service.GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(anchor.NovelId, anchor.AnchorId, null),
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(created.RunId, recovered!.RunId);
    }

    [Fact]
    public async Task EnqueuePropagatesModelPreflightFailureWithoutPersistingAnyRun()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(
            options,
            new EmptyChapterSplitAnalyzer(),
            modelPreflight: new ThrowingPreflight());
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.EnqueueMaterializationAsync(
                new EnqueueReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed, exception.ErrorCode);
        Assert.Equal(0, await CountRunsAsync(options));
    }

    [Fact]
    public async Task EnqueueMarksAConfirmedProfileStaleWhenTheSourceChangedBeforePreflight()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var preflight = new RecordingPreflight(new ReferenceMaterializationModelPreflightResult(
            new ReferenceMaterializationModelIdentityPayload("llm", "model"),
            new ReferenceMaterializationModelIdentityPayload("embedding", "model", 3)));
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer(), modelPreflight: preflight);
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "sources", "service.md"), "# 第一章\n\n新来源。\n\n# 第二章\n\n仍然是新来源。\n");

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.EnqueueMaterializationAsync(
                new EnqueueReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.ChapterSplitProfileStale, exception.ErrorCode);
        Assert.Equal(0, preflight.CallCount);
        Assert.Equal(0, await CountRunsAsync(options));
        Assert.Equal(ReferenceChapterSplitProfileStates.Stale, await ReadProfileStatusAsync(options, profile.SplitProfileId));
    }

    [Fact]
    public async Task RetryRejectsModelIdentityDriftAndRequiresANewRun()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(
            options,
            new EmptyChapterSplitAnalyzer(),
            modelPreflight: new RecordingPreflight(new ReferenceMaterializationModelPreflightResult(
                new ReferenceMaterializationModelIdentityPayload("different-llm", "different-model"),
                new ReferenceMaterializationModelIdentityPayload("different-embedding", "different-model", 8))));
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));
        var run = await store.CreateAsync(new ReferenceMaterializationRunSeed(
            Guid.NewGuid().ToString("N"),
            anchor.AnchorId,
            profile.SplitProfileId,
            Guid.NewGuid().ToString("N"),
            "policy-v1",
            "candidate-v1",
            "qualifier-v1",
            new ReferenceMaterializationModelIdentityPayload("llm", "model"),
            new ReferenceMaterializationModelIdentityPayload("embedding", "model", 8),
            DateTimeOffset.UtcNow), CancellationToken.None);
        var claim = await store.ClaimCurrentBatchAsync(run.RunId, "failing-owner", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(claim);
        await store.FailCurrentBatchAsync(
            claim!,
            ReferenceMaterializationErrorCodes.LlmRequestFailed,
            "Provider timed out.",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.RetryMaterializationAsync(
                new RetryReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, run.RunId),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.RetryRequiresNewRun, exception.ErrorCode);
        var failed = await store.GetAsync(run.RunId, CancellationToken.None);
        Assert.Equal(ReferenceMaterializationRunStates.Failed, failed?.Status);
    }

    [Fact]
    public async Task RetryRejectsAQualifierSchemaThatDoesNotMatchTheCurrentMaterializationSchema()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(
            options,
            new EmptyChapterSplitAnalyzer(),
            modelPreflight: new RecordingPreflight(new ReferenceMaterializationModelPreflightResult(
                new ReferenceMaterializationModelIdentityPayload("llm", "model"),
                new ReferenceMaterializationModelIdentityPayload("embedding", "model", 8))));
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);
        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));
        var run = await store.CreateAsync(new ReferenceMaterializationRunSeed(
            Guid.NewGuid().ToString("N"),
            anchor.AnchorId,
            profile.SplitProfileId,
            Guid.NewGuid().ToString("N"),
            "policy-v1",
            "candidate-v1",
            "material-qualifier-v1",
            new ReferenceMaterializationModelIdentityPayload("llm", "model"),
            new ReferenceMaterializationModelIdentityPayload("embedding", "model", 8),
            DateTimeOffset.UtcNow), CancellationToken.None);
        var claim = await store.ClaimCurrentBatchAsync(run.RunId, "failing-owner", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(claim);
        await store.FailCurrentBatchAsync(
            claim!,
            ReferenceMaterializationErrorCodes.LlmOutputInvalid,
            "Schema did not validate.",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.RetryMaterializationAsync(
                new RetryReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, run.RunId),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.RetryRequiresNewRun, exception.ErrorCode);
    }

    [Fact]
    public async Task CandidateListingReturnsOnlyTheRequestedRunAndDecisionWithBoundedPreviews()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var service = new SqliteReferenceMaterializationService(options, new EmptyChapterSplitAnalyzer());
        var profile = await service.PreviewChapterSplitAsync(
            new PreviewReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, "# {title}"),
            CancellationToken.None);
        await service.ConfirmChapterSplitAsync(
            new ConfirmReferenceChapterSplitPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId),
            CancellationToken.None);

        var store = new SqliteReferenceMaterializationRunStore(new ReferenceCorpusDatabasePathResolver(options));
        var run = await store.CreateAsync(CreateSeed(anchor.AnchorId, profile.SplitProfileId), CancellationToken.None);
        var built = await store.BuildCandidatesForChapterAsync(run.RunId, chapterIndex: 1, CancellationToken.None);
        var workItem = await store.ReadQualificationWorkItemAsync(run.RunId, built.ChapterIndex, CancellationToken.None);
        var persisted = await store.PersistQualificationAsync(
            run.RunId,
            built.ChapterIndex,
            new ReferenceMaterializationQualificationResult(
                workItem.Request.Candidates.Select(ReviewRequiredDecision).ToArray()),
            CancellationToken.None);

        var listed = await service.ListMaterializationCandidatesAsync(
            new ListReferenceMaterializationCandidatesPayload(
                anchor.NovelId,
                anchor.AnchorId,
                run.RunId,
                ReferenceMaterializationCandidateDecisions.ReviewRequired,
                Page: 1,
                Size: 20),
            CancellationToken.None);

        Assert.Equal(persisted.ReviewCount, listed.Total);
        Assert.NotEmpty(listed.Items);
        Assert.All(listed.Items, item =>
        {
            Assert.Equal(run.RunId, item.RunId);
            Assert.Equal(anchor.AnchorId, item.AnchorId);
            Assert.Equal(ReferenceMaterializationCandidateDecisions.ReviewRequired, item.Decision);
            Assert.Equal("llm_qualifier", item.DecisionOrigin);
            Assert.InRange(item.TextPreview.Length, 1, 515);
            Assert.Equal(1, item.RowVersion);
            Assert.NotEmpty(item.ReasonCodes);
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(AppInitializationOptions options)
    {
        await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("服务入口", "", ""), CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "service.md");
        await File.WriteAllTextAsync(sourcePath, "# 第一章\n\n雨声压住窗沿。\n\n# 第二章\n\n门外响起第三次敲门。\n");
        var anchors = new SqliteReferenceAnchorService(options, novels);
        return await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "服务入口来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
    }

    private static async ValueTask<int> CountRunsAsync(AppInitializationOptions options)
    {
        var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_materialization_runs;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async ValueTask<string> ReadProfileStatusAsync(AppInitializationOptions options, string splitProfileId)
    {
        var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM reference_chapter_split_profiles WHERE split_profile_id = $split_profile_id;";
        command.Parameters.AddWithValue("$split_profile_id", splitProfileId);
        return (string)(await command.ExecuteScalarAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Split profile does not exist."));
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

    private static ReferenceMaterializationRunSeed CreateSeed(long anchorId, string splitProfileId) => new(
        Guid.NewGuid().ToString("N"),
        anchorId,
        splitProfileId,
        Guid.NewGuid().ToString("N"),
        "materialization-policy-v1",
        "candidate-window-v1",
        ReferenceMaterializationChatCompletionQualifier.SchemaVersion,
        new ReferenceMaterializationModelIdentityPayload("llm-provider", "llm-model"),
        new ReferenceMaterializationModelIdentityPayload("embedding-provider", "embedding-model", 8),
        DateTimeOffset.UtcNow);

    private static ReferenceMaterializationCandidateQualification ReviewRequiredDecision(
        ReferenceMaterializationQualificationCandidate candidate) => new(
        candidate.CandidateId,
        ReferenceMaterializationCandidateDecisions.ReviewRequired,
        candidate.SourceNodes.Select(node => new ReferenceMaterializationQualificationSpan(node.NodeId, 0, node.Text.Length)).ToArray(),
        new ReferenceMaterializationQualityScores(0.8, 0.8, 0.6, 0.7, 0.5, 0.6),
        new ReferenceMaterializationQualificationTags(["reveal"], ["suspense"], ["close_third"], ["subtext"])
        {
            SceneBeatRoles = ["turn_beat"],
            CharacterRelations = ["mistrust"],
            CausalInformationRoles = ["withheld_information"]
        },
        0.5,
        ["needs_human_context"]);

    private sealed class EmptyChapterSplitAnalyzer : IReferenceChapterSplitAnalyzer
    {
        public ValueTask<ReferenceChapterSplitModelResult> AnalyzeAsync(
            ReferenceChapterSplitModelRequest input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(ReferenceChapterSplitModelResult.Empty);
        }
    }

    private sealed class RecordingPreflight(ReferenceMaterializationModelPreflightResult result) : IReferenceMaterializationModelPreflight
    {
        public int CallCount { get; private set; }

        public ValueTask<ReferenceMaterializationModelPreflightResult> VerifyAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingPreflight : IReferenceMaterializationModelPreflight
    {
        public ValueTask<ReferenceMaterializationModelPreflightResult> VerifyAsync(CancellationToken cancellationToken)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                "Embedding health check failed.");
        }
    }
}
