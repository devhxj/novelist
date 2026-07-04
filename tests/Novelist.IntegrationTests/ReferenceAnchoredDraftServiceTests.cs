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

        var afterBinding = await service.GetChapterBlueprintAsync(novel.Id, blueprint.BlueprintId, CancellationToken.None);
        Assert.NotNull(afterBinding);
        Assert.Equal(ReferenceBlueprintStates.MaterialBound, afterBinding.Status);
        Assert.NotNull(afterBinding.LatestReview);
        Assert.Equal(review.ReviewId, afterBinding.LatestReview?.ReviewId);
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
        Assert.Equal(
            originalContent,
            await chapters.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
        var reloadedChapters = new FileSystemChapterContentService(options, novels);
        Assert.Equal(
            originalContent,
            await reloadedChapters.GetContentAsync(novel.Id, chapter.FilePath, CancellationToken.None));
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
                [new ReferenceBlueprintRevisionChangePayload(
                    "beat:" + blueprint.Beats[0].BeatId + ":no_reuse_reason",
                    "transition beat only carries approved chapter-state pressure without reusable source material")],
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
        private readonly List<SearchReferenceMaterialsPayload> _searchInputs = [];
        private readonly List<AdaptReferenceMaterialPayload> _adaptInputs = [];

        public IReadOnlyList<SearchReferenceMaterialsPayload> SearchInputs => _searchInputs;
        public IReadOnlyList<AdaptReferenceMaterialPayload> AdaptInputs => _adaptInputs;

        public FixedReferenceAnchorService(
            ReferenceMaterialPayload material,
            bool applySearchFilters = false)
        {
            _materials = [material];
            _applySearchFilters = applySearchFilters;
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

        private static bool MatchesSearch(
            SearchReferenceMaterialsPayload input,
            ReferenceMaterialPayload material)
        {
            return MatchesAnyFilter(material.MaterialType, input.MaterialTypes) &&
                MatchesAnyFilter(material.EmotionTag, input.EmotionTags) &&
                MatchesAnyFilter(material.FunctionTag, input.FunctionTags) &&
                MatchesAnyFilter(material.PovTag, input.PovTags) &&
                MatchesAnyFilter(material.TechniqueTag, input.TechniqueTags) &&
                (string.IsNullOrWhiteSpace(input.Query) ||
                    material.Text.Contains(input.Query, StringComparison.OrdinalIgnoreCase));
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

        public ValueTask<ReferenceMaterialPayload> UpdateMaterialTagsAsync(
            UpdateReferenceMaterialTagsPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
            CreateReferenceAnchorPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
            long novelId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public ValueTask<AdaptReferenceMaterialResultPayload> AdaptMaterialAsync(
            AdaptReferenceMaterialPayload input,
            CancellationToken cancellationToken)
        {
            _adaptInputs.Add(input);
            var material = _materials.First(item => string.Equals(item.MaterialId, input.MaterialId, StringComparison.Ordinal));
            return ValueTask.FromResult(new AdaptReferenceMaterialResultPayload(
                "fixed-candidate-" + input.MaterialId,
                input.MaterialId,
                input.MaxRewriteLevel,
                material.Text,
                input.SlotValues,
                NonSlotEdits: [],
                new ReferenceReuseAuditPayload(
                    "fixed-audit-" + input.MaterialId,
                    "passed",
                    input.MaxRewriteLevel,
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
    }
}
