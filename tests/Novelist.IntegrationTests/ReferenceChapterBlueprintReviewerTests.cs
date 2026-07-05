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
    public void BuildReviewFailsFinalHookWithUnsupportedFact()
    {
        var blueprint = Blueprint(
            beat => beat,
            finalHook: "周鸣其实是卧底");

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ContinuityErrors, item => item.Contains("final hook", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.ContinuityErrors, item => item.Contains("周鸣是卧底", StringComparison.Ordinal));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "continuity" &&
                defect.FieldPath == "final_hook");
    }

    [Fact]
    public void BuildReviewAllowsFinalHookFactWhenSetupEarlier()
    {
        var blueprint = Blueprint(
            beat => beat,
            knownFacts: ["周鸣是卧底"],
            finalHook: "周鸣其实是卧底");

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
    }

    [Fact]
    public void BuildReviewFailsSceneFactOutsideKnownFacts()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SceneFacts = ["雨声压低了整条街的呼吸", "周鸣其实是卧底"]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ContinuityErrors, item => item.Contains("scene fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.ContinuityErrors, item => item.Contains("周鸣是卧底", StringComparison.Ordinal));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "continuity" &&
                defect.FieldPath.Contains("scene_facts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsSceneFactWhenKnownFactSetup()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SceneFacts = ["雨声压低了整条街的呼吸", "周鸣其实是卧底"]
            },
            knownFacts: ["雨声压低了整条街的呼吸", "周鸣是卧底"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
    }

    [Fact]
    public void BuildReviewFailsSceneFactConflictingWithPovForbiddenKnowledge()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SceneFacts = ["雨声压低了整条街的呼吸", "周鸣是卧底"],
                ViewpointForbiddenKnowledge = ["周鸣是卧底"]
            },
            knownFacts: ["雨声压低了整条街的呼吸", "周鸣是卧底"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.PovErrors, item => item.Contains("forbidden POV", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.PovErrors, item => item.Contains("周鸣是卧底", StringComparison.Ordinal));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "pov" &&
                defect.FieldPath.Contains("scene_facts", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsMissingCharacterMisbeliefs()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CharacterMisbeliefs = []
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CharacterStateErrors, item => item.Contains("misbelief", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "character_state" &&
                defect.FieldPath.Contains("character_misbeliefs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsMissingRelationshipPressure()
    {
        var blueprint = Blueprint(beat => beat with
        {
            RelationshipPressure = []
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CharacterStateErrors, item => item.Contains("relationship pressure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "character_state" &&
                defect.FieldPath.Contains("relationship_pressure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsGenericParagraphIntention()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ParagraphIntention = "写得更好，更有代入感"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("generic paragraph intention", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "execution" &&
                defect.FieldPath.Contains("paragraph_intention", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsGenericExecutionMode()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ExecutionMode = "正常写，自然展开"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("generic execution mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "execution" &&
                defect.FieldPath.Contains("execution_mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsGenericCandidateRejectionRule()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CandidateRejectionRule = "不好的不要，质量差就拒绝"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("generic candidate rejection rule", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "execution" &&
                defect.FieldPath.Contains("candidate_rejection_rule", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsGenericAntiScreenplayDuty()
    {
        var blueprint = Blueprint(beat => beat with
        {
            AntiScreenplayDuty = "避免剧本化"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ScreenplayDriftRisks, item => item.Contains("generic anti-screenplay duty", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "screenplay_drift" &&
                defect.FieldPath.Contains("anti_screenplay_duty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsGenericNarrationStrategy()
    {
        var blueprint = Blueprint(beat => beat with
        {
            NarrationStrategy = "正常叙述，写得有画面感"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NarrationErrors, item => item.Contains("generic narration strategy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "narration" &&
                defect.FieldPath.Contains("narration_strategy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsMissingRhythmStrategy()
    {
        var blueprint = Blueprint(beat => beat with
        {
            RhythmStrategy = ""
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NarrationErrors, item => item.Contains("rhythm strategy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "narration" &&
                defect.FieldPath.Contains("rhythm_strategy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsGenericRhythmStrategy()
    {
        var blueprint = Blueprint(beat => beat with
        {
            RhythmStrategy = "节奏自然流畅，快慢结合"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NarrationErrors, item => item.Contains("generic rhythm strategy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "narration" &&
                defect.FieldPath.Contains("rhythm_strategy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsGenericSourceBackedDetailTarget()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SourceBackedDetailTarget = "加一点细节，让画面更丰富"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NovelisticNarrationErrors, item => item.Contains("generic source-backed detail target", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "novelistic_narration" &&
                defect.FieldPath.Contains("source_backed_detail_target", StringComparison.OrdinalIgnoreCase));
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
        IReadOnlyList<string>? knownFacts = null,
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
            knownFacts ?? ["雨声压低了整条街的呼吸"],
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
