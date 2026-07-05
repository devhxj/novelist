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
    public void BuildReviewWarnsWhenTooManyBeatsSkipReferenceReuse()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            Beats =
            [
                Beat("1:beat:1") with
                {
                    BeatIndex = 1,
                    NoReuseReason = "transition only, no reusable source material"
                },
                Beat("1:beat:2") with
                {
                    BeatIndex = 2,
                    NoReuseReason = "transition only, no reusable source material"
                },
                Beat("1:beat:3") with
                {
                    BeatIndex = 3,
                    NoReuseReason = "transition only, no reusable source material"
                }
            ]
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Empty(review.RequiredFixes);
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.Severity == "warning" &&
                defect.FieldPath.Contains("no_reuse_reason", StringComparison.OrdinalIgnoreCase) &&
                defect.Reason.Contains("too many", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewWarnsWhenEveryBeatRequestsSameMaterialType()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            Beats =
            [
                Beat("1:beat:1") with { BeatIndex = 1 },
                Beat("1:beat:2") with { BeatIndex = 2 },
                Beat("1:beat:3") with { BeatIndex = 3 }
            ]
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Empty(review.RequiredFixes);
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.Severity == "warning" &&
                defect.FieldPath.Contains("material_types", StringComparison.OrdinalIgnoreCase) &&
                defect.Reason.Contains("same reference material type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewWarnsWhenTooManyBeatsUseSameNarrativeDuty()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            Beats =
            [
                Beat("1:beat:1") with { BeatIndex = 1, ProseDuties = ["interiority"] },
                Beat("1:beat:2") with { BeatIndex = 2, ProseDuties = ["interiority"] },
                Beat("1:beat:3") with { BeatIndex = 3, ProseDuties = ["interiority"] }
            ]
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Empty(review.RequiredFixes);
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "execution" &&
                defect.Severity == "warning" &&
                defect.FieldPath.Contains("prose_duties", StringComparison.OrdinalIgnoreCase) &&
                defect.Reason.Contains("same narrative duty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewWarnsWhenAdjacentParagraphIntentionsRepeatMechanically()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            Beats =
            [
                Beat("1:beat:1") with { BeatIndex = 1, ParagraphIntention = "dwell on visible hesitation before the choice" },
                Beat("1:beat:2") with { BeatIndex = 2, ParagraphIntention = "dwell on visible hesitation before the choice" },
                Beat("1:beat:3") with { BeatIndex = 3, ParagraphIntention = "dwell on visible hesitation before the choice" }
            ]
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Empty(review.RequiredFixes);
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "execution" &&
                defect.Severity == "warning" &&
                defect.FieldPath.Contains("paragraph_intention", StringComparison.OrdinalIgnoreCase) &&
                defect.Reason.Contains("repeat mechanically", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewWarnsWhenMaxRewriteLevelExceedsDefault()
    {
        var blueprint = Blueprint(beat => beat with
        {
            MaxRewriteLevel = ReferenceRewriteLevels.L2
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Empty(review.RequiredFixes);
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.Severity == "warning" &&
                defect.FieldPath.Contains("max_rewrite_level", StringComparison.OrdinalIgnoreCase) &&
                defect.Reason.Contains("project default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewWarnsWhenEmotionTransitionsAreAllImmediate()
    {
        static ReferenceChapterBlueprintBeatPayload ImmediateEmotionBeat(string beatId, int beatIndex)
        {
            return Beat(beatId) with
            {
                BeatIndex = beatIndex,
                EmotionBefore = "controlled",
                EmotionAfter = "heightened",
                SuppressedReaction = "direct reaction",
                RhythmStrategy = "immediate release",
                SubtextPlan = "state emotion directly"
            };
        }

        var blueprint = Blueprint(beat => beat) with
        {
            Beats =
            [
                ImmediateEmotionBeat("1:beat:1", 1),
                ImmediateEmotionBeat("1:beat:2", 2),
                ImmediateEmotionBeat("1:beat:3", 3)
            ]
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Empty(review.RequiredFixes);
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "emotion" &&
                defect.Severity == "warning" &&
                defect.FieldPath.Contains("emotion", StringComparison.OrdinalIgnoreCase) &&
                defect.Reason.Contains("direct and immediate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsEmotionEvidenceQueryForSubtextAndExternalEvidenceDuties()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ProseDuties = ["subtext", "external_evidence"],
            ReferenceQuery = beat.ReferenceQuery with
            {
                FunctionTags = ["emotion_evidence"],
                EmotionTags = [],
                PovTags = [],
                TechniqueTags = []
            }
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.Empty(review.MaterialFitErrors);
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
    public void BuildReviewFailsUnsupportedNarrativeFunctionFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            NarrativeFunction = "delay the 密室钥匙 reveal through pressure"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.LogicErrors, item => item.Contains("unsupported narrative function fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "logic" &&
                defect.FieldPath.Contains("narrative_function", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsMissingNarrativeFunctionAsReferenceDefect()
    {
        var blueprint = Blueprint(beat => beat with
        {
            NarrativeFunction = string.Empty
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("intended use", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("narrative_function", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedLogicPremiseFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            LogicPremise = "密室钥匙 changes the choice"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.LogicErrors, item => item.Contains("unsupported logic premise fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "logic" &&
                defect.FieldPath.Contains("logic_premise", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedConflictPressureFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ConflictPressure = "密室钥匙 forces a choice"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.LogicErrors, item => item.Contains("unsupported conflict pressure fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "logic" &&
                defect.FieldPath.Contains("conflict_pressure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedChapterFunctionFact()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            ChapterFunction = "turn pressure around 密室钥匙"
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.LogicErrors, item => item.Contains("unsupported chapter function fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "logic" &&
                defect.FieldPath.Contains("chapter_function", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInChapterFunction()
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]) with
        {
            ChapterFunction = "turn pressure around 凶手身份"
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("chapter function", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("chapter_function", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedPreviousStateFact()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            PreviousState = "pressure from 密室钥匙 remains unresolved"
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ContinuityErrors, item => item.Contains("unsupported previous state fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "continuity" &&
                defect.FieldPath.Contains("previous_state", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedFinalStateFact()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            FinalState = "林岚 chooses around 密室钥匙"
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ContinuityErrors, item => item.Contains("unsupported final state fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "continuity" &&
                defect.FieldPath.Contains("final_state", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInPreviousState()
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]) with
        {
            PreviousState = "pressure from 凶手身份 remains unresolved"
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("previous state", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("previous_state", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInFinalState()
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]) with
        {
            FinalState = "林岚 chooses around 凶手身份"
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("final state", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("final_state", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedLogicAnalysisSummaryFact()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            LogicAnalysis = new ReferenceChapterBlueprintAnalysisTrackPayload(
                "logic",
                "logic turns on 密室钥匙",
                ["point"])
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.LogicErrors, item => item.Contains("unsupported logic analysis fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "logic" &&
                defect.FieldPath.Contains("logic_analysis.summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInLogicAnalysisSummary()
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]) with
        {
            LogicAnalysis = new ReferenceChapterBlueprintAnalysisTrackPayload(
                "logic",
                "logic turns on 凶手身份",
                ["point"])
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("logic analysis", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("logic_analysis.summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedLogicAnalysisPointFact()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            LogicAnalysis = new ReferenceChapterBlueprintAnalysisTrackPayload(
                "logic",
                "logic",
                ["turn on 密室钥匙"])
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.LogicErrors, item => item.Contains("unsupported logic analysis point fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "logic" &&
                defect.FieldPath.Contains("logic_analysis.points", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInLogicAnalysisPoints()
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]) with
        {
            LogicAnalysis = new ReferenceChapterBlueprintAnalysisTrackPayload(
                "logic",
                "logic",
                ["turn on 凶手身份"])
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("logic analysis point", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("logic_analysis.points", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("emotion_analysis", "emotion", "emotion")]
    [InlineData("narration_analysis", "narration", "narration")]
    [InlineData("character_analysis", "character_state", "character")]
    [InlineData("reference_analysis", "reference_binding", "reference")]
    [InlineData("transition_plan", "transition", "transition")]
    public void BuildReviewFailsUnsupportedAnalysisTrackSummaryFact(
        string fieldPath,
        string category,
        string displayName)
    {
        var blueprint = WithAnalysisTrack(
            Blueprint(beat => beat),
            fieldPath,
            displayName + " turns on 密室钥匙",
            ["point"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(ReviewMessagesForCategory(review, category), item => item.Contains(
            "unsupported " + displayName + " analysis fact",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == category &&
                defect.FieldPath.Contains(fieldPath + ".summary", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("emotion_analysis", "emotion", "emotion")]
    [InlineData("narration_analysis", "narration", "narration")]
    [InlineData("character_analysis", "character_state", "character")]
    [InlineData("reference_analysis", "reference_binding", "reference")]
    [InlineData("transition_plan", "transition", "transition")]
    public void BuildReviewFailsUnsupportedAnalysisTrackPointFact(
        string fieldPath,
        string category,
        string displayName)
    {
        var blueprint = WithAnalysisTrack(
            Blueprint(beat => beat),
            fieldPath,
            displayName,
            ["turn on 密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(ReviewMessagesForCategory(review, category), item => item.Contains(
            "unsupported " + displayName + " analysis point fact",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == category &&
                defect.FieldPath.Contains(fieldPath + ".points", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("emotion_analysis", "emotion")]
    [InlineData("narration_analysis", "narration")]
    [InlineData("character_analysis", "character")]
    [InlineData("reference_analysis", "reference")]
    [InlineData("transition_plan", "transition")]
    public void BuildReviewFailsForbiddenFactInAnalysisTrackSummary(
        string fieldPath,
        string displayName)
    {
        var blueprint = WithAnalysisTrack(
            Blueprint(
                beat => beat,
                forbiddenFacts: ["凶手身份"],
                knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]),
            fieldPath,
            displayName + " turns on 凶手身份",
            ["point"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains(
            displayName + " analysis",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains(fieldPath + ".summary", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("emotion_analysis", "emotion")]
    [InlineData("narration_analysis", "narration")]
    [InlineData("character_analysis", "character")]
    [InlineData("reference_analysis", "reference")]
    [InlineData("transition_plan", "transition")]
    public void BuildReviewFailsForbiddenFactInAnalysisTrackPoints(
        string fieldPath,
        string displayName)
    {
        var blueprint = WithAnalysisTrack(
            Blueprint(
                beat => beat,
                forbiddenFacts: ["凶手身份"],
                knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]),
            fieldPath,
            displayName,
            ["turn on 凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains(
            displayName + " analysis point",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains(fieldPath + ".points", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedExecutionContractSummaryFact()
    {
        var blueprint = Blueprint(beat => beat) with
        {
            ExecutionContract = ExecutionContract() with
            {
                Summary = "execution turns on 密室钥匙"
            }
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("unsupported execution contract fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "execution" &&
                defect.FieldPath.Contains("execution_contract.summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInExecutionContractSummary()
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]) with
        {
            ExecutionContract = ExecutionContract() with
            {
                Summary = "execution turns on 凶手身份"
            }
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("execution contract", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("execution_contract.summary", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("paragraph_intentions")]
    [InlineData("execution_modes")]
    [InlineData("anti_screenplay_duties")]
    [InlineData("source_backed_detail_targets")]
    [InlineData("candidate_rejection_rules")]
    public void BuildReviewFailsUnsupportedExecutionContractListFact(string fieldName)
    {
        var blueprint = Blueprint(beat => beat) with
        {
            ExecutionContract = WithExecutionContractList(
                ExecutionContract(),
                fieldName,
                ["turn on 密室钥匙"])
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("unsupported execution contract " + fieldName + " fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "execution" &&
                defect.FieldPath.Contains("execution_contract." + fieldName, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("paragraph_intentions")]
    [InlineData("execution_modes")]
    [InlineData("anti_screenplay_duties")]
    [InlineData("source_backed_detail_targets")]
    [InlineData("candidate_rejection_rules")]
    public void BuildReviewFailsForbiddenFactInExecutionContractLists(string fieldName)
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]) with
        {
            ExecutionContract = WithExecutionContractList(
                ExecutionContract(),
                fieldName,
                ["turn on 凶手身份"])
        };

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("execution contract " + fieldName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("execution_contract." + fieldName, StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedCausalityInFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CausalityIn = "because 密室钥匙 pressure carries over"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CausalityErrors, item => item.Contains("unsupported causality_in fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "causality" &&
                defect.FieldPath.Contains("causality_in", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedCausalityOutFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CausalityOut = "therefore 密室钥匙 consequence forces the next beat"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CausalityErrors, item => item.Contains("unsupported causality_out fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "causality" &&
                defect.FieldPath.Contains("causality_out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedTransitionInFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            TransitionIn = "pressure from 密室钥匙 carries into the doorway"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.TransitionErrors, item => item.Contains("unsupported transition_in fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "transition" &&
                defect.FieldPath.Contains("transition_in", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedTransitionOutFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            TransitionOut = "transition after 密室钥匙 pushes the next consequence"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.TransitionErrors, item => item.Contains("unsupported transition_out fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "transition" &&
                defect.FieldPath.Contains("transition_out", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedExternalEvidenceFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ExternalEvidence = "密室钥匙"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.EmotionErrors, item => item.Contains("unsupported external evidence fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "emotion" &&
                defect.FieldPath.Contains("external_evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsExternalEvidenceFactWhenKnownFactApprovesIt()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                ExternalEvidence = "密室钥匙"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.DoesNotContain(review.EmotionErrors, item => item.Contains("external evidence fact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedEmotionTriggerFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            EmotionTrigger = "密室钥匙"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.EmotionErrors, item => item.Contains("unsupported emotion trigger fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "emotion" &&
                defect.FieldPath.Contains("emotion_trigger", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsEmotionTriggerFactWhenKnownFactApprovesIt()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                EmotionTrigger = "密室钥匙"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.DoesNotContain(review.EmotionErrors, item => item.Contains("emotion trigger fact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedSuppressedReactionFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SuppressedReaction = "密室钥匙"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.EmotionErrors, item => item.Contains("unsupported suppressed reaction fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "emotion" &&
                defect.FieldPath.Contains("suppressed_reaction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsSuppressedReactionFactWhenKnownFactApprovesIt()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SuppressedReaction = "密室钥匙"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.DoesNotContain(review.EmotionErrors, item => item.Contains("suppressed reaction fact", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsBeatScopedForbiddenFactInSceneFacts()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SceneFacts = ["雨声压低了整条街的呼吸", "凶手身份"],
                ForbiddenFacts = ["凶手身份"]
            },
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("beat forbidden fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("scene_facts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInReferenceQuery()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                ReferenceQuery = beat.ReferenceQuery with
                {
                    Query = "凶手身份"
                }
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("reference query", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("reference_query", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInSourceBackedDetailTarget()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SourceBackedDetailTarget = "凶手身份"
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("source-backed detail target", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("source_backed_detail_target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInSensoryAnchorTarget()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SensoryAnchorTarget = "凶手身份"
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("sensory anchor target", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("sensory_anchor_target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInSubtextPlan()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SubtextPlan = "凶手身份"
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("subtext plan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("subtext_plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInExternalEvidence()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                ExternalEvidence = "凶手身份"
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("external evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("external_evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInEmotionTrigger()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                EmotionTrigger = "凶手身份"
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("emotion trigger", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("emotion_trigger", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInSuppressedReaction()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SuppressedReaction = "凶手身份"
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("suppressed reaction", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("suppressed_reaction", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedCharacterStateFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CharacterStatesBefore = ["controlled"],
            CharacterStatesAfter = ["密室钥匙"]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CharacterStateErrors, item => item.Contains("unsupported character state fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "character_state" &&
                defect.FieldPath.Contains("character_states", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInCharacterState()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                CharacterStatesBefore = ["controlled"],
                CharacterStatesAfter = ["凶手身份"]
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("character state", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("character_states", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedCharacterGoalFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CharacterGoals = ["密室钥匙"]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CharacterStateErrors, item => item.Contains("unsupported character goal fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "character_state" &&
                defect.FieldPath.Contains("character_goals", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInCharacterGoal()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                CharacterGoals = ["凶手身份"]
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("character goal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("character_goals", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedCharacterMisbeliefFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CharacterMisbeliefs = ["密室钥匙"]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CharacterStateErrors, item => item.Contains("unsupported character misbelief fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
                defect => defect.Category == "character_state" &&
                defect.FieldPath.Contains("character_misbeliefs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInCharacterMisbelief()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                CharacterMisbeliefs = ["凶手身份"]
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("character misbelief", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
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
    public void BuildReviewFailsUnsupportedRelationshipPressureFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            RelationshipPressure = ["密室钥匙"]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.CharacterStateErrors, item => item.Contains("unsupported relationship pressure fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
                defect => defect.Category == "character_state" &&
                defect.FieldPath.Contains("relationship_pressure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInRelationshipPressure()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                RelationshipPressure = ["凶手身份"]
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("relationship pressure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
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
    public void BuildReviewFailsUnsupportedParagraphIntentionFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ParagraphIntention = "停留在密室钥匙造成的迟疑"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("unsupported paragraph intention fact", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedExecutionModeFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ExecutionMode = "withhold 密室钥匙 until the turn"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("unsupported execution mode fact", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedCandidateRejectionRuleFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            CandidateRejectionRule = "reject if candidate reveals 密室钥匙 before the approved turn"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ExecutionErrors, item => item.Contains("unsupported candidate rejection rule fact", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedAntiScreenplayDutyFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            AntiScreenplayDuty = "show hesitation around 密室钥匙 instead of stage blocking"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ScreenplayDriftRisks, item => item.Contains("unsupported anti-screenplay duty fact", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedNarrationStrategyFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            NarrationStrategy = "close POV withhold 密室钥匙 through tactile detail"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NarrationErrors, item => item.Contains("unsupported narration strategy fact", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedRhythmStrategyFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            RhythmStrategy = "delay the 密室钥匙 reveal with a slow turn"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NarrationErrors, item => item.Contains("unsupported rhythm strategy fact", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedSourceBackedDetailTargetFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SourceBackedDetailTarget = "密室钥匙"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NovelisticNarrationErrors, item => item.Contains("unsupported source-backed detail target fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "novelistic_narration" &&
                defect.FieldPath.Contains("source_backed_detail_target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsSourceBackedDetailTargetFactWhenKnownFactApprovesIt()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SourceBackedDetailTarget = "密室钥匙"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.DoesNotContain(review.NovelisticNarrationErrors, item => item.Contains("source-backed detail target fact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedSensoryAnchorTargetFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SensoryAnchorTarget = "密室钥匙"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NovelisticNarrationErrors, item => item.Contains("unsupported sensory anchor target fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "novelistic_narration" &&
                defect.FieldPath.Contains("sensory_anchor_target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsSensoryAnchorTargetFactWhenKnownFactApprovesIt()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SensoryAnchorTarget = "密室钥匙"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.DoesNotContain(review.NovelisticNarrationErrors, item => item.Contains("sensory anchor target fact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedSubtextPlanFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SubtextPlan = "密室钥匙"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.NovelisticNarrationErrors, item => item.Contains("unsupported subtext plan fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "novelistic_narration" &&
                defect.FieldPath.Contains("subtext_plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsSubtextPlanFactWhenKnownFactApprovesIt()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SubtextPlan = "密室钥匙"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.DoesNotContain(review.NovelisticNarrationErrors, item => item.Contains("subtext plan fact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsGenericSlotPlan()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SlotPlan =
            [
                new ReferenceSlotValuePayload("object", "随便替换一个东西")
            ]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("generic slot plan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("slot_plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedSlotPlanFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SlotPlan =
            [
                new ReferenceSlotValuePayload("object", "密室钥匙")
            ]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("unsupported slot plan fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("slot_plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsSlotPlanFactWhenKnownFactApprovesIt()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SlotPlan =
                [
                    new ReferenceSlotValuePayload("object", "密室钥匙")
                ]
            },
            knownFacts: ["雨声压低了整条街的呼吸", "密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.DoesNotContain(review.ReferenceBindingErrors, item => item.Contains("slot plan fact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInSlotPlan()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                SlotPlan =
                [
                    new ReferenceSlotValuePayload("object", "凶手身份")
                ]
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("slot plan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("slot_plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedNoReuseReasonFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            NoReuseReason = "transition carries 密室钥匙 without reusable source"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("unsupported no_reuse_reason fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("no_reuse_reason", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInNoReuseReason()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                NoReuseReason = "transition carries 凶手身份 without reusable source"
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("no_reuse_reason", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("no_reuse_reason", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsForbiddenFactInViewpointAllowedKnowledge()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                ViewpointAllowedKnowledge = ["雨声压低了整条街的呼吸", "凶手身份"]
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("viewpoint allowed knowledge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("viewpoint_allowed_knowledge", StringComparison.OrdinalIgnoreCase));
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
    public void BuildReviewFailsUnsupportedMaxRewriteLevel()
    {
        var blueprint = Blueprint(beat => beat with
        {
            MaxRewriteLevel = "LX"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("unsupported max_rewrite_level", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("max_rewrite_level", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedRequiredMaterialType()
    {
        var blueprint = Blueprint(beat => beat with
        {
            RequiredMaterialTypes = ["invalid"]
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("unsupported required_material_types", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("required_material_types", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedReferenceQueryMaterialType()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ReferenceQuery = beat.ReferenceQuery with
            {
                MaterialTypes = ["invalid"]
            }
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("unsupported reference_query.material_types", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("reference_query.material_types", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void BuildReviewFailsUnsupportedReferenceQueryFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ReferenceQuery = beat.ReferenceQuery with
            {
                Query = "密室钥匙"
            }
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("unsupported reference query fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("reference_query", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewAllowsReferenceQueryFactWhenKnownFactApprovesIt()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                ReferenceQuery = beat.ReferenceQuery with
                {
                    Query = "密室钥匙"
                }
            },
            knownFacts: ["雨声压低了整条街的呼吸", "密室钥匙"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Passed, review.Status);
        Assert.DoesNotContain(review.ReferenceBindingErrors, item => item.Contains("reference query fact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsUnsupportedLockedPhrasePolicyFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            LockedPhrasePolicy = "preserve cadence around 密室钥匙"
        });

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ReferenceBindingErrors, item => item.Contains("unsupported locked_phrase_policy fact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "reference_binding" &&
                defect.FieldPath.Contains("locked_phrase_policy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildReviewFailsForbiddenFactInLockedPhrasePolicy()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                LockedPhrasePolicy = "preserve cadence around 凶手身份"
            },
            forbiddenFacts: ["凶手身份"],
            knownFacts: ["雨声压低了整条街的呼吸", "凶手身份"]);

        var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);

        Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
        Assert.Contains(review.ForbiddenFactErrors, item => item.Contains("locked_phrase_policy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Defects,
            defect => defect.Category == "forbidden_fact" &&
                defect.FieldPath.Contains("locked_phrase_policy", StringComparison.OrdinalIgnoreCase));
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

    private static ReferenceChapterBlueprintPayload WithAnalysisTrack(
        ReferenceChapterBlueprintPayload blueprint,
        string fieldPath,
        string summary,
        IReadOnlyList<string> points)
    {
        var track = new ReferenceChapterBlueprintAnalysisTrackPayload(
            fieldPath.Replace("_analysis", "", StringComparison.Ordinal).Replace("_plan", "", StringComparison.Ordinal),
            summary,
            points);
        return fieldPath switch
        {
            "emotion_analysis" => blueprint with { EmotionAnalysis = track },
            "narration_analysis" => blueprint with { NarrationAnalysis = track },
            "character_analysis" => blueprint with { CharacterAnalysis = track },
            "reference_analysis" => blueprint with { ReferenceAnalysis = track },
            "transition_plan" => blueprint with { TransitionPlan = track },
            _ => throw new ArgumentException("Unsupported analysis track.", nameof(fieldPath))
        };
    }

    private static IReadOnlyList<string> ReviewMessagesForCategory(
        ReferenceChapterBlueprintReviewPayload review,
        string category)
    {
        return category switch
        {
            "emotion" => review.EmotionErrors,
            "narration" => review.NarrationErrors,
            "character_state" => review.CharacterStateErrors,
            "reference_binding" => review.ReferenceBindingErrors,
            "transition" => review.TransitionErrors,
            _ => throw new ArgumentException("Unsupported review category.", nameof(category))
        };
    }

    private static ReferenceChapterBlueprintExecutionTrackPayload ExecutionContract()
    {
        return new ReferenceChapterBlueprintExecutionTrackPayload(
            "execution",
            "execution",
            ["intention"],
            ["dwell"],
            ["anti-screenplay"],
            ["detail"],
            ["reject"]);
    }

    private static ReferenceChapterBlueprintExecutionTrackPayload WithExecutionContractList(
        ReferenceChapterBlueprintExecutionTrackPayload contract,
        string fieldName,
        IReadOnlyList<string> values)
    {
        return fieldName switch
        {
            "paragraph_intentions" => contract with { ParagraphIntentions = values },
            "execution_modes" => contract with { ExecutionModes = values },
            "anti_screenplay_duties" => contract with { AntiScreenplayDuties = values },
            "source_backed_detail_targets" => contract with { SourceBackedDetailTargets = values },
            "candidate_rejection_rules" => contract with { CandidateRejectionRules = values },
            _ => throw new ArgumentException("Unsupported execution contract field.", nameof(fieldName))
        };
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
