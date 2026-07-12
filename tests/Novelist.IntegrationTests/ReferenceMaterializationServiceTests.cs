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
            new EnqueueReferenceMaterializationPayload(anchor.NovelId, anchor.AnchorId, profile.SplitProfileId, ChapterBatchSize: 10),
            CancellationToken.None);
        var status = await service.GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(anchor.NovelId, anchor.AnchorId, created.RunId),
            CancellationToken.None);
        var progress = await service.ListMaterializationChapterProgressAsync(
            new ListReferenceMaterializationChapterProgressPayload(anchor.NovelId, anchor.AnchorId, created.RunId, 1, 20),
            CancellationToken.None);

        Assert.Equal(1, preflight.CallCount);
        Assert.Equal(ReferenceMaterializationRunStates.Queued, created.Status);
        Assert.Equal(10, created.ChapterBatchSize);
        Assert.Equal("llm-provider", created.Llm.Provider);
        Assert.Equal("embedding-model", created.Embedding.ModelId);
        Assert.NotNull(status);
        Assert.Equal(created.GenerationId, status!.GenerationId);
        Assert.Equal(2, progress.Total);
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
