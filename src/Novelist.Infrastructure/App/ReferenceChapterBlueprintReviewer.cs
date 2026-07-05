using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceChapterBlueprintReviewer
{
    public const int CurrentReviewVersion = 42;

    public static ReferenceChapterBlueprintReviewPayload BuildReview(
        ReferenceChapterBlueprintPayload blueprint,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var logicErrors = new List<string>();
        var causalityErrors = new List<string>();
        var emotionErrors = new List<string>();
        var narrationErrors = new List<string>();
        var executionErrors = new List<string>();
        var characterStateErrors = new List<string>();
        var povErrors = new List<string>();
        var continuityErrors = new List<string>();
        var transitionErrors = new List<string>();
        var forbiddenFactErrors = new List<string>();
        var referenceBindingErrors = new List<string>();
        var materialFitErrors = new List<string>();
        var screenplayRisks = new List<string>();
        var aiRisks = new List<string>();
        var novelisticNarrationErrors = new List<string>();
        var defects = new List<ReferenceChapterBlueprintReviewDefectPayload>();

        void AddDefect(
            List<string> bucket,
            string category,
            string fieldPath,
            string beatId,
            string reason,
            string requiredFix,
            string severity = "error")
        {
            bucket.Add(reason);
            defects.Add(new ReferenceChapterBlueprintReviewDefectPayload(
                category,
                fieldPath,
                beatId,
                severity,
                reason,
                requiredFix));
        }

        void AddBeatDefect(
            List<string> bucket,
            string category,
            ReferenceChapterBlueprintBeatPayload beat,
            string fieldName,
            string reason,
            string requiredFix)
        {
            AddDefect(bucket, category, "beat:" + beat.BeatId + ":" + fieldName, beat.BeatId, reason, requiredFix);
        }

        if (IsEmptyTrack(blueprint.LogicAnalysis) ||
            IsEmptyTrack(blueprint.EmotionAnalysis) ||
            IsEmptyTrack(blueprint.NarrationAnalysis) ||
            IsEmptyTrack(blueprint.CharacterAnalysis) ||
            IsEmptyTrack(blueprint.ReferenceAnalysis) ||
            IsEmptyTrack(blueprint.TransitionPlan))
        {
            AddDefect(
                logicErrors,
                "logic",
                "analysis_tracks",
                string.Empty,
                "Blueprint must contain complete logic, emotion, narration, character, reference, and transition tracks.",
                "Complete the logic, emotion, narration, character, reference, and transition analysis tracks.");
        }

        if (IsEmptyExecutionTrack(blueprint.ExecutionContract))
        {
            AddDefect(
                executionErrors,
                "execution",
                "execution_contract",
                string.Empty,
                "Blueprint must contain a complete execution track.",
                "Complete paragraph intentions, execution modes, anti-screenplay duties, source-backed detail targets, and rejection rules.");
        }

        if (blueprint.Beats.Count == 0)
        {
            AddDefect(
                causalityErrors,
                "causality",
                "beats",
                string.Empty,
                "Blueprint must contain at least one beat.",
                "Add at least one reviewable beat before running blueprint review.");
        }

        foreach (var beat in blueprint.Beats.OrderBy(item => item.BeatIndex))
        {
            foreach (var unsupportedNarrativeFunctionFact in FindUnsupportedNarrativeFunctionFacts(blueprint, beat))
            {
                AddBeatDefect(
                    logicErrors,
                    "logic",
                    beat,
                    "narrative_function",
                    $"Beat {beat.BeatIndex} contains unsupported narrative function fact: {unsupportedNarrativeFunctionFact}",
                    "Set up the narrative_function fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }

            if (beat.BeatIndex > 1 && string.IsNullOrWhiteSpace(beat.CausalityIn))
            {
                AddBeatDefect(
                    causalityErrors,
                    "causality",
                    beat,
                    "causality_in",
                    $"Beat {beat.BeatIndex} is missing causality_in.",
                    "Add causality_in showing why this beat follows from the previous beat.");
            }

            if (string.IsNullOrWhiteSpace(beat.CausalityOut))
            {
                AddBeatDefect(
                    causalityErrors,
                    "causality",
                    beat,
                    "causality_out",
                    $"Beat {beat.BeatIndex} is missing causality_out.",
                    "Add causality_out showing the consequence this beat creates for the next beat or hook.");
            }

            if (string.IsNullOrWhiteSpace(beat.TransitionIn) || string.IsNullOrWhiteSpace(beat.TransitionOut))
            {
                AddBeatDefect(
                    transitionErrors,
                    "transition",
                    beat,
                    "transition",
                    $"Beat {beat.BeatIndex} is missing transition reason.",
                    "Fill transition_in and transition_out with causal, emotional, informational, or viewpoint pressure.");
            }
            else if (!HasTransitionPressure(beat.TransitionIn) || !HasTransitionPressure(beat.TransitionOut))
            {
                AddBeatDefect(
                    transitionErrors,
                    "transition",
                    beat,
                    "transition",
                    $"Beat {beat.BeatIndex} transition lacks causal, emotional, informational, or viewpoint pressure.",
                    "Rewrite transition_in and transition_out so the movement is forced by story pressure.");
            }

            var emotionChanges = !string.Equals(beat.EmotionBefore, beat.EmotionAfter, StringComparison.Ordinal);
            if (emotionChanges &&
                (string.IsNullOrWhiteSpace(beat.EmotionTrigger) ||
                    string.IsNullOrWhiteSpace(beat.SuppressedReaction) ||
                    string.IsNullOrWhiteSpace(beat.ExternalEvidence)))
            {
                AddBeatDefect(
                    emotionErrors,
                    "emotion",
                    beat,
                    "emotion_mechanic",
                    $"Beat {beat.BeatIndex} changes emotion without trigger, suppressed reaction, or external evidence.",
                    "Add emotion_trigger, suppressed_reaction, and external_evidence for the declared emotion change.");
            }

            if (emotionChanges &&
                (UsesFakeEmotionMechanic(beat.EmotionTrigger) ||
                    UsesFakeEmotionMechanic(beat.SuppressedReaction) ||
                    UsesFakeEmotionMechanic(beat.ExternalEvidence)))
            {
                AddBeatDefect(
                    emotionErrors,
                    "emotion",
                    beat,
                    "emotion_mechanic",
                    $"Beat {beat.BeatIndex} uses fake emotion mechanic; trigger, suppressed reaction, and external evidence must be concrete.",
                    "Replace generic emotion mechanics with concrete trigger, suppressed reaction, and observable evidence.");
            }

            foreach (var unsupportedExternalEvidenceFact in FindUnsupportedExternalEvidenceFacts(blueprint, beat))
            {
                AddBeatDefect(
                    emotionErrors,
                    "emotion",
                    beat,
                    "external_evidence",
                    $"Beat {beat.BeatIndex} contains unsupported external evidence fact: {unsupportedExternalEvidenceFact}",
                    "Set up the external_evidence fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }

            foreach (var unsupportedEmotionTriggerFact in FindUnsupportedEmotionTriggerFacts(blueprint, beat))
            {
                AddBeatDefect(
                    emotionErrors,
                    "emotion",
                    beat,
                    "emotion_trigger",
                    $"Beat {beat.BeatIndex} contains unsupported emotion trigger fact: {unsupportedEmotionTriggerFact}",
                    "Set up the emotion_trigger fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }

            foreach (var unsupportedSuppressedReactionFact in FindUnsupportedSuppressedReactionFacts(blueprint, beat))
            {
                AddBeatDefect(
                    emotionErrors,
                    "emotion",
                    beat,
                    "suppressed_reaction",
                    $"Beat {beat.BeatIndex} contains unsupported suppressed reaction fact: {unsupportedSuppressedReactionFact}",
                    "Set up the suppressed_reaction fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }

            if (beat.CharacterGoals.Count == 0 || beat.CharacterStatesBefore.Count == 0 || beat.CharacterStatesAfter.Count == 0)
            {
                AddBeatDefect(
                    characterStateErrors,
                    "character_state",
                    beat,
                    "character_state",
                    $"Beat {beat.BeatIndex} is missing character state mechanics.",
                    "Fill character goals plus before/after state mechanics for this beat.");
            }
            else if (beat.CharacterMisbeliefs.Count == 0)
            {
                AddBeatDefect(
                    characterStateErrors,
                    "character_state",
                    beat,
                    "character_misbeliefs",
                    $"Beat {beat.BeatIndex} is missing character misbelief mechanics.",
                    "Fill character_misbeliefs so the beat exposes what the character misunderstands, avoids, or cannot yet see.");
            }
            else if (beat.RelationshipPressure.Count == 0)
            {
                AddBeatDefect(
                    characterStateErrors,
                    "character_state",
                    beat,
                    "relationship_pressure",
                    $"Beat {beat.BeatIndex} is missing relationship pressure mechanics.",
                    "Fill relationship_pressure so the beat exposes how the scene changes leverage, trust, distance, or obligation.");
            }
            else if (HasNoCharacterStateDelta(beat.CharacterStatesBefore, beat.CharacterStatesAfter))
            {
                AddBeatDefect(
                    characterStateErrors,
                    "character_state",
                    beat,
                    "character_state_delta",
                    $"Beat {beat.BeatIndex} has no role-state delta between before and after states.",
                    "Change character_states_after to show the pressure, leverage, knowledge, relationship, or role-state delta created by this beat.");
            }

            foreach (var unsupportedCharacterStateFact in FindUnsupportedCharacterStateFacts(blueprint, beat))
            {
                AddBeatDefect(
                    characterStateErrors,
                    "character_state",
                    beat,
                    "character_states",
                    $"Beat {beat.BeatIndex} contains unsupported character state fact: {unsupportedCharacterStateFact}",
                    "Move the character state fact into approved known facts or scene facts before using it as role-state context.");
            }

            foreach (var unsupportedCharacterGoalFact in FindUnsupportedCharacterGoalFacts(blueprint, beat))
            {
                AddBeatDefect(
                    characterStateErrors,
                    "character_state",
                    beat,
                    "character_goals",
                    $"Beat {beat.BeatIndex} contains unsupported character goal fact: {unsupportedCharacterGoalFact}",
                    "Move the character_goals fact into approved known facts or scene facts before using it as role-state motivation.");
            }

            foreach (var unsupportedCharacterMisbeliefFact in FindUnsupportedCharacterMisbeliefFacts(blueprint, beat))
            {
                AddBeatDefect(
                    characterStateErrors,
                    "character_state",
                    beat,
                    "character_misbeliefs",
                    $"Beat {beat.BeatIndex} contains unsupported character misbelief fact: {unsupportedCharacterMisbeliefFact}",
                    "Move the character_misbeliefs fact into approved known facts or scene facts before using it as role-state pressure.");
            }

            foreach (var unsupportedRelationshipPressureFact in FindUnsupportedRelationshipPressureFacts(blueprint, beat))
            {
                AddBeatDefect(
                    characterStateErrors,
                    "character_state",
                    beat,
                    "relationship_pressure",
                    $"Beat {beat.BeatIndex} contains unsupported relationship pressure fact: {unsupportedRelationshipPressureFact}",
                    "Move the relationship_pressure fact into approved known facts or scene facts before using it as relationship leverage.");
            }

            if (beat.ViewpointForbiddenKnowledge.Any(forbidden =>
                    beat.ViewpointAllowedKnowledge.Contains(forbidden, StringComparer.OrdinalIgnoreCase)))
            {
                AddBeatDefect(
                    povErrors,
                    "pov",
                    beat,
                    "viewpoint_allowed_knowledge",
                    $"Beat {beat.BeatIndex} allows viewpoint knowledge that is also forbidden.",
                    "Remove forbidden knowledge from the allowed POV boundary.");
            }

            foreach (var unsupportedFact in FindUnsupportedViewpointFacts(blueprint, beat))
            {
                AddBeatDefect(
                    povErrors,
                    "pov",
                    beat,
                    "viewpoint_allowed_knowledge",
                    $"Beat {beat.BeatIndex} allows POV knowledge outside approved facts: {unsupportedFact}",
                    "Remove the unsupported POV knowledge or add it to approved known/scene facts before review.");
            }

            foreach (var unsupportedFact in FindUnsupportedSceneFacts(blueprint, beat))
            {
                AddBeatDefect(
                    continuityErrors,
                    "continuity",
                    beat,
                    "scene_facts",
                    $"Beat {beat.BeatIndex} introduces unsupported scene fact: {unsupportedFact}",
                    "Remove the unsupported scene fact or add it to known facts or declared slot values before review.");
            }

            foreach (var forbiddenFact in FindSceneFactsConflictingWithForbiddenPov(beat))
            {
                AddBeatDefect(
                    povErrors,
                    "pov",
                    beat,
                    "scene_facts",
                    $"Beat {beat.BeatIndex} scene fact conflicts with forbidden POV knowledge: {forbiddenFact}",
                    "Remove the forbidden POV fact from scene_facts or move the beat to a POV that may know it.");
            }

            foreach (var forbidden in beat.ForbiddenFacts.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                foreach (var field in FindBeatScopedForbiddenFactFields(beat, forbidden))
                {
                    AddBeatDefect(
                        forbiddenFactErrors,
                        "forbidden_fact",
                        beat,
                        field,
                        $"Beat forbidden fact appears in {FormatFieldName(field)}: {forbidden}",
                        $"Remove the beat forbidden fact from {field} before it becomes part of the draft contract.");
                }
            }

            var proseDuties = beat.ProseDuties
                .Where(duty => !string.IsNullOrWhiteSpace(duty))
                .ToArray();
            if (proseDuties.Length == 0)
            {
                AddBeatDefect(
                    executionErrors,
                    "execution",
                    beat,
                    "prose_duties",
                    $"Beat {beat.BeatIndex} is missing prose duties.",
                    "Add prose duties such as interiority, external_evidence, transition, subtext, or source_detail.");
            }
            else if (proseDuties.All(duty =>
                    string.Equals(duty, "action", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(duty, "dialogue", StringComparison.OrdinalIgnoreCase)))
            {
                AddBeatDefect(
                    screenplayRisks,
                    "screenplay_drift",
                    beat,
                    "prose_duties",
                    $"Beat {beat.BeatIndex} has only action/dialogue prose duties.",
                    "Add a novelistic prose duty beyond action/dialogue, such as interiority, subtext, sensory pressure, or transition work.");
            }

            if (string.IsNullOrWhiteSpace(beat.ParagraphIntention) ||
                string.IsNullOrWhiteSpace(beat.ExecutionMode) ||
                string.IsNullOrWhiteSpace(beat.AntiScreenplayDuty) ||
                string.IsNullOrWhiteSpace(beat.CandidateRejectionRule))
            {
                AddBeatDefect(
                    executionErrors,
                    "execution",
                    beat,
                    "execution_contract",
                    $"Beat {beat.BeatIndex} is missing paragraph intention, execution mode, anti-screenplay duty, or rejection rule.",
                    "Fill paragraph_intention, execution_mode, anti_screenplay_duty, and candidate_rejection_rule.");
            }
            else
            {
                if (UsesGenericParagraphIntention(beat.ParagraphIntention))
                {
                    AddBeatDefect(
                        executionErrors,
                        "execution",
                        beat,
                        "paragraph_intention",
                        $"Beat {beat.BeatIndex} uses generic paragraph intention.",
                        "Rewrite paragraph_intention as a concrete prose job, such as dwell, withhold, reveal, contrast, linger, or turn tied to this beat.");
                }

                foreach (var unsupportedParagraphIntentionFact in FindUnsupportedParagraphIntentionFacts(blueprint, beat))
                {
                    AddBeatDefect(
                        executionErrors,
                        "execution",
                        beat,
                        "paragraph_intention",
                        $"Beat {beat.BeatIndex} contains unsupported paragraph intention fact: {unsupportedParagraphIntentionFact}",
                        "Set up the paragraph_intention fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
                }

                if (UsesGenericExecutionMode(beat.ExecutionMode))
                {
                    AddBeatDefect(
                        executionErrors,
                        "execution",
                        beat,
                        "execution_mode",
                        $"Beat {beat.BeatIndex} uses generic execution mode.",
                        "Rewrite execution_mode as a concrete drafting operation, such as dwell, compress, withhold, reveal, braid evidence, or stage interiority.");
                }

                foreach (var unsupportedExecutionModeFact in FindUnsupportedExecutionModeFacts(blueprint, beat))
                {
                    AddBeatDefect(
                        executionErrors,
                        "execution",
                        beat,
                        "execution_mode",
                        $"Beat {beat.BeatIndex} contains unsupported execution mode fact: {unsupportedExecutionModeFact}",
                        "Set up the execution_mode fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
                }

                if (UsesGenericCandidateRejectionRule(beat.CandidateRejectionRule))
                {
                    AddBeatDefect(
                        executionErrors,
                        "execution",
                        beat,
                        "candidate_rejection_rule",
                        $"Beat {beat.BeatIndex} uses generic candidate rejection rule.",
                        "Rewrite candidate_rejection_rule as a concrete failure condition, such as action-only, dialogue-only, missing evidence, POV leak, or unsupported reveal.");
                }

                foreach (var unsupportedCandidateRejectionRuleFact in FindUnsupportedCandidateRejectionRuleFacts(blueprint, beat))
                {
                    AddBeatDefect(
                        executionErrors,
                        "execution",
                        beat,
                        "candidate_rejection_rule",
                        $"Beat {beat.BeatIndex} contains unsupported candidate rejection rule fact: {unsupportedCandidateRejectionRuleFact}",
                        "Set up the candidate_rejection_rule fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
                }

                if (UsesGenericAntiScreenplayDuty(beat.AntiScreenplayDuty))
                {
                    AddBeatDefect(
                        screenplayRisks,
                        "screenplay_drift",
                        beat,
                        "anti_screenplay_duty",
                        $"Beat {beat.BeatIndex} uses generic anti-screenplay duty.",
                        "Rewrite anti_screenplay_duty as concrete prose work beyond stage directions, dialogue labels, or camera blocking.");
                }

                foreach (var unsupportedAntiScreenplayDutyFact in FindUnsupportedAntiScreenplayDutyFacts(blueprint, beat))
                {
                    AddBeatDefect(
                        screenplayRisks,
                        "screenplay_drift",
                        beat,
                        "anti_screenplay_duty",
                        $"Beat {beat.BeatIndex} contains unsupported anti-screenplay duty fact: {unsupportedAntiScreenplayDutyFact}",
                        "Set up the anti_screenplay_duty fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
                }
            }

            if ((string.Equals(beat.BeatType, ReferenceBlueprintBeatTypes.Action, StringComparison.Ordinal) ||
                    string.Equals(beat.BeatType, ReferenceBlueprintBeatTypes.DialogueExchange, StringComparison.Ordinal)) &&
                string.IsNullOrWhiteSpace(beat.SubtextPlan) &&
                string.IsNullOrWhiteSpace(beat.SensoryAnchorTarget) &&
                string.IsNullOrWhiteSpace(beat.SourceBackedDetailTarget))
            {
                AddBeatDefect(
                    novelisticNarrationErrors,
                    "novelistic_narration",
                    beat,
                    "novelistic_targets",
                    $"Beat {beat.BeatIndex} reads like screenplay blocking without subtext, sensory anchor, or source-backed detail.",
                    "Add subtext_plan, sensory_anchor_target, or source_backed_detail_target so the beat can draft as prose.");
            }

            if (!string.IsNullOrWhiteSpace(beat.SourceBackedDetailTarget) &&
                UsesGenericSourceBackedDetailTarget(beat.SourceBackedDetailTarget))
            {
                AddBeatDefect(
                    novelisticNarrationErrors,
                    "novelistic_narration",
                    beat,
                    "source_backed_detail_target",
                    $"Beat {beat.BeatIndex} uses generic source-backed detail target.",
                    "Rewrite source_backed_detail_target as a concrete detail from approved source material, such as an object, sensory cue, gesture, or environmental pressure.");
            }

            foreach (var unsupportedSourceDetailTargetFact in FindUnsupportedSourceBackedDetailTargetFacts(blueprint, beat))
            {
                AddBeatDefect(
                    novelisticNarrationErrors,
                    "novelistic_narration",
                    beat,
                    "source_backed_detail_target",
                    $"Beat {beat.BeatIndex} contains unsupported source-backed detail target fact: {unsupportedSourceDetailTargetFact}",
                    "Set up the source_backed_detail_target fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }

            foreach (var unsupportedSensoryAnchorTargetFact in FindUnsupportedSensoryAnchorTargetFacts(blueprint, beat))
            {
                AddBeatDefect(
                    novelisticNarrationErrors,
                    "novelistic_narration",
                    beat,
                    "sensory_anchor_target",
                    $"Beat {beat.BeatIndex} contains unsupported sensory anchor target fact: {unsupportedSensoryAnchorTargetFact}",
                    "Set up the sensory_anchor_target fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }

            foreach (var unsupportedSubtextPlanFact in FindUnsupportedSubtextPlanFacts(blueprint, beat))
            {
                AddBeatDefect(
                    novelisticNarrationErrors,
                    "novelistic_narration",
                    beat,
                    "subtext_plan",
                    $"Beat {beat.BeatIndex} contains unsupported subtext plan fact: {unsupportedSubtextPlanFact}",
                    "Set up the subtext_plan fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }

            if (string.IsNullOrWhiteSpace(beat.ReferenceQuery.Query) || beat.RequiredMaterialTypes.Count == 0)
            {
                AddBeatDefect(
                    referenceBindingErrors,
                    "reference_binding",
                    beat,
                    "reference_query",
                    $"Beat {beat.BeatIndex} is missing reference query or material type.",
                    "Fill reference_query.query and required material types before material binding.");
            }
            else if (!HasReferenceQueryBeatFit(beat))
            {
                AddBeatDefect(
                    materialFitErrors,
                    "material_fit",
                    beat,
                    "reference_query",
                    $"Beat {beat.BeatIndex} reference query lacks material fit with beat function, emotion, POV, or prose duties.",
                    "Align reference query tags with beat function, emotion, POV, technique, or prose duties.");
            }

            foreach (var unsupportedReferenceQueryFact in FindUnsupportedReferenceQueryFacts(blueprint, beat))
            {
                AddBeatDefect(
                    referenceBindingErrors,
                    "reference_binding",
                    beat,
                    "reference_query",
                    $"Beat {beat.BeatIndex} contains unsupported reference query fact: {unsupportedReferenceQueryFact}",
                    "Set up the reference_query fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before material binding.");
            }

            if (beat.SlotPlan.Any(slot => UsesGenericSlotPlan(slot.SlotName, slot.Value)))
            {
                AddBeatDefect(
                    referenceBindingErrors,
                    "reference_binding",
                    beat,
                    "slot_plan",
                    $"Beat {beat.BeatIndex} uses generic slot plan.",
                    "Rewrite slot_plan values as concrete approved replacements, such as a named object, place, sensory cue, or evidence item.");
            }

            foreach (var unsupportedSlotPlanFact in FindUnsupportedSlotPlanFacts(blueprint, beat))
            {
                AddBeatDefect(
                    referenceBindingErrors,
                    "reference_binding",
                    beat,
                    "slot_plan",
                    $"Beat {beat.BeatIndex} contains unsupported slot plan fact: {unsupportedSlotPlanFact}",
                    "Move the slot_plan fact into approved known facts, scene facts, or viewpoint knowledge before using it as a replacement.");
            }

            if (string.IsNullOrWhiteSpace(beat.NarrationStrategy))
            {
                AddBeatDefect(
                    narrationErrors,
                    "narration",
                    beat,
                    "narration_strategy",
                    $"Beat {beat.BeatIndex} is missing narration strategy.",
                    "Add a narration strategy that constrains POV, distance, and prose execution.");
            }
            else if (UsesGenericNarrationStrategy(beat.NarrationStrategy))
            {
                AddBeatDefect(
                    narrationErrors,
                    "narration",
                    beat,
                    "narration_strategy",
                    $"Beat {beat.BeatIndex} uses generic narration strategy.",
                    "Rewrite narration_strategy with concrete POV distance, sensory/interiority limits, and the narration work this beat must perform.");
            }

            foreach (var unsupportedNarrationStrategyFact in FindUnsupportedNarrationStrategyFacts(blueprint, beat))
            {
                AddBeatDefect(
                    narrationErrors,
                    "narration",
                    beat,
                    "narration_strategy",
                    $"Beat {beat.BeatIndex} contains unsupported narration strategy fact: {unsupportedNarrationStrategyFact}",
                    "Set up the narration_strategy fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }

            if (string.IsNullOrWhiteSpace(beat.RhythmStrategy))
            {
                AddBeatDefect(
                    narrationErrors,
                    "narration",
                    beat,
                    "rhythm_strategy",
                    $"Beat {beat.BeatIndex} is missing rhythm strategy.",
                    "Add a rhythm strategy that names pacing pressure, sentence movement, delay, turn, or release for this beat.");
            }
            else if (UsesGenericRhythmStrategy(beat.RhythmStrategy))
            {
                AddBeatDefect(
                    narrationErrors,
                    "narration",
                    beat,
                    "rhythm_strategy",
                    $"Beat {beat.BeatIndex} uses generic rhythm strategy.",
                    "Rewrite rhythm_strategy with concrete pacing pressure, sentence movement, delay, turn, or release for this beat.");
            }

            foreach (var unsupportedRhythmStrategyFact in FindUnsupportedRhythmStrategyFacts(blueprint, beat))
            {
                AddBeatDefect(
                    narrationErrors,
                    "narration",
                    beat,
                    "rhythm_strategy",
                    $"Beat {beat.BeatIndex} contains unsupported rhythm strategy fact: {unsupportedRhythmStrategyFact}",
                    "Set up the rhythm_strategy fact in approved known facts, scene facts, viewpoint knowledge, or slot plan before drafting.");
            }
        }

        foreach (var forbidden in blueprint.ForbiddenFacts.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            if (ContainsForbidden(blueprint.FinalHook, forbidden))
            {
                AddDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    "final_hook",
                    string.Empty,
                    $"Forbidden fact appears in blueprint: {forbidden}",
                    "Remove the forbidden fact from the final hook or move it out of the forbidden fact set.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => beat.SceneFacts.Any(fact => ContainsForbidden(fact, forbidden))))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "scene_facts",
                    $"Forbidden fact appears in blueprint: {forbidden}",
                    "Remove the forbidden fact from beat scene facts or move it out of the forbidden fact set.");
            }

            foreach (var beat in blueprint.Beats.Where(beat =>
                beat.CharacterStatesBefore.Concat(beat.CharacterStatesAfter).Any(state => ContainsForbidden(state, forbidden))))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "character_states",
                    $"Forbidden fact appears in character state: {forbidden}",
                    "Remove the forbidden fact from beat character_states before it is treated as role-state context.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => beat.CharacterGoals.Any(goal => ContainsForbidden(goal, forbidden))))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "character_goals",
                    $"Forbidden fact appears in character goal: {forbidden}",
                    "Remove the forbidden fact from beat character_goals before it is treated as role-state motivation.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => beat.CharacterMisbeliefs.Any(misbelief => ContainsForbidden(misbelief, forbidden))))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "character_misbeliefs",
                    $"Forbidden fact appears in character misbelief: {forbidden}",
                    "Remove the forbidden fact from beat character_misbeliefs before it is treated as role-state pressure.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => beat.RelationshipPressure.Any(pressure => ContainsForbidden(pressure, forbidden))))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "relationship_pressure",
                    $"Forbidden fact appears in relationship pressure: {forbidden}",
                    "Remove the forbidden fact from beat relationship_pressure before it is treated as relationship leverage.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => beat.ViewpointAllowedKnowledge.Any(fact => ContainsForbidden(fact, forbidden))))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "viewpoint_allowed_knowledge",
                    $"Forbidden fact appears in viewpoint allowed knowledge: {forbidden}",
                    "Remove the forbidden fact from beat viewpoint_allowed_knowledge before it is treated as POV-approved.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => ContainsForbidden(beat.ReferenceQuery.Query, forbidden)))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "reference_query",
                    $"Forbidden fact appears in reference query: {forbidden}",
                    "Remove the forbidden fact from beat reference_query before material binding.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => beat.SlotPlan.Any(slot => ContainsForbidden(slot.Value, forbidden))))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "slot_plan",
                    $"Forbidden fact appears in slot plan: {forbidden}",
                    "Remove the forbidden fact from beat slot_plan before using it as a replacement.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => ContainsForbidden(beat.EmotionTrigger, forbidden)))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "emotion_trigger",
                    $"Forbidden fact appears in emotion trigger: {forbidden}",
                    "Remove the forbidden fact from beat emotion_trigger before drafting.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => ContainsForbidden(beat.SuppressedReaction, forbidden)))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "suppressed_reaction",
                    $"Forbidden fact appears in suppressed reaction: {forbidden}",
                    "Remove the forbidden fact from beat suppressed_reaction before drafting.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => ContainsForbidden(beat.ExternalEvidence, forbidden)))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "external_evidence",
                    $"Forbidden fact appears in external evidence: {forbidden}",
                    "Remove the forbidden fact from beat external_evidence before drafting.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => ContainsForbidden(beat.SourceBackedDetailTarget, forbidden)))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "source_backed_detail_target",
                    $"Forbidden fact appears in source-backed detail target: {forbidden}",
                    "Remove the forbidden fact from beat source_backed_detail_target before drafting.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => ContainsForbidden(beat.SensoryAnchorTarget, forbidden)))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "sensory_anchor_target",
                    $"Forbidden fact appears in sensory anchor target: {forbidden}",
                    "Remove the forbidden fact from beat sensory_anchor_target before drafting.");
            }

            foreach (var beat in blueprint.Beats.Where(beat => ContainsForbidden(beat.SubtextPlan, forbidden)))
            {
                AddBeatDefect(
                    forbiddenFactErrors,
                    "forbidden_fact",
                    beat,
                    "subtext_plan",
                    $"Forbidden fact appears in subtext plan: {forbidden}",
                    "Remove the forbidden fact from beat subtext_plan before drafting.");
            }
        }

        foreach (var unsupportedFact in FindUnsupportedFinalHookFacts(blueprint))
        {
            AddDefect(
                continuityErrors,
                "continuity",
                "final_hook",
                string.Empty,
                $"Final hook depends on unsupported fact: {unsupportedFact}",
                "Set up the final hook fact in known facts, beat scene facts, or approved POV knowledge before review.");
        }

        if (blueprint.RiskFlags.Any(flag => flag.Contains("ai", StringComparison.OrdinalIgnoreCase)))
        {
            AddDefect(
                aiRisks,
                "ai_prose",
                "risk_flags",
                string.Empty,
                "Blueprint already carries AI prose risk flags.",
                "Clear or address AI prose risk flags before relying on this review for drafting.",
                "warning");
        }

        var defectCount = logicErrors.Count + causalityErrors.Count + emotionErrors.Count +
            narrationErrors.Count + executionErrors.Count + characterStateErrors.Count + povErrors.Count +
            continuityErrors.Count + transitionErrors.Count + forbiddenFactErrors.Count +
            referenceBindingErrors.Count + materialFitErrors.Count + screenplayRisks.Count +
            novelisticNarrationErrors.Count;
        var status = defectCount == 0
            ? ReferenceBlueprintReviewStatuses.Passed
            : ReferenceBlueprintReviewStatuses.Failed;
        var requiredFixes = new[]
        {
            logicErrors,
            causalityErrors,
            emotionErrors,
            narrationErrors,
            executionErrors,
            characterStateErrors,
            povErrors,
            continuityErrors,
            transitionErrors,
            forbiddenFactErrors,
            referenceBindingErrors,
            materialFitErrors,
            screenplayRisks,
            novelisticNarrationErrors
        }.SelectMany(items => items).ToArray();

        return new ReferenceChapterBlueprintReviewPayload(
            "review-" + Guid.NewGuid().ToString("N"),
            blueprint.BlueprintId,
            blueprint.ContextHash,
            blueprint.SourcePlanHash,
            blueprint.AnalysisContractHash,
            CurrentReviewVersion,
            status,
            Math.Max(0, 1.0 - defectCount * 0.1),
            logicErrors,
            causalityErrors,
            emotionErrors,
            narrationErrors,
            executionErrors,
            characterStateErrors,
            povErrors,
            continuityErrors,
            transitionErrors,
            forbiddenFactErrors,
            referenceBindingErrors,
            materialFitErrors,
            screenplayRisks,
            aiRisks,
            novelisticNarrationErrors,
            requiredFixes,
            defects,
            now);
    }

    private static bool IsEmptyTrack(ReferenceChapterBlueprintAnalysisTrackPayload track)
    {
        return string.IsNullOrWhiteSpace(track.Track) ||
            string.IsNullOrWhiteSpace(track.Summary) ||
            track.Points.Count == 0;
    }

    private static bool IsEmptyExecutionTrack(ReferenceChapterBlueprintExecutionTrackPayload track)
    {
        return string.IsNullOrWhiteSpace(track.Track) ||
            string.IsNullOrWhiteSpace(track.Summary) ||
            track.ParagraphIntentions.Count == 0 ||
            track.ExecutionModes.Count == 0 ||
            track.AntiScreenplayDuties.Count == 0 ||
            track.CandidateRejectionRules.Count == 0;
    }

    private static bool ContainsForbidden(string value, string forbidden)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Contains(forbidden, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindBeatScopedForbiddenFactFields(
        ReferenceChapterBlueprintBeatPayload beat,
        string forbidden)
    {
        if (beat.SceneFacts.Any(fact => ContainsForbidden(fact, forbidden)))
        {
            yield return "scene_facts";
        }

        if (beat.ViewpointAllowedKnowledge.Any(fact => ContainsForbidden(fact, forbidden)))
        {
            yield return "viewpoint_allowed_knowledge";
        }

        if (beat.CharacterStatesBefore.Concat(beat.CharacterStatesAfter).Any(state => ContainsForbidden(state, forbidden)))
        {
            yield return "character_states";
        }

        if (beat.CharacterGoals.Any(goal => ContainsForbidden(goal, forbidden)))
        {
            yield return "character_goals";
        }

        if (beat.CharacterMisbeliefs.Any(misbelief => ContainsForbidden(misbelief, forbidden)))
        {
            yield return "character_misbeliefs";
        }

        if (beat.RelationshipPressure.Any(pressure => ContainsForbidden(pressure, forbidden)))
        {
            yield return "relationship_pressure";
        }

        if (ContainsForbidden(beat.EmotionTrigger, forbidden))
        {
            yield return "emotion_trigger";
        }

        if (ContainsForbidden(beat.SuppressedReaction, forbidden))
        {
            yield return "suppressed_reaction";
        }

        if (ContainsForbidden(beat.ExternalEvidence, forbidden))
        {
            yield return "external_evidence";
        }

        if (ContainsForbidden(beat.SourceBackedDetailTarget, forbidden))
        {
            yield return "source_backed_detail_target";
        }

        if (ContainsForbidden(beat.SensoryAnchorTarget, forbidden))
        {
            yield return "sensory_anchor_target";
        }

        if (ContainsForbidden(beat.SubtextPlan, forbidden))
        {
            yield return "subtext_plan";
        }

        if (ContainsForbidden(beat.ReferenceQuery.Query, forbidden))
        {
            yield return "reference_query";
        }

        if (beat.SlotPlan.Any(slot => ContainsForbidden(slot.Value, forbidden)))
        {
            yield return "slot_plan";
        }
    }

    private static string FormatFieldName(string field)
    {
        return field.Replace('_', ' ');
    }

    private static bool HasTransitionPressure(string value)
    {
        return ContainsAny(
            value,
            [
                "because", "therefore", "pressure", "conflict", "reveal", "transition",
                "因为", "所以", "于是", "导致", "迫使", "逼", "压力", "冲突", "后果",
                "代价", "线索", "证据", "发现", "意识", "怀疑", "秘密", "揭露", "追问",
                "误会", "关系", "目的", "阻碍", "情绪", "视角", "信息", "余波", "承接"
            ]);
    }

    private static bool UsesFakeEmotionMechanic(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            ContainsAny(
                value,
                [
                    "plot needs", "generic emotion", "because the plot",
                    "剧情需要", "为了剧情", "莫名", "突然情绪", "自然就", "情绪变化",
                    "表现出", "有反应", "很痛苦", "很难过", "很愤怒", "很开心",
                    "感觉不好", "说不清"
                ]);
    }

    private static IEnumerable<string> FindUnsupportedCharacterStateFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var approvedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return beat.CharacterStatesBefore
            .Concat(beat.CharacterStatesAfter)
            .SelectMany(ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases)
            .Where(fact => !IsAllowedFact(fact, approvedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedCharacterGoalFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var approvedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return beat.CharacterGoals
            .SelectMany(ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases)
            .Where(fact => !IsAllowedFact(fact, approvedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedCharacterMisbeliefFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var approvedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return beat.CharacterMisbeliefs
            .SelectMany(ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases)
            .Where(fact => !IsAllowedFact(fact, approvedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedRelationshipPressureFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var approvedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return beat.RelationshipPressure
            .SelectMany(ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases)
            .Where(fact => !IsAllowedFact(fact, approvedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedViewpointFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var approvedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var allowedKnowledge in beat.ViewpointAllowedKnowledge.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            foreach (var auditableFact in ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(allowedKnowledge))
            {
                if (!approvedFacts.Any(approved => approved.Contains(auditableFact, StringComparison.OrdinalIgnoreCase) ||
                        auditableFact.Contains(approved, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return auditableFact;
                }
            }
        }
    }

    private static bool HasNoCharacterStateDelta(
        IReadOnlyList<string> statesBefore,
        IReadOnlyList<string> statesAfter)
    {
        var before = NormalizeStateValues(statesBefore).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = NormalizeStateValues(statesAfter).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return before.Count > 0 && after.Count > 0 && before.SetEquals(after);
    }

    private static bool UsesGenericParagraphIntention(string value)
    {
        return ContainsAny(
            value,
            [
                "make it better", "make it emotional", "more emotional", "more moving",
                "写得更好", "写得好看", "更有代入感", "更有感染力", "更感人",
                "加强情绪", "增强感染力", "润色一下", "优化一下", "情绪拉满", "氛围拉满"
            ]);
    }

    private static bool UsesGenericExecutionMode(string value)
    {
        return ContainsAny(
            value,
            [
                "write normally", "normal execution", "standard execution", "regular execution",
                "execute normally", "as needed", "naturally", "smoothly",
                "正常写", "正常执行", "常规执行", "按常规", "根据需要",
                "自然展开", "自然推进", "顺着写", "流畅推进", "正常推进"
            ]);
    }

    private static bool UsesGenericCandidateRejectionRule(string value)
    {
        return ContainsAny(
            value,
            [
                "bad output", "low quality", "poor quality", "not good", "if bad",
                "质量差", "不好的不要", "不好就拒绝", "写得不好", "不够好",
                "效果不好", "不合适就拒绝", "不满意", "看起来不好", "泛泛而谈"
            ]);
    }

    private static bool UsesGenericAntiScreenplayDuty(string value)
    {
        return ContainsAny(
            value,
            [
                "avoid screenplay", "avoid script", "anti-screenplay", "not screenplay",
                "not a script", "no camera", "不要剧本化", "避免剧本化", "防止剧本化",
                "别写成剧本", "不要写成剧本", "不是剧本", "非剧本化", "不要镜头感"
            ]);
    }

    private static bool UsesGenericNarrationStrategy(string value)
    {
        return ContainsAny(
            value,
            [
                "normal narration", "standard narration", "regular narration", "write normally",
                "make it vivid", "cinematic feel", "more immersive", "more visual",
                "正常叙述", "常规叙述", "普通叙述", "自然叙述", "正常写",
                "写得有画面感", "更有画面感", "有画面感", "更沉浸", "更流畅",
                "代入感", "电影感"
            ]);
    }

    private static bool UsesGenericRhythmStrategy(string value)
    {
        return ContainsAny(
            value,
            [
                "normal rhythm", "normal pacing", "balanced pacing", "smooth pacing",
                "keep rhythm", "control pacing", "fast and slow",
                "正常节奏", "节奏正常", "常规节奏", "自然节奏", "节奏自然",
                "节奏自然流畅", "自然流畅", "节奏流畅", "保持节奏", "控制节奏",
                "快慢结合", "有张有弛", "张弛有度"
            ]);
    }

    private static bool UsesGenericSourceBackedDetailTarget(string value)
    {
        return ContainsAny(
            value,
            [
                "add detail", "add details", "more detail", "some detail", "make it detailed",
                "rich detail", "richer detail", "具体一点", "加一点细节", "加点细节",
                "加些细节", "增加细节", "补充细节", "丰富细节", "细节丰富",
                "多写细节", "写得细一点", "写细一点", "加些描写", "画面更丰富"
            ]);
    }

    private static bool UsesGenericSlotPlan(string slotName, string value)
    {
        if (string.IsNullOrWhiteSpace(slotName) || string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return ContainsAny(
            value,
            [
                "anything", "something", "placeholder", "replace later", "fill later",
                "random", "whatever", "generic object", "generic place",
                "随便", "任意", "某个", "某种", "一个东西", "某样东西", "占位",
                "之后再填", "后面再填", "待定", "替换一下", "随便替换"
            ]);
    }

    private static IEnumerable<string> FindUnsupportedExternalEvidenceFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.ExternalEvidence)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedEmotionTriggerFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.EmotionTrigger)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedSuppressedReactionFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.SuppressedReaction)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedSourceBackedDetailTargetFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.SourceBackedDetailTarget)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedSensoryAnchorTargetFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.SensoryAnchorTarget)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedSubtextPlanFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.SubtextPlan)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedParagraphIntentionFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.ParagraphIntention)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedNarrativeFunctionFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.NarrativeFunction)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedExecutionModeFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.ExecutionMode)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedCandidateRejectionRuleFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.CandidateRejectionRule)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedAntiScreenplayDutyFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.AntiScreenplayDuty)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedNarrationStrategyFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.NarrationStrategy)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedRhythmStrategyFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.RhythmStrategy)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedReferenceQueryFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(beat.ReferenceQuery.Query)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedSlotPlanFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return beat.SlotPlan
            .Select(slot => slot.Value)
            .SelectMany(ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindUnsupportedFinalHookFacts(ReferenceChapterBlueprintPayload blueprint)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(blueprint.Beats.SelectMany(beat => beat.SceneFacts))
            .Concat(blueprint.Beats.SelectMany(beat => beat.ViewpointAllowedKnowledge))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var fact in ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(blueprint.FinalHook))
        {
            if (!IsAllowedFact(fact, allowedFacts))
            {
                yield return fact;
            }
        }
    }

    private static IEnumerable<string> FindUnsupportedSceneFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SlotPlan.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return beat.SceneFacts
            .SelectMany(ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases)
            .Where(fact => !IsAllowedFact(fact, allowedFacts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindSceneFactsConflictingWithForbiddenPov(
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var forbiddenKnowledge = beat.ViewpointForbiddenKnowledge
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var sceneFact in beat.SceneFacts.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            foreach (var forbidden in forbiddenKnowledge)
            {
                if (ContainsForbidden(sceneFact, forbidden))
                {
                    yield return forbidden;
                    continue;
                }

                var sceneAuditableFacts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(sceneFact);
                var forbiddenAuditableFacts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(forbidden);
                foreach (var fact in sceneAuditableFacts.Where(fact => IsAllowedFact(fact, forbiddenAuditableFacts)))
                {
                    yield return fact;
                }
            }
        }
    }

    private static bool IsAllowedFact(string fact, IReadOnlyList<string> allowedFacts)
    {
        return allowedFacts.Any(allowed => allowed.Contains(fact, StringComparison.OrdinalIgnoreCase) ||
            fact.Contains(allowed, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasReferenceQueryBeatFit(ReferenceChapterBlueprintBeatPayload beat)
    {
        if (!string.IsNullOrWhiteSpace(beat.NoReuseReason))
        {
            return true;
        }

        var functionTags = NormalizeTags(beat.ReferenceQuery.FunctionTags).ToArray();
        var emotionTags = NormalizeTags(beat.ReferenceQuery.EmotionTags).ToArray();
        var povTags = NormalizeTags(beat.ReferenceQuery.PovTags).Where(tag => tag != "unknown").ToArray();
        var techniqueTags = NormalizeTags(beat.ReferenceQuery.TechniqueTags).ToArray();
        var proseDuties = NormalizeTags(beat.ProseDuties).ToArray();
        var narrativeFunction = NormalizeToken(beat.NarrativeFunction);
        var narrativeDistance = NormalizeToken(beat.NarrativeDistance);
        var emotionContext = NormalizeToken(string.Join(" ", [beat.EmotionBefore, beat.EmotionAfter, beat.EmotionTrigger]));
        var narrationContext = NormalizeToken(string.Join(" ", [beat.NarrationStrategy, beat.RhythmStrategy, beat.ParagraphIntention, beat.ExecutionMode]));

        return functionTags.Any(tag =>
                proseDuties.Contains(tag, StringComparer.OrdinalIgnoreCase) ||
                narrativeFunction.Contains(tag, StringComparison.OrdinalIgnoreCase) ||
                IsFunctionCompatibleWithProseDuty(tag, proseDuties)) ||
            povTags.Any(tag => narrativeDistance.Contains(tag, StringComparison.OrdinalIgnoreCase)) ||
            emotionTags.Any(tag => emotionContext.Contains(tag, StringComparison.OrdinalIgnoreCase)) ||
            techniqueTags.Any(tag =>
                proseDuties.Contains(tag, StringComparer.OrdinalIgnoreCase) ||
                narrationContext.Contains(tag, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFunctionCompatibleWithProseDuty(string functionTag, IReadOnlyList<string> proseDuties)
    {
        return functionTag switch
        {
            "environment" => ContainsAnyTag(proseDuties, ["external_evidence", "sensory", "sensory_anchor", "source_detail", "source_backed_detail"]),
            "narration" => ContainsAnyTag(proseDuties, ["interiority", "transition", "causality", "subtext"]),
            "interiority" => ContainsAnyTag(proseDuties, ["interiority"]),
            "emotion" or "emotion_evidence" or "afterbeat" => ContainsAnyTag(proseDuties, ["interiority", "external_evidence", "subtext"]),
            "transition" => ContainsAnyTag(proseDuties, ["transition", "causality"]),
            "dialogue" => ContainsAnyTag(proseDuties, ["dialogue", "subtext"]),
            "action" => ContainsAnyTag(proseDuties, ["action"]),
            "reveal" or "identity_reveal" => ContainsAnyTag(proseDuties, ["source_detail", "source_backed_detail", "causality", "transition"]),
            _ => false
        };
    }

    private static IEnumerable<string> NormalizeTags(IEnumerable<string> tags)
    {
        return tags
            .Select(NormalizeToken)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> NormalizeStateValues(IEnumerable<string> values)
    {
        return values
            .Select(value => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeToken(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('-', '_').ToLowerInvariant();
    }

    private static bool ContainsAnyTag(IReadOnlyList<string> values, IReadOnlyList<string> candidates)
    {
        return values.Any(value => candidates.Contains(value, StringComparer.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> markers)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
