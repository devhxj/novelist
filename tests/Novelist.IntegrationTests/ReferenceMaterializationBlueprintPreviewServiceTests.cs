using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationBlueprintPreviewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GeneratePersistsOnlyActiveSemanticMaterialLinksWithTheirFrozenGenerations()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        await SetActiveGenerationAsync(options, anchor.AnchorId, "generation-active-a");
        var materials = new FakeMaterializationService(anchor.NovelId, anchor.AnchorId, "generation-active-a");
        var service = new SqliteReferenceMaterializationBlueprintPreviewService(options, materials);

        var preview = await service.GenerateAsync(
            new GenerateReferenceMaterializationBlueprintPreviewPayload(
                anchor.NovelId,
                [anchor.AnchorId],
                "安排一段逐步升级的冲突并在结尾留下钩子",
                RequestedCount: 3),
            CancellationToken.None);

        var persisted = await service.GetAsync(
            new GetReferenceMaterializationBlueprintPreviewPayload(anchor.NovelId, preview.SessionId),
            CancellationToken.None);

        Assert.Equal(ReferenceMaterializationBlueprintPreviewStatuses.Active, preview.Status);
        Assert.Equal(ReferenceMaterializationBlueprintPreviewNextActions.None, preview.NextAction);
        var source = Assert.Single(preview.Sources);
        Assert.Equal(anchor.AnchorId, source.AnchorId);
        Assert.Equal("generation-active-a", source.GenerationId);
        Assert.NotEmpty(preview.Candidates);
        Assert.All(
            preview.Candidates.SelectMany(candidate => candidate.Beats).SelectMany(beat => beat.Materials),
            link => Assert.Equal("generation-active-a", link.GenerationId));
        Assert.NotNull(persisted);
        Assert.Equal(preview.SessionId, persisted!.SessionId);
        Assert.Equal("generation-active-a", Assert.Single(persisted.Sources).GenerationId);
    }

    [Fact]
    public async Task GetMarksPreviewStaleAfterActiveGenerationChangesWithoutRelinkingMaterials()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        await SetActiveGenerationAsync(options, anchor.AnchorId, "generation-active-a");
        var materials = new FakeMaterializationService(anchor.NovelId, anchor.AnchorId, "generation-active-a");
        var service = new SqliteReferenceMaterializationBlueprintPreviewService(options, materials);
        var preview = await service.GenerateAsync(
            new GenerateReferenceMaterializationBlueprintPreviewPayload(
                anchor.NovelId,
                [anchor.AnchorId],
                "安排一段逐步升级的冲突并在结尾留下钩子"),
            CancellationToken.None);

        await SetActiveGenerationAsync(options, anchor.AnchorId, "generation-active-b");

        var stale = await service.GetAsync(
            new GetReferenceMaterializationBlueprintPreviewPayload(anchor.NovelId, preview.SessionId),
            CancellationToken.None);

        Assert.NotNull(stale);
        Assert.Equal(ReferenceMaterializationBlueprintPreviewStatuses.Stale, stale!.Status);
        Assert.Equal(ReferenceMaterializationBlueprintPreviewNextActions.Rebuild, stale.NextAction);
        Assert.Equal([anchor.AnchorId], stale.StaleAnchorIds);
        Assert.All(
            stale.Candidates.SelectMany(candidate => candidate.Beats).SelectMany(beat => beat.Materials),
            link => Assert.Equal("generation-active-a", link.GenerationId));

        await SetActiveGenerationAsync(options, anchor.AnchorId, "generation-active-a");
        var stillStale = await service.GetAsync(
            new GetReferenceMaterializationBlueprintPreviewPayload(anchor.NovelId, preview.SessionId),
            CancellationToken.None);

        Assert.NotNull(stillStale);
        Assert.Equal(ReferenceMaterializationBlueprintPreviewStatuses.Stale, stillStale!.Status);
        Assert.Equal(ReferenceMaterializationBlueprintPreviewNextActions.Rebuild, stillStale.NextAction);
        Assert.Equal([anchor.AnchorId], stillStale.StaleAnchorIds);
    }

    [Fact]
    public async Task GenerateFailsWhenASelectedReferenceHasNoMaterialReadyGeneration()
    {
        var options = CreateOptions();
        var anchor = await CreateAnchorAsync(options);
        var materials = new FakeMaterializationService(anchor.NovelId, anchor.AnchorId, generationId: null);
        var service = new SqliteReferenceMaterializationBlueprintPreviewService(options, materials);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.GenerateAsync(
                new GenerateReferenceMaterializationBlueprintPreviewPayload(
                    anchor.NovelId,
                    [anchor.AnchorId],
                    "安排一段逐步升级的冲突并在结尾留下钩子"),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.BlueprintMaterialNotReady, exception.ErrorCode);
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
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图预演", string.Empty, string.Empty), CancellationToken.None);
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "preview.md");
        await File.WriteAllTextAsync(sourcePath, "# 第一章\n\n雨声压住窗沿。\n", CancellationToken.None);
        return await new SqliteReferenceAnchorService(options, novels).CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "蓝图预演来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
    }

    private static async ValueTask SetActiveGenerationAsync(
        AppInitializationOptions options,
        long anchorId,
        string generationId)
    {
        var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_anchor_materialization_state (
                anchor_id, active_generation_id, previous_generation_id, row_version, updated_at)
            VALUES ($anchor_id, $generation_id, NULL, 0, $updated_at)
            ON CONFLICT(anchor_id) DO UPDATE SET
                active_generation_id = excluded.active_generation_id,
                row_version = reference_anchor_materialization_state.row_version + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private AppInitializationOptions CreateOptions() => new()
    {
        DefaultDataDirectory = Path.Combine(_root, "data")
    };

    private sealed class FakeMaterializationService : IReferenceMaterializationService
    {
        private readonly long _novelId;
        private readonly long _anchorId;
        private readonly string? _generationId;

        public FakeMaterializationService(long novelId, long anchorId, string? generationId)
        {
            _novelId = novelId;
            _anchorId = anchorId;
            _generationId = generationId;
        }

        public ValueTask<ReferenceChapterSplitProfilePayload> AnalyzeChapterSplitAsync(AnalyzeReferenceChapterSplitPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReferenceChapterSplitProfilePayload> PreviewChapterSplitAsync(PreviewReferenceChapterSplitPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReferenceChapterSplitProfilePayload> ConfirmChapterSplitAsync(ConfirmReferenceChapterSplitPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReferenceMaterializationStatusPayload> EnqueueMaterializationAsync(EnqueueReferenceMaterializationPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReferenceMaterializationStatusPayload?> GetMaterializationStatusAsync(GetReferenceMaterializationStatusPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReferenceMaterializationStatusPayload> RetryMaterializationAsync(RetryReferenceMaterializationPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PageResultPayload<ReferenceMaterializationChapterProgressPayload>> ListMaterializationChapterProgressAsync(ListReferenceMaterializationChapterProgressPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PageResultPayload<ReferenceMaterializationCandidatePayload>> ListMaterializationCandidatesAsync(ListReferenceMaterializationCandidatesPayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReferenceMaterializationCandidateReviewResultPayload> ReviewMaterializationCandidateAsync(ReviewReferenceMaterializationCandidatePayload input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PageResultPayload<ReferenceMaterializationMaterialPayload>> ListActiveMaterialsAsync(
            ListActiveReferenceMaterializationMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_novelId, input.NovelId);
            Assert.Equal(_anchorId, input.AnchorId);
            if (_generationId is null)
            {
                return ValueTask.FromResult(new PageResultPayload<ReferenceMaterializationMaterialPayload>([], 0, input.Page, input.Size, 0));
            }

            var first = Material("material-1", _generationId, "conflict", 0.94, "冲突在雨夜里逼近。", ["conflict", "turn"]);
            var second = Material("material-2", _generationId, "hook", 0.88, "门外的第三次敲门打断了沉默。", ["hook", "pacing"]);
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterializationMaterialPayload>([first, second], 2, input.Page, input.Size, 1));
        }

        public ValueTask<IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload>> SearchActiveMaterialsAsync(
            SearchActiveReferenceMaterializationMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_novelId, input.NovelId);
            Assert.Equal(_anchorId, input.AnchorId);
            if (_generationId is null)
            {
                return ValueTask.FromResult<IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload>>([]);
            }

            IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload> hits =
            [
                new ReferenceMaterializationSemanticSearchHitPayload(Material("material-1", _generationId, "conflict", 0.94, "冲突在雨夜里逼近。", ["conflict", "turn"]), 0.93),
                new ReferenceMaterializationSemanticSearchHitPayload(Material("material-2", _generationId, "hook", 0.88, "门外的第三次敲门打断了沉默。", ["hook", "pacing"]), 0.82)
            ];
            return ValueTask.FromResult(hits);
        }

        private ReferenceMaterializationMaterialPayload Material(
            string materialId,
            string generationId,
            string materialType,
            double quality,
            string text,
            IReadOnlyList<string> functions) => new(
                materialId,
                _anchorId,
                generationId,
                materialType,
                text,
                quality,
                0.91,
                new ReferenceMaterializationMaterialTagsPayload(functions, ["tension"], ["close_third"], ["withholding"]),
                ["high_information_density"]);
    }
}
