using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
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
        Assert.Equal("logic", blueprint.LogicAnalysis.Track);
        Assert.Equal("emotion", blueprint.EmotionAnalysis.Track);
        Assert.Equal("narration", blueprint.NarrationAnalysis.Track);
        Assert.Equal("character", blueprint.CharacterAnalysis.Track);
        Assert.Equal("reference", blueprint.ReferenceAnalysis.Track);
        Assert.Equal("transition", blueprint.TransitionPlan.Track);
        Assert.Equal("execution", blueprint.ExecutionContract.Track);
        Assert.False(string.IsNullOrWhiteSpace(blueprint.AnalysisContractHash));
        var beat = Assert.Single(blueprint.Beats);
        Assert.False(string.IsNullOrWhiteSpace(beat.TransitionIn));
        Assert.False(string.IsNullOrWhiteSpace(beat.ExternalEvidence));
        Assert.False(string.IsNullOrWhiteSpace(beat.ParagraphIntention));
        Assert.False(string.IsNullOrWhiteSpace(beat.ExecutionMode));
        Assert.False(string.IsNullOrWhiteSpace(beat.AntiScreenplayDuty));
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
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
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
                new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
                CancellationToken.None));
        var draftException = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(novel.Id, blueprint.BlueprintId, BeatIds: []),
                CancellationToken.None));
        Assert.Contains("approved", draftException.Message, StringComparison.OrdinalIgnoreCase);
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
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
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
                new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
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

        var revised = await service.ReviseChapterBlueprintAsync(
            new ReviseReferenceChapterBlueprintPayload(
                novel.Id,
                blueprint.BlueprintId,
                [
                    new ReferenceBlueprintRevisionChangePayload("beat:" + beatId + ":pov_character", "周鸣"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + beatId + ":viewpoint_allowed_knowledge",
                        JsonSerializer.Serialize(new[] { "周鸣看到门缝里的血迹" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + beatId + ":character_states_after",
                        JsonSerializer.Serialize(new[] { "周鸣 exposed" })),
                    new ReferenceBlueprintRevisionChangePayload("beat:" + beatId + ":emotion_trigger", "门缝里的血迹"),
                    new ReferenceBlueprintRevisionChangePayload("beat:" + beatId + ":external_evidence", "指尖发紧"),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + beatId + ":scene_facts",
                        JsonSerializer.Serialize(new[] { "门缝里的血迹" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + beatId + ":prose_duties",
                        JsonSerializer.Serialize(new[] { "interiority", "external_evidence", "transition" })),
                    new ReferenceBlueprintRevisionChangePayload(
                        "beat:" + beatId + ":reference_query.function_tags",
                        JsonSerializer.Serialize(new[] { "identity_reveal" }))
                ],
                "user",
                "edit beat contract fields"),
            CancellationToken.None);

        var revisedBeat = revised.Beats[0];
        Assert.Equal(ReferenceBlueprintStates.Draft, revised.Status);
        Assert.NotEqual(blueprint.AnalysisContractHash, revised.AnalysisContractHash);
        Assert.Equal("周鸣", revisedBeat.PovCharacter);
        Assert.Equal(["周鸣看到门缝里的血迹"], revisedBeat.ViewpointAllowedKnowledge);
        Assert.Equal(["周鸣 exposed"], revisedBeat.CharacterStatesAfter);
        Assert.Equal("门缝里的血迹", revisedBeat.EmotionTrigger);
        Assert.Equal("指尖发紧", revisedBeat.ExternalEvidence);
        Assert.Equal(["门缝里的血迹"], revisedBeat.SceneFacts);
        Assert.Equal(["interiority", "external_evidence", "transition"], revisedBeat.ProseDuties);
        Assert.Equal(["identity_reveal"], revisedBeat.ReferenceQuery.FunctionTags);
        Assert.Null(revised.LatestReview);

        var revisionFieldPaths = await ReadRevisionFieldPathsAsync(options, blueprint.BlueprintId);
        Assert.Contains("beat:" + beatId + ":pov_character", revisionFieldPaths);
        Assert.Contains("beat:" + beatId + ":viewpoint_allowed_knowledge", revisionFieldPaths);
        Assert.Contains("beat:" + beatId + ":character_states_after", revisionFieldPaths);
        Assert.Contains("beat:" + beatId + ":emotion_trigger", revisionFieldPaths);
        Assert.Contains("beat:" + beatId + ":external_evidence", revisionFieldPaths);
        Assert.Contains("beat:" + beatId + ":scene_facts", revisionFieldPaths);
        Assert.Contains("beat:" + beatId + ":prose_duties", revisionFieldPaths);
        Assert.Contains("beat:" + beatId + ":reference_query.function_tags", revisionFieldPaths);

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
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, MaxResultsPerBeat: 3),
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
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
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
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
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
                    "max_results_per_beat": 2
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
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
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
        Assert.Equal("passed", candidate.GetProperty("audit_status").GetString());
        Assert.Equal("passed", result.GetProperty("audit").GetProperty("status").GetString());

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
            new BindReferenceBlueprintMaterialsPayload(novel.Id, blueprint.BlueprintId, 2),
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
            new BindReferenceBlueprintMaterialsPayload(novel.Id, revised.BlueprintId, 2),
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
}
