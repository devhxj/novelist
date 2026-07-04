using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceChapterBlueprintReviewerTests
{
    [Fact]
    public void BuildReviewPassesCompleteBlueprint()
    {
        var blueprint = Blueprint(beat => beat);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Equal(blueprint.ContextHash, review.ContextHash);
        Assert.Equal(blueprint.SourcePlanHash, review.SourcePlanHash);
        Assert.Equal(blueprint.AnalysisContractHash, review.AnalysisContractHash);
        Assert.Empty(review.RequiredFixes);
    }

    [Fact]
    public void BuildReviewFailsMissingExecutionTrack()
    {
        var blueprint = Blueprint(
            beat => beat,
            execution: new ReferenceChapterBlueprintExecutionTrackPayload(
                "execution",
                "execution",
                [],
                ["dwell"],
                ["anti-screenplay"],
                ["detail"],
                ["reject"]));

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("execution track", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.RequiredFixes, item => item.Contains("execution track", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsMissingAnalysisTrack()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            LogicAnalysis = new ReferenceChapterBlueprintAnalysisTrackPayload("logic", "", [])
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.LogicErrors, item => item.Contains("logic, emotion, narration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsMissingCausalityOut()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CausalityOut = ""
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CausalityErrors, item => item.Contains("causality_out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewEmitsStructuredDefectsForBeatFields()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CausalityOut = ""
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        var defect = Assert.Single(review.Defects, item => item.FieldPath == "beat:1:beat:1:causality_out");
        Assert.Equal("1:beat:1", defect.BeatId);
        Assert.Equal("causality", defect.Category);
        Assert.Equal("error", defect.Severity);
        Assert.Contains("causality_out", defect.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("causality_out", defect.RequiredFix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReviewFailsEmotionChangeWithoutExternalEvidence()
    {
        var blueprint = Blueprint(beat => beat with
        {
            EmotionBefore = "克制",
            EmotionAfter = "紧张",
            ExternalEvidence = ""
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.EmotionErrors, item => item.Contains("external evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInFinalHook()
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"],
            finalHook: "凶手身份在门后出现");

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("凶手身份", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReviewFailsActionBeatWithoutNovelisticDuties()
    {
        var blueprint = Blueprint(beat => beat with
        {
            BeatType = ReferenceBlueprintBeatTypes.Action,
            ProseDuties = ["action"],
            SubtextPlan = string.Empty,
            SensoryAnchorTarget = string.Empty,
            SourceBackedDetailTarget = string.Empty
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ScreenplayDriftRisks, item => item.Contains("action/dialogue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.NovelisticNarrationErrors, item => item.Contains("screenplay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsDialogueBeatWithoutNovelisticDuties()
    {
        var blueprint = Blueprint(beat => beat with
        {
            BeatType = ReferenceBlueprintBeatTypes.DialogueExchange,
            ProseDuties = ["dialogue"],
            SubtextPlan = string.Empty,
            SensoryAnchorTarget = string.Empty,
            SourceBackedDetailTarget = string.Empty
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ScreenplayDriftRisks, item => item.Contains("action/dialogue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.NovelisticNarrationErrors, item => item.Contains("screenplay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsFakeEmotionMechanics()
    {
        var blueprint = Blueprint(beat => beat with
        {
            EmotionBefore = "克制",
            EmotionAfter = "崩溃",
            EmotionTrigger = "剧情需要",
            SuppressedReaction = "有反应",
            ExternalEvidence = "表现出痛苦"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.EmotionErrors, item => item.Contains("fake emotion", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.RequiredFixes, item => item.Contains("emotion mechanic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsWhenCharacterStateHasNoDelta()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CharacterStatesBefore = ["controlled"],
            CharacterStatesAfter = ["controlled"]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CharacterStateErrors, item => item.Contains("role-state delta", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "character_state" &&
                defect.FieldPath.Contains("character_state_delta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsHardTransitionWithoutNarrativePressure()
    {
        var blueprint = Blueprint(beat => beat with
        {
            TransitionIn = "来到旧宅",
            TransitionOut = "第二天转到仓库"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.TransitionErrors, item => item.Contains("pressure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsPovAllowedKnowledgeOutsideApprovedFacts()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ViewpointAllowedKnowledge = ["雨声压低了整条街的呼吸", "周鸣是卧底"]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.PovErrors, item => item.Contains("周鸣是卧底", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReviewFailsMissingProseDutiesAsExecutionDefect()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ProseDuties = []
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("prose duties", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsMaterialQueryWithoutBeatFit()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ReferenceQuery = beat.ReferenceQuery with
            {
                FunctionTags = ["dialogue"],
                EmotionTags = ["triumph"],
                PovTags = ["omniscient"],
                TechniqueTags = []
            }
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.MaterialFitErrors, item => item.Contains("material fit", StringComparison.OrdinalIgnoreCase));
    }

    private static ReferenceChapterBlueprintPayload Blueprint(
        Func<ReferenceChapterBlueprintBeatPayload, ReferenceChapterBlueprintBeatPayload> configureBeat,
        ReferenceChapterBlueprintExecutionTrackPayload? execution = null,
        IReadOnlyList<string>? forbiddenFacts = null,
        string finalHook = "hook")
    {
        var beat = configureBeat(Beat("1:beat:1"));
        return new ReferenceChapterBlueprintPayload(
            1,
            10,
            1,
            "测试蓝图",
            ReferenceBlueprintStates.Draft,
            "next",
            "source-hash",
            "context-hash",
            "analysis-hash",
            1,
            0,
            1,
            "雨夜压力",
            new ReferenceChapterBlueprintAnalysisTrackPayload("logic", "logic", ["point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("emotion", "emotion", ["point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("narration", "narration", ["point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("character", "character", ["point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("reference", "reference", ["point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("transition", "transition", ["point"]),
            execution ?? new ReferenceChapterBlueprintExecutionTrackPayload(
                "execution",
                "execution",
                ["intention"],
                ["dwell"],
                ["anti-screenplay"],
                ["detail"],
                ["reject"]),
            "previous",
            "final",
            finalHook,
            "林岚",
            "close",
            ["雨声压低了整条街的呼吸"],
            forbiddenFacts ?? [],
            [],
            [beat],
            LatestReview: null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static ReferenceChapterBlueprintBeatPayload Beat(string beatId)
    {
        return new ReferenceChapterBlueprintBeatPayload(
            beatId,
            1,
            1,
            ReferenceBlueprintBeatTypes.Interiority,
            "show pressure",
            "premise",
            "pressure",
            "in",
            "out",
            "transition in",
            "transition out",
            "林岚",
            "close",
            ["雨声压低了整条街的呼吸"],
            [],
            ["controlled"],
            ["pressured"],
            ["pursue clue"],
            ["misbelief"],
            ["pressure"],
            "chapter pressure",
            "controlled",
            "pressured",
            "swallows response",
            "visible pause",
            "close narration",
            "slow rhythm",
            "dwell before action",
            "dwell",
            "show pressure beyond action/dialogue",
            "rain detail",
            "restraint",
            "source detail",
            "reject action only",
            ["雨声压低了整条街的呼吸"],
            [],
            new ReferenceMaterialQueryPayload(
                "雨声压低了整条街的呼吸",
                [ReferenceMaterialTypes.Sentence],
                [],
                ["environment"],
                ["close"],
                [],
                3),
            [ReferenceMaterialTypes.Sentence],
            ReferenceRewriteLevels.L1,
            [],
            "preserve source order",
            string.Empty,
            ["interiority", "external_evidence"],
            []);
    }
}
