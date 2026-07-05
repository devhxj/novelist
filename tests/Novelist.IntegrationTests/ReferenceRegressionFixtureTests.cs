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
                forbiddenFacts: ForbiddenFactsForMutation(mutation),
                finalHook: FinalHookForMutation(mutation),
                chapterFunction: ChapterFunctionForMutation(mutation),
                previousState: PreviousStateForMutation(mutation),
                finalState: FinalStateForMutation(mutation),
                logicAnalysisSummary: LogicAnalysisSummaryForMutation(mutation),
                logicAnalysisPoints: LogicAnalysisPointsForMutation(mutation),
                analysisTrackMutation: AnalysisTrackForMutation(mutation),
                executionContractMutation: ExecutionContractForMutation(mutation));

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
            var blueprint = Blueprint(
                beat => ApplyDraftMutation(beat, mutation),
                knownFacts: KnownFactsForMutation(mutation));
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
            "unsupported_external_evidence_fact" => beat with
            {
                ExternalEvidence = "密室钥匙"
            },
            "forbidden_external_evidence_fact" => beat with
            {
                ExternalEvidence = "凶手身份"
            },
            "unsupported_emotion_trigger_fact" => beat with
            {
                EmotionTrigger = "密室钥匙"
            },
            "forbidden_emotion_trigger_fact" => beat with
            {
                EmotionTrigger = "凶手身份"
            },
            "unsupported_suppressed_reaction_fact" => beat with
            {
                SuppressedReaction = "密室钥匙"
            },
            "forbidden_suppressed_reaction_fact" => beat with
            {
                SuppressedReaction = "凶手身份"
            },
            "hard_transition" => beat with
            {
                TransitionIn = "来到旧宅",
                TransitionOut = "第二天转到仓库"
            },
            "unsupported_causality_in_fact" => beat with
            {
                CausalityIn = "because 密室钥匙 pressure carries over"
            },
            "unsupported_causality_out_fact" => beat with
            {
                CausalityOut = "therefore 密室钥匙 consequence forces the next beat"
            },
            "unsupported_transition_in_fact" => beat with
            {
                TransitionIn = "pressure from 密室钥匙 carries into the doorway"
            },
            "unsupported_transition_out_fact" => beat with
            {
                TransitionOut = "transition after 密室钥匙 pushes the next consequence"
            },
            "pov_leak" => beat with
            {
                ViewpointAllowedKnowledge = ["雨声压低了整条街的呼吸", "周鸣是卧底"]
            },
            "forbidden_viewpoint_allowed_knowledge_fact" => beat with
            {
                ViewpointAllowedKnowledge = ["雨声压低了整条街的呼吸", "凶手身份"]
            },
            "beat_forbidden_scene_fact" => beat with
            {
                SceneFacts = ["雨声压低了整条街的呼吸", "凶手身份"],
                ForbiddenFacts = ["凶手身份"]
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
            "unsupported_character_state_fact" => beat with
            {
                CharacterStatesBefore = ["controlled"],
                CharacterStatesAfter = ["密室钥匙"]
            },
            "forbidden_character_state_fact" => beat with
            {
                CharacterStatesBefore = ["controlled"],
                CharacterStatesAfter = ["凶手身份"]
            },
            "unsupported_character_goal_fact" => beat with
            {
                CharacterGoals = ["密室钥匙"]
            },
            "forbidden_character_goal_fact" => beat with
            {
                CharacterGoals = ["凶手身份"]
            },
            "unsupported_character_misbelief_fact" => beat with
            {
                CharacterMisbeliefs = ["密室钥匙"]
            },
            "forbidden_character_misbelief_fact" => beat with
            {
                CharacterMisbeliefs = ["凶手身份"]
            },
            "missing_relationship_pressure" => beat with
            {
                RelationshipPressure = []
            },
            "unsupported_relationship_pressure_fact" => beat with
            {
                RelationshipPressure = ["密室钥匙"]
            },
            "forbidden_relationship_pressure_fact" => beat with
            {
                RelationshipPressure = ["凶手身份"]
            },
            "unsupported_narrative_function_fact" => beat with
            {
                NarrativeFunction = "delay the 密室钥匙 reveal through pressure"
            },
            "unsupported_logic_premise_fact" => beat with
            {
                LogicPremise = "密室钥匙 changes the choice"
            },
            "unsupported_conflict_pressure_fact" => beat with
            {
                ConflictPressure = "密室钥匙 forces a choice"
            },
            "unsupported_chapter_function_fact" => beat,
            "forbidden_chapter_function_fact" => beat,
            "unsupported_previous_state_fact" => beat,
            "unsupported_final_state_fact" => beat,
            "forbidden_previous_state_fact" => beat,
            "forbidden_final_state_fact" => beat,
            "unsupported_logic_analysis_summary_fact" => beat,
            "forbidden_logic_analysis_summary_fact" => beat,
            "unsupported_logic_analysis_point_fact" => beat,
            "forbidden_logic_analysis_point_fact" => beat,
            "unsupported_emotion_analysis_summary_fact" => beat,
            "unsupported_emotion_analysis_point_fact" => beat,
            "forbidden_emotion_analysis_summary_fact" => beat,
            "forbidden_emotion_analysis_point_fact" => beat,
            "unsupported_narration_analysis_summary_fact" => beat,
            "unsupported_narration_analysis_point_fact" => beat,
            "forbidden_narration_analysis_summary_fact" => beat,
            "forbidden_narration_analysis_point_fact" => beat,
            "unsupported_character_analysis_summary_fact" => beat,
            "unsupported_character_analysis_point_fact" => beat,
            "forbidden_character_analysis_summary_fact" => beat,
            "forbidden_character_analysis_point_fact" => beat,
            "unsupported_reference_analysis_summary_fact" => beat,
            "unsupported_reference_analysis_point_fact" => beat,
            "forbidden_reference_analysis_summary_fact" => beat,
            "forbidden_reference_analysis_point_fact" => beat,
            "unsupported_transition_plan_summary_fact" => beat,
            "unsupported_transition_plan_point_fact" => beat,
            "forbidden_transition_plan_summary_fact" => beat,
            "forbidden_transition_plan_point_fact" => beat,
            "unsupported_execution_contract_summary_fact" => beat,
            "forbidden_execution_contract_summary_fact" => beat,
            "unsupported_execution_contract_paragraph_intentions_fact" => beat,
            "forbidden_execution_contract_paragraph_intentions_fact" => beat,
            "unsupported_execution_contract_execution_modes_fact" => beat,
            "forbidden_execution_contract_execution_modes_fact" => beat,
            "unsupported_execution_contract_anti_screenplay_duties_fact" => beat,
            "forbidden_execution_contract_anti_screenplay_duties_fact" => beat,
            "unsupported_execution_contract_source_backed_detail_targets_fact" => beat,
            "forbidden_execution_contract_source_backed_detail_targets_fact" => beat,
            "unsupported_execution_contract_candidate_rejection_rules_fact" => beat,
            "forbidden_execution_contract_candidate_rejection_rules_fact" => beat,
            "generic_paragraph_intention" => beat with
            {
                ParagraphIntention = "写得更好，更有代入感"
            },
            "unsupported_paragraph_intention_fact" => beat with
            {
                ParagraphIntention = "停留在密室钥匙造成的迟疑"
            },
            "generic_execution_mode" => beat with
            {
                ExecutionMode = "正常写，自然展开"
            },
            "unsupported_execution_mode_fact" => beat with
            {
                ExecutionMode = "withhold 密室钥匙 until the turn"
            },
            "generic_candidate_rejection_rule" => beat with
            {
                CandidateRejectionRule = "不好的不要，质量差就拒绝"
            },
            "unsupported_candidate_rejection_rule_fact" => beat with
            {
                CandidateRejectionRule = "reject if candidate reveals 密室钥匙 before the approved turn"
            },
            "generic_anti_screenplay_duty" => beat with
            {
                AntiScreenplayDuty = "避免剧本化"
            },
            "unsupported_anti_screenplay_duty_fact" => beat with
            {
                AntiScreenplayDuty = "show hesitation around 密室钥匙 instead of stage blocking"
            },
            "generic_narration_strategy" => beat with
            {
                NarrationStrategy = "正常叙述，写得有画面感"
            },
            "unsupported_narration_strategy_fact" => beat with
            {
                NarrationStrategy = "close POV withhold 密室钥匙 through tactile detail"
            },
            "generic_rhythm_strategy" => beat with
            {
                RhythmStrategy = "节奏自然流畅，快慢结合"
            },
            "unsupported_rhythm_strategy_fact" => beat with
            {
                RhythmStrategy = "delay the 密室钥匙 reveal with a slow turn"
            },
            "generic_source_backed_detail_target" => beat with
            {
                SourceBackedDetailTarget = "加一点细节，让画面更丰富"
            },
            "unsupported_source_backed_detail_target_fact" => beat with
            {
                SourceBackedDetailTarget = "密室钥匙"
            },
            "forbidden_source_backed_detail_target_fact" => beat with
            {
                SourceBackedDetailTarget = "凶手身份"
            },
            "unsupported_sensory_anchor_target_fact" => beat with
            {
                SensoryAnchorTarget = "密室钥匙"
            },
            "forbidden_sensory_anchor_target_fact" => beat with
            {
                SensoryAnchorTarget = "凶手身份"
            },
            "unsupported_subtext_plan_fact" => beat with
            {
                SubtextPlan = "密室钥匙"
            },
            "forbidden_subtext_plan_fact" => beat with
            {
                SubtextPlan = "凶手身份"
            },
            "generic_slot_plan" => beat with
            {
                SlotPlan = [new ReferenceSlotValuePayload("object", "随便替换一个东西")]
            },
            "unsupported_slot_plan_fact" => beat with
            {
                SlotPlan = [new ReferenceSlotValuePayload("object", "密室钥匙")]
            },
            "forbidden_slot_plan_fact" => beat with
            {
                SlotPlan = [new ReferenceSlotValuePayload("object", "凶手身份")]
            },
            "unsupported_no_reuse_reason_fact" => beat with
            {
                NoReuseReason = "transition carries 密室钥匙 without reusable source"
            },
            "forbidden_no_reuse_reason_fact" => beat with
            {
                NoReuseReason = "transition carries 凶手身份 without reusable source"
            },
            "unsupported_locked_phrase_policy_fact" => beat with
            {
                LockedPhrasePolicy = "preserve cadence around 密室钥匙"
            },
            "forbidden_locked_phrase_policy_fact" => beat with
            {
                LockedPhrasePolicy = "preserve cadence around 凶手身份"
            },
            "unsupported_reference_query_fact" => beat with
            {
                ReferenceQuery = beat.ReferenceQuery with
                {
                    Query = "密室钥匙"
                }
            },
            "forbidden_reference_query_fact" => beat with
            {
                ReferenceQuery = beat.ReferenceQuery with
                {
                    Query = "凶手身份"
                }
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

    private static string ChapterFunctionForMutation(string mutation)
    {
        return mutation switch
        {
            "unsupported_chapter_function_fact" => "turn pressure around 密室钥匙",
            "forbidden_chapter_function_fact" => "turn pressure around 凶手身份",
            _ => "雨夜压力"
        };
    }

    private static string PreviousStateForMutation(string mutation)
    {
        return mutation switch
        {
            "unsupported_previous_state_fact" => "pressure from 密室钥匙 remains unresolved",
            "forbidden_previous_state_fact" => "pressure from 凶手身份 remains unresolved",
            _ => "previous"
        };
    }

    private static string FinalStateForMutation(string mutation)
    {
        return mutation switch
        {
            "unsupported_final_state_fact" => "林岚 chooses around 密室钥匙",
            "forbidden_final_state_fact" => "林岚 chooses around 凶手身份",
            _ => "final"
        };
    }

    private static string LogicAnalysisSummaryForMutation(string mutation)
    {
        return mutation switch
        {
            "unsupported_logic_analysis_summary_fact" => "logic turns on 密室钥匙",
            "forbidden_logic_analysis_summary_fact" => "logic turns on 凶手身份",
            _ => "logic"
        };
    }

    private static IReadOnlyList<string> LogicAnalysisPointsForMutation(string mutation)
    {
        return mutation switch
        {
            "unsupported_logic_analysis_point_fact" => ["turn on 密室钥匙"],
            "forbidden_logic_analysis_point_fact" => ["turn on 凶手身份"],
            _ => ["point"]
        };
    }

    private static AnalysisTrackMutation? AnalysisTrackForMutation(string mutation)
    {
        return mutation switch
        {
            "unsupported_emotion_analysis_summary_fact" => new AnalysisTrackMutation(
                "emotion_analysis",
                "emotion",
                "emotion turns on 密室钥匙",
                ["point"]),
            "unsupported_emotion_analysis_point_fact" => new AnalysisTrackMutation(
                "emotion_analysis",
                "emotion",
                "emotion",
                ["turn on 密室钥匙"]),
            "forbidden_emotion_analysis_summary_fact" => new AnalysisTrackMutation(
                "emotion_analysis",
                "emotion",
                "emotion turns on 凶手身份",
                ["point"]),
            "forbidden_emotion_analysis_point_fact" => new AnalysisTrackMutation(
                "emotion_analysis",
                "emotion",
                "emotion",
                ["turn on 凶手身份"]),
            "unsupported_narration_analysis_summary_fact" => new AnalysisTrackMutation(
                "narration_analysis",
                "narration",
                "narration turns on 密室钥匙",
                ["point"]),
            "unsupported_narration_analysis_point_fact" => new AnalysisTrackMutation(
                "narration_analysis",
                "narration",
                "narration",
                ["turn on 密室钥匙"]),
            "forbidden_narration_analysis_summary_fact" => new AnalysisTrackMutation(
                "narration_analysis",
                "narration",
                "narration turns on 凶手身份",
                ["point"]),
            "forbidden_narration_analysis_point_fact" => new AnalysisTrackMutation(
                "narration_analysis",
                "narration",
                "narration",
                ["turn on 凶手身份"]),
            "unsupported_character_analysis_summary_fact" => new AnalysisTrackMutation(
                "character_analysis",
                "character",
                "character turns on 密室钥匙",
                ["point"]),
            "unsupported_character_analysis_point_fact" => new AnalysisTrackMutation(
                "character_analysis",
                "character",
                "character",
                ["turn on 密室钥匙"]),
            "forbidden_character_analysis_summary_fact" => new AnalysisTrackMutation(
                "character_analysis",
                "character",
                "character turns on 凶手身份",
                ["point"]),
            "forbidden_character_analysis_point_fact" => new AnalysisTrackMutation(
                "character_analysis",
                "character",
                "character",
                ["turn on 凶手身份"]),
            "unsupported_reference_analysis_summary_fact" => new AnalysisTrackMutation(
                "reference_analysis",
                "reference",
                "reference turns on 密室钥匙",
                ["point"]),
            "unsupported_reference_analysis_point_fact" => new AnalysisTrackMutation(
                "reference_analysis",
                "reference",
                "reference",
                ["turn on 密室钥匙"]),
            "forbidden_reference_analysis_summary_fact" => new AnalysisTrackMutation(
                "reference_analysis",
                "reference",
                "reference turns on 凶手身份",
                ["point"]),
            "forbidden_reference_analysis_point_fact" => new AnalysisTrackMutation(
                "reference_analysis",
                "reference",
                "reference",
                ["turn on 凶手身份"]),
            "unsupported_transition_plan_summary_fact" => new AnalysisTrackMutation(
                "transition_plan",
                "transition",
                "transition turns on 密室钥匙",
                ["point"]),
            "unsupported_transition_plan_point_fact" => new AnalysisTrackMutation(
                "transition_plan",
                "transition",
                "transition",
                ["turn on 密室钥匙"]),
            "forbidden_transition_plan_summary_fact" => new AnalysisTrackMutation(
                "transition_plan",
                "transition",
                "transition turns on 凶手身份",
                ["point"]),
            "forbidden_transition_plan_point_fact" => new AnalysisTrackMutation(
                "transition_plan",
                "transition",
                "transition",
                ["turn on 凶手身份"]),
            _ => null
        };
    }

    private static ExecutionContractMutation? ExecutionContractForMutation(string mutation)
    {
        return mutation switch
        {
            "unsupported_execution_contract_summary_fact" => new ExecutionContractMutation("summary", ["execution turns on 密室钥匙"]),
            "forbidden_execution_contract_summary_fact" => new ExecutionContractMutation("summary", ["execution turns on 凶手身份"]),
            "unsupported_execution_contract_paragraph_intentions_fact" => new ExecutionContractMutation("paragraph_intentions", ["turn on 密室钥匙"]),
            "forbidden_execution_contract_paragraph_intentions_fact" => new ExecutionContractMutation("paragraph_intentions", ["turn on 凶手身份"]),
            "unsupported_execution_contract_execution_modes_fact" => new ExecutionContractMutation("execution_modes", ["turn on 密室钥匙"]),
            "forbidden_execution_contract_execution_modes_fact" => new ExecutionContractMutation("execution_modes", ["turn on 凶手身份"]),
            "unsupported_execution_contract_anti_screenplay_duties_fact" => new ExecutionContractMutation("anti_screenplay_duties", ["turn on 密室钥匙"]),
            "forbidden_execution_contract_anti_screenplay_duties_fact" => new ExecutionContractMutation("anti_screenplay_duties", ["turn on 凶手身份"]),
            "unsupported_execution_contract_source_backed_detail_targets_fact" => new ExecutionContractMutation("source_backed_detail_targets", ["turn on 密室钥匙"]),
            "forbidden_execution_contract_source_backed_detail_targets_fact" => new ExecutionContractMutation("source_backed_detail_targets", ["turn on 凶手身份"]),
            "unsupported_execution_contract_candidate_rejection_rules_fact" => new ExecutionContractMutation("candidate_rejection_rules", ["turn on 密室钥匙"]),
            "forbidden_execution_contract_candidate_rejection_rules_fact" => new ExecutionContractMutation("candidate_rejection_rules", ["turn on 凶手身份"]),
            _ => null
        };
    }

    private static IReadOnlyList<string> KnownFactsForMutation(string mutation)
    {
        if (IsForbiddenAnalysisTrackMutation(mutation) || IsForbiddenExecutionContractMutation(mutation))
        {
            return ["雨声压低了整条街的呼吸", "凶手身份"];
        }

        return mutation switch
        {
            "pov_forbidden_scene_fact" => ["雨声压低了整条街的呼吸", "周鸣是卧底"],
            "beat_forbidden_scene_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_viewpoint_allowed_knowledge_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_character_state_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_character_goal_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_character_misbelief_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_relationship_pressure_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_chapter_function_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_previous_state_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_final_state_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_logic_analysis_summary_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_logic_analysis_point_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_emotion_trigger_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_suppressed_reaction_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_external_evidence_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_reference_query_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_slot_plan_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_no_reuse_reason_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_locked_phrase_policy_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_source_backed_detail_target_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_sensory_anchor_target_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "forbidden_subtext_plan_fact" => ["雨声压低了整条街的呼吸", "凶手身份"],
            "limited_pov_hidden_position" => ["雨声压低了整条街的呼吸", "周鸣在门后"],
            "limited_pov_offstage_fact" => ["雨声压低了整条街的呼吸", "周鸣握住那把钥匙", "那把钥匙"],
            "limited_pov_barrier_offstage_action" => ["雨声压低了整条街的呼吸", "周鸣按住门把"],
            _ => ["雨声压低了整条街的呼吸"]
        };
    }

    private static IReadOnlyList<string> ForbiddenFactsForMutation(string mutation)
    {
        if (IsForbiddenAnalysisTrackMutation(mutation) || IsForbiddenExecutionContractMutation(mutation))
        {
            return ["凶手身份"];
        }

        return mutation switch
        {
            "forbidden_viewpoint_allowed_knowledge_fact" => ["凶手身份"],
            "forbidden_character_state_fact" => ["凶手身份"],
            "forbidden_character_goal_fact" => ["凶手身份"],
            "forbidden_character_misbelief_fact" => ["凶手身份"],
            "forbidden_relationship_pressure_fact" => ["凶手身份"],
            "forbidden_chapter_function_fact" => ["凶手身份"],
            "forbidden_previous_state_fact" => ["凶手身份"],
            "forbidden_final_state_fact" => ["凶手身份"],
            "forbidden_logic_analysis_summary_fact" => ["凶手身份"],
            "forbidden_logic_analysis_point_fact" => ["凶手身份"],
            "forbidden_emotion_trigger_fact" => ["凶手身份"],
            "forbidden_suppressed_reaction_fact" => ["凶手身份"],
            "forbidden_external_evidence_fact" => ["凶手身份"],
            "forbidden_reference_query_fact" => ["凶手身份"],
            "forbidden_slot_plan_fact" => ["凶手身份"],
            "forbidden_no_reuse_reason_fact" => ["凶手身份"],
            "forbidden_locked_phrase_policy_fact" => ["凶手身份"],
            "forbidden_source_backed_detail_target_fact" => ["凶手身份"],
            "forbidden_sensory_anchor_target_fact" => ["凶手身份"],
            "forbidden_subtext_plan_fact" => ["凶手身份"],
            _ => []
        };
    }

    private static bool IsForbiddenAnalysisTrackMutation(string mutation)
    {
        return mutation.StartsWith("forbidden_", StringComparison.Ordinal) &&
            (mutation.Contains("_analysis_", StringComparison.Ordinal) ||
                mutation.Contains("transition_plan_", StringComparison.Ordinal));
    }

    private static bool IsForbiddenExecutionContractMutation(string mutation)
    {
        return mutation.StartsWith("forbidden_execution_contract_", StringComparison.Ordinal);
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
            "limited_pov_offstage_fact" => beat with
            {
                PovCharacter = "林岚",
                NarrativeDistance = "limited"
            },
            "limited_pov_hidden_position" => beat with
            {
                PovCharacter = "林岚",
                NarrativeDistance = "limited"
            },
            "limited_pov_barrier_offstage_action" => beat with
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
        IReadOnlyList<string>? forbiddenFacts = null,
        string finalHook = "hook",
        string chapterFunction = "雨夜压力",
        string previousState = "previous",
        string finalState = "final",
        string logicAnalysisSummary = "logic",
        IReadOnlyList<string>? logicAnalysisPoints = null,
        AnalysisTrackMutation? analysisTrackMutation = null,
        ExecutionContractMutation? executionContractMutation = null)
    {
        var beat = configureBeat(Beat("1:beat:1"));
        var logicTrack = new ReferenceChapterBlueprintAnalysisTrackPayload(
            "logic",
            logicAnalysisSummary,
            logicAnalysisPoints ?? ["point"]);
        var emotionTrack = new ReferenceChapterBlueprintAnalysisTrackPayload("emotion", "emotion", ["point"]);
        var narrationTrack = new ReferenceChapterBlueprintAnalysisTrackPayload("narration", "narration", ["point"]);
        var characterTrack = new ReferenceChapterBlueprintAnalysisTrackPayload("character", "character", ["point"]);
        var referenceTrack = new ReferenceChapterBlueprintAnalysisTrackPayload("reference", "reference", ["point"]);
        var transitionTrack = new ReferenceChapterBlueprintAnalysisTrackPayload("transition", "transition", ["point"]);
        if (analysisTrackMutation is not null)
        {
            var track = new ReferenceChapterBlueprintAnalysisTrackPayload(
                analysisTrackMutation.Track,
                analysisTrackMutation.Summary,
                analysisTrackMutation.Points);
            switch (analysisTrackMutation.FieldPath)
            {
                case "emotion_analysis":
                    emotionTrack = track;
                    break;
                case "narration_analysis":
                    narrationTrack = track;
                    break;
                case "character_analysis":
                    characterTrack = track;
                    break;
                case "reference_analysis":
                    referenceTrack = track;
                    break;
                case "transition_plan":
                    transitionTrack = track;
                    break;
                default:
                    throw new ArgumentException("Unsupported analysis track mutation.", nameof(analysisTrackMutation));
            }
        }

        var executionContract = new ReferenceChapterBlueprintExecutionTrackPayload(
            "execution",
            "execution",
            ["intention"],
            ["dwell"],
            ["anti-screenplay"],
            ["detail"],
            ["reject"]);
        if (executionContractMutation is not null)
        {
            executionContract = executionContractMutation.FieldName switch
            {
                "summary" => executionContract with { Summary = executionContractMutation.Values[0] },
                "paragraph_intentions" => executionContract with { ParagraphIntentions = executionContractMutation.Values },
                "execution_modes" => executionContract with { ExecutionModes = executionContractMutation.Values },
                "anti_screenplay_duties" => executionContract with { AntiScreenplayDuties = executionContractMutation.Values },
                "source_backed_detail_targets" => executionContract with { SourceBackedDetailTargets = executionContractMutation.Values },
                "candidate_rejection_rules" => executionContract with { CandidateRejectionRules = executionContractMutation.Values },
                _ => throw new ArgumentException("Unsupported execution contract mutation.", nameof(executionContractMutation))
            };
        }

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
            chapterFunction,
            logicTrack,
            emotionTrack,
            narrationTrack,
            characterTrack,
            referenceTrack,
            transitionTrack,
            executionContract,
            previousState,
            finalState,
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

    private sealed record AnalysisTrackMutation(
        string FieldPath,
        string Track,
        string Summary,
        IReadOnlyList<string> Points);

    private sealed record ExecutionContractMutation(
        string FieldName,
        IReadOnlyList<string> Values);
}
