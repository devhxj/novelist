using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceAnchoredDraftServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CorpusDrivenWritingDraftSchemaProvisionsBlueprintBeatPieces()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料驱动蓝图地基", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);

        var blueprints = await service.GetChapterBlueprintsAsync(novel.Id, chapterNumber: null, CancellationToken.None);

        Assert.Empty(blueprints);
        var columns = await ReadTableColumnsAsync(options, "reference_blueprint_beat_pieces");
        Assert.Equal(
            ["beat_id", "node_id", "observation_id", "role_in_beat", "sequence_index"],
            columns);
        Assert.Contains(
            "idx_reference_blueprint_beat_pieces_beat",
            await ReadIndexNamesAsync(options, "reference_blueprint_beat_pieces"));
    }

    [Fact]
    public async Task GenerateReviewAndApproveBlueprintPersistsAnalysisAndExecutionContract()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角发现线索后决定去见证人。"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);

        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                ChapterNumber: 3,
                Title: "第三章蓝图",
                ChapterGoal: "让主角从怀疑走向行动",
                AnchorIds: [7],
                KnownFacts: ["证人存在", "线索已经出现"],
                ForbiddenFacts: ["凶手身份"]),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintStates.Draft, blueprint.Status);
        Assert.Equal(novel.Id, blueprint.NovelId);
        Assert.Equal(3, blueprint.ChapterNumber);
        Assert.Equal("让主角从怀疑走向行动", blueprint.ChapterFunction);
        Assert.Equal("logic", blueprint.LogicAnalysis.Track);
        Assert.Equal("emotion", blueprint.EmotionAnalysis.Track);
        Assert.Equal("narration", blueprint.NarrationAnalysis.Track);
        Assert.Equal("character", blueprint.CharacterAnalysis.Track);
        Assert.Equal("reference", blueprint.ReferenceAnalysis.Track);
        Assert.Equal("transition", blueprint.TransitionPlan.Track);
        Assert.Equal("execution", blueprint.ExecutionContract.Track);
        Assert.False(string.IsNullOrWhiteSpace(blueprint.AnalysisContractHash));
        var beat = Assert.Single(blueprint.Beats);
        Assert.False(string.IsNullOrWhiteSpace(beat.NarrativeFunction));
        Assert.False(string.IsNullOrWhiteSpace(beat.LogicPremise));
        Assert.False(string.IsNullOrWhiteSpace(beat.ConflictPressure));
        Assert.False(string.IsNullOrWhiteSpace(beat.CausalityIn));
        Assert.False(string.IsNullOrWhiteSpace(beat.CausalityOut));
        Assert.False(string.IsNullOrWhiteSpace(beat.TransitionIn));
        Assert.False(string.IsNullOrWhiteSpace(beat.TransitionOut));
        Assert.False(string.IsNullOrWhiteSpace(beat.PovCharacter));
        Assert.False(string.IsNullOrWhiteSpace(beat.NarrativeDistance));
        Assert.NotEmpty(beat.ViewpointAllowedKnowledge);
        Assert.Contains("凶手身份", beat.ViewpointForbiddenKnowledge);
        Assert.NotEmpty(beat.CharacterStatesBefore);
        Assert.NotEmpty(beat.CharacterStatesAfter);
        Assert.NotEmpty(beat.CharacterGoals);
        Assert.NotEmpty(beat.CharacterMisbeliefs);
        Assert.NotEmpty(beat.RelationshipPressure);
        Assert.False(string.IsNullOrWhiteSpace(beat.EmotionTrigger));
        Assert.False(string.IsNullOrWhiteSpace(beat.EmotionBefore));
        Assert.False(string.IsNullOrWhiteSpace(beat.EmotionAfter));
        Assert.False(string.IsNullOrWhiteSpace(beat.SuppressedReaction));
        Assert.False(string.IsNullOrWhiteSpace(beat.ExternalEvidence));
        Assert.False(string.IsNullOrWhiteSpace(beat.NarrationStrategy));
        Assert.False(string.IsNullOrWhiteSpace(beat.RhythmStrategy));
        Assert.False(string.IsNullOrWhiteSpace(beat.ParagraphIntention));
        Assert.False(string.IsNullOrWhiteSpace(beat.ExecutionMode));
        Assert.False(string.IsNullOrWhiteSpace(beat.AntiScreenplayDuty));
        Assert.False(string.IsNullOrWhiteSpace(beat.SensoryAnchorTarget));
        Assert.False(string.IsNullOrWhiteSpace(beat.SubtextPlan));
        Assert.False(string.IsNullOrWhiteSpace(beat.SourceBackedDetailTarget));
        Assert.False(string.IsNullOrWhiteSpace(beat.CandidateRejectionRule));
        Assert.Contains("证人存在", beat.SceneFacts);
        Assert.Contains("凶手身份", beat.ForbiddenFacts);
        Assert.False(string.IsNullOrWhiteSpace(beat.ReferenceQuery.Query));
        Assert.NotEmpty(beat.ReferenceQuery.FunctionTags);
        Assert.NotEmpty(beat.RequiredMaterialTypes);
        Assert.False(string.IsNullOrWhiteSpace(beat.MaxRewriteLevel));
        Assert.NotNull(beat.SlotPlan);
        Assert.False(string.IsNullOrWhiteSpace(beat.LockedPhrasePolicy));
        Assert.Equal(string.Empty, beat.NoReuseReason);
        Assert.Contains("external_evidence", beat.ProseDuties);

        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Equal(blueprint.ContextHash, review.ContextHash);
        Assert.Equal(blueprint.SourcePlanHash, review.SourcePlanHash);
        Assert.Equal(blueprint.AnalysisContractHash, review.AnalysisContractHash);
        Assert.Empty(review.LogicErrors);
        Assert.Empty(review.ExecutionErrors);
        Assert.Empty(review.NovelisticNarrationErrors);
        Assert.Empty(review.MaterialFitErrors);
        Assert.Empty(review.ReferenceBindingErrors);

        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintStates.Approved, approved.Status);
        Assert.NotNull(approved.LatestReview);
        Assert.Equal(review.ReviewId, approved.LatestReview?.ReviewId);

        var list = await service.GetChapterBlueprintsAsync(novel.Id, 3, CancellationToken.None);
        var summary = Assert.Single(list);
        Assert.Equal(ReferenceBlueprintStates.Approved, summary.Status);
    }

    [Fact]
    public async Task GenerateChapterBlueprintPersistsNormalizedContextPackHash()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图上下文哈希测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "  主角在雨夜门口等待。\r\n"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);
        var input = new GenerateReferenceChapterBlueprintPayload(
            novel.Id,
            ChapterNumber: 23,
            Title: "第二十三章蓝图",
            ChapterGoal: "  让主角从等待转为行动  ",
            AnchorIds: [7],
            KnownFacts: ["  主角在门口  ", ""],
            ForbiddenFacts: ["  不能揭露凶手身份  "]);
        var expectedContextHash = ReferenceChapterBlueprintNormalizer.ComputeContextHash(
            new ReferenceChapterBlueprintContextPack(
                NovelId: novel.Id,
                ChapterNumber: 23,
                SourcePlanScope: "next",
                SourcePlanContent: "  主角在雨夜门口等待。\r\n",
                ChapterGoal: "  让主角从等待转为行动  ",
                AnchorIds: [7],
                KnownFacts: ["  主角在门口  ", ""],
                ForbiddenFacts: ["  不能揭露凶手身份  "]));

        var blueprint = await service.GenerateChapterBlueprintAsync(input, CancellationToken.None);
        var reloaded = await service.GetChapterBlueprintAsync(novel.Id, blueprint.BlueprintId, CancellationToken.None);

        Assert.Equal(expectedContextHash, blueprint.ContextHash);
        Assert.NotNull(reloaded);
        Assert.Equal(expectedContextHash, reloaded.ContextHash);
    }

    [Fact]
    public async Task GeneratedBlueprintExposesStableGeneratorVersionWithoutPromptSnapshots()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图生成器版本测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角在门口停住，决定确认线索。"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);

        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                ChapterNumber: 25,
                Title: "第二十五章蓝图",
                ChapterGoal: "确认线索并压住情绪",
                AnchorIds: [],
                KnownFacts: ["主角已经到达门口"],
                ForbiddenFacts: ["凶手身份"]),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        var stored = await ReadBlueprintReproducibilityRowAsync(options, blueprint.BlueprintId);
        var columns = await ReadTableColumnsAsync(options, "reference_chapter_blueprints");

        Assert.Equal("reference-blueprint-v1", blueprint.BuildVersion);
        Assert.Equal("reference-blueprint-v1", stored.BuildVersion);
        Assert.Equal(blueprint.ContextHash, stored.ContextHash);
        Assert.Equal(blueprint.SourcePlanHash, stored.SourcePlanHash);
        Assert.Equal(blueprint.AnalysisContractHash, stored.AnalysisContractHash);
        Assert.Equal(ReferenceChapterBlueprintReviewer.CurrentReviewVersion, review.ReviewVersion);
        Assert.DoesNotContain(columns, name => name.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("schema_snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateChapterBlueprintDoesNotPersistProseLikePlanAsBeatDuty()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图正文防护测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var proseLikePlan = """
            雨水顺着林岚的发梢滴下来，她站在旧宅门前，手指贴着冰冷的铜环，却迟迟没有敲下去。屋里传来一声很轻的咳嗽，像是有人故意把声音压进墙缝里，又像是她自己把恐惧听成了回音。她终于抬起手，门后的灯影在这一刻熄灭。
            """;
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", proseLikePlan),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);

        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                ChapterNumber: 24,
                Title: "第二十四章蓝图",
                ChapterGoal: "让林岚在门前完成从犹豫到行动的转折",
                AnchorIds: [],
                KnownFacts: ["林岚已经到达旧宅门前"],
                ForbiddenFacts: []),
            CancellationToken.None);

        var beat = Assert.Single(blueprint.Beats);
        Assert.DoesNotContain("雨水顺着林岚的发梢滴下来", beat.LogicPremise, StringComparison.Ordinal);
        Assert.Contains("final prose", beat.LogicPremise, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("structured", beat.LogicPremise, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interiority", beat.ProseDuties);
        Assert.False(string.IsNullOrWhiteSpace(beat.ParagraphIntention));
    }

    [Fact]
    public async Task ReviseApprovedBlueprintInvalidatesApprovalAndMaterialLinks()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图修订测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "revision-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "修订参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                10,
                "第十章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        var binding = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);
        Assert.Contains(binding.Links, link => link.Selected);

        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload(
                    "beat:" + blueprint.Beats[0].BeatId + ":paragraph_intention",
                    "linger on the rain pressure before moving")],
                "user",
                "tighten novelistic execution"),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintStates.Draft, revised.Status);
        Assert.NotEqual(blueprint.AnalysisContractHash, revised.AnalysisContractHash);
        Assert.Equal("linger on the rain pressure before moving", revised.Beats[0].ParagraphIntention);
        Assert.Null(revised.LatestReview);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.BindBlueprintMaterialsAsync(
                new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
                CancellationToken.None));
        var draftException = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("approved", draftException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RevisedBlueprintBeatCanBeReviewedAndApprovedAgain()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图重新评审测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                21,
                "第二十一章蓝图",
                "修订后重新评审",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var firstReview = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, firstReview.ReviewId),
            CancellationToken.None);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload(
                    "beat:" + blueprint.Beats[0].BeatId + ":paragraph_intention",
                    "hold the protagonist at the threshold before the next action")],
                "user",
                "manual beat edit"),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintStates.Draft, revised.Status);
        Assert.Null(revised.LatestReview);
        Assert.Equal("hold the protagonist at the threshold before the next action", revised.Beats[0].ParagraphIntention);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, firstReview.ReviewId),
                CancellationToken.None));

        var secondReview = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, secondReview.Status);
        Assert.NotEqual(firstReview.AnalysisContractHash, secondReview.AnalysisContractHash);

        var approvedAgain = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, secondReview.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintStates.Approved, approvedAgain.Status);
        Assert.NotNull(approvedAgain.LatestReview);
        Assert.Equal(secondReview.ReviewId, approvedAgain.LatestReview.ReviewId);
        Assert.Equal("hold the protagonist at the threshold before the next action", approvedAgain.Beats[0].ParagraphIntention);
    }

    [Fact]
    public async Task ReviseApprovedBlueprintSupportsKnownFactsAndReferenceQueryEdits()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图事实修订测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "revision-contract-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "事实修订参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                15,
                "第十五章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        var binding = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);
        Assert.Contains(binding.Links, link => link.Selected);

        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        "known_facts",
                        JsonSerializer.Serialize(new[] { "雨声压低了整条街的呼吸", "周鸣是卧底" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + blueprint.Beats[0].BeatId + ":reference_query.query",
                        "周鸣是卧底")
                ],
                "user",
                "update factual boundary and material query"),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintStates.Draft, revised.Status);
        Assert.NotEqual(blueprint.AnalysisContractHash, revised.AnalysisContractHash);
        Assert.Contains("周鸣是卧底", revised.KnownFacts);
        Assert.Equal("周鸣是卧底", revised.Beats[0].ReferenceQuery.Query);
        Assert.Null(revised.LatestReview);
        var revisionFieldPaths = await ReadRevisionFieldPathsAsync(options, blueprint.BlueprintId);
        Assert.Contains("known_facts", revisionFieldPaths);
        Assert.Contains("beat:" + blueprint.Beats[0].BeatId + ":reference_query.query", revisionFieldPaths);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.BindBlueprintMaterialsAsync(
                new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReviseApprovedBlueprintSupportsAnalysisAndExecutionTrackEdits()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图分析修订测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                16,
                "第十六章蓝图",
                "先评审再写正文",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        "logic_analysis.summary",
                        "chapter logic must delay the reveal until the final pressure point"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "execution_contract.paragraph_intentions",
                        JsonSerializer.Serialize(new[] { "hold interior pressure before action", "surface the consequence through external evidence" }))
                ],
                "user",
                "tighten analysis contract"),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintStates.Draft, revised.Status);
        Assert.NotEqual(blueprint.AnalysisContractHash, revised.AnalysisContractHash);
        Assert.Equal("chapter logic must delay the reveal until the final pressure point", revised.LogicAnalysis.Summary);
        Assert.Equal(
            ["hold interior pressure before action", "surface the consequence through external evidence"],
            revised.ExecutionContract.ParagraphIntentions);
        Assert.Null(revised.LatestReview);
        var revisionFieldPaths = await ReadRevisionFieldPathsAsync(options, blueprint.BlueprintId);
        Assert.Contains("logic_analysis.summary", revisionFieldPaths);
        Assert.Contains("execution_contract.paragraph_intentions", revisionFieldPaths);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReviseApprovedBlueprintSupportsBeatContractFieldEdits()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图节拍字段修订测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                17,
                "第十七章蓝图",
                "修订节拍视角和情绪机制",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        var beatId = blueprint.Beats[0].BeatId;
        var slotPlan = new[]
        {
            new ReferenceSlotValuePayload("object", "门缝血迹"),
            new ReferenceSlotValuePayload("reaction", "指尖发紧")
        };
        var prefix = "beat:" + beatId + ":";
        var changes = new (string Field, string Value)[]
        {
            ("narrative_function", "delay the reveal through pressure"),
            ("logic_premise", "blood at the door changes the character choice"),
            ("conflict_pressure", "the door forces a choice before safety returns"),
            ("causality_in", "the character arrives because the previous clue points here"),
            ("causality_out", "the character must act after seeing the blood"),
            ("transition_in", "pressure from the prior clue carries into the doorway"),
            ("transition_out", "the discovery pushes the next scene into consequence"),
            ("pov_character", "周鸣"),
            ("narrative_distance", "close"),
            ("viewpoint_allowed_knowledge", JsonSerializer.Serialize(new[] { "周鸣看到门缝里的血迹" })),
            ("viewpoint_forbidden_knowledge", JsonSerializer.Serialize(new[] { "周鸣不知道屋内真相" })),
            ("character_states_before", JsonSerializer.Serialize(new[] { "周鸣保持克制" })),
            ("character_states_after", JsonSerializer.Serialize(new[] { "周鸣 exposed" })),
            ("character_goals", JsonSerializer.Serialize(new[] { "确认门后发生了什么" })),
            ("character_misbeliefs", JsonSerializer.Serialize(new[] { "以为门后仍然安全" })),
            ("relationship_pressure", JsonSerializer.Serialize(new[] { "同伴的沉默迫使周鸣独自判断" })),
            ("emotion_trigger", "门缝里的血迹"),
            ("emotion_before", "controlled"),
            ("emotion_after", "alert"),
            ("suppressed_reaction", "周鸣压住后退冲动"),
            ("external_evidence", "指尖发紧"),
            ("narration_strategy", "hold close interiority around the visible clue"),
            ("rhythm_strategy", "slow the sentence before the turn"),
            ("paragraph_intention", "linger on the threshold before action"),
            ("execution_mode", "dwell"),
            ("anti_screenplay_duty", "show pressure through interiority before movement"),
            ("sensory_anchor_target", "cold metal at the door"),
            ("subtext_plan", "make the hesitation imply fear without naming it"),
            ("source_backed_detail_target", "blood line in the door seam"),
            ("candidate_rejection_rule", "reject movement-only prose"),
            ("scene_facts", JsonSerializer.Serialize(new[] { "门缝里的血迹" })),
            ("forbidden_facts", JsonSerializer.Serialize(new[] { "屋内真相" })),
            ("required_material_types", JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
            ("max_rewrite_level", ReferenceRewriteLevels.L1),
            ("slot_plan", JsonSerializer.Serialize(slotPlan)),
            ("locked_phrase_policy", "preserve only cadence, not wording"),
            ("no_reuse_reason", "transition carries approved pressure without reusable source"),
            ("prose_duties", JsonSerializer.Serialize(new[] { "interiority", "external_evidence", "transition" })),
            ("reference_query.query", "门缝里的血迹"),
            ("reference_query.material_types", JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
            ("reference_query.emotion_tags", JsonSerializer.Serialize(new[] { "alert" })),
            ("reference_query.function_tags", JsonSerializer.Serialize(new[] { "identity_reveal" })),
            ("reference_query.pov_tags", JsonSerializer.Serialize(new[] { "close" })),
            ("reference_query.technique_tags", JsonSerializer.Serialize(new[] { "threshold" })),
            ("reference_query.max_results", "4")
        };

        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                changes
                    .Select(change => new ReferenceBlueprintRevisionChangePayload(prefix + change.Field, change.Value))
                    .ToArray(),
                "user",
                "edit beat contract fields"),
            CancellationToken.None);

        var revisedBeat = revised.Beats[0];
        Assert.Equal(ReferenceBlueprintStates.Draft, revised.Status);
        Assert.NotEqual(blueprint.AnalysisContractHash, revised.AnalysisContractHash);
        Assert.Equal("周鸣", revisedBeat.PovCharacter);
        Assert.Equal(["周鸣看到门缝里的血迹"], revisedBeat.ViewpointAllowedKnowledge);
        Assert.Equal(["周鸣不知道屋内真相"], revisedBeat.ViewpointForbiddenKnowledge);
        Assert.Equal(["周鸣保持克制"], revisedBeat.CharacterStatesBefore);
        Assert.Equal(["周鸣 exposed"], revisedBeat.CharacterStatesAfter);
        Assert.Equal(["确认门后发生了什么"], revisedBeat.CharacterGoals);
        Assert.Equal(["以为门后仍然安全"], revisedBeat.CharacterMisbeliefs);
        Assert.Equal(["同伴的沉默迫使周鸣独自判断"], revisedBeat.RelationshipPressure);
        Assert.Equal("门缝里的血迹", revisedBeat.EmotionTrigger);
        Assert.Equal("指尖发紧", revisedBeat.ExternalEvidence);
        Assert.Equal(["门缝里的血迹"], revisedBeat.SceneFacts);
        Assert.Equal(["屋内真相"], revisedBeat.ForbiddenFacts);
        Assert.Equal(["interiority", "external_evidence", "transition"], revisedBeat.ProseDuties);
        Assert.Equal(["identity_reveal"], revisedBeat.ReferenceQuery.FunctionTags);
        Assert.Equal(["alert"], revisedBeat.ReferenceQuery.EmotionTags);
        Assert.Equal(["close"], revisedBeat.ReferenceQuery.PovTags);
        Assert.Equal(["threshold"], revisedBeat.ReferenceQuery.TechniqueTags);
        Assert.Equal(4, revisedBeat.ReferenceQuery.MaxResults);
        Assert.Equal(slotPlan, revisedBeat.SlotPlan);
        Assert.Null(revised.LatestReview);

        var revisionFieldPaths = await ReadRevisionFieldPathsAsync(options, blueprint.BlueprintId);
        foreach (var (field, _) in changes)
        {
            Assert.Contains(prefix + field, revisionFieldPaths);
        }

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApproveBlueprintRejectsFailedReview()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("失败评审测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                1,
                "第一章蓝图",
                "测试禁用事实",
                [],
                KnownFacts: ["门"],
                ForbiddenFacts: ["final hook"]),
            CancellationToken.None);

        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApproveBlueprintRejectsReviewVersionMismatch()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("评审版本门禁测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                22,
                "第二十二章蓝图",
                "审批必须匹配当前评审版本",
                [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);

        await SetReviewVersionAsync(options, review.ReviewId, ReferenceChapterBlueprintReviewer.CurrentReviewVersion + 1);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
                CancellationToken.None));

        Assert.Contains("current passing review", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApproveBlueprintRecordsFrozenApprovalSnapshot()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("审批记录测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                23,
                "第二十三章蓝图",
                "审批记录必须冻结评审合同",
                [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        var beforeApproval = DateTimeOffset.UtcNow;

        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId, "user"),
            CancellationToken.None);

        var approval = await ReadBlueprintApprovalAsync(options, blueprint.BlueprintId, review.ReviewId);
        Assert.False(string.IsNullOrWhiteSpace(approval.ApprovalId));
        Assert.Equal(blueprint.BlueprintId, approval.BlueprintId);
        Assert.Equal(review.ReviewId, approval.ReviewId);
        Assert.Equal(review.ContextHash, approval.ContextHash);
        Assert.Equal(review.SourcePlanHash, approval.SourcePlanHash);
        Assert.Equal(review.AnalysisContractHash, approval.AnalysisContractHash);
        Assert.Equal(review.ReviewVersion, approval.ReviewVersion);
        Assert.Equal("user", approval.ApproverOrigin);
        Assert.True(approval.ApprovedAt >= beforeApproval.AddSeconds(-1));
        Assert.True(approval.ApprovedAt <= DateTimeOffset.UtcNow.AddSeconds(1));

        await SetReviewContextHashAsync(options, review.ReviewId, "changed-after-approval");

        var frozenApproval = await ReadBlueprintApprovalAsync(options, blueprint.BlueprintId, review.ReviewId);
        Assert.Equal(review.ContextHash, frozenApproval.ContextHash);
    }

    [Fact]
    public async Task ApproveBlueprintRejectsAgentOriginForStyleContractBlueprint()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("风格审批来源测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                24,
                "第二十四章风格蓝图",
                "风格合约必须由用户批准",
                [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var styleContractJson = JsonSerializer.Serialize(
            new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [99],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Loose,
                MinStyleFit: 0,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: [],
                ForbiddenStyleRisks: ["source_leak"]),
            BridgeJson.SerializerOptions);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload($"beat:{blueprint.Beats[0].BeatId}:style_contract", styleContractJson)],
                "agent",
                "propose style contract"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId, "agent"),
                CancellationToken.None));

        Assert.Contains("style contract", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user approval", exception.Message, StringComparison.OrdinalIgnoreCase);

        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId, "user"),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintStates.Approved, approved.Status);
    }

    [Fact]
    public async Task ReviewChapterBlueprintPersistsStructuredDefects()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("结构化评审缺陷测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                24,
                "第二十四章蓝图",
                "评审缺陷必须带字段路径",
                [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatId = blueprint.Beats[0].BeatId;
        await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload("beat:" + beatId + ":causality_out", "")],
                "user",
                "force structured review defect"),
            CancellationToken.None);

        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);

        var defect = Assert.Single(review.Defects, item => item.FieldPath == "beat:" + beatId + ":causality_out");
        Assert.Equal(beatId, defect.BeatId);
        Assert.Equal("error", defect.Severity);
        Assert.Contains("causality_out", defect.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(defect.RequiredFix));

        var persisted = await service.GetChapterBlueprintAsync(novel.Id, blueprint.BlueprintId, CancellationToken.None);
        Assert.NotNull(persisted?.LatestReview);
        Assert.Contains(
            persisted.LatestReview.Defects,
            item => item.FieldPath == "beat:" + beatId + ":causality_out" &&
                item.BeatId == beatId &&
                item.Severity == "error" &&
                !string.IsNullOrWhiteSpace(item.RequiredFix));
    }

    [Fact]
    public async Task ReviewChapterBlueprintReusesExistingReviewForUnchangedContract()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("幂等评审测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                21,
                "第二十一章蓝图",
                "同一蓝图重复评审应复用结果",
                [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);

        var first = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        var second = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);

        Assert.Equal(first.ReviewId, second.ReviewId);
        Assert.Equal(first.ReviewedAt, second.ReviewedAt);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(1, await CountBlueprintReviewsAsync(options, blueprint.BlueprintId));

        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, first.ReviewId),
            CancellationToken.None);
        var third = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        var reloaded = await service.GetChapterBlueprintAsync(novel.Id, blueprint.BlueprintId, CancellationToken.None);

        Assert.Equal(first.ReviewId, third.ReviewId);
        Assert.Equal(ReferenceBlueprintStates.Approved, reloaded?.Status);
        Assert.Equal(1, await CountBlueprintReviewsAsync(options, blueprint.BlueprintId));
    }

    [Fact]
    public async Task BindBlueprintMaterialsRanksAndPersistsReferenceLinksForApprovedBlueprint()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料绑定测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低了街道，主角在门口停住。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "雨夜参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                4,
                "第四章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸", "主角在门口"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var result = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);

        Assert.Equal(blueprint.BlueprintId, result.BlueprintId);
        Assert.NotEmpty(result.Links);
        var link = Assert.Single(result.Links, item => item.Selected);
        Assert.Equal(blueprint.BlueprintId, link.BlueprintId);
        Assert.Equal(blueprint.Beats[0].BeatId, link.BeatId);
        Assert.False(string.IsNullOrWhiteSpace(link.MaterialId));
        Assert.Equal(blueprint.Beats[0].MaxRewriteLevel, link.MaxRewriteLevel);
        Assert.True(link.Selected);
        Assert.True(link.Score > 0);
        Assert.True(link.ScoreComponents.Count > 0);
        Assert.Contains("function", link.ScoreComponents.Keys);
        Assert.Contains("lexical", link.ScoreComponents.Keys);
        Assert.Contains("Beat", link.FitExplanation, StringComparison.Ordinal);
        Assert.Contains("function", link.FitExplanation, StringComparison.OrdinalIgnoreCase);

        var afterBinding = await service.GetChapterBlueprintAsync(novel.Id, blueprint.BlueprintId, CancellationToken.None);
        Assert.NotNull(afterBinding);
        Assert.Equal(ReferenceBlueprintStates.MaterialBound, afterBinding.Status);
        Assert.NotNull(afterBinding.LatestReview);
        Assert.Equal(review.ReviewId, afterBinding.LatestReview?.ReviewId);
    }

    [Fact]
    public async Task BindBlueprintMaterialsPreservesLexicalScoreForUnknownLicenseTruncatedPreviews()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("未知授权绑定评分测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "unknown-binding.md",
            """
            # 第一章

            雨声压低了整条街的呼吸，周鸣在门口停了很久，钥匙在掌心硌出一点冷意，走廊尽头的灯反复闪烁，他没有立刻敲门，只把那口气慢慢咽回去。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "未知授权绑定参考", null, sourcePath, "markdown", "unknown"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                46,
                "未知授权绑定蓝图",
                "敲门前的压住反应",
                [anchor.AnchorId],
                KnownFacts: ["周鸣在门口", "敲门前压住反应"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":reference_query.query",
                        "慢慢咽回去"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":reference_query.material_types",
                        "[\"sentence\"]"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":reference_query.function_tags",
                        "[\"interiority\"]"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":required_material_types",
                        "[\"sentence\"]"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":prose_duties",
                        "[\"external_evidence\"]")
                ],
                "user",
                "verify unknown-license preview truncation does not weaken binding lexical score"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var result = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 1, SelectTopCandidate: true),
            CancellationToken.None);

        var selected = Assert.Single(result.Links, link => link.Selected);
        Assert.True(selected.ScoreComponents.TryGetValue("lexical", out var lexical));
        Assert.True(lexical > 0);
    }

    [Fact]
    public async Task BindBlueprintMaterialsCanReturnRankedCandidatesWithoutSelectingThem()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料候选预览测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低了街道，主角在门口停住。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "anchor-candidate-preview.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "候选预览参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                44,
                "第四十四章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸", "主角在门口"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var candidates = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(
                novel.Id,
                blueprint.BlueprintId,
                MaxResultsPerBeat: 3,
                SelectTopCandidate: false),
            CancellationToken.None);

        Assert.NotEmpty(candidates.Links);
        Assert.All(candidates.Links, link => Assert.False(link.Selected));
        var afterPreview = await service.GetChapterBlueprintAsync(novel.Id, blueprint.BlueprintId, CancellationToken.None);
        Assert.NotNull(afterPreview);
        Assert.Equal(ReferenceBlueprintStates.Approved, afterPreview.Status);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("selected reference material links", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BindBlueprintMaterialsBoostsPreviouslyAcceptedReferenceMaterial()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("反馈排序测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "feedback-ranking.md",
            """
            # 第一章

            雨声压低了街的呼吸。

            雨声压低了街的呼吸，她想起旧门。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "反馈排序参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var materials = await referenceAnchors.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "雨声压低",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var acceptedMaterial = Assert.Single(materials.Items, item => item.Text == "雨声压低了街的呼吸。");
        var defaultPreferredMaterial = Assert.Single(materials.Items, item => item.Text == "雨声压低了街的呼吸，她想起旧门。");

        var baselineBlueprint = await CreateApprovedSentenceBlueprintAsync(31);
        var baseline = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, baselineBlueprint.BlueprintId, MaxResultsPerBeat: 2, SelectTopCandidate: true),
            CancellationToken.None);
        var baselineSelected = Assert.Single(baseline.Links, item => item.Selected);
        Assert.Equal(defaultPreferredMaterial.MaterialId, baselineSelected.MaterialId);
        Assert.DoesNotContain("accepted_feedback", baselineSelected.ScoreComponents.Keys);

        await referenceAnchors.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                novel.Id,
                ReferenceFeedbackTargetTypes.Material,
                acceptedMaterial.MaterialId,
                ReferenceFeedbackDecisions.Accepted,
                acceptedMaterial.MaterialId,
                CandidateId: "",
                BlueprintId: baselineBlueprint.BlueprintId,
                BeatId: baselineBlueprint.Beats[0].BeatId,
                FeedbackTags: ["useful_reference"],
                Note: "这个环境压力细节适合相似节拍",
                EditedText: "",
                Origin: "user"),
            CancellationToken.None);

        var boostedBlueprint = await CreateApprovedSentenceBlueprintAsync(32);
        var boosted = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, boostedBlueprint.BlueprintId, MaxResultsPerBeat: 2, SelectTopCandidate: true),
            CancellationToken.None);
        var boostedSelected = Assert.Single(boosted.Links, item => item.Selected);

        Assert.Equal(acceptedMaterial.MaterialId, boostedSelected.MaterialId);
        Assert.True(boostedSelected.ScoreComponents["accepted_feedback"] > 0);

        async Task<ReferenceChapterBlueprintPayload> CreateApprovedSentenceBlueprintAsync(int chapterNumber)
        {
            var generated = await service.GenerateChapterBlueprintAsync(
                new GenerateReferenceChapterBlueprintPayload(
                    novel.Id,
                    chapterNumber,
                    "反馈排序蓝图",
                    "雨声压低",
                    [anchor.AnchorId],
                    KnownFacts: ["雨声压低了街的呼吸"],
                    ForbiddenFacts: []),
                CancellationToken.None);
            var revised = await service.ReviseChapterBlueprintAsync(
                new ReviseReferenceChapterBlueprintPayload(
                    novel.Id,
                    generated.BlueprintId,
                    [
                        new ReferenceBlueprintRevisionChangePayload(
                            "beat:" + generated.Beats[0].BeatId + ":reference_query.query",
                            "雨声压低"),
                        new ReferenceBlueprintRevisionChangePayload(
                            "beat:" + generated.Beats[0].BeatId + ":reference_query.material_types",
                            "[\"sentence\"]"),
                        new ReferenceBlueprintRevisionChangePayload(
                            "beat:" + generated.Beats[0].BeatId + ":required_material_types",
                            "[\"sentence\"]")
                    ],
                    "user",
                    "limit ranking fixture to sentence materials"),
                CancellationToken.None);
            var review = await service.ReviewChapterBlueprintAsync(
                new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
                CancellationToken.None);
            return await service.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task WorkspaceCorpusFeedbackBoostsOnlyTheNovelThatRecordedUsage()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var sourceNovel = await novels.CreateNovelAsync(new CreateNovelPayload("共享反馈来源小说", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("共享反馈隔离小说", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "workspace-feedback-ranking.md",
            """
            # 第一章

            雨声压低了街的呼吸。

            雨声压低了街的呼吸，她想起旧门。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(sourceNovel.Id, "工作区反馈排序参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, anchor.AnchorId);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var materials = await referenceAnchors.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                sourceNovel.Id,
                [anchor.AnchorId],
                "雨声压低",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var acceptedMaterial = Assert.Single(materials.Items, item => item.Text == "雨声压低了街的呼吸。");
        var defaultPreferredMaterial = Assert.Single(materials.Items, item => item.Text == "雨声压低了街的呼吸，她想起旧门。");

        var sourceBaselineBlueprint = await CreateApprovedWorkspaceSentenceBlueprintAsync(sourceNovel.Id, 41);
        var sourceBaseline = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(sourceNovel.Id, sourceBaselineBlueprint.BlueprintId, MaxResultsPerBeat: 2, SelectTopCandidate: true),
            CancellationToken.None);
        var sourceBaselineSelected = Assert.Single(sourceBaseline.Links, item => item.Selected);
        Assert.Equal(defaultPreferredMaterial.MaterialId, sourceBaselineSelected.MaterialId);
        Assert.DoesNotContain("accepted_feedback", sourceBaselineSelected.ScoreComponents.Keys);

        await referenceAnchors.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                sourceNovel.Id,
                ReferenceFeedbackTargetTypes.Material,
                acceptedMaterial.MaterialId,
                ReferenceFeedbackDecisions.Accepted,
                acceptedMaterial.MaterialId,
                CandidateId: "",
                BlueprintId: sourceBaselineBlueprint.BlueprintId,
                BeatId: sourceBaselineBlueprint.Beats[0].BeatId,
                FeedbackTags: ["source_novel_usage"],
                Note: "source novel accepts this shared material",
                EditedText: "",
                Origin: "user"),
            CancellationToken.None);

        var sourceBoostedBlueprint = await CreateApprovedWorkspaceSentenceBlueprintAsync(sourceNovel.Id, 42);
        var sourceBoosted = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(sourceNovel.Id, sourceBoostedBlueprint.BlueprintId, MaxResultsPerBeat: 2, SelectTopCandidate: true),
            CancellationToken.None);
        var sourceBoostedSelected = Assert.Single(sourceBoosted.Links, item => item.Selected);
        Assert.Equal(acceptedMaterial.MaterialId, sourceBoostedSelected.MaterialId);
        Assert.True(sourceBoostedSelected.ScoreComponents["accepted_feedback"] > 0);

        var otherBlueprint = await CreateApprovedWorkspaceSentenceBlueprintAsync(otherNovel.Id, 41);
        var otherBinding = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(otherNovel.Id, otherBlueprint.BlueprintId, MaxResultsPerBeat: 2, SelectTopCandidate: true),
            CancellationToken.None);
        var otherSelected = Assert.Single(otherBinding.Links, item => item.Selected);
        Assert.Equal(defaultPreferredMaterial.MaterialId, otherSelected.MaterialId);
        Assert.DoesNotContain("accepted_feedback", otherSelected.ScoreComponents.Keys);

        async Task<ReferenceChapterBlueprintPayload> CreateApprovedWorkspaceSentenceBlueprintAsync(long novelId, int chapterNumber)
        {
            var generated = await service.GenerateChapterBlueprintAsync(
                new GenerateReferenceChapterBlueprintPayload(
                    novelId,
                    chapterNumber,
                    "共享反馈排序蓝图",
                    "雨声压低",
                    AnchorIds: [],
                    KnownFacts: ["雨声压低了街的呼吸"],
                    ForbiddenFacts: []),
                CancellationToken.None);
            var revised = await service.ReviseChapterBlueprintAsync(
                new ReviseReferenceChapterBlueprintPayload(
                    novelId,
                    generated.BlueprintId,
                    [
                        new ReferenceBlueprintRevisionChangePayload(
                            "beat:" + generated.Beats[0].BeatId + ":reference_query.query",
                            "雨声压低"),
                        new ReferenceBlueprintRevisionChangePayload(
                            "beat:" + generated.Beats[0].BeatId + ":reference_query.material_types",
                            "[\"sentence\"]"),
                        new ReferenceBlueprintRevisionChangePayload(
                            "beat:" + generated.Beats[0].BeatId + ":required_material_types",
                            "[\"sentence\"]")
                    ],
                    "user",
                    "limit workspace ranking fixture to sentence materials"),
                CancellationToken.None);
            var review = await service.ReviewChapterBlueprintAsync(
                new ReviewReferenceChapterBlueprintPayload(novelId, revised.BlueprintId),
                CancellationToken.None);
            return await service.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(novelId, revised.BlueprintId, review.ReviewId),
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task BindBlueprintMaterialsCarriesSearchEmbeddingScoreIntoLinkComponents()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("绑定向量评分测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var material = new ReferenceMaterialPayload(
            MaterialId: "embedding-ranked-material",
            AnchorId: 1,
            SourceSegmentId: "segment-1",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "environment",
            EmotionTag: "neutral",
            SceneTag: "environment",
            PovTag: "close",
            TechniqueTag: "sensory_detail",
            FunctionConfidence: 0.8,
            EmotionConfidence: 0.7,
            PovConfidence: 0.7,
            Text: "雨声压低了门口。",
            SourceHash: "source-hash",
            ExtractorVersion: "test",
            UserVerified: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ScoreComponents: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["embedding"] = 3.8
            });
        var referenceAnchors = new FixedReferenceAnchorService(material, applySearchFilters: true);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                45,
                "绑定向量评分蓝图",
                "雨声压低",
                [1],
                KnownFacts: ["雨声压低了门口"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + generated.Beats[0].BeatId + ":";
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.query",
                        "雨声压低"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "environment" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(new[] { "close" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence }))
                ],
                "user",
                "construct embedding-scored material binding fixture"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var result = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, revised.BlueprintId, MaxResultsPerBeat: 1, SelectTopCandidate: true),
            CancellationToken.None);

        var selected = Assert.Single(result.Links, link => link.Selected);
        Assert.True(selected.ScoreComponents["embedding"] > 0);
        Assert.Contains("embedding", selected.FitExplanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BindBlueprintMaterialsRejectsLexicalMatchesWithoutFunctionalFit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语义匹配门禁测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var semanticOnlyMaterial = new ReferenceMaterialPayload(
            MaterialId: "semantic-only-material",
            AnchorId: 1,
            SourceSegmentId: "segment-1",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "dialogue",
            EmotionTag: "spoken",
            SceneTag: "conversation",
            PovTag: "unknown",
            TechniqueTag: "dialogue_exchange",
            FunctionConfidence: 0.8,
            EmotionConfidence: 0.7,
            PovConfidence: 0.55,
            Text: "雨声压低了街的呼吸。",
            SourceHash: "source-hash",
            ExtractorVersion: "test",
            UserVerified: false,
            CreatedAt: DateTimeOffset.UtcNow);
        var referenceAnchors = new FixedReferenceAnchorService(semanticOnlyMaterial);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                41,
                "语义匹配门禁蓝图",
                "雨声压低",
                [1],
                KnownFacts: ["雨声压低了街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":reference_query.query",
                        "雨声压低"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":reference_query.material_types",
                        "[\"sentence\"]"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":reference_query.function_tags",
                        "[\"environment\"]"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":reference_query.pov_tags",
                        "[\"close\"]"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":required_material_types",
                        "[\"sentence\"]"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + generated.Beats[0].BeatId + ":prose_duties",
                        "[\"external_evidence\"]")
                ],
                "user",
                "construct lexical-only binding fixture"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var result = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);

        Assert.Empty(result.Links);
    }

    [Fact]
    public async Task BindBlueprintMaterialsMarksExpandedQueryFallbackAsLowConfidenceWeakMatch()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("查询放宽绑定测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var weakMaterial = new ReferenceMaterialPayload(
            MaterialId: "expanded-query-material",
            AnchorId: 1,
            SourceSegmentId: "segment-expanded-query",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "environment",
            EmotionTag: "reflective",
            SceneTag: "environment",
            PovTag: "close",
            TechniqueTag: "sensory_detail",
            FunctionConfidence: 0.8,
            EmotionConfidence: 0.7,
            PovConfidence: 0.7,
            Text: "雨声压低了街的呼吸，他心里一紧。",
            SourceHash: "source-hash-expanded-query",
            ExtractorVersion: "test",
            UserVerified: false,
            CreatedAt: DateTimeOffset.UtcNow);
        var referenceAnchors = new FixedReferenceAnchorService(weakMaterial, applySearchFilters: true);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                43,
                "查询放宽蓝图",
                "雨声压低",
                [1],
                KnownFacts: ["雨声压低了街的呼吸", "门缝里的血迹"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + generated.Beats[0].BeatId + ":";
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.query",
                        "门缝里的血迹"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.emotion_tags",
                        JsonSerializer.Serialize(new[] { "reflective" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "environment" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(new[] { "close" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.technique_tags",
                        JsonSerializer.Serialize(new[] { "sensory_detail" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "prose_duties",
                        JsonSerializer.Serialize(new[] { "external_evidence" }))
                ],
                "user",
                "verify expanded query fallback binding"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var result = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);

        Assert.Equal(2, referenceAnchors.SearchInputs.Count);
        Assert.Equal("门缝里的血迹", referenceAnchors.SearchInputs[0].Query);
        Assert.Equal(string.Empty, referenceAnchors.SearchInputs[1].Query);
        var selected = Assert.Single(result.Links, link => link.Selected);
        Assert.Equal("expanded-query-material", selected.MaterialId);
        Assert.True(selected.ScoreComponents.TryGetValue("low_confidence", out var lowConfidence));
        Assert.True(lowConfidence < 0);
        Assert.Contains("expanded query", selected.FitExplanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("weak match", selected.FitExplanation, StringComparison.OrdinalIgnoreCase);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, approved.BlueprintId, [approved.Beats[0].BeatId]),
            CancellationToken.None);
        var candidate = Assert.Single(draft.Candidates);
        Assert.Equal("failed", draft.Audit?.Status);
        Assert.Contains(draft.Audit?.ProvenanceErrors ?? [], item => item.Contains("low-confidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(draft.Audit?.RequiredFixes ?? [], item => item.Contains("stronger reference material", StringComparison.OrdinalIgnoreCase));

        var persistedAudit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, approved.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);
        Assert.Equal("failed", persistedAudit.Status);
        Assert.Contains(persistedAudit.ProvenanceErrors, item => item.Contains("weak match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BindBlueprintMaterialsMarksLowStyleFitAsLowConfidenceWeakMatch()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("低风格匹配绑定测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var weakStyleMaterial = new ReferenceMaterialPayload(
            MaterialId: "low-style-fit-material",
            AnchorId: 1,
            SourceSegmentId: "segment-low-style-fit",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "environment",
            EmotionTag: "reflective",
            SceneTag: "environment",
            PovTag: "close",
            TechniqueTag: "sensory_detail",
            FunctionConfidence: 0.8,
            EmotionConfidence: 0.7,
            PovConfidence: 0.7,
            Text: "雨声压低了街的呼吸，他心里一紧。",
            SourceHash: "source-hash-low-style-fit",
            ExtractorVersion: "test",
            UserVerified: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ScoreComponents: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["style_fit"] = 0.25,
                ["lexical"] = 2.5
            });
        var referenceAnchors = new FixedReferenceAnchorService(weakStyleMaterial, applySearchFilters: true);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                44,
                "低风格匹配蓝图",
                "雨声压低",
                [1],
                KnownFacts: ["雨声压低了街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + generated.Beats[0].BeatId + ":";
        var styleContractJson = JsonSerializer.Serialize(
            new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [77],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                MinStyleFit: 1.0,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["source_leak"]),
            BridgeJson.SerializerOptions);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.query",
                        "雨声压低"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.emotion_tags",
                        JsonSerializer.Serialize(new[] { "reflective" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "environment" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(new[] { "close" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.technique_tags",
                        JsonSerializer.Serialize(new[] { "sensory_detail" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "prose_duties",
                        JsonSerializer.Serialize(new[] { "external_evidence" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "style_contract",
                        styleContractJson)
                ],
                "user",
                "verify style-fit weak match binding"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var result = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);

        var searchInput = Assert.Single(referenceAnchors.SearchInputs);
        Assert.Equal([77], searchInput.StyleProfileIds);
        Assert.Equal(["dialogue_ratio"], searchInput.StyleDimensions);
        Assert.Equal(ReferenceStyleImitationIntensities.Strong, searchInput.ImitationIntensity);
        var selected = Assert.Single(result.Links, link => link.Selected);
        Assert.True(selected.ScoreComponents.TryGetValue("style_fit", out var styleFit));
        Assert.Equal(0.25, styleFit);
        Assert.True(selected.ScoreComponents.TryGetValue("low_confidence", out var lowConfidence));
        Assert.True(lowConfidence < 0);
        Assert.Contains("low style fit", selected.FitExplanation, StringComparison.OrdinalIgnoreCase);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, approved.BlueprintId, [approved.Beats[0].BeatId]),
            CancellationToken.None);
        Assert.Equal("failed", draft.Audit?.Status);
        Assert.Contains(draft.Audit?.ProvenanceErrors ?? [], item => item.Contains("low-confidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(draft.Audit?.RequiredFixes ?? [], item => item.Contains("retrieval gap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BlueprintApprovalSummaryIncludesCompactStyleContractPlan()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("风格审批摘要测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                45,
                "风格审批摘要蓝图",
                "雨声压低",
                [1],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + blueprint.Beats[0].BeatId + ":";
        var styleContractJson = JsonSerializer.Serialize(
            new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [99],
                StyleDimensions: ["dialogue_ratio", "sensory_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                MinStyleFit: 0.8,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["source_leak", "style_distance"]),
            BridgeJson.SerializerOptions);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload(beatPath + "style_contract", styleContractJson)],
                "user",
                "add style contract for approval summary"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);

        var summary = SqliteReferenceAnchoredDraftService.BuildBlueprintApprovalSummary(revised, review);

        Assert.Contains("style contracts:", summary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("beat 1", summary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profiles=99", summary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intensity=strong", summary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("min_fit=0.8", summary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dims=dialogue_ratio,sensory_ratio", summary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evidence=dialogue_exchange", summary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("risks=source_leak,style_distance", summary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BindBlueprintMaterialsUsesBlueprintTagFiltersAndExternalEvidenceDutyFit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("职责标签绑定测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var evidenceMaterial = new ReferenceMaterialPayload(
            MaterialId: "external-evidence-material",
            AnchorId: 1,
            SourceSegmentId: "segment-1",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "environment",
            EmotionTag: "reflective",
            SceneTag: "environment",
            PovTag: "close",
            TechniqueTag: "sensory_detail",
            FunctionConfidence: 0.8,
            EmotionConfidence: 0.7,
            PovConfidence: 0.7,
            Text: "雨声压低了街的呼吸，他心里一紧。",
            SourceHash: "source-hash",
            ExtractorVersion: "test",
            UserVerified: false,
            CreatedAt: DateTimeOffset.UtcNow);
        var referenceAnchors = new FixedReferenceAnchorService(evidenceMaterial, applySearchFilters: true);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                42,
                "职责标签蓝图",
                "雨声压低",
                [1],
                KnownFacts: ["雨声压低了街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + generated.Beats[0].BeatId + ":";
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.query",
                        "雨声压低"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.emotion_tags",
                        JsonSerializer.Serialize(new[] { "reflective" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "environment" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(new[] { "close" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.technique_tags",
                        JsonSerializer.Serialize(new[] { "sensory_detail" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "prose_duties",
                        JsonSerializer.Serialize(new[] { "external_evidence" }))
                ],
                "user",
                "verify tag-filtered duty binding"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var result = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);

        var search = Assert.Single(referenceAnchors.SearchInputs);
        Assert.Equal(["reflective"], search.EmotionTags);
        Assert.Equal(["environment"], search.FunctionTags);
        Assert.Equal(["close"], search.PovTags);
        Assert.Equal(["sensory_detail"], search.TechniqueTags);
        var selected = Assert.Single(result.Links, link => link.Selected);
        Assert.Equal("external-evidence-material", selected.MaterialId);
        Assert.True(selected.ScoreComponents["emotion"] > 0);
        Assert.True(selected.ScoreComponents["pov"] > 0);
        Assert.True(selected.ScoreComponents["prose_duty"] > 0);
    }

    [Fact]
    public async Task BindBlueprintMaterialsTreatsEmotionEvidenceAsSubtextAndExternalEvidenceDutyFit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("外显潜台词绑定测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var subtextEvidenceMaterial = new ReferenceMaterialPayload(
            MaterialId: "subtext-evidence-material",
            AnchorId: 1,
            SourceSegmentId: "segment-1",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "emotion_evidence",
            EmotionTag: "restrained",
            SceneTag: "scene",
            PovTag: "unknown",
            TechniqueTag: "external_evidence",
            FunctionConfidence: 0.8,
            EmotionConfidence: 0.7,
            PovConfidence: 0.55,
            Text: "她只把杯子推远。",
            SourceHash: "source-hash",
            ExtractorVersion: "test",
            UserVerified: false,
            CreatedAt: DateTimeOffset.UtcNow);
        var referenceAnchors = new FixedReferenceAnchorService(subtextEvidenceMaterial, applySearchFilters: true);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                43,
                "外显潜台词绑定蓝图",
                "杯子推远",
                [1],
                KnownFacts: ["她只把杯子推远"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + generated.Beats[0].BeatId + ":";
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.query",
                        "杯子推远"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(Array.Empty<string>())),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.emotion_tags",
                        JsonSerializer.Serialize(Array.Empty<string>())),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(Array.Empty<string>())),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.technique_tags",
                        JsonSerializer.Serialize(new[] { "external_evidence" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "prose_duties",
                        JsonSerializer.Serialize(new[] { "subtext", "external_evidence" }))
                ],
                "user",
                "verify emotion evidence duty binding"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var result = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);

        var selected = Assert.Single(result.Links, link => link.Selected);
        Assert.Equal("subtext-evidence-material", selected.MaterialId);
        Assert.True(selected.ScoreComponents["prose_duty"] > 0);
        Assert.Contains("prose duty", selected.FitExplanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateDraftFromBlueprintSendsOnlyBeatScopedReviewedInputsToAdapter()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿输入边界测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var material = new ReferenceMaterialPayload(
            MaterialId: "bounded-material",
            AnchorId: 1,
            SourceSegmentId: "segment-1",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "environment",
            EmotionTag: "reflective",
            SceneTag: "threshold",
            PovTag: "close",
            TechniqueTag: "sensory_detail",
            FunctionConfidence: 0.9,
            EmotionConfidence: 0.8,
            PovConfidence: 0.8,
            Text: "雨声压低了街的呼吸，周鸣心里一紧，指尖在门缝血迹前发紧。",
            SourceHash: "source-hash",
            ExtractorVersion: "test",
            UserVerified: true,
            CreatedAt: DateTimeOffset.UtcNow);
        var referenceAnchors = new FixedReferenceAnchorService(material, applySearchFilters: true);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                43,
                "草稿输入边界蓝图",
                "雨声压低",
                [1],
                KnownFacts: ["雨声压低了街的呼吸"],
                ForbiddenFacts: ["凶手身份"]),
            CancellationToken.None);
        var beatPath = "beat:" + generated.Beats[0].BeatId + ":";
        var slotPlan = new[]
        {
            new ReferenceSlotValuePayload("clue", "门缝血迹"),
            new ReferenceSlotValuePayload("reaction", "指尖发紧")
        };
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(beatPath + "reference_query.query", "雨声压低"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "environment" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(new[] { "close" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.technique_tags",
                        JsonSerializer.Serialize(new[] { "sensory_detail" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "slot_plan",
                        JsonSerializer.Serialize(slotPlan)),
                    new ReferenceBlueprintRevisionChangePayload(beatPath + "max_rewrite_level", ReferenceRewriteLevels.L1),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "scene_facts",
                        JsonSerializer.Serialize(new[] { "门缝血迹" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "viewpoint_allowed_knowledge",
                        JsonSerializer.Serialize(new[] { "周鸣看到门缝血迹" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "prose_duties",
                        JsonSerializer.Serialize(new[] { "interiority", "external_evidence", "sensory" }))
                ],
                "user",
                "bound draft adapter inputs"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);
        var binding = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);
        var selected = Assert.Single(binding.Links, link => link.Selected);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, approved.BlueprintId, [revised.Beats[0].BeatId]),
            CancellationToken.None);

        Assert.Equal("passed", draft.Audit?.Status);
        var adaptInput = Assert.Single(referenceAnchors.AdaptInputs);
        Assert.Equal(selected.MaterialId, adaptInput.MaterialId);
        Assert.Equal(ReferenceRewriteLevels.L1, adaptInput.MaxRewriteLevel);
        Assert.Equal(slotPlan, adaptInput.SlotValues);
        Assert.Contains("门缝血迹", adaptInput.SceneFacts);
        Assert.Contains("周鸣看到门缝血迹", adaptInput.SceneFacts);
        Assert.DoesNotContain("凶手身份", adaptInput.SceneFacts);
    }

    [Fact]
    public async Task GenerateDraftRecordsStyleAttemptsForLooseModerateAndStrongCandidates()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("风格候选矩阵测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var material = new ReferenceMaterialPayload(
            MaterialId: "style-candidate-material",
            AnchorId: 1,
            SourceSegmentId: "segment-style-candidate",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "dialogue",
            EmotionTag: "pressure",
            SceneTag: "dialogue",
            PovTag: "close",
            TechniqueTag: "dialogue_exchange",
            FunctionConfidence: 1,
            EmotionConfidence: 1,
            PovConfidence: 1,
            Text: "“你听见了吗？”雨声压着门缝。",
            SourceHash: "source-hash-style-candidate",
            ExtractorVersion: "test",
            UserVerified: true,
            CreatedAt: DateTimeOffset.UtcNow,
            ScoreComponents: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["style_fit"] = 1.25,
                ["lexical"] = 2.5
            });
        var referenceAnchors = new FixedReferenceAnchorService(material, applySearchFilters: true);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var generated = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                35,
                "风格候选矩阵蓝图",
                "雨声压着门缝",
                [1],
                KnownFacts: ["雨声压着门缝"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + generated.Beats[0].BeatId + ":";
        var styleContractJson = JsonSerializer.Serialize(
            new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [301],
                StyleDimensions: ["dialogue_ratio", "sensory_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                MinStyleFit: 0.8,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["source_leak"]),
            BridgeJson.SerializerOptions);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                generated.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(beatPath + "reference_query.query", "雨声压着门缝"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.emotion_tags",
                        JsonSerializer.Serialize(new[] { "pressure" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "dialogue" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(new[] { "close" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.technique_tags",
                        JsonSerializer.Serialize(new[] { "dialogue_exchange" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "prose_duties",
                        JsonSerializer.Serialize(new[] { "dialogue", "external_evidence" })),
                    new ReferenceBlueprintRevisionChangePayload(beatPath + "style_contract", styleContractJson)
                ],
                "user",
                "style candidate matrix"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);
        var binding = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);
        var selected = Assert.Single(binding.Links, link => link.Selected);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(
                novel.Id,
                approved.BlueprintId,
                [approved.Beats[0].BeatId],
                StyleIntensities:
                [
                    ReferenceStyleImitationIntensities.Loose,
                    ReferenceStyleImitationIntensities.Moderate,
                    ReferenceStyleImitationIntensities.Strong
                ],
                CandidatesPerBeat: 3),
            CancellationToken.None);

        Assert.Equal(3, draft.Candidates.Count);
        Assert.Equal(3, referenceAnchors.AdaptInputs.Count);
        Assert.Equal(
            [ReferenceStyleImitationIntensities.Loose, ReferenceStyleImitationIntensities.Moderate, ReferenceStyleImitationIntensities.Strong],
            draft.Candidates.Select(candidate => Assert.Single(candidate.StyleAttempts ?? []).ImitationIntensity).ToArray());
        Assert.All(draft.Candidates, candidate =>
        {
            Assert.Equal(approved.Beats[0].BeatId, candidate.BeatId);
            Assert.Equal(selected.MaterialId, candidate.MaterialId);
            var attempt = Assert.Single(candidate.StyleAttempts ?? []);
            Assert.Equal([301], attempt.StyleProfileIds);
            Assert.Equal(["dialogue_ratio", "sensory_ratio"], attempt.StyleDimensions);
            Assert.Equal(0.8, attempt.MinStyleFit);
            Assert.Equal("moderate", attempt.AllowedCloseness);
            Assert.Equal(["dialogue_exchange"], attempt.RequiredEvidenceTypes);
            Assert.Equal(["source_leak"], attempt.ForbiddenStyleRisks);
            Assert.Equal(1.25, attempt.SelectedMaterialStyleFit);
            Assert.False(attempt.SelectedMaterialLowConfidence);
            Assert.Equal(ReferenceStyleAttemptStatuses.Attempted, attempt.Status);
        });
        Assert.All(referenceAnchors.AdaptInputs, input =>
        {
            Assert.Equal(selected.MaterialId, input.MaterialId);
            Assert.NotNull(input.StyleContext);
            Assert.Equal([301], input.StyleContext!.StyleProfileIds);
            Assert.Equal(1.25, input.StyleContext.SelectedMaterialStyleFit);
        });
        Assert.DoesNotContain(draft.Candidates, candidate => candidate.StyleAttempts?.Any(attempt =>
            attempt.GetType().GetProperties().Any(property =>
                property.Name.Contains("SourceText", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase))) == true);

        var persistedAudit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(
                novel.Id,
                approved.BlueprintId,
                draft.Candidates.Select(candidate => candidate.CandidateId).ToArray()),
            CancellationToken.None);
        Assert.Equal(draft.Audit?.Status, persistedAudit.Status);
        Assert.Equal(draft.Candidates.Select(candidate => candidate.CandidateId).ToArray(), persistedAudit.CandidateIds);
        Assert.Equal(persistedAudit.CandidateIds, persistedAudit.ReadableReport?.CandidateIds);

        var auditRows = await ReadDraftAuditRowsAsync(options, approved.BlueprintId);
        Assert.Equal(2, auditRows.Count);
        Assert.Contains(auditRows, row => row.AuditId == draft.Audit?.AuditId);
        Assert.Contains(auditRows, row => row.AuditId == persistedAudit.AuditId);
        Assert.All(auditRows, row =>
        {
            Assert.Equal(approved.BlueprintId, row.BlueprintId);
            Assert.Equal(draft.Candidates.Select(candidate => candidate.CandidateId).ToArray(), row.CandidateIds);
            Assert.NotNull(row.ReadableReport);
            Assert.Equal(row.CandidateIds, row.ReadableReport.CandidateIds);
            Assert.DoesNotContain("candidate_text", row.ReadableReportJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source_text", row.ReadableReportJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prompt", row.ReadableReportJson, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task AuditDraftAgainstBlueprintPersistsReadableReportForMissingCandidateIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("缺失候选审计报告测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                36,
                "缺失候选蓝图",
                "雨声压低",
                [1],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var audit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, approved.BlueprintId, ["missing-candidate-1"]),
            CancellationToken.None);

        Assert.Equal("failed", audit.Status);
        Assert.Equal(["missing-candidate-1"], audit.CandidateIds);
        Assert.Equal(["missing-candidate-1"], audit.ReadableReport?.CandidateIds);
        Assert.Contains(audit.ProvenanceErrors, error => error.Contains("missing-candidate-1", StringComparison.Ordinal));
        var finding = Assert.Single(audit.ReadableReport?.Findings ?? []);
        Assert.Equal("provenance", finding.Category);
        Assert.Equal(["missing-candidate-1"], finding.CandidateIds);
        Assert.Contains("missing-candidate-1", finding.Message, StringComparison.Ordinal);

        var row = Assert.Single(await ReadDraftAuditRowsAsync(options, approved.BlueprintId));
        Assert.Equal(audit.AuditId, row.AuditId);
        Assert.Equal(["missing-candidate-1"], row.CandidateIds);
        Assert.Contains("missing-candidate-1", row.ReadableReportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate_text", row.ReadableReportJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", row.ReadableReportJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", row.ReadableReportJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDraftAuditsReturnsPersistedReportsWithoutCandidateOrSourceText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿审计报告查询测试", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿审计报告隔离测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                37,
                "第三十七章蓝图",
                "用无复用过渡承接压力",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + blueprint.Beats[0].BeatId + ":source_backed_detail_target",
                        string.Empty),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + blueprint.Beats[0].BeatId + ":no_reuse_reason",
                        "transition beat only carries approved chapter-state pressure without reusable source material")
                ],
                "user",
                "approve no-reuse draft generation path"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);
        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, [revised.Beats[0].BeatId]),
            CancellationToken.None);
        Assert.NotNull(draft.Audit);
        var generatedAudit = draft.Audit!;
        var candidate = Assert.Single(draft.Candidates);
        var persistedAudit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);
        var missingAudit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, ["missing-candidate-1"]),
            CancellationToken.None);

        var audits = await service.GetDraftAuditsAsync(
            new GetReferenceAnchoredDraftAuditsPayload(novel.Id, revised.BlueprintId, CandidateIds: null, Limit: 10),
            CancellationToken.None);

        Assert.Equal([missingAudit.AuditId, persistedAudit.AuditId, generatedAudit.AuditId], audits.Select(audit => audit.AuditId).ToArray());
        Assert.All(audits, audit =>
        {
            Assert.Equal(revised.BlueprintId, audit.BlueprintId);
            Assert.NotNull(audit.ReadableReport);
            var json = JsonSerializer.Serialize(audit, BridgeJson.SerializerOptions);
            Assert.DoesNotContain("candidate_text", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source_text", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        });

        for (var index = 0; index < 8; index++)
        {
            await service.AuditDraftAgainstBlueprintAsync(
                new AuditReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, [$"missing-candidate-extra-{index}"]),
                CancellationToken.None);
        }

        var filtered = await service.GetDraftAuditsAsync(
            new GetReferenceAnchoredDraftAuditsPayload(novel.Id, revised.BlueprintId, [candidate.CandidateId], Limit: 1),
            CancellationToken.None);

        Assert.Equal([persistedAudit.AuditId], filtered.Select(audit => audit.AuditId).ToArray());
        Assert.All(filtered, audit => Assert.Contains(candidate.CandidateId, audit.CandidateIds ?? []));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GetDraftAuditsAsync(
                new GetReferenceAnchoredDraftAuditsPayload(otherNovel.Id, revised.BlueprintId, CandidateIds: null, Limit: 10),
                CancellationToken.None));
    }

    [Fact]
    public async Task MaterialBoundBlueprintStillRejectsDraftWithoutCurrentPassingReview()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿评审门禁测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "draft-review-gate.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "草稿评审门禁参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                11,
                "第十一章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
            CancellationToken.None);

        Assert.Equal(blueprint.BlueprintId, draft.BlueprintId);
        var candidate = Assert.Single(draft.Candidates);
        Assert.Equal(blueprint.BlueprintId, candidate.BlueprintId);
        Assert.Equal(blueprint.Beats[0].BeatId, candidate.BeatId);
        Assert.False(string.IsNullOrWhiteSpace(candidate.MaterialId));
        Assert.False(string.IsNullOrWhiteSpace(candidate.Text));
        Assert.Contains(candidate.RewriteLevel, new[] { ReferenceRewriteLevels.L0, ReferenceRewriteLevels.L1 });
        Assert.Equal("passed", candidate.AuditStatus);
        Assert.NotNull(draft.Audit);
        Assert.Equal("passed", draft.Audit?.Status);

        var persistedAudit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);
        Assert.Equal("passed", persistedAudit.Status);
        Assert.Equal(candidate.RewriteLevel, persistedAudit.RewriteLevel);
        Assert.Empty(persistedAudit.ProvenanceErrors);
        Assert.Empty(persistedAudit.BlueprintErrors);

        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload(
                    "beat:" + blueprint.Beats[0].BeatId + ":candidate_rejection_rule",
                    "reject prose without source-backed rain pressure")],
                "user",
                "invalidate current review"),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintStates.Draft, revised.Status);

        var validation = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("approved", validation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditDraftAgainstBlueprintFailsWhenPersistedL2CandidateCopiesSelectedMaterial()
    {
        const string sourceText = "雨声压低了整条街的呼吸，林岚在门口停住，指尖慢慢发紧，心里一紧。";
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿近源审计测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "draft-source-leak-anchor.md",
            $"""
            # 第一章

            {sourceText}
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "草稿近源参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                12,
                "第十二章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: [sourceText],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);
        var selected = Assert.Single(await ReadSelectedMaterialLinksAsync(options, blueprint.BlueprintId));
        var candidate = new ReferenceDraftParagraphCandidatePayload(
            "manual-source-leak-candidate",
            blueprint.BlueprintId,
            selected.BeatId,
            selected.MaterialId,
            ReferenceRewriteLevels.L2,
            sourceText,
            ChangedSlots: [],
            NonSlotEdits: [],
            AuditStatus: "passed",
            DateTimeOffset.UnixEpoch);
        await InsertDraftCandidateAsync(options, candidate);

        var audit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("source-leak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            audit.RequiredFixes,
            item => item.Contains("n-gram", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("source-span", StringComparison.OrdinalIgnoreCase));

        var findings = await service.GetStyleAuditFindingsAsync(
            new GetReferenceStyleAuditFindingsPayload(
                novel.Id,
                blueprint.BlueprintId,
                [candidate.CandidateId],
                ["source_leak"],
                10),
            CancellationToken.None);

        Assert.NotEmpty(findings);
        Assert.All(findings, finding =>
        {
            Assert.Equal(audit.AuditId, finding.AuditId);
            Assert.Equal("source_leak", finding.RiskType);
            Assert.Equal([candidate.CandidateId], finding.CandidateIds);
            var findingJson = JsonSerializer.Serialize(finding, BridgeJson.SerializerOptions);
            Assert.DoesNotContain(sourceText, findingJson, StringComparison.Ordinal);
            Assert.DoesNotContain("candidate_text", findingJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source_text", findingJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prompt", findingJson, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(findings, finding => finding.Message.Contains("source-leak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuditDraftAgainstBlueprintUsesPersistedStyleProfileFeatureDistance()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿风格距离审计测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "draft-style-distance-profile.md",
            """
            # 第一章

            “你来了？”

            “我来了。”

            “门外有人。”

            “别回头。”
            """);
        var realReferenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await realReferenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "高对话风格参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "高对话风格",
                "dialogue-heavy deterministic baseline",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var profileDialogueRatio = profile.Features.NumericFeatures.Single(feature => feature.FeatureKey == "dialogue_ratio").Value;
        Assert.True(profileDialogueRatio > 0.7);

        var selectedMaterial = new ReferenceMaterialPayload(
            MaterialId: "dialogue-style-selected",
            AnchorId: anchor.AnchorId,
            SourceSegmentId: "segment-dialogue-style-selected",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "dialogue",
            EmotionTag: "pressure",
            SceneTag: "dialogue",
            PovTag: "close",
            TechniqueTag: "dialogue_exchange",
            FunctionConfidence: 1,
            EmotionConfidence: 1,
            PovConfidence: 1,
            Text: "“你来了？”",
            SourceHash: "source-hash-dialogue-style-selected",
            ExtractorVersion: "test",
            UserVerified: true,
            CreatedAt: DateTimeOffset.UtcNow,
            ScoreComponents: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["style_fit"] = 1.25,
                ["lexical"] = 2.5
            });
        var referenceAnchors = new FixedReferenceAnchorService(selectedMaterial, applySearchFilters: true);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                13,
                "第十三章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + blueprint.Beats[0].BeatId + ":";
        var styleContractJson = JsonSerializer.Serialize(
            new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [profile.ProfileId],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                MinStyleFit: 0.8,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["style_distance"]),
            BridgeJson.SerializerOptions);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(beatPath + "reference_query.query", "你来了"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.emotion_tags",
                        JsonSerializer.Serialize(new[] { "pressure" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "dialogue" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(new[] { "close" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.technique_tags",
                        JsonSerializer.Serialize(new[] { "dialogue_exchange" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "prose_duties",
                        JsonSerializer.Serialize(new[] { "interiority", "external_evidence" })),
                    new ReferenceBlueprintRevisionChangePayload(beatPath + "style_contract", styleContractJson)
                ],
                "user",
                "verify style profile distance audit"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        Assert.Equal("passed", review.Status);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);
        var selected = Assert.Single(await ReadSelectedMaterialLinksAsync(options, approved.BlueprintId));
        var candidate = new ReferenceDraftParagraphCandidatePayload(
            "manual-style-distance-candidate",
            approved.BlueprintId,
            selected.BeatId,
            selected.MaterialId,
            ReferenceRewriteLevels.L2,
            "雨声压低了整条街的呼吸，林岚心里一紧，指尖在杯沿发紧，却仍然没有后退。",
            ChangedSlots: [],
            NonSlotEdits: [],
            AuditStatus: "passed",
            DateTimeOffset.UnixEpoch);
        await InsertDraftCandidateAsync(options, candidate);

        var audit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, approved.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("style-distance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("dialogue_ratio", StringComparison.OrdinalIgnoreCase));

        var findings = await service.GetStyleAuditFindingsAsync(
            new GetReferenceStyleAuditFindingsPayload(
                novel.Id,
                approved.BlueprintId,
                [candidate.CandidateId],
                ["style_distance"],
                10),
            CancellationToken.None);

        Assert.NotEmpty(findings);
        Assert.All(findings, finding =>
        {
            Assert.Equal(audit.AuditId, finding.AuditId);
            Assert.Equal("style_distance", finding.RiskType);
            Assert.Equal([candidate.CandidateId], finding.CandidateIds);
            var findingJson = JsonSerializer.Serialize(finding, BridgeJson.SerializerOptions);
            Assert.DoesNotContain(candidate.Text, findingJson, StringComparison.Ordinal);
            Assert.DoesNotContain("candidate_text", findingJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source_text", findingJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prompt", findingJson, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(findings, finding => finding.Message.Contains("dialogue_ratio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateDraftFromBlueprintReturnsCandidatesWithoutMutatingChapterContent()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("候选草稿不落章测试", "", ""), CancellationToken.None);
        var chapters = new FileSystemChapterContentService(options, novels);
        var chapter = await chapters.CreateChapterAsync(
            new CreateChapterPayload(novel.Id, "第一章"),
            CancellationToken.None);
        const string originalContent = "林岚站在门口，雨声压低了整条街的呼吸。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, originalContent),
            CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "candidate-only-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "候选草稿参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                chapter.ChapterNumber,
                "第一章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
            CancellationToken.None);

        var candidate = Assert.Single(draft.Candidates);
        Assert.Equal(blueprint.BlueprintId, candidate.BlueprintId);
        Assert.False(string.IsNullOrWhiteSpace(candidate.Text));
        var auditRowsBeforeCandidateRead = await ReadDraftAuditRowsAsync(options, blueprint.BlueprintId);

        var persistedCandidates = await service.GetDraftCandidatesAsync(
            new GetReferenceDraftCandidatesPayload(novel.Id, blueprint.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);

        var persistedCandidate = Assert.Single(persistedCandidates);
        Assert.Equal(candidate.CandidateId, persistedCandidate.CandidateId);
        Assert.Equal(candidate.Text, persistedCandidate.Text);
        Assert.Empty(await service.GetDraftCandidatesAsync(
            new GetReferenceDraftCandidatesPayload(novel.Id, blueprint.BlueprintId, []),
            CancellationToken.None));
        Assert.Empty(await service.GetDraftCandidatesAsync(
            new GetReferenceDraftCandidatesPayload(novel.Id, blueprint.BlueprintId, ["missing-candidate"]),
            CancellationToken.None));
        Assert.Equal(
            originalContent,
            await chapters.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
        var reloadedChapters = new FileSystemChapterContentService(options, novels);
        Assert.Equal(
            originalContent,
            await reloadedChapters.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
        Assert.Equal(
            auditRowsBeforeCandidateRead.Select(row => row.AuditId).ToArray(),
            (await ReadDraftAuditRowsAsync(options, blueprint.BlueprintId)).Select(row => row.AuditId).ToArray());

        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("候选读取隔离测试", "", ""), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GetDraftCandidatesAsync(
                new GetReferenceDraftCandidatesPayload(otherNovel.Id, blueprint.BlueprintId, [candidate.CandidateId]),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApprovedBlueprintBecomesStaleWhenSourceChapterPlanChanges()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图失效测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角先在雨夜门口等待。"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            planning,
            new SqliteReferenceAnchorService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                7,
                "第七章蓝图",
                "雨夜等待",
                AnchorIds: [],
                KnownFacts: ["主角在门口"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);

        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角改为直接进入屋内。"),
            CancellationToken.None);

        var stale = await service.GetChapterBlueprintAsync(novel.Id, blueprint.BlueprintId, CancellationToken.None);

        Assert.NotNull(stale);
        Assert.Equal(ReferenceBlueprintStates.Stale, stale.Status);
        var summary = Assert.Single(await service.GetChapterBlueprintsAsync(novel.Id, 7, CancellationToken.None));
        Assert.Equal(ReferenceBlueprintStates.Stale, summary.Status);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.BindBlueprintMaterialsAsync(
                new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApprovedBlueprintWithoutSelectedMaterialsRejectsDraftGeneration()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿材料门禁测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                8,
                "第八章蓝图",
                "先绑定材料再生成正文",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));

        Assert.Contains("material", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApprovedNoReuseBeatGeneratesDraftWithoutSelectedMaterialLink()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("无复用草稿门禁测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                19,
                "第十九章蓝图",
                "用无复用过渡承接压力",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + blueprint.Beats[0].BeatId + ":source_backed_detail_target",
                        string.Empty),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + blueprint.Beats[0].BeatId + ":no_reuse_reason",
                        "transition beat only carries approved chapter-state pressure without reusable source material")
                ],
                "user",
                "approve no-reuse draft generation path"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, [revised.Beats[0].BeatId]),
            CancellationToken.None);

        var candidate = Assert.Single(draft.Candidates);
        Assert.Equal(revised.Beats[0].BeatId, candidate.BeatId);
        Assert.StartsWith("no-reuse:", candidate.MaterialId, StringComparison.Ordinal);
        Assert.Equal(ReferenceRewriteLevels.L0, candidate.RewriteLevel);
        Assert.False(string.IsNullOrWhiteSpace(candidate.Text));
        Assert.Equal("passed", draft.Audit?.Status);
        Assert.Empty(draft.Audit?.ProvenanceErrors ?? []);

        var persistedAudit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);
        Assert.Equal("passed", persistedAudit.Status);
        Assert.Empty(persistedAudit.ProvenanceErrors);
    }

    [Fact]
    public async Task GenerateDraftFromBlueprintRejectsMissingFailedStaleOrUnapprovedBlueprints()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿蓝图状态门禁测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);

        var missing = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, 987654321, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("does not exist", missing.Message, StringComparison.OrdinalIgnoreCase);

        var failedBlueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                17,
                "第十七章蓝图",
                "制造压力并留下钩子",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var failedRevision = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                failedBlueprint.BlueprintId,
                StrictReviewGateRevisionChanges(failedBlueprint.Beats[0].BeatId),
                "user",
                "exercise failed review gate"),
            CancellationToken.None);
        var failedReview = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, failedRevision.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, failedReview.Status);
        var failedState = await service.GetChapterBlueprintAsync(novel.Id, failedRevision.BlueprintId, CancellationToken.None);
        Assert.NotNull(failedState);
        Assert.Equal(ReferenceBlueprintStates.ReviewFailed, failedState.Status);
        var failed = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, failedRevision.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("approved", failed.Message, StringComparison.OrdinalIgnoreCase);

        var unapprovedBlueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                18,
                "第十八章蓝图",
                "先审批再生成正文",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintStates.Draft, unapprovedBlueprint.Status);
        var unapproved = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, unapprovedBlueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("approved", unapproved.Message, StringComparison.OrdinalIgnoreCase);

        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角先在雨夜门口等待。"),
            CancellationToken.None);
        var staleBlueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                19,
                "第十九章蓝图",
                "雨夜等待",
                AnchorIds: [],
                KnownFacts: ["主角在门口"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var staleReview = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, staleBlueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, staleBlueprint.BlueprintId, staleReview.ReviewId),
            CancellationToken.None);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角改为直接进入屋内。"),
            CancellationToken.None);
        var staleState = await service.GetChapterBlueprintAsync(novel.Id, staleBlueprint.BlueprintId, CancellationToken.None);
        Assert.NotNull(staleState);
        Assert.Equal(ReferenceBlueprintStates.Stale, staleState.Status);

        var stale = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, staleBlueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("stale", stale.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApprovedBlueprintWithMismatchedLatestReviewRejectsDraftGeneration()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿评审哈希门禁测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                20,
                "第二十章蓝图",
                "审批哈希必须保持一致",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await SetReviewContextHashAsync(options, review.ReviewId, "old-context-hash");

        var reloaded = await service.GetChapterBlueprintAsync(novel.Id, blueprint.BlueprintId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(ReferenceBlueprintStates.Approved, reloaded.Status);
        Assert.NotNull(reloaded.LatestReview);
        Assert.NotEqual(reloaded.ContextHash, reloaded.LatestReview.ContextHash);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("current passing blueprint review", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewPassedBlueprintWithoutExplicitApprovalCannotBindOrDraft()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("显式批准门禁测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "review-passed-without-approval-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "未批准绑定参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                16,
                "第十六章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);

        var bindException = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.BindBlueprintMaterialsAsync(
                new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
                CancellationToken.None));
        Assert.Contains("approved", bindException.Message, StringComparison.OrdinalIgnoreCase);

        var draftException = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("approved", draftException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DraftGenerationRejectsMaterialLinksCreatedForDifferentAnalysisContract()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料哈希门禁测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "material-link-hash-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "材料哈希参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                15,
                "第十五章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);
        await SetMaterialLinkAnalysisHashAsync(options, blueprint.BlueprintId, "old-analysis-contract-hash");

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));

        Assert.Contains("current blueprint analysis contract", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkspaceCorpusMaterialLinksAreBoundToCurrentBlueprintAnalysisContract()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("共享语料哈希门禁测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "workspace-material-link-hash-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "共享材料哈希参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, anchor.AnchorId);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                21,
                "第二十一章蓝图",
                "雨声压低了整条街的呼吸",
                AnchorIds: [],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);

        var boundLinks = await ReadSelectedMaterialLinksAsync(options, approved.BlueprintId);
        var selected = Assert.Single(boundLinks);
        Assert.StartsWith(anchor.AnchorId + ":material:", selected.MaterialId, StringComparison.Ordinal);
        var linkStates = await ReadMaterialLinkStatesAsync(options, approved.BlueprintId);
        Assert.NotEmpty(linkStates);
        Assert.All(linkStates, link =>
        {
            Assert.Equal(approved.AnalysisContractHash, link.AnalysisContractHash);
            Assert.Equal("active", link.Status);
        });

        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                approved.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload(
                    "beat:" + approved.Beats[0].BeatId + ":external_evidence",
                    "visible pause and changed action demonstrate the pressure")],
                "user",
                "change analysis contract after workspace material binding"),
            CancellationToken.None);

        Assert.NotEqual(approved.AnalysisContractHash, revised.AnalysisContractHash);
        Assert.All(await ReadMaterialLinkStatesAsync(options, approved.BlueprintId), link =>
            Assert.Equal("stale", link.Status));

        var revisionReview = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, revisionReview.ReviewId),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, BeatIds: []),
                CancellationToken.None));

        Assert.Contains("current blueprint analysis contract", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewChapterBlueprintReturnsStrictGateDefectsAfterRevision()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("严格评审门禁测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                11,
                "第十一章蓝图",
                "制造压力并留下钩子",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                StrictReviewGateRevisionChanges(blueprint.Beats[0].BeatId),
                "user",
                "exercise strict review gates"),
            CancellationToken.None);

        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.EmotionErrors, item => item.Contains("fake emotion", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.TransitionErrors, item => item.Contains("pressure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.PovErrors, item => item.Contains("周鸣是卧底", StringComparison.Ordinal));
        Assert.Contains(review.ExecutionErrors, item => item.Contains("prose duties", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.MaterialFitErrors, item => item.Contains("material fit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReviewDoesNotSkipMaterialFitForSourceBackedBeatWithNoReuseReason()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("来源必需评审门禁测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                32,
                "第三十二章蓝图",
                "雨声压低街道",
                AnchorIds: [],
                KnownFacts: ["主角已经到场", "雨声压低街道"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var beatPath = "beat:" + blueprint.Beats[0].BeatId + ":";
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "source_backed_detail_target",
                        "rain pressure detail from source material"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "no_reuse_reason",
                        "attempted skip even though source-backed detail remains required"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.query",
                        "雨声压低街道"),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "dialogue" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.emotion_tags",
                        JsonSerializer.Serialize(Array.Empty<string>())),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.pov_tags",
                        JsonSerializer.Serialize(Array.Empty<string>())),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "reference_query.technique_tags",
                        JsonSerializer.Serialize(Array.Empty<string>())),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "prose_duties",
                        JsonSerializer.Serialize(new[] { "external_evidence" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        beatPath + "required_material_types",
                        JsonSerializer.Serialize(new[] { ReferenceMaterialTypes.Sentence }))
                ],
                "test",
                "verify source-backed no-reuse still requires material fit"),
            CancellationToken.None);

        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.MaterialFitErrors, item => item.Contains("material fit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BridgeReferenceAnchoredDraftHandlersGenerateReviewAndApproveBlueprint()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图桥接测试", "", ""), CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchoredDraftHandlers(new SqliteReferenceAnchoredDraftService(
                options,
                novels,
                new FileSystemPlanningService(options, novels)));

        using var generated = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_generate_blueprint",
              "method": "GenerateReferenceChapterBlueprint",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "chapter_number": 2,
                    "title": "第二章蓝图",
                    "chapter_goal": "制造压力并留下钩子",
                    "anchor_ids": [],
                    "known_facts": ["主角已经到场"],
                    "forbidden_facts": []
                  }
                ]
              }
            }
            """));

        Assert.True(generated.RootElement.GetProperty("ok").GetBoolean());
        var blueprintId = generated.RootElement.GetProperty("result").GetProperty("blueprint_id").GetInt64();
        Assert.Equal("logic", generated.RootElement.GetProperty("result").GetProperty("logic_analysis").GetProperty("track").GetString());

        using var reviewed = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_review_blueprint",
              "method": "ReviewReferenceChapterBlueprint",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprintId}}
                  }
                ]
              }
            }
            """));

        var reviewId = reviewed.RootElement.GetProperty("result").GetProperty("review_id").GetString();
        Assert.Equal("passed", reviewed.RootElement.GetProperty("result").GetProperty("status").GetString());

        using var approved = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_approve_blueprint",
              "method": "ApproveReferenceChapterBlueprint",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprintId}},
                    "review_id": {{JsonSerializer.Serialize(reviewId)}}
                  }
                ]
              }
            }
            """));

        Assert.True(approved.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("approved", approved.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task BridgeReferenceAnchoredDraftHandlersSurfaceStrictReviewGateDefects()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("严格评审桥接测试", "", ""), CancellationToken.None);
        var draftService = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await draftService.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                12,
                "第十二章蓝图",
                "制造压力并留下钩子",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        await draftService.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                StrictReviewGateRevisionChanges(blueprint.Beats[0].BeatId),
                "user",
                "exercise strict review gates"),
            CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchoredDraftHandlers(draftService);

        using var reviewed = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_review_strict_blueprint",
              "method": "ReviewReferenceChapterBlueprint",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprint.BlueprintId}}
                  }
                ]
              }
            }
            """));

        Assert.True(reviewed.RootElement.GetProperty("ok").GetBoolean());
        var result = reviewed.RootElement.GetProperty("result");
        Assert.Equal("failed", result.GetProperty("status").GetString());
        AssertJsonArrayContains(result.GetProperty("emotion_errors"), "fake emotion");
        AssertJsonArrayContains(result.GetProperty("transition_errors"), "pressure");
        AssertJsonArrayContains(result.GetProperty("pov_errors"), "周鸣是卧底");
        AssertJsonArrayContains(result.GetProperty("execution_errors"), "prose duties");
        AssertJsonArrayContains(result.GetProperty("material_fit_errors"), "material fit");
    }

    [Fact]
    public async Task BridgeReferenceAnchoredDraftHandlersBindMaterialsOnlyAfterApproval()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图绑定桥接测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "桥接绑定参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var draftService = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await draftService.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                5,
                "第五章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchoredDraftHandlers(draftService);

        using var rejected = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bind_unapproved",
              "method": "BindReferenceBlueprintMaterials",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprint.BlueprintId}},
                    "max_results_per_beat": 2
                  }
                ]
              }
            }
            """));

        Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("VALIDATION_ERROR", rejected.RootElement.GetProperty("error").GetProperty("code").GetString());

        var review = await draftService.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await draftService.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);

        using var bound = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bind_approved",
              "method": "BindReferenceBlueprintMaterials",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprint.BlueprintId}},
                    "max_results_per_beat": 2,
                    "select_top_candidate": true
                  }
                ]
              }
            }
            """));

        Assert.True(bound.RootElement.GetProperty("ok").GetBoolean());
        var links = bound.RootElement.GetProperty("result").GetProperty("links").EnumerateArray().ToArray();
        Assert.NotEmpty(links);
        Assert.Contains(links, item => item.GetProperty("selected").GetBoolean());
        var selectedLink = links.First(item => item.GetProperty("selected").GetBoolean());
        var scoreComponents = selectedLink.GetProperty("score_components");
        Assert.True(scoreComponents.TryGetProperty("function", out _));
        Assert.True(scoreComponents.TryGetProperty("lexical", out _));
        Assert.Contains("Beat", selectedLink.GetProperty("fit_explanation").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeReferenceAnchoredDraftHandlersGenerateAndAuditBoundDraftCandidates()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿候选桥接测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "draft-bridge-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "桥接草稿参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var draftService = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await draftService.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                12,
                "第十二章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await draftService.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await draftService.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await draftService.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchoredDraftHandlers(draftService);

        using var generated = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_generate_bound_draft",
              "method": "GenerateReferenceAnchoredDraft",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprint.BlueprintId}},
                    "beat_ids": []
                  }
                ]
              }
            }
            """));

        Assert.True(generated.RootElement.GetProperty("ok").GetBoolean());
        var result = generated.RootElement.GetProperty("result");
        Assert.Equal(blueprint.BlueprintId, result.GetProperty("blueprint_id").GetInt64());
        var candidate = Assert.Single(result.GetProperty("candidates").EnumerateArray());
        var candidateId = candidate.GetProperty("candidate_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(candidateId));
        Assert.Equal(blueprint.BlueprintId, candidate.GetProperty("blueprint_id").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("beat_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("material_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("text").GetString()));
        Assert.Equal("L0", candidate.GetProperty("rewrite_level").GetString());
        Assert.Equal(JsonValueKind.Array, candidate.GetProperty("changed_slots").ValueKind);
        Assert.Equal(JsonValueKind.Array, candidate.GetProperty("non_slot_edits").ValueKind);
        Assert.Equal("passed", candidate.GetProperty("audit_status").GetString());
        Assert.True(candidate.TryGetProperty("created_at", out _));
        Assert.Equal("passed", result.GetProperty("audit").GetProperty("status").GetString());
        Assert.Equal(blueprint.BlueprintId, result.GetProperty("audit").GetProperty("blueprint_id").GetInt64());

        using var audited = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_audit_bound_draft",
              "method": "AuditReferenceAnchoredDraft",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprint.BlueprintId}},
                    "candidate_ids": [{{JsonSerializer.Serialize(candidateId)}}]
                  }
                ]
              }
            }
            """));

        Assert.True(audited.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("passed", audited.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task DraftAuditFailsWhenCandidateContainsForbiddenFact()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿禁止事实审计测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "forbidden-draft-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸，凶手身份在门后闪了一下。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "禁止事实参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                13,
                "第十三章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: ["凶手身份"]),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
            CancellationToken.None);

        var candidate = Assert.Single(draft.Candidates);
        Assert.Contains("凶手身份", candidate.Text, StringComparison.Ordinal);
        Assert.NotNull(draft.Audit);
        Assert.Equal("failed", draft.Audit?.Status);
        Assert.Contains(draft.Audit?.UnsupportedFactErrors ?? [], item => item.Contains("凶手身份", StringComparison.Ordinal));

        var persistedAudit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);
        Assert.Equal("failed", persistedAudit.Status);
        Assert.Contains(persistedAudit.UnsupportedFactErrors, item => item.Contains("凶手身份", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DraftAuditFailsWhenRequiredProseDutyPhraseIsMissing()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿职责审计测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        var sourcePath = CreateSourceFile(
            "required-duty-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "职责参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var blueprint = await service.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                14,
                "第十四章蓝图",
                "雨声压低了整条街的呼吸",
                [anchor.AnchorId],
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload(
                    "beat:" + blueprint.Beats[0].BeatId + ":source_backed_detail_target",
                    "required phrase: 门口停住")],
                "test",
                "require explicit prose duty phrase"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);
        await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, revised.BlueprintId, 2, SelectTopCandidate: true),
            CancellationToken.None);

        var draft = await service.GenerateDraftFromBlueprintAsync(
            new GenerateReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, BeatIds: []),
            CancellationToken.None);

        var candidate = Assert.Single(draft.Candidates);
        Assert.DoesNotContain("门口停住", candidate.Text, StringComparison.Ordinal);
        Assert.NotNull(draft.Audit);
        Assert.Equal("failed", draft.Audit?.Status);
        Assert.Contains(draft.Audit?.BlueprintErrors ?? [], item => item.Contains("required prose target", StringComparison.OrdinalIgnoreCase));

        var persistedAudit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(novel.Id, revised.BlueprintId, [candidate.CandidateId]),
            CancellationToken.None);
        Assert.Equal("failed", persistedAudit.Status);
        Assert.Contains(persistedAudit.BlueprintErrors, item => item.Contains("required prose target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BridgeReferenceAnchoredDraftHandlersRejectDraftGenerationBeforeApproval()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿门禁桥接测试", "", ""), CancellationToken.None);
        var draftService = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await draftService.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                6,
                "第六章蓝图",
                "先评审再生成正文",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchoredDraftHandlers(draftService);

        using var generated = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_generate_draft_unapproved",
              "method": "GenerateReferenceAnchoredDraft",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprint.BlueprintId}},
                    "beat_ids": []
                  }
                ]
              }
            }
            """));

        Assert.False(generated.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("VALIDATION_ERROR", generated.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("approved", generated.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BridgeReferenceAnchoredDraftHandlersRejectDraftGenerationWithoutMaterialLinks()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("草稿材料桥接门禁测试", "", ""), CancellationToken.None);
        var draftService = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var blueprint = await draftService.GenerateChapterBlueprintAsync(
            new GenerateReferenceChapterBlueprintPayload(
                novel.Id,
                9,
                "第九章蓝图",
                "审批后仍需绑定材料",
                AnchorIds: [],
                KnownFacts: ["主角已经到场"],
                ForbiddenFacts: []),
            CancellationToken.None);
        var review = await draftService.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId),
            CancellationToken.None);
        await draftService.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, blueprint.BlueprintId, review.ReviewId),
            CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchoredDraftHandlers(draftService);

        using var generated = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_generate_draft_without_links",
              "method": "GenerateReferenceAnchoredDraft",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "blueprint_id": {{blueprint.BlueprintId}},
                    "beat_ids": []
                  }
                ]
              }
            }
            """));

        Assert.False(generated.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("VALIDATION_ERROR", generated.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("material", generated.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BridgeReferenceAnchoredDraftHandlersReturnStableValidationErrorForInvalidPayload()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var draftService = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchoredDraftHandlers(draftService);

        using var invalid = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_reference_draft_args",
              "method": "GetReferenceChapterBlueprints",
              "payload": { "args": ["not-a-novel-id", null] }
            }
            """));

        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());
        var error = invalid.RootElement.GetProperty("error");
        Assert.Equal(BridgeErrorCodes.ValidationError, error.GetProperty("code").GetString());
        Assert.Equal("Value must be an integer.", error.GetProperty("details").GetProperty("novelId").GetString());
    }

    [Fact]
    public async Task BridgeReferenceOrchestrationRunUsesWorkspaceCorpusWhenAnchorIdsAreOmitted()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("桥接共享语料编排目标", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "bridge-workspace-corpus-orchestration.md",
            """
            # 第一章

            雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var workspaceAnchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "桥接工作区共享参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, workspaceAnchor.AnchorId);
        var draftService = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchoredDraftHandlers(draftService);

        using var started = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_start_workspace_corpus_orchestration",
              "method": "StartReferenceOrchestrationRun",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "chapter_number": 17,
                    "chapter_goal": "雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸",
                    "known_facts": ["雨声压低街道", "主角在门口停住", "心里意识到压力仍然压着呼吸"],
                    "forbidden_facts": [],
                    "corpus_search_policy": {
                      "mode": "story_context",
                      "max_results_per_beat": 3,
                      "license_statuses": ["user_provided"],
                      "include_anchor_ids": [],
                      "exclude_anchor_ids": []
                    },
                    "source_confirmed": true
                  }
                ]
              }
            }
            """));

        Assert.True(started.RootElement.GetProperty("ok").GetBoolean());
        var startResult = started.RootElement.GetProperty("result");
        Assert.Equal("waiting_for_user", startResult.GetProperty("status").GetString());
        Assert.Equal("blueprint_approval", startResult.GetProperty("stage").GetString());
        Assert.Equal(0, startResult.GetProperty("anchor_ids").GetArrayLength());
        Assert.Equal("story_context", startResult.GetProperty("corpus_search_policy").GetProperty("mode").GetString());
        var runId = startResult.GetProperty("run_id").GetString();
        var reviewId = startResult.GetProperty("review_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));
        Assert.False(string.IsNullOrWhiteSpace(reviewId));

        using var resumed = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_resume_workspace_corpus_orchestration",
              "method": "ResumeReferenceOrchestrationRun",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "run_id": {{JsonSerializer.Serialize(runId)}},
                    "decision_type": "approve_blueprint",
                    "decision_payload": {{JsonSerializer.Serialize(reviewId)}}
                  }
                ]
              }
            }
            """));

        Assert.True(resumed.RootElement.GetProperty("ok").GetBoolean());
        var resumeResult = resumed.RootElement.GetProperty("result");
        Assert.Equal("waiting_for_user", resumeResult.GetProperty("status").GetString());
        Assert.Equal("final_insertion", resumeResult.GetProperty("stage").GetString());
        Assert.Equal("final_insertion_required", resumeResult.GetProperty("last_stop_reason").GetString());
        Assert.NotEmpty(resumeResult.GetProperty("candidate_ids").EnumerateArray());
        Assert.Equal("approve_final_insertion", resumeResult.GetProperty("current_decision").GetProperty("decision_type").GetString());

        var selectedLinks = await ReadSelectedMaterialLinksAsync(options, resumeResult.GetProperty("blueprint_id").GetInt64());
        var selected = Assert.Single(selectedLinks);
        Assert.StartsWith(workspaceAnchor.AnchorId + ":material:", selected.MaterialId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunPersistsResumeAndCancelState()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排状态测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 4,
                ChapterGoal: "雨夜门口确认事实边界",
                KnownFacts: ["林岚在门口", "雨声压低街道"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 4,
                    LicenseStatuses: ["user_provided", "unknown"],
                    IncludeAnchorIds: [7],
                    ExcludeAnchorIds: [9]),
                SourceConfirmed: false),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, started.Status);
        Assert.Equal(ReferenceOrchestrationStages.SourceConfirmation, started.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.SourceConfirmationRequired, started.LastStopReason);
        Assert.NotNull(started.CurrentDecision);
        Assert.Contains("confirm_source", started.CurrentDecision.RequiredActions);
        Assert.Contains("confirm_license_status", started.CurrentDecision.RequiredActions);
        Assert.Empty(started.AnchorIds);
        Assert.Equal(4, started.CorpusSearchPolicy.MaxResultsPerBeat);

        var reloadedService = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));
        var loaded = await reloadedService.GetOrchestrationRunAsync(novel.Id, started.RunId, CancellationToken.None);
        var list = await reloadedService.GetOrchestrationRunsAsync(novel.Id, 4, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(started.RunId, loaded.RunId);
        Assert.Single(list);
        Assert.Equal(started.RunId, list[0].RunId);
        var initialEvents = await reloadedService.GetOrchestrationRunEventsAsync(novel.Id, started.RunId, CancellationToken.None);
        Assert.Contains(initialEvents, item =>
            string.Equals(item.EventType, "run_started", StringComparison.Ordinal) &&
            string.Equals(item.DecisionType, ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts, StringComparison.Ordinal));
        Assert.Contains(initialEvents, item =>
            string.Equals(item.EventType, "required_decision", StringComparison.Ordinal) &&
            string.Equals(item.StopReason, ReferenceOrchestrationStopReasons.SourceConfirmationRequired, StringComparison.Ordinal));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await reloadedService.ResumeOrchestrationRunAsync(
                new ResumeReferenceOrchestrationRunPayload(
                    novel.Id,
                    started.RunId,
                    ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                    "wrong decision"),
                CancellationToken.None));

        var resumed = await reloadedService.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts,
                "confirmed"),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, resumed.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, resumed.Stage);
        Assert.True(resumed.BlueprintId > 0);
        Assert.StartsWith("review-", resumed.ReviewId, StringComparison.Ordinal);
        Assert.Equal(ReferenceOrchestrationStopReasons.BlueprintApprovalRequired, resumed.LastStopReason);
        Assert.NotNull(resumed.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, resumed.CurrentDecision.DecisionType);
        Assert.Equal(ReferenceOrchestrationStopReasons.BlueprintApprovalRequired, resumed.CurrentDecision.StopReason);
        Assert.Contains("approve_blueprint", resumed.CurrentDecision.RequiredActions);
        Assert.Equal("雨夜门口确认事实边界", resumed.CurrentDecision.ApprovalSummary.ChapterFunction);
        Assert.Contains("known: 林岚在门口", resumed.CurrentDecision.ApprovalSummary.FactBoundaryChanges);
        Assert.Contains("forbidden: 凶手身份", resumed.CurrentDecision.ApprovalSummary.FactBoundaryChanges);

        var blueprint = await reloadedService.GetChapterBlueprintAsync(novel.Id, resumed.BlueprintId, CancellationToken.None);
        Assert.NotNull(blueprint);
        Assert.Equal(ReferenceBlueprintStates.ReviewPassed, blueprint.Status);
        Assert.NotNull(blueprint.LatestReview);
        Assert.Equal(resumed.ReviewId, blueprint.LatestReview.ReviewId);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, blueprint.LatestReview.Status);
        var resumedEvents = await reloadedService.GetOrchestrationRunEventsAsync(novel.Id, started.RunId, CancellationToken.None);
        Assert.Contains(resumedEvents, item =>
            string.Equals(item.EventType, "decision_resumed", StringComparison.Ordinal) &&
            string.Equals(item.DecisionType, ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts, StringComparison.Ordinal) &&
            item.Summary.Contains("confirmed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resumedEvents, item =>
            string.Equals(item.EventType, "required_decision", StringComparison.Ordinal) &&
            string.Equals(item.DecisionType, ReferenceOrchestrationDecisionTypes.ApproveBlueprint, StringComparison.Ordinal));

        var cancelled = await reloadedService.CancelOrchestrationRunAsync(
            new CancelReferenceOrchestrationRunPayload(novel.Id, started.RunId, "user stopped run"),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.Cancelled, cancelled.Status);
        Assert.Equal(ReferenceOrchestrationStopReasons.Cancelled, cancelled.LastStopReason);
        Assert.Equal("user stopped run", cancelled.ErrorMessage);
        Assert.Null(cancelled.CurrentDecision);
        var cancelledEvents = await reloadedService.GetOrchestrationRunEventsAsync(novel.Id, started.RunId, CancellationToken.None);
        Assert.Contains(cancelledEvents, item =>
            string.Equals(item.EventType, "run_cancelled", StringComparison.Ordinal) &&
            item.Summary.Contains("user stopped run", StringComparison.Ordinal));
        Assert.True(cancelledEvents.Select(item => item.EventId).SequenceEqual(cancelledEvents.Select(item => item.EventId).Order()));
    }

    [Fact]
    public async Task ReferenceOrchestrationRunWithConfirmedSourceAutoReviewsBlueprintAndStopsForApproval()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("自动编排测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角在雨夜门口确认线索，决定去找证人。"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);

        var run = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 6,
                ChapterGoal: "让主角从犹豫走向行动",
                KnownFacts: ["证人存在", "雨夜门口出现线索"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 5,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, run.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, run.Stage);
        Assert.True(run.BlueprintId > 0);
        Assert.StartsWith("review-", run.ReviewId, StringComparison.Ordinal);
        Assert.Equal(ReferenceOrchestrationStopReasons.BlueprintApprovalRequired, run.LastStopReason);
        Assert.NotNull(run.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, run.CurrentDecision.DecisionType);
        Assert.Equal("让主角从犹豫走向行动", run.CurrentDecision.ApprovalSummary.ChapterFunction);
        Assert.Contains("known: 证人存在", run.CurrentDecision.ApprovalSummary.FactBoundaryChanges);
        Assert.Contains("forbidden: 凶手身份", run.CurrentDecision.ApprovalSummary.FactBoundaryChanges);

        var blueprint = await service.GetChapterBlueprintAsync(novel.Id, run.BlueprintId, CancellationToken.None);
        Assert.NotNull(blueprint);
        Assert.Equal(ReferenceBlueprintStates.ReviewPassed, blueprint.Status);
        Assert.NotNull(blueprint.LatestReview);
        Assert.Equal(run.ReviewId, blueprint.LatestReview.ReviewId);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, blueprint.LatestReview.Status);

        var reloadedService = new SqliteReferenceAnchoredDraftService(options, novels, planning);
        var loaded = await reloadedService.GetOrchestrationRunAsync(novel.Id, run.RunId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(run.BlueprintId, loaded.BlueprintId);
        Assert.Equal(run.ReviewId, loaded.ReviewId);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, loaded.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, loaded.CurrentDecision?.DecisionType);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunAppliesStylePolicyToGeneratedBlueprintAndApprovalSummary()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排风格策略测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角在雨夜门口确认线索，决定去找证人。"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);
        var stylePolicy = new ReferenceOrchestrationStylePolicyPayload(
            StyleProfileIds: [301],
            StyleDimensions: ["dialogue_ratio", "sensory_ratio"],
            ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
            MinStyleFit: 0.8,
            AllowedCloseness: "moderate",
            RequiredEvidenceTypes: ["dialogue_exchange"],
            ForbiddenStyleRisks: ["source_leak", "style_distance"]);

        var run = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 6,
                ChapterGoal: "让主角从犹豫走向行动",
                KnownFacts: ["证人存在", "雨夜门口出现线索"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 5,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true,
                StylePolicy: stylePolicy),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, run.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, run.Stage);
        Assert.NotNull(run.StylePolicy);
        Assert.Equal([301], run.StylePolicy.StyleProfileIds);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, run.CurrentDecision?.DecisionType);
        Assert.Contains("style contracts:", run.CurrentDecision!.ApprovalSummary.MaterialUsePlan, StringComparison.Ordinal);
        Assert.Contains("profiles=301", run.CurrentDecision.ApprovalSummary.MaterialUsePlan, StringComparison.Ordinal);
        Assert.Contains("intensity=strong", run.CurrentDecision.ApprovalSummary.MaterialUsePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("source_text", run.CurrentDecision.ApprovalSummary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", run.CurrentDecision.ApprovalSummary.MaterialUsePlan, StringComparison.OrdinalIgnoreCase);

        var blueprint = await service.GetChapterBlueprintAsync(novel.Id, run.BlueprintId, CancellationToken.None);
        Assert.NotNull(blueprint);
        var beat = Assert.Single(blueprint.Beats);
        Assert.NotNull(beat.StyleContract);
        Assert.Equal([301], beat.StyleContract.StyleProfileIds);
        Assert.Equal(["dialogue_ratio", "sensory_ratio"], beat.StyleContract.StyleDimensions);
        Assert.Equal(ReferenceStyleImitationIntensities.Strong, beat.StyleContract.ImitationIntensity);
        Assert.Equal(0.8, beat.StyleContract.MinStyleFit);
        Assert.Equal("moderate", beat.StyleContract.AllowedCloseness);
        Assert.Equal(["dialogue_exchange"], beat.StyleContract.RequiredEvidenceTypes);
        Assert.Equal(["source_leak", "style_distance"], beat.StyleContract.ForbiddenStyleRisks);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, blueprint.LatestReview?.Status);

        var reloadedService = new SqliteReferenceAnchoredDraftService(options, novels, planning);
        var loaded = await reloadedService.GetOrchestrationRunAsync(novel.Id, run.RunId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded.StylePolicy);
        Assert.Equal([301], loaded.StylePolicy.StyleProfileIds);
        Assert.Equal(ReferenceStyleImitationIntensities.Strong, loaded.StylePolicy.ImitationIntensity);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunStopsForHighRiskDecisionWhenBlueprintBecomesStaleBeforeApproval()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排蓝图失效测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角在雨夜门口确认线索，决定去找证人。"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 7,
                ChapterGoal: "让主角从犹豫走向行动",
                KnownFacts: ["证人存在", "雨夜门口出现线索"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 5,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, started.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, started.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, started.CurrentDecision?.DecisionType);

        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角改为直接进入屋内，不再等待证人。"),
            CancellationToken.None);

        var stopped = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, stopped.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, stopped.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.HighRiskGateBlocked, stopped.LastStopReason);
        Assert.NotNull(stopped.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, stopped.CurrentDecision.DecisionType);
        Assert.Equal(ReferenceOrchestrationStopReasons.HighRiskGateBlocked, stopped.CurrentDecision.StopReason);
        Assert.Contains("inspect_stale_blueprint", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("regenerate_blueprint", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("stale", stopped.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            stopped.CurrentDecision.ApprovalSummary.HighRiskFindings,
            finding => finding.Contains("stale_blueprint", StringComparison.Ordinal));

        var staleBlueprint = await service.GetChapterBlueprintAsync(novel.Id, started.BlueprintId, CancellationToken.None);
        Assert.NotNull(staleBlueprint);
        Assert.Equal(ReferenceBlueprintStates.Stale, staleBlueprint.Status);

        var resolved = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop,
                "acknowledged"),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.Failed, resolved.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, resolved.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.HighRiskGateBlocked, resolved.LastStopReason);
        Assert.Null(resolved.CurrentDecision);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunStopsForBlueprintRevisionWhenAutoReviewFails()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排失败评审测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));

        var run = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 8,
                ChapterGoal: "测试禁用事实门禁",
                KnownFacts: ["门"],
                ForbiddenFacts: ["final hook"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, run.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintReview, run.Stage);
        Assert.True(run.BlueprintId > 0);
        Assert.StartsWith("review-", run.ReviewId, StringComparison.Ordinal);
        Assert.Equal(ReferenceOrchestrationStopReasons.BlueprintRevisionApprovalRequired, run.LastStopReason);
        Assert.NotNull(run.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, run.CurrentDecision.DecisionType);
        Assert.Equal(ReferenceOrchestrationStopReasons.BlueprintRevisionApprovalRequired, run.CurrentDecision.StopReason);
        Assert.Contains("revise_blueprint", run.CurrentDecision.RequiredActions);
        Assert.NotEmpty(run.CurrentDecision.ApprovalSummary.HighRiskFindings);

        var blueprint = await service.GetChapterBlueprintAsync(novel.Id, run.BlueprintId, CancellationToken.None);
        Assert.NotNull(blueprint);
        Assert.Equal(ReferenceBlueprintStates.ReviewFailed, blueprint.Status);
        Assert.NotNull(blueprint.LatestReview);
        Assert.Equal(run.ReviewId, blueprint.LatestReview.ReviewId);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, blueprint.LatestReview.Status);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunAppliesProposedBlueprintRevisionThenContinuesAfterApproval()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排修订续跑测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "orchestration-revision-continuation.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "修订续跑参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var run = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 12,
                ChapterGoal: "雨声压低了整条街的呼吸",
                KnownFacts: ["雨声压低了整条街的呼吸", "主角在门口"],
                ForbiddenFacts: ["final hook"],
                AnchorIds: [anchor.AnchorId],
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [anchor.AnchorId],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, run.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintReview, run.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, run.CurrentDecision?.DecisionType);
        Assert.NotNull(run.CurrentDecision?.ProposedBlueprintRevision);
        Assert.Equal(run.BlueprintId, run.CurrentDecision.ProposedBlueprintRevision.BlueprintId);
        Assert.Equal(run.ReviewId, run.CurrentDecision.ProposedBlueprintRevision.ReviewId);
        Assert.NotEmpty(run.CurrentDecision.ProposedBlueprintRevision.Changes);

        var afterRevision = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                run.RunId,
                ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision,
                string.Empty),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, afterRevision.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, afterRevision.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.BlueprintApprovalRequired, afterRevision.LastStopReason);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, afterRevision.CurrentDecision?.DecisionType);
        Assert.NotEqual(run.ReviewId, afterRevision.ReviewId);

        var revisedBlueprint = await service.GetChapterBlueprintAsync(novel.Id, afterRevision.BlueprintId, CancellationToken.None);
        Assert.NotNull(revisedBlueprint);
        Assert.Equal(ReferenceBlueprintStates.ReviewPassed, revisedBlueprint.Status);
        Assert.Equal(afterRevision.ReviewId, revisedBlueprint.LatestReview?.ReviewId);
        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, revisedBlueprint.LatestReview?.Status);
        Assert.DoesNotContain("final hook", revisedBlueprint.FinalHook, StringComparison.OrdinalIgnoreCase);
        var revisionFieldPaths = await ReadRevisionFieldPathsAsync(options, run.BlueprintId);
        Assert.Contains("final_hook", revisionFieldPaths);

        var completedSafeStages = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                afterRevision.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                afterRevision.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, completedSafeStages.Status);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, completedSafeStages.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion, completedSafeStages.CurrentDecision?.DecisionType);
        Assert.NotEmpty(completedSafeStages.CandidateIds);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunRejectsMismatchedBlueprintRevisionProposal()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排修订校验测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));

        var run = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 13,
                ChapterGoal: "测试禁用事实门禁",
                KnownFacts: ["门"],
                ForbiddenFacts: ["final hook"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, run.CurrentDecision?.DecisionType);
        Assert.NotNull(run.CurrentDecision?.ProposedBlueprintRevision);
        var mismatchedProposal = run.CurrentDecision.ProposedBlueprintRevision with { ReviewId = "review-stale" };
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ResumeOrchestrationRunAsync(
                new ResumeReferenceOrchestrationRunPayload(
                    novel.Id,
                    run.RunId,
                    ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision,
                    JsonSerializer.Serialize(mismatchedProposal, BridgeJson.SerializerOptions)),
                CancellationToken.None));

        Assert.Contains("current orchestration review", exception.Message, StringComparison.OrdinalIgnoreCase);

        var loaded = await service.GetOrchestrationRunAsync(novel.Id, run.RunId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, loaded.Status);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, loaded.CurrentDecision?.DecisionType);
        Assert.Equal(run.ReviewId, loaded.ReviewId);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunRejectsClientModifiedBlueprintRevisionProposal()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排修订防替换测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));

        var run = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 17,
                ChapterGoal: "测试禁用事实门禁",
                KnownFacts: ["门"],
                ForbiddenFacts: ["final hook"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, run.CurrentDecision?.DecisionType);
        Assert.NotNull(run.CurrentDecision?.ProposedBlueprintRevision);
        var modifiedProposal = run.CurrentDecision.ProposedBlueprintRevision with
        {
            Changes =
            [
                new ReferenceBlueprintRevisionChangePayload(
                    "final_hook",
                    "client supplied replacement that was never proposed")
            ]
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ResumeOrchestrationRunAsync(
                new ResumeReferenceOrchestrationRunPayload(
                    novel.Id,
                    run.RunId,
                    ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision,
                    JsonSerializer.Serialize(modifiedProposal, BridgeJson.SerializerOptions)),
                CancellationToken.None));

        Assert.Contains("pending orchestration proposal", exception.Message, StringComparison.OrdinalIgnoreCase);

        var loaded = await service.GetOrchestrationRunAsync(novel.Id, run.RunId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, loaded.Status);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, loaded.CurrentDecision?.DecisionType);
        Assert.Equal(run.CurrentDecision.ProposedBlueprintRevision.Changes, loaded.CurrentDecision?.ProposedBlueprintRevision?.Changes);

        var blueprint = await service.GetChapterBlueprintAsync(novel.Id, run.BlueprintId, CancellationToken.None);
        Assert.NotNull(blueprint);
        Assert.Equal(ReferenceBlueprintStates.ReviewFailed, blueprint.Status);
        Assert.Contains("final hook", blueprint.FinalHook, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadRevisionFieldPathsAsync(options, run.BlueprintId));
    }

    [Fact]
    public async Task ReferenceOrchestrationRunPersistsInjectedBlueprintRevisionProposalUntilUserApprovesIt()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排注入修订建议测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "orchestration-injected-revision.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "注入修订参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var proposalProvider = new FixedBlueprintRevisionProposalProvider(
            Origin: "ai_assistant",
            RevisionReason: "AI suggested field-level fix proposal",
            Changes:
            [
                new ReferenceBlueprintRevisionChangePayload(
                    "final_hook",
                    "AI suggested hook stays inside approved known facts")
            ]);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            planning,
            referenceAnchors,
            proposalProvider);

        var run = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 16,
                ChapterGoal: "雨声压低了整条街的呼吸",
                KnownFacts: ["雨声压低了整条街的呼吸", "主角在门口"],
                ForbiddenFacts: ["final hook"],
                AnchorIds: [anchor.AnchorId],
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [anchor.AnchorId],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, run.Status);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, run.CurrentDecision?.DecisionType);
        Assert.NotNull(run.CurrentDecision?.ProposedBlueprintRevision);
        Assert.Equal(run.BlueprintId, proposalProvider.LastBlueprintId);
        Assert.Equal(run.ReviewId, proposalProvider.LastReviewId);
        Assert.Equal(run.BlueprintId, run.CurrentDecision.ProposedBlueprintRevision.BlueprintId);
        Assert.Equal(run.ReviewId, run.CurrentDecision.ProposedBlueprintRevision.ReviewId);
        Assert.Equal("ai_assistant", run.CurrentDecision.ProposedBlueprintRevision.Origin);
        Assert.Equal("AI suggested field-level fix proposal", run.CurrentDecision.ProposedBlueprintRevision.RevisionReason);
        var proposedChange = Assert.Single(run.CurrentDecision.ProposedBlueprintRevision.Changes);
        Assert.Equal("final_hook", proposedChange.FieldPath);
        Assert.Contains("AI suggested hook", proposedChange.NewValue, StringComparison.Ordinal);

        var beforeApproval = await service.GetChapterBlueprintAsync(novel.Id, run.BlueprintId, CancellationToken.None);
        Assert.NotNull(beforeApproval);
        Assert.Equal(ReferenceBlueprintStates.ReviewFailed, beforeApproval.Status);
        Assert.Contains("final hook", beforeApproval.FinalHook, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadRevisionFieldPathsAsync(options, run.BlueprintId));

        var afterApproval = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                run.RunId,
                ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision,
                string.Empty),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, afterApproval.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, afterApproval.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, afterApproval.CurrentDecision?.DecisionType);
        var revised = await service.GetChapterBlueprintAsync(novel.Id, afterApproval.BlueprintId, CancellationToken.None);
        Assert.NotNull(revised);
        Assert.Equal(ReferenceBlueprintStates.ReviewPassed, revised.Status);
        Assert.Equal("AI suggested hook stays inside approved known facts", revised.FinalHook);
        var revisionFieldPaths = await ReadRevisionFieldPathsAsync(options, run.BlueprintId);
        Assert.Contains("final_hook", revisionFieldPaths);

        var completedSafeStages = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                afterApproval.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                afterApproval.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, completedSafeStages.Status);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, completedSafeStages.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.FinalInsertionRequired, completedSafeStages.LastStopReason);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion, completedSafeStages.CurrentDecision?.DecisionType);
        Assert.NotEmpty(completedSafeStages.CandidateIds);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunPersistsAiBlueprintRevisionProposalUntilUserApprovesIt()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排 AI 修订建议测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "orchestration-ai-revision.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "AI 修订参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var chat = new RecordingChatCompletionClient(
            """
            {
              "origin": "ignored",
              "revision_reason": "AI suggested field-level fix proposal",
              "changes": [
                { "field_path": "final_hook", "new_value": "AI suggested hook stays inside approved known facts" },
                { "field_path": "known_facts", "new_value": "[\"client should not get this\"]" }
              ]
            }
            """);
        var proposalProvider = new AiReferenceBlueprintRevisionProposalProvider(
            new FixedAppSettingsService("deepseek/deepseek-chat", "high"),
            chat);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            planning,
            referenceAnchors,
            proposalProvider);

        var run = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 18,
                ChapterGoal: "雨声压低了整条街的呼吸",
                KnownFacts: ["雨声压低了整条街的呼吸", "主角在门口"],
                ForbiddenFacts: ["final hook"],
                AnchorIds: [anchor.AnchorId],
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [anchor.AnchorId],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.NotNull(chat.LastRequest);
        Assert.Equal("deepseek", chat.LastRequest.ProviderName);
        Assert.Equal("deepseek-chat", chat.LastRequest.ModelId);
        Assert.Equal("high", chat.LastRequest.ReasoningEffort);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, run.CurrentDecision?.DecisionType);
        Assert.NotNull(run.CurrentDecision?.ProposedBlueprintRevision);
        Assert.Equal(run.BlueprintId, run.CurrentDecision.ProposedBlueprintRevision.BlueprintId);
        Assert.Equal(run.ReviewId, run.CurrentDecision.ProposedBlueprintRevision.ReviewId);
        Assert.Equal("ai_assistant", run.CurrentDecision.ProposedBlueprintRevision.Origin);
        Assert.Equal("AI suggested field-level fix proposal", run.CurrentDecision.ProposedBlueprintRevision.RevisionReason);
        var proposedChange = Assert.Single(run.CurrentDecision.ProposedBlueprintRevision.Changes);
        Assert.Equal("final_hook", proposedChange.FieldPath);
        Assert.Equal("AI suggested hook stays inside approved known facts", proposedChange.NewValue);

        var beforeApproval = await service.GetChapterBlueprintAsync(novel.Id, run.BlueprintId, CancellationToken.None);
        Assert.NotNull(beforeApproval);
        Assert.Equal(ReferenceBlueprintStates.ReviewFailed, beforeApproval.Status);
        Assert.Contains("final hook", beforeApproval.FinalHook, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadRevisionFieldPathsAsync(options, run.BlueprintId));

        var afterApproval = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                run.RunId,
                ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision,
                string.Empty),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, afterApproval.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, afterApproval.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, afterApproval.CurrentDecision?.DecisionType);
        var revised = await service.GetChapterBlueprintAsync(novel.Id, afterApproval.BlueprintId, CancellationToken.None);
        Assert.NotNull(revised);
        Assert.Equal(ReferenceBlueprintStates.ReviewPassed, revised.Status);
        Assert.Equal("AI suggested hook stays inside approved known facts", revised.FinalHook);
        Assert.Contains("final_hook", await ReadRevisionFieldPathsAsync(options, run.BlueprintId));
    }

    [Fact]
    public async Task ReferenceOrchestrationRunAfterBlueprintApprovalGeneratesAuditedCandidatesAndStopsForFinalInsertion()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("自动候选编排测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住，确认线索后去找证人。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "orchestration-happy-path.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "自动候选参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 9,
                ChapterGoal: "雨声压低了整条街的呼吸",
                KnownFacts: ["雨声压低了整条街的呼吸", "主角在门口"],
                ForbiddenFacts: [],
                AnchorIds: [anchor.AnchorId],
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [anchor.AnchorId],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, started.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, started.Stage);
        Assert.True(started.BlueprintId > 0);
        Assert.StartsWith("review-", started.ReviewId, StringComparison.Ordinal);

        var completedSafeStages = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, completedSafeStages.Status);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, completedSafeStages.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.FinalInsertionRequired, completedSafeStages.LastStopReason);
        Assert.NotNull(completedSafeStages.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion, completedSafeStages.CurrentDecision.DecisionType);
        Assert.Contains("review_candidates", completedSafeStages.CurrentDecision.RequiredActions);
        Assert.Contains("approve_final_insertion", completedSafeStages.CurrentDecision.RequiredActions);
        Assert.NotEmpty(completedSafeStages.CandidateIds);

        var audit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(
                novel.Id,
                completedSafeStages.BlueprintId,
                completedSafeStages.CandidateIds),
            CancellationToken.None);

        Assert.Equal("passed", audit.Status);

        var afterAutomation = await service.GetChapterBlueprintAsync(novel.Id, completedSafeStages.BlueprintId, CancellationToken.None);
        Assert.NotNull(afterAutomation);
        Assert.Equal(ReferenceBlueprintStates.MaterialBound, afterAutomation.Status);

        var reloadedService = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var loaded = await reloadedService.GetOrchestrationRunAsync(novel.Id, started.RunId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(completedSafeStages.CandidateIds, loaded.CandidateIds);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, loaded.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion, loaded.CurrentDecision?.DecisionType);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunRejectsFinalInsertionResumeAndKeepsManualInsertionBoundary()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("最终插入边界测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住，确认线索后去找证人。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "orchestration-final-insertion-boundary.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "最终插入边界参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 16,
                ChapterGoal: "雨声压低了整条街的呼吸",
                KnownFacts: ["雨声压低了整条街的呼吸", "主角在门口"],
                ForbiddenFacts: [],
                AnchorIds: [anchor.AnchorId],
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [anchor.AnchorId],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);
        var stopped = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, stopped.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion, stopped.CurrentDecision?.DecisionType);
        Assert.NotEmpty(stopped.CandidateIds);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ResumeOrchestrationRunAsync(
                new ResumeReferenceOrchestrationRunPayload(
                    novel.Id,
                    started.RunId,
                    ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion,
                    string.Join('\n', stopped.CandidateIds)),
                CancellationToken.None));

        Assert.Contains("Final insertion", exception.Message, StringComparison.OrdinalIgnoreCase);
        var loaded = await service.GetOrchestrationRunAsync(novel.Id, started.RunId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, loaded.Status);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, loaded.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.FinalInsertionRequired, loaded.LastStopReason);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion, loaded.CurrentDecision?.DecisionType);
        Assert.Equal(stopped.CandidateIds, loaded.CandidateIds);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunUsesWorkspaceCorpusAnchorsWithoutExplicitAnchorIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("共享语料编排目标", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "workspace-corpus-orchestration.md",
            """
            # 第一章

            雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var workspaceAnchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "工作区编排共享参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, workspaceAnchor.AnchorId);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 14,
                ChapterGoal: "雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸",
                KnownFacts: ["雨声压低街道", "主角在门口停住", "心里意识到压力仍然压着呼吸"],
                ForbiddenFacts: [],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, started.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, started.Stage);
        Assert.Empty(started.AnchorIds);

        var completedSafeStages = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, completedSafeStages.Status);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, completedSafeStages.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.FinalInsertionRequired, completedSafeStages.LastStopReason);
        Assert.NotEmpty(completedSafeStages.CandidateIds);

        var audit = await service.AuditDraftAgainstBlueprintAsync(
            new AuditReferenceAnchoredDraftPayload(
                novel.Id,
                completedSafeStages.BlueprintId,
                completedSafeStages.CandidateIds),
            CancellationToken.None);
        Assert.Equal("passed", audit.Status);

        var selectedLinks = await ReadSelectedMaterialLinksAsync(options, completedSafeStages.BlueprintId);
        var selected = Assert.Single(selectedLinks);
        Assert.StartsWith(workspaceAnchor.AnchorId + ":material:", selected.MaterialId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunFiltersWorkspaceCorpusAnchorsByLicensePolicy()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("共享语料授权过滤目标", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸。"),
            CancellationToken.None);
        var allowedSourcePath = CreateSourceFile(
            "workspace-license-allowed.md",
            """
            # 第一章

            因为雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸，指尖发紧。只是那阵沉默没有散开，他没有立刻回答，把那口气慢慢咽回去。
            """);
        var unknownSourcePath = CreateSourceFile(
            "workspace-license-unknown.md",
            """
            # 第一章

            雨声压低了街道，他在门口停住，心里意识到另一个未经授权的秘密正在逼近。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var allowedAnchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "授权共享参考", null, allowedSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var unknownAnchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "未知授权共享参考", null, unknownSourcePath, "markdown", "unknown"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, allowedAnchor.AnchorId);
        await MarkAnchorAsWorkspaceCorpusAsync(options, unknownAnchor.AnchorId);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 15,
                ChapterGoal: "雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸",
                KnownFacts: ["雨声压低街道", "主角在门口停住", "心里意识到压力仍然压着呼吸"],
                ForbiddenFacts: ["未经授权的秘密"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);
        var completedSafeStages = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, completedSafeStages.Stage);
        var selectedLinks = await ReadSelectedMaterialLinksAsync(options, completedSafeStages.BlueprintId);
        var selected = Assert.Single(selectedLinks);
        Assert.StartsWith(allowedAnchor.AnchorId + ":material:", selected.MaterialId, StringComparison.Ordinal);
        Assert.DoesNotContain(unknownAnchor.AnchorId + ":material:", selected.MaterialId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunUsesCorpusSearchPolicyWhenBindingMaterials()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排语料策略测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var includeAnchorId = 707L;
        var unknownLicenseAnchorId = 808L;
        var excludedAnchorId = 909L;
        var referenceAnchors = new FixedReferenceAnchorService(
            [
                new ReferenceMaterialPayload(
                    MaterialId: "included-corpus-material",
                    AnchorId: includeAnchorId,
                    SourceSegmentId: "seg-included",
                    MaterialType: ReferenceMaterialTypes.Sentence,
                    FunctionTag: "interiority",
                    EmotionTag: "pressure",
                    SceneTag: "rain",
                    PovTag: "close",
                    TechniqueTag: "sensory",
                    FunctionConfidence: 1,
                    EmotionConfidence: 1,
                    PovConfidence: 1,
                    Text: "雨声压低街道，主角在门口停住，指尖发紧，心里意识到压力仍然压着呼吸。只是那阵沉默没有散开，他没有立刻回答，把那口气慢慢咽回去。",
                    SourceHash: "included-hash",
                    ExtractorVersion: "test",
                    UserVerified: true,
                    ScoreComponents: null,
                    CreatedAt: DateTimeOffset.UtcNow),
                new ReferenceMaterialPayload(
                    MaterialId: "unknown-license-corpus-material",
                    AnchorId: unknownLicenseAnchorId,
                    SourceSegmentId: "seg-unknown-license",
                    MaterialType: ReferenceMaterialTypes.Sentence,
                    FunctionTag: "interiority",
                    EmotionTag: "pressure",
                    SceneTag: "rain",
                    PovTag: "close",
                    TechniqueTag: "sensory",
                    FunctionConfidence: 1,
                    EmotionConfidence: 1,
                    PovConfidence: 1,
                    Text: "雨声压低了街道，他在门口停住，陌生档案被递到掌心。",
                    SourceHash: "unknown-license-hash",
                    ExtractorVersion: "test",
                    UserVerified: true,
                    ScoreComponents: null,
                    CreatedAt: DateTimeOffset.UtcNow),
                new ReferenceMaterialPayload(
                    MaterialId: "excluded-corpus-material",
                    AnchorId: excludedAnchorId,
                    SourceSegmentId: "seg-excluded",
                    MaterialType: ReferenceMaterialTypes.Sentence,
                    FunctionTag: "interiority",
                    EmotionTag: "pressure",
                    SceneTag: "rain",
                    PovTag: "close",
                    TechniqueTag: "sensory",
                    FunctionConfidence: 1,
                    EmotionConfidence: 1,
                    PovConfidence: 1,
                    Text: "雨声压低了街道，他在门口停住，凶手身份在门后亮出来。",
                    SourceHash: "excluded-hash",
                    ExtractorVersion: "test",
                    UserVerified: true,
                    ScoreComponents: null,
                    CreatedAt: DateTimeOffset.UtcNow)
            ],
            applySearchFilters: true,
            licenseStatuses: new Dictionary<long, string>
            {
                [unknownLicenseAnchorId] = "unknown"
            },
            adaptedTextByMaterialId: new Dictionary<string, string>
            {
                ["included-corpus-material"] = "因为雨声压低街道，主角在门口停住，指尖发紧，心里意识到压力仍然压着呼吸。只是那阵沉默没有散开，他没有立刻回答，把那口气慢慢咽回去。"
            });
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 12,
                ChapterGoal: "雨声压低街道，主角在门口停住",
                KnownFacts: ["雨声压低街道", "主角在门口停住"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: [excludedAnchorId]),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, started.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, started.Stage);

        var completedSafeStages = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, completedSafeStages.Status);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, completedSafeStages.Stage);
        Assert.NotEmpty(referenceAnchors.SearchInputs);
        Assert.All(referenceAnchors.SearchInputs, input =>
        {
            Assert.Equal([includeAnchorId], input.AnchorIds);
            Assert.DoesNotContain(excludedAnchorId, input.AnchorIds);
        });
        var adapted = Assert.Single(referenceAnchors.AdaptInputs);
        Assert.Equal("included-corpus-material", adapted.MaterialId);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunMarksFailureWhenReferenceMaterialServiceIsMissing()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排绑定失败测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(
            options,
            novels,
            new FileSystemPlanningService(options, novels));

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 10,
                ChapterGoal: "无材料服务时不能继续",
                KnownFacts: ["门"],
                ForbiddenFacts: [],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, started.Stage);

        var failed = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.Failed, failed.Status);
        Assert.Equal(ReferenceOrchestrationStages.MaterialBinding, failed.Stage);
        Assert.Null(failed.CurrentDecision);
        Assert.Empty(failed.CandidateIds);
        Assert.Contains("Reference material binding requires a configured reference anchor service", failed.ErrorMessage);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunStopsForHighRiskDecisionWhenMaterialBindingHasMissingLinks()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排材料缺口测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角在门口停住，必须用来源材料支撑雨夜压力。"),
            CancellationToken.None);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 11,
                ChapterGoal: "雨夜门口的压力必须有来源材料支撑",
                KnownFacts: ["主角在门口"],
                ForbiddenFacts: [],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, started.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, started.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, started.CurrentDecision?.DecisionType);

        var stopped = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, stopped.Status);
        Assert.Equal(ReferenceOrchestrationStages.MaterialBinding, stopped.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.HighRiskGateBlocked, stopped.LastStopReason);
        Assert.NotNull(stopped.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, stopped.CurrentDecision.DecisionType);
        Assert.Equal(ReferenceOrchestrationStopReasons.HighRiskGateBlocked, stopped.CurrentDecision.StopReason);
        Assert.Contains("inspect_material_binding_gap", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("import_or_restore_reference_material", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("relax_license_or_anchor_policy", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("revise_blueprint_reference_query", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("restart_or_cancel_run", stopped.CurrentDecision.RequiredActions);
        Assert.Empty(stopped.CandidateIds);
        Assert.Contains("selected reference material links", stopped.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            stopped.CurrentDecision.ApprovalSummary.HighRiskFindings,
            finding => finding.Contains("missing_material_link", StringComparison.Ordinal));

        var resolved = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop,
                "acknowledged"),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.Failed, resolved.Status);
        Assert.Equal(ReferenceOrchestrationStages.MaterialBinding, resolved.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.HighRiskGateBlocked, resolved.LastStopReason);
        Assert.Null(resolved.CurrentDecision);
    }

    [Fact]
    public async Task SourceBackedNoReuseBeatStillRequiresSelectedMaterialBeforeDraftGeneration()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排来源必需缺口测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角在门口停住，雨夜压力必须有来源材料支撑。"),
            CancellationToken.None);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);
        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 31,
                ChapterGoal: "雨夜压力必须有来源材料支撑",
                KnownFacts: ["主角在门口"],
                ForbiddenFacts: [],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);
        var blueprint = await service.GetChapterBlueprintAsync(novel.Id, started.BlueprintId, CancellationToken.None);
        Assert.NotNull(blueprint);
        var beat = Assert.Single(blueprint.Beats);
        Assert.False(string.IsNullOrWhiteSpace(beat.SourceBackedDetailTarget));
        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [new ReferenceBlueprintRevisionChangePayload(
                    "beat:" + beat.BeatId + ":no_reuse_reason",
                    "attempted skip even though source-backed detail is still required")],
                "test",
                "force source-required no-reuse gap"),
            CancellationToken.None);
        var review = await service.ReviewChapterBlueprintAsync(
            new ReviewReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId),
            CancellationToken.None);
        var approved = await service.ApproveChapterBlueprintAsync(
            new ApproveReferenceChapterBlueprintPayload(novel.Id, revised.BlueprintId, review.ReviewId),
            CancellationToken.None);

        var binding = await service.BindBlueprintMaterialsAsync(
            new BindReferenceBlueprintMaterialsPayload(novel.Id, approved.BlueprintId, MaxResultsPerBeat: 3, SelectTopCandidate: true),
            CancellationToken.None);
        var missing = ReferenceAnchoredDraftPreflight.RequiredMaterialBeatIds(approved.Beats)
            .Except(binding.Links.Where(link => link.Selected).Select(link => link.BeatId), StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([beat.BeatId], missing);
        Assert.DoesNotContain(binding.Links, link => link.Selected);
        var draftException = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, approved.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("selected reference material links", draftException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunStopsForHighRiskDecisionWhenDraftAuditFailsAfterBlueprintApproval()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排草稿审计失败测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "orchestration-draft-audit-failure.md",
            """
            # 第一章

            雨声压低了整条街的呼吸，凶手身份在门后闪了一下。
            """);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "草稿失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 11,
                ChapterGoal: "雨声压低了整条街的呼吸",
                KnownFacts: ["雨声压低了整条街的呼吸"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: [anchor.AnchorId],
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [anchor.AnchorId],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, started.Stage);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, started.CurrentDecision?.DecisionType);

        var stopped = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, stopped.Status);
        Assert.Equal(ReferenceOrchestrationStages.DraftAudit, stopped.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.DraftAuditFailed, stopped.LastStopReason);
        Assert.NotNull(stopped.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, stopped.CurrentDecision.DecisionType);
        Assert.Equal(ReferenceOrchestrationStopReasons.DraftAuditFailed, stopped.CurrentDecision.StopReason);
        Assert.Contains("inspect_draft_audit", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("inspect_fact_boundary", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("revise_blueprint_or_candidates", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("regenerate_candidates", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("restart_or_cancel_run", stopped.CurrentDecision.RequiredActions);
        Assert.NotEmpty(stopped.CandidateIds);
        Assert.Contains("凶手身份", stopped.ErrorMessage);
        Assert.Contains(
            stopped.CurrentDecision.ApprovalSummary.HighRiskFindings,
            finding => finding.Contains("凶手身份", StringComparison.Ordinal));

        var loaded = await service.GetOrchestrationRunAsync(novel.Id, started.RunId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, loaded.Status);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, loaded.CurrentDecision?.DecisionType);

        var resume = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ResumeOrchestrationRunAsync(
                new ResumeReferenceOrchestrationRunPayload(
                    novel.Id,
                    started.RunId,
                    ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion,
                    string.Empty),
                CancellationToken.None));
        Assert.Contains("Decision type does not match", resume.Message, StringComparison.OrdinalIgnoreCase);

        var resolved = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop,
                "acknowledged"),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.Failed, resolved.Status);
        Assert.Equal(ReferenceOrchestrationStages.DraftAudit, resolved.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.DraftAuditFailed, resolved.LastStopReason);
        Assert.Null(resolved.CurrentDecision);
        Assert.Contains("凶手身份", resolved.ErrorMessage);

        var finalInsertionAfterResolve = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ResumeOrchestrationRunAsync(
                new ResumeReferenceOrchestrationRunPayload(
                    novel.Id,
                    started.RunId,
                    ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion,
                    string.Empty),
                CancellationToken.None));
        Assert.Contains("no pending decision", finalInsertionAfterResolve.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReferenceOrchestrationRunStopsForHighRiskDecisionWhenDraftCandidateCopiesSourceMaterial()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排来源贴近风险测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "orchestration-source-leak.md",
            """
            # 来源贴近风险

            雨声压低了整条街的呼吸，林岚在门口停住，指尖慢慢发紧，心里一紧。
            """);
        var persistedReferenceAnchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await persistedReferenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "来源贴近风险参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await persistedReferenceAnchors.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "雨声压低",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var material = Assert.Single(materials.Items);
        var materialText = material.Text;
        var referenceAnchors = new FixedReferenceAnchorService(
            material,
            applySearchFilters: true,
            adaptedTextByMaterialId: new Dictionary<string, string>
            {
                [material.MaterialId] = materialText
            },
            rewriteLevelByMaterialId: new Dictionary<string, string>
            {
                [material.MaterialId] = ReferenceRewriteLevels.L2
            });
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 12,
                ChapterGoal: "雨声压低街道，主角在门口停住",
                KnownFacts: ["雨声压低街道", "主角在门口停住", "指尖发凉", "呼吸慢下来"],
                ForbiddenFacts: [],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        var stopped = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, stopped.Status);
        Assert.Equal(ReferenceOrchestrationStages.DraftAudit, stopped.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.DraftAuditFailed, stopped.LastStopReason);
        Assert.NotNull(stopped.CurrentDecision);
        Assert.Contains("inspect_source_leak_findings", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("lower_imitation_intensity_or_rebind_material", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("regenerate_candidates", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("restart_or_cancel_run", stopped.CurrentDecision.RequiredActions);
        Assert.Contains(
            stopped.CurrentDecision.ApprovalSummary.HighRiskFindings,
            finding => finding.Contains("source-leak", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            stopped.CurrentDecision.RequiredActions,
            action => action.Contains(materialText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReferenceOrchestrationRunStopsForHighRiskDecisionWhenDraftCandidateLeaksPovKnowledge()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排POV泄漏测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var material = BuildFixedMaterial(
            "pov-leak-material",
            "雨声压低街道，他在门口停住，指尖发凉，心里意识到压力仍然压着呼吸。");
        var referenceAnchors = new FixedReferenceAnchorService(
            material,
            applySearchFilters: true,
            adaptedTextByMaterialId: new Dictionary<string, string>
            {
                [material.MaterialId] = "因为雨声压低街道，主角在门口停住，指尖发凉，心里意识到压力仍然压着呼吸。只是那阵沉默没有散开，凶手身份在门后亮出来。"
            });
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 13,
                ChapterGoal: "雨声压低街道，主角在门口停住",
                KnownFacts: ["雨声压低街道", "主角在门口停住"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        var stopped = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, stopped.Status);
        Assert.Equal(ReferenceOrchestrationStages.DraftAudit, stopped.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.DraftAuditFailed, stopped.LastStopReason);
        Assert.NotNull(stopped.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, stopped.CurrentDecision.DecisionType);
        Assert.Contains("inspect_pov_boundary", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("revise_blueprint_or_candidates", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("regenerate_candidates", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("凶手身份", stopped.ErrorMessage);
        Assert.Contains(
            stopped.CurrentDecision.ApprovalSummary.HighRiskFindings,
            finding => finding.Contains("pov:", StringComparison.Ordinal) &&
                finding.Contains("凶手身份", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReferenceOrchestrationRunStopsForHighRiskDecisionWhenDraftCandidateIntroducesUnsupportedFact()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排未支持事实测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var material = BuildFixedMaterial(
            "unsupported-fact-material",
            "雨声压低街道，他在门口停住，指尖发凉，心里意识到压力仍然压着呼吸。");
        var referenceAnchors = new FixedReferenceAnchorService(
            material,
            applySearchFilters: true,
            adaptedTextByMaterialId: new Dictionary<string, string>
            {
                [material.MaterialId] = "因为雨声压低街道，主角在门口停住，指尖发凉，心里意识到压力仍然压着呼吸。只是那阵沉默没有散开，陌生档案被递到掌心。"
            });
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 14,
                ChapterGoal: "雨声压低街道，主角在门口停住",
                KnownFacts: ["雨声压低街道", "主角在门口停住"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        var stopped = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, stopped.Status);
        Assert.Equal(ReferenceOrchestrationStages.DraftAudit, stopped.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.DraftAuditFailed, stopped.LastStopReason);
        Assert.NotNull(stopped.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, stopped.CurrentDecision.DecisionType);
        Assert.Contains("inspect_fact_boundary", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("revise_blueprint_or_candidates", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("regenerate_candidates", stopped.CurrentDecision.RequiredActions);
        Assert.Contains("陌生档案", stopped.ErrorMessage);
        Assert.Contains(
            stopped.CurrentDecision.ApprovalSummary.HighRiskFindings,
            finding => finding.Contains("unsupported_fact:", StringComparison.Ordinal) &&
                finding.Contains("陌生档案", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ReferenceRewriteLevels.L3)]
    [InlineData(ReferenceRewriteLevels.L4)]
    public async Task ReferenceOrchestrationRunStopsForHighRiskDecisionWhenDraftCandidateUsesHighRewriteLevel(
        string rewriteLevel)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("编排高改写测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var material = BuildFixedMaterial(
            "high-rewrite-material-" + rewriteLevel,
            "雨声压低街道，他在门口停住，指尖发凉，心里意识到压力仍然压着呼吸。");
        var referenceAnchors = new FixedReferenceAnchorService(
            material,
            applySearchFilters: true,
            rewriteLevelByMaterialId: new Dictionary<string, string>
            {
                [material.MaterialId] = rewriteLevel
            });
        var service = new SqliteReferenceAnchoredDraftService(options, novels, planning, referenceAnchors);

        var started = await service.StartOrchestrationRunAsync(
            new StartReferenceOrchestrationRunPayload(
                novel.Id,
                ChapterNumber: 15,
                ChapterGoal: "雨声压低街道，主角在门口停住",
                KnownFacts: ["雨声压低街道", "主角在门口停住"],
                ForbiddenFacts: ["凶手身份"],
                AnchorIds: null,
                CorpusSearchPolicy: new ReferenceCorpusSearchPolicyPayload(
                    "story_context",
                    MaxResultsPerBeat: 3,
                    LicenseStatuses: ["user_provided"],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SourceConfirmed: true),
            CancellationToken.None);

        var stopped = await service.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                started.RunId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                started.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, stopped.Status);
        Assert.Equal(ReferenceOrchestrationStages.DraftAudit, stopped.Stage);
        Assert.Equal(ReferenceOrchestrationStopReasons.DraftAuditFailed, stopped.LastStopReason);
        Assert.NotNull(stopped.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, stopped.CurrentDecision.DecisionType);
        Assert.Equal(rewriteLevel, stopped.CurrentDecision.ApprovalSummary.RewriteBudget);
        Assert.Contains(rewriteLevel, stopped.ErrorMessage);
        Assert.Contains(
            stopped.CurrentDecision.ApprovalSummary.HighRiskFindings,
            finding => finding.Contains(rewriteLevel, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private string CreateSourceFile(string fileName, string content)
    {
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async ValueTask<BlueprintApprovalRow> ReadBlueprintApprovalAsync(
        AppInitializationOptions options,
        long blueprintId,
        string reviewId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT approval_id, blueprint_id, review_id, context_hash, source_plan_hash,
                   analysis_contract_hash, review_version, approver_origin, approved_at
            FROM reference_chapter_blueprint_approvals
            WHERE blueprint_id = $blueprint_id AND review_id = $review_id
            ORDER BY approved_at DESC, approval_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        command.Parameters.AddWithValue("$review_id", reviewId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new BlueprintApprovalRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)));
    }

    private sealed record BlueprintApprovalRow(
        string ApprovalId,
        long BlueprintId,
        string ReviewId,
        string ContextHash,
        string SourcePlanHash,
        string AnalysisContractHash,
        int ReviewVersion,
        string ApproverOrigin,
        DateTimeOffset ApprovedAt);

    private static async ValueTask<BlueprintReproducibilityRow> ReadBlueprintReproducibilityRowAsync(
        AppInitializationOptions options,
        long blueprintId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT build_version, context_hash, source_plan_hash, analysis_contract_hash
            FROM reference_chapter_blueprints
            WHERE blueprint_id = $blueprint_id;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new BlueprintReproducibilityRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private sealed record BlueprintReproducibilityRow(
        string BuildVersion,
        string ContextHash,
        string SourcePlanHash,
        string AnalysisContractHash);

    private sealed record DraftAuditRow(
        string AuditId,
        long BlueprintId,
        IReadOnlyList<string> CandidateIds,
        string Status,
        string RewriteLevel,
        ReferenceDraftAuditReadableReportPayload ReadableReport,
        string ReadableReportJson);

    private static async ValueTask<IReadOnlyList<string>> ReadTableColumnsAsync(
        AppInitializationOptions options,
        string tableName)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table_name) ORDER BY cid ASC;";
        command.Parameters.AddWithValue("$table_name", tableName);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async ValueTask<IReadOnlyList<string>> ReadIndexNamesAsync(
        AppInitializationOptions options,
        string tableName)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_index_list($table_name) ORDER BY name ASC;";
        command.Parameters.AddWithValue("$table_name", tableName);
        var indexes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }

    private static async ValueTask<IReadOnlyList<DraftAuditRow>> ReadDraftAuditRowsAsync(
        AppInitializationOptions options,
        long blueprintId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audit_id, blueprint_id, candidate_ids_json, status, rewrite_level, readable_report_json
            FROM reference_draft_audits
            WHERE blueprint_id = $blueprint_id
            ORDER BY audited_at ASC, audit_id ASC;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        var rows = new List<DraftAuditRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var readableReportJson = reader.GetString(5);
            rows.Add(new DraftAuditRow(
                reader.GetString(0),
                reader.GetInt64(1),
                JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(2), BridgeJson.SerializerOptions) ?? [],
                reader.GetString(3),
                reader.GetString(4),
                JsonSerializer.Deserialize<ReferenceDraftAuditReadableReportPayload>(readableReportJson, BridgeJson.SerializerOptions)
                    ?? throw new InvalidOperationException("Stored draft audit readable report is empty."),
                readableReportJson));
        }

        return rows;
    }

    private static async ValueTask InsertDraftCandidateAsync(
        AppInitializationOptions options,
        ReferenceDraftParagraphCandidatePayload candidate)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO reference_draft_paragraph_candidates
              (candidate_id, blueprint_id, beat_id, material_id, rewrite_level, text,
               changed_slots_json, non_slot_edits_json, audit_status, created_at, style_attempts_json)
            VALUES
              ($candidate_id, $blueprint_id, $beat_id, $material_id, $rewrite_level, $text,
               $changed_slots_json, $non_slot_edits_json, $audit_status, $created_at, $style_attempts_json);
            """;
        command.Parameters.AddWithValue("$candidate_id", candidate.CandidateId);
        command.Parameters.AddWithValue("$blueprint_id", candidate.BlueprintId);
        command.Parameters.AddWithValue("$beat_id", candidate.BeatId);
        command.Parameters.AddWithValue("$material_id", candidate.MaterialId);
        command.Parameters.AddWithValue("$rewrite_level", candidate.RewriteLevel);
        command.Parameters.AddWithValue("$text", candidate.Text);
        command.Parameters.AddWithValue("$changed_slots_json", JsonSerializer.Serialize(candidate.ChangedSlots, BridgeJson.SerializerOptions));
        command.Parameters.AddWithValue("$non_slot_edits_json", JsonSerializer.Serialize(candidate.NonSlotEdits, BridgeJson.SerializerOptions));
        command.Parameters.AddWithValue("$audit_status", candidate.AuditStatus);
        command.Parameters.AddWithValue("$created_at", candidate.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$style_attempts_json", JsonSerializer.Serialize(candidate.StyleAttempts ?? [], BridgeJson.SerializerOptions));
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<IReadOnlyList<ReferenceBlueprintMaterialLinkPayload>> ReadSelectedMaterialLinksAsync(
        AppInitializationOptions options,
        long blueprintId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT link_id, blueprint_id, beat_id, material_id, intended_use, max_rewrite_level,
                   selected, score, score_components_json, fit_explanation, created_at
            FROM reference_blueprint_material_links
            WHERE blueprint_id = $blueprint_id AND selected != 0
            ORDER BY beat_id ASC, material_id ASC;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        var links = new List<ReferenceBlueprintMaterialLinkPayload>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            links.Add(new ReferenceBlueprintMaterialLinkPayload(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6) != 0,
                reader.GetDouble(7),
                JsonSerializer.Deserialize<IReadOnlyDictionary<string, double>>(reader.GetString(8), BridgeJson.SerializerOptions)
                    ?? new Dictionary<string, double>(),
                reader.GetString(9),
                DateTimeOffset.Parse(reader.GetString(10))));
        }

        return links;
    }

    private static async ValueTask<IReadOnlyList<MaterialLinkStateRow>> ReadMaterialLinkStatesAsync(
        AppInitializationOptions options,
        long blueprintId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT link_id, analysis_contract_hash, status
            FROM reference_blueprint_material_links
            WHERE blueprint_id = $blueprint_id
            ORDER BY link_id ASC;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        var rows = new List<MaterialLinkStateRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new MaterialLinkStateRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return rows;
    }

    private sealed record MaterialLinkStateRow(
        string LinkId,
        string AnalysisContractHash,
        string Status);

    private sealed class FixedBlueprintRevisionProposalProvider(
        string Origin,
        string RevisionReason,
        IReadOnlyList<ReferenceBlueprintRevisionChangePayload> Changes) : IReferenceBlueprintRevisionProposalProvider
    {
        public long LastBlueprintId { get; private set; }

        public string LastReviewId { get; private set; } = string.Empty;

        public ValueTask<ReferenceOrchestrationBlueprintRevisionProposalPayload> ProposeRevisionAsync(
            ReferenceChapterBlueprintPayload blueprint,
            ReferenceChapterBlueprintReviewPayload review,
            CancellationToken cancellationToken)
        {
            LastBlueprintId = blueprint.BlueprintId;
            LastReviewId = review.ReviewId;
            return ValueTask.FromResult(new ReferenceOrchestrationBlueprintRevisionProposalPayload(
                BlueprintId: -1,
                ReviewId: "provider-mismatched-review",
                Origin,
                RevisionReason,
                Changes));
        }
    }

    private sealed class RecordingChatCompletionClient(string response) : IChatCompletionClient
    {
        public int CallCount { get; private set; }

        public ChatCompletionRequest? LastRequest { get; private set; }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("Reference blueprint revision proposals use streaming chat output.");
        }

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            await Task.CompletedTask;
            yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, response);
        }
    }

    private sealed class FixedAppSettingsService(string selectedModelKey, string reasoningEffort) : IAppSettingsService
    {
        public ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AppSettingsPayload(
                1,
                0,
                selectedModelKey,
                reasoningEffort,
                "manual",
                360,
                string.Empty,
                string.Empty));
        }

        public ValueTask SaveSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SaveAvatarAsync(byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SaveUserNameAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetApprovalModeAsync(string mode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetChatPanelWidthAsync(int width, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetLastNovelAsync(long novelId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetLastSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetReasoningEffortAsync(string reasoningEffort, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetSelectedModelAsync(
            string selectedModelKey,
            string reasoningEffort,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async ValueTask MarkAnchorAsWorkspaceCorpusAsync(
        AppInitializationOptions options,
        long anchorId,
        string visibility = ReferenceCorpusVisibilities.Workspace)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_anchors
            SET novel_id = 0,
                corpus_visibility = $corpus_visibility
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$corpus_visibility", visibility);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, updated);
    }

    private static async ValueTask SetMaterialLinkAnalysisHashAsync(
        AppInitializationOptions options,
        long blueprintId,
        string analysisContractHash)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_blueprint_material_links
            SET analysis_contract_hash = $analysis_contract_hash
            WHERE blueprint_id = $blueprint_id;
            """;
        command.Parameters.AddWithValue("$analysis_contract_hash", analysisContractHash);
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.True(updated > 0);
    }

    private static async ValueTask SetReviewContextHashAsync(
        AppInitializationOptions options,
        string reviewId,
        string contextHash)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_chapter_blueprint_reviews
            SET context_hash = $context_hash
            WHERE review_id = $review_id;
            """;
        command.Parameters.AddWithValue("$context_hash", contextHash);
        command.Parameters.AddWithValue("$review_id", reviewId);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.True(updated > 0);
    }

    private static async ValueTask SetReviewVersionAsync(
        AppInitializationOptions options,
        string reviewId,
        int reviewVersion)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_chapter_blueprint_reviews
            SET review_version = $review_version
            WHERE review_id = $review_id;
            """;
        command.Parameters.AddWithValue("$review_version", reviewVersion);
        command.Parameters.AddWithValue("$review_id", reviewId);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.True(updated > 0);
    }

    private static async ValueTask<int> CountBlueprintReviewsAsync(
        AppInitializationOptions options,
        long blueprintId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_chapter_blueprint_reviews
            WHERE blueprint_id = $blueprint_id;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask<IReadOnlyList<string>> ReadRevisionFieldPathsAsync(
        AppInitializationOptions options,
        long blueprintId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT changed_field_path
            FROM reference_chapter_blueprint_revisions
            WHERE blueprint_id = $blueprint_id
            ORDER BY created_at ASC;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        var fieldPaths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            fieldPaths.Add(reader.GetString(0));
        }

        return fieldPaths;
    }

    private static IReadOnlyList<ReferenceBlueprintRevisionChangePayload> StrictReviewGateRevisionChanges(string beatId)
    {
        var prefix = "beat:" + beatId + ":";
        return
        [
            new ReferenceBlueprintRevisionChangePayload(prefix + "emotion_trigger", "剧情需要"),
            new ReferenceBlueprintRevisionChangePayload(prefix + "suppressed_reaction", "有反应"),
            new ReferenceBlueprintRevisionChangePayload(prefix + "external_evidence", "表现出痛苦"),
            new ReferenceBlueprintRevisionChangePayload(prefix + "transition_in", "来到旧宅"),
            new ReferenceBlueprintRevisionChangePayload(prefix + "transition_out", "第二天转到仓库"),
            new ReferenceBlueprintRevisionChangePayload(
                prefix + "viewpoint_allowed_knowledge",
                JsonSerializer.Serialize(new[] { "主角已经到场", "周鸣是卧底" })),
            new ReferenceBlueprintRevisionChangePayload(
                prefix + "prose_duties",
                JsonSerializer.Serialize(Array.Empty<string>())),
            new ReferenceBlueprintRevisionChangePayload(
                prefix + "reference_query.function_tags",
                JsonSerializer.Serialize(new[] { "dialogue" })),
            new ReferenceBlueprintRevisionChangePayload(
                prefix + "reference_query.emotion_tags",
                JsonSerializer.Serialize(new[] { "triumph" })),
            new ReferenceBlueprintRevisionChangePayload(
                prefix + "reference_query.pov_tags",
                JsonSerializer.Serialize(new[] { "omniscient" }))
        ];
    }

    private static ReferenceMaterialPayload BuildFixedMaterial(string materialId, string text)
    {
        return new ReferenceMaterialPayload(
            MaterialId: materialId,
            AnchorId: 4242,
            SourceSegmentId: "seg-" + materialId,
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "interiority",
            EmotionTag: "pressure",
            SceneTag: "rain",
            PovTag: "close",
            TechniqueTag: "sensory",
            FunctionConfidence: 1,
            EmotionConfidence: 1,
            PovConfidence: 1,
            Text: text,
            SourceHash: "hash-" + materialId,
            ExtractorVersion: "test",
            UserVerified: true,
            ScoreComponents: null,
            CreatedAt: DateTimeOffset.UtcNow);
    }

    private static void AssertJsonArrayContains(JsonElement element, string expected)
    {
        Assert.Contains(
            element.EnumerateArray(),
            item => item.GetString()?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private sealed class FixedReferenceAnchorService : IReferenceAnchorService
    {
        private readonly IReadOnlyList<ReferenceMaterialPayload> _materials;
        private readonly bool _applySearchFilters;
        private readonly IReadOnlyDictionary<long, string> _licenseStatuses;
        private readonly IReadOnlyDictionary<string, string> _adaptedTextByMaterialId;
        private readonly IReadOnlyDictionary<string, string> _rewriteLevelByMaterialId;
        private readonly List<SearchReferenceMaterialsPayload> _searchInputs = [];
        private readonly List<AdaptReferenceMaterialPayload> _adaptInputs = [];

        public IReadOnlyList<SearchReferenceMaterialsPayload> SearchInputs => _searchInputs;
        public IReadOnlyList<AdaptReferenceMaterialPayload> AdaptInputs => _adaptInputs;

        public FixedReferenceAnchorService(
            ReferenceMaterialPayload material,
            bool applySearchFilters = false,
            IReadOnlyDictionary<string, string>? adaptedTextByMaterialId = null,
            IReadOnlyDictionary<string, string>? rewriteLevelByMaterialId = null)
        {
            _materials = [material];
            _applySearchFilters = applySearchFilters;
            _licenseStatuses = new Dictionary<long, string>();
            _adaptedTextByMaterialId = adaptedTextByMaterialId ?? new Dictionary<string, string>();
            _rewriteLevelByMaterialId = rewriteLevelByMaterialId ?? new Dictionary<string, string>();
        }

        public FixedReferenceAnchorService(
            IReadOnlyList<ReferenceMaterialPayload> materials,
            bool applySearchFilters = false,
            IReadOnlyDictionary<long, string>? licenseStatuses = null,
            IReadOnlyDictionary<string, string>? adaptedTextByMaterialId = null,
            IReadOnlyDictionary<string, string>? rewriteLevelByMaterialId = null)
        {
            _materials = materials;
            _applySearchFilters = applySearchFilters;
            _licenseStatuses = licenseStatuses ?? new Dictionary<long, string>();
            _adaptedTextByMaterialId = adaptedTextByMaterialId ?? new Dictionary<string, string>();
            _rewriteLevelByMaterialId = rewriteLevelByMaterialId ?? new Dictionary<string, string>();
        }

        public ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(
            SearchReferenceMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            _searchInputs.Add(input);
            var items = _applySearchFilters
                ? _materials.Where(material => MatchesSearch(input, material)).ToArray()
                : _materials.ToArray();
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialPayload>(
                items,
                Total: items.Length,
                Page: input.Page,
                Size: input.Size,
                TotalPages: items.Length == 0 ? 0 : 1));
        }

        public ValueTask<ReferenceMaterialCoveragePayload> GetMaterialCoverageAsync(
            GetReferenceMaterialCoveragePayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceMaterialCoveragePayload(0, 0, []));
        }

        public ValueTask<PageResultPayload<ReferenceMaterialTagReviewItemPayload>> GetMaterialTagReviewQueueAsync(
            GetReferenceMaterialTagReviewQueuePayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialTagReviewItemPayload>(
                [],
                Total: 0,
                Page: input.Page,
                Size: input.Size,
                TotalPages: 0));
        }

        private static bool MatchesSearch(
            SearchReferenceMaterialsPayload input,
            ReferenceMaterialPayload material)
        {
            return MatchesAnchorFilter(material.AnchorId, input.AnchorIds) &&
                MatchesAnyFilter(material.MaterialType, input.MaterialTypes) &&
                MatchesAnyFilter(material.EmotionTag, input.EmotionTags) &&
                MatchesAnyFilter(material.FunctionTag, input.FunctionTags) &&
                MatchesAnyFilter(material.PovTag, input.PovTags) &&
                MatchesAnyFilter(material.TechniqueTag, input.TechniqueTags) &&
                (string.IsNullOrWhiteSpace(input.Query) ||
                    material.Text.Contains(input.Query, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesAnchorFilter(long anchorId, IReadOnlyList<long> filters)
        {
            return filters.Count == 0 || filters.Contains(anchorId);
        }

        private static bool MatchesAnyFilter(string value, IReadOnlyList<string> filters)
        {
            return filters.Count == 0 ||
                filters.Any(filter => string.Equals(filter, value, StringComparison.OrdinalIgnoreCase));
        }

        public ValueTask<IReadOnlyList<ReferenceUserFeedbackPayload>> GetUserFeedbackAsync(
            GetReferenceUserFeedbackPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ReferenceUserFeedbackPayload>>([]);
        }

        public ValueTask<ReferenceMaterialDetailPayload?> GetMaterialDetailAsync(
            GetReferenceMaterialDetailPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ReferenceMaterialDetailPayload?>(null);
        }

        public ValueTask<ReferenceSourceSegmentDetailPayload?> GetSourceSegmentDetailAsync(
            GetReferenceSourceSegmentDetailPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ReferenceSourceSegmentDetailPayload?>(null);
        }

        public ValueTask<ReferenceSourceProcessingDetailPayload?> GetSourceProcessingDetailAsync(
            GetReferenceSourceProcessingDetailPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ReferenceSourceProcessingDetailPayload?>(null);
        }

        public ValueTask<ReferenceMaterialPayload> UpdateMaterialTagsAsync(
            UpdateReferenceMaterialTagsPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ReferenceMaterialPayload>> UpdateMaterialsTagsAsync(
            UpdateReferenceMaterialsTagsPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
            CreateReferenceAnchorPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferenceAnchorPayload> RegisterMaterializationSourceAsync(
            CreateReferenceAnchorPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> CreateAnchorsAsync(
            CreateReferenceAnchorsPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CreateReferenceAnchorsResultPayload> CreateAnchorsWithResultAsync(
            CreateReferenceAnchorsPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferenceAnchorPayload> PromoteAnchorToWorkspaceCorpusAsync(
            PromoteReferenceAnchorToWorkspaceCorpusPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> PromoteAnchorsToWorkspaceCorpusAsync(
            PromoteReferenceAnchorsToWorkspaceCorpusPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
            UpdateReferenceAnchorMetadataPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
            long novelId,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var anchors = _materials
                .GroupBy(material => material.AnchorId)
                .Select(group => new ReferenceAnchorPayload(
                    group.Key,
                    novelId,
                    "fixed anchor " + group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    string.Empty,
                    "fixed.md",
                    "markdown",
                    _licenseStatuses.TryGetValue(group.Key, out var licenseStatus)
                        ? licenseStatus
                        : "user_provided",
                    "hash-" + group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "test",
                    ReferenceAnchorBuildStates.Ready,
                    now,
                    now))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<ReferenceAnchorPayload>>(anchors);
        }

        public ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

public ValueTask<ReferenceAnchorBuildStatusPayload?> GetBuildStatusAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken) =>
throw new NotSupportedException();

 public ValueTask<ReferenceMaterialEmbeddingBackfillPayload> BackfillMaterialEmbeddingsAsync(
 BackfillReferenceMaterialEmbeddingsPayload input,
 CancellationToken cancellationToken) =>
 ValueTask.FromResult(new ReferenceMaterialEmbeddingBackfillPayload(
 "test", "test", 1, 0, 0, 0, 0, 0, 0, []));

        public ValueTask<AdaptReferenceMaterialResultPayload> AdaptMaterialAsync(
            AdaptReferenceMaterialPayload input,
            CancellationToken cancellationToken)
        {
            _adaptInputs.Add(input);
            var material = _materials.First(item => string.Equals(item.MaterialId, input.MaterialId, StringComparison.Ordinal));
            var text = _adaptedTextByMaterialId.TryGetValue(input.MaterialId, out var adaptedText)
                ? adaptedText
                : material.Text;
            var rewriteLevel = _rewriteLevelByMaterialId.TryGetValue(input.MaterialId, out var configuredRewriteLevel)
                ? configuredRewriteLevel
                : input.MaxRewriteLevel;
            return ValueTask.FromResult(new AdaptReferenceMaterialResultPayload(
                "fixed-candidate-" + input.MaterialId,
                input.MaterialId,
                rewriteLevel,
                text,
                input.SlotValues,
                NonSlotEdits: [],
                new ReferenceReuseAuditPayload(
                    "fixed-audit-" + input.MaterialId,
                    "passed",
                    rewriteLevel,
                    ProvenanceErrors: [],
                    UnsupportedFactErrors: [],
                    AiProseRisks: [],
                    NonSlotEdits: [],
                    RequiredFixes: [],
                    DateTimeOffset.UnixEpoch)));
        }

        public ValueTask<ReferenceReuseAuditPayload> AuditCandidateAsync(
            AuditReferenceReusePayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferenceUserFeedbackPayload> RecordUserFeedbackAsync(
            RecordReferenceUserFeedbackPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DeleteAnchorAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DeleteAnchorsAsync(
            DeleteReferenceAnchorsPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DeleteMaterialsAsync(
            DeleteReferenceMaterialsPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask RestoreMaterialsAsync(
            RestoreReferenceMaterialsPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
