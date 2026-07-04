using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceRegressionFixtureTests
{
    [Fact]
    public void BlueprintRegressionFixturesFailReview()
    {
        using var fixtures = LoadFixtures();

        foreach (var fixture in fixtures.RootElement.GetProperty("blueprints").EnumerateArray())
        {
            var name = fixture.GetProperty("name").GetString() ?? "<unnamed>";
            var mutation = fixture.GetProperty("mutation").GetString() ?? string.Empty;
            var expected = fixture.GetProperty("expected_error").GetString() ?? string.Empty;
            var blueprint = Blueprint(
                beat => ApplyBlueprintMutation(beat, mutation),
                knownFacts: KnownFactsForMutation(mutation),
                finalHook: FinalHookForMutation(mutation));

            var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UnixEpoch);
            var messages = AllReviewMessages(review).ToArray();

            Assert.Equal(ReferenceBlueprintReviewStatuses.Failed, review.Status);
            Assert.True(
                messages.Any(message => message.Contains(expected, StringComparison.OrdinalIgnoreCase)),
                $"{name} expected review error containing '{expected}', got: {string.Join(" | ", messages)}");
        }
    }

    [Fact]
    public void DraftCandidateRegressionFixturesFailAudit()
    {
        using var fixtures = LoadFixtures();

        foreach (var fixture in fixtures.RootElement.GetProperty("draft_candidates").EnumerateArray())
        {
            var name = fixture.GetProperty("name").GetString() ?? "<unnamed>";
            var mutation = fixture.GetProperty("mutation").GetString() ?? string.Empty;
            var expected = fixture.GetProperty("expected_error").GetString() ?? string.Empty;
            var text = fixture.GetProperty("text").GetString() ?? string.Empty;
            var blueprint = Blueprint(beat => ApplyDraftMutation(beat, mutation));
            var candidate = Candidate(blueprint, text);

            var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(blueprint, [candidate], DateTimeOffset.UnixEpoch);
            var messages = AllDraftAuditMessages(audit).ToArray();

            Assert.Equal("failed", audit.Status);
            Assert.True(
                messages.Any(message => message.Contains(expected, StringComparison.OrdinalIgnoreCase)),
                $"{name} expected audit error containing '{expected}', got: {string.Join(" | ", messages)}");
        }
    }

    private static JsonDocument LoadFixtures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference-regressions.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static ReferenceChapterBlueprintBeatPayload ApplyBlueprintMutation(
        ReferenceChapterBlueprintBeatPayload beat,
        string mutation)
    {
        return mutation switch
        {
            "fake_emotion" => beat with
            {
                EmotionBefore = "克制",
                EmotionAfter = "崩溃",
                EmotionTrigger = "剧情需要",
                SuppressedReaction = "有反应",
                ExternalEvidence = "表现出痛苦"
            },
            "hard_transition" => beat with
            {
                TransitionIn = "来到旧宅",
                TransitionOut = "第二天转到仓库"
            },
            "pov_leak" => beat with
            {
                ViewpointAllowedKnowledge = ["雨声压低了整条街的呼吸", "周鸣是卧底"]
            },
            "missing_prose_duty" => beat with
            {
                ProseDuties = []
            },
            "action_dialogue_only" => beat with
            {
                BeatType = ReferenceBlueprintBeatTypes.Action,
                ProseDuties = ["action", "dialogue"]
            },
            "missing_character_state_delta" => beat with
            {
                CharacterStatesBefore = ["controlled"],
                CharacterStatesAfter = ["controlled"]
            },
            "missing_character_misbeliefs" => beat with
            {
                CharacterMisbeliefs = []
            },
            "missing_relationship_pressure" => beat with
            {
                RelationshipPressure = []
            },
            "generic_paragraph_intention" => beat with
            {
                ParagraphIntention = "写得更好，更有代入感"
            },
            "generic_narration_strategy" => beat with
            {
                NarrationStrategy = "正常叙述，写得有画面感"
            },
            "unsupported_final_hook" => beat,
            "unsupported_scene_fact" => beat with
            {
                SceneFacts = ["雨声压低了整条街的呼吸", "周鸣其实是卧底"]
            },
            "pov_forbidden_scene_fact" => beat with
            {
                SceneFacts = ["雨声压低了整条街的呼吸", "周鸣是卧底"],
                ViewpointForbiddenKnowledge = ["周鸣是卧底"]
            },
            "material_mismatch" => beat with
            {
                ReferenceQuery = beat.ReferenceQuery with
                {
                    FunctionTags = ["dialogue"],
                    EmotionTags = ["triumph"],
                    PovTags = ["omniscient"],
                    TechniqueTags = []
                }
            },
            _ => beat
        };
    }

    private static string FinalHookForMutation(string mutation)
    {
        return mutation switch
        {
            "unsupported_final_hook" => "周鸣其实是卧底",
            "pov_forbidden_scene_fact" => "雨声仍在门外压低呼吸",
            _ => "hook"
        };
    }

    private static IReadOnlyList<string> KnownFactsForMutation(string mutation)
    {
        return mutation switch
        {
            "pov_forbidden_scene_fact" => ["雨声压低了整条街的呼吸", "周鸣是卧底"],
            _ => ["雨声压低了整条街的呼吸"]
        };
    }

    private static ReferenceChapterBlueprintBeatPayload ApplyDraftMutation(
        ReferenceChapterBlueprintBeatPayload beat,
        string mutation)
    {
        return mutation switch
        {
            "action_only" => beat with
            {
                ProseDuties = ["interiority", "external_evidence", "transition"],
                AntiScreenplayDuty = "show pressure beyond action"
            },
            "forbidden_fact" => beat with
            {
                ForbiddenFacts = ["凶手身份"]
            },
            "non_pov_character" => beat with
            {
                PovCharacter = "林岚",
                CharacterStatesBefore = ["林岚 controlled", "周鸣 guarded"]
            },
            "limited_pov" => beat with
            {
                PovCharacter = "林岚",
                NarrativeDistance = "limited"
            },
            "required_subtext" => beat with
            {
                SubtextPlan = "required: 没有回答"
            },
            _ => beat
        };
    }

    private static IEnumerable<string> AllReviewMessages(ReferenceChapterBlueprintReviewPayload review)
    {
        return review.LogicErrors
            .Concat(review.CausalityErrors)
            .Concat(review.EmotionErrors)
            .Concat(review.NarrationErrors)
            .Concat(review.ExecutionErrors)
            .Concat(review.CharacterStateErrors)
            .Concat(review.PovErrors)
            .Concat(review.ContinuityErrors)
            .Concat(review.TransitionErrors)
            .Concat(review.ForbiddenFactErrors)
            .Concat(review.ReferenceBindingErrors)
            .Concat(review.MaterialFitErrors)
            .Concat(review.ScreenplayDriftRisks)
            .Concat(review.AiProseRisks)
            .Concat(review.NovelisticNarrationErrors)
            .Concat(review.RequiredFixes);
    }

    private static IEnumerable<string> AllDraftAuditMessages(ReferenceAnchoredDraftAuditPayload audit)
    {
        return audit.ProvenanceErrors
            .Concat(audit.BlueprintErrors)
            .Concat(audit.UnsupportedFactErrors)
            .Concat(audit.PovErrors)
            .Concat(audit.AiProseRisks)
            .Concat(audit.RequiredFixes);
    }

    private static ReferenceChapterBlueprintPayload Blueprint(
        Func<ReferenceChapterBlueprintBeatPayload, ReferenceChapterBlueprintBeatPayload> configureBeat,
        IReadOnlyList<string>? knownFacts = null,
        string finalHook = "hook")
    {
        var beat = configureBeat(Beat("1:beat:1"));
        return new ReferenceChapterBlueprintPayload(
            1,
            10,
            1,
            "测试蓝图",
            ReferenceBlueprintStates.MaterialBound,
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
            new ReferenceChapterBlueprintExecutionTrackPayload(
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
            [],
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
            ["凶手身份"],
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

    private static ReferenceDraftParagraphCandidatePayload Candidate(
        ReferenceChapterBlueprintPayload blueprint,
        string text)
    {
        return new ReferenceDraftParagraphCandidatePayload(
            "candidate-1",
            blueprint.BlueprintId,
            blueprint.Beats[0].BeatId,
            "material-1",
            ReferenceRewriteLevels.L0,
            text,
            [],
            [],
            "passed",
            DateTimeOffset.UnixEpoch);
    }
}
