using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceChapterBlueprintReviewer
{
    public const int CurrentReviewVersion = 3;

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
            "emotion" or "afterbeat" => ContainsAnyTag(proseDuties, ["interiority", "external_evidence", "subtext"]),
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
