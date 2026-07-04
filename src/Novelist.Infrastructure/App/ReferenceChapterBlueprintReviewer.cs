using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceChapterBlueprintReviewer
{
    public const int CurrentReviewVersion = 1;

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

        if (IsEmptyTrack(blueprint.LogicAnalysis) ||
            IsEmptyTrack(blueprint.EmotionAnalysis) ||
            IsEmptyTrack(blueprint.NarrationAnalysis) ||
            IsEmptyTrack(blueprint.CharacterAnalysis) ||
            IsEmptyTrack(blueprint.ReferenceAnalysis) ||
            IsEmptyTrack(blueprint.TransitionPlan))
        {
            logicErrors.Add("Blueprint must contain complete logic, emotion, narration, character, reference, and transition tracks.");
        }

        if (IsEmptyExecutionTrack(blueprint.ExecutionContract))
        {
            executionErrors.Add("Blueprint must contain a complete execution track.");
        }

        if (blueprint.Beats.Count == 0)
        {
            causalityErrors.Add("Blueprint must contain at least one beat.");
        }

        foreach (var beat in blueprint.Beats.OrderBy(item => item.BeatIndex))
        {
            if (beat.BeatIndex > 1 && string.IsNullOrWhiteSpace(beat.CausalityIn))
            {
                causalityErrors.Add($"Beat {beat.BeatIndex} is missing causality_in.");
            }

            if (string.IsNullOrWhiteSpace(beat.CausalityOut))
            {
                causalityErrors.Add($"Beat {beat.BeatIndex} is missing causality_out.");
            }

            if (string.IsNullOrWhiteSpace(beat.TransitionIn) || string.IsNullOrWhiteSpace(beat.TransitionOut))
            {
                transitionErrors.Add($"Beat {beat.BeatIndex} is missing transition reason.");
            }
            else if (!HasTransitionPressure(beat.TransitionIn) || !HasTransitionPressure(beat.TransitionOut))
            {
                transitionErrors.Add($"Beat {beat.BeatIndex} transition lacks causal, emotional, informational, or viewpoint pressure.");
            }

            var emotionChanges = !string.Equals(beat.EmotionBefore, beat.EmotionAfter, StringComparison.Ordinal);
            if (emotionChanges &&
                (string.IsNullOrWhiteSpace(beat.EmotionTrigger) ||
                    string.IsNullOrWhiteSpace(beat.SuppressedReaction) ||
                    string.IsNullOrWhiteSpace(beat.ExternalEvidence)))
            {
                emotionErrors.Add($"Beat {beat.BeatIndex} changes emotion without trigger, suppressed reaction, or external evidence.");
            }

            if (emotionChanges &&
                (UsesFakeEmotionMechanic(beat.EmotionTrigger) ||
                    UsesFakeEmotionMechanic(beat.SuppressedReaction) ||
                    UsesFakeEmotionMechanic(beat.ExternalEvidence)))
            {
                emotionErrors.Add($"Beat {beat.BeatIndex} uses fake emotion mechanic; trigger, suppressed reaction, and external evidence must be concrete.");
            }

            if (beat.CharacterGoals.Count == 0 || beat.CharacterStatesBefore.Count == 0 || beat.CharacterStatesAfter.Count == 0)
            {
                characterStateErrors.Add($"Beat {beat.BeatIndex} is missing character state mechanics.");
            }

            if (beat.ViewpointForbiddenKnowledge.Any(forbidden =>
                    beat.ViewpointAllowedKnowledge.Contains(forbidden, StringComparer.OrdinalIgnoreCase)))
            {
                povErrors.Add($"Beat {beat.BeatIndex} allows viewpoint knowledge that is also forbidden.");
            }

            foreach (var unsupportedFact in FindUnsupportedViewpointFacts(blueprint, beat))
            {
                povErrors.Add($"Beat {beat.BeatIndex} allows POV knowledge outside approved facts: {unsupportedFact}");
            }

            var proseDuties = beat.ProseDuties
                .Where(duty => !string.IsNullOrWhiteSpace(duty))
                .ToArray();
            if (proseDuties.Length == 0)
            {
                executionErrors.Add($"Beat {beat.BeatIndex} is missing prose duties.");
            }
            else if (proseDuties.All(duty =>
                    string.Equals(duty, "action", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(duty, "dialogue", StringComparison.OrdinalIgnoreCase)))
            {
                screenplayRisks.Add($"Beat {beat.BeatIndex} has only action/dialogue prose duties.");
            }

            if (string.IsNullOrWhiteSpace(beat.ParagraphIntention) ||
                string.IsNullOrWhiteSpace(beat.ExecutionMode) ||
                string.IsNullOrWhiteSpace(beat.AntiScreenplayDuty) ||
                string.IsNullOrWhiteSpace(beat.CandidateRejectionRule))
            {
                executionErrors.Add($"Beat {beat.BeatIndex} is missing paragraph intention, execution mode, anti-screenplay duty, or rejection rule.");
            }

            if ((string.Equals(beat.BeatType, ReferenceBlueprintBeatTypes.Action, StringComparison.Ordinal) ||
                    string.Equals(beat.BeatType, ReferenceBlueprintBeatTypes.DialogueExchange, StringComparison.Ordinal)) &&
                string.IsNullOrWhiteSpace(beat.SubtextPlan) &&
                string.IsNullOrWhiteSpace(beat.SensoryAnchorTarget) &&
                string.IsNullOrWhiteSpace(beat.SourceBackedDetailTarget))
            {
                novelisticNarrationErrors.Add($"Beat {beat.BeatIndex} reads like screenplay blocking without subtext, sensory anchor, or source-backed detail.");
            }

            if (string.IsNullOrWhiteSpace(beat.ReferenceQuery.Query) || beat.RequiredMaterialTypes.Count == 0)
            {
                referenceBindingErrors.Add($"Beat {beat.BeatIndex} is missing reference query or material type.");
            }
            else if (!HasReferenceQueryBeatFit(beat))
            {
                materialFitErrors.Add($"Beat {beat.BeatIndex} reference query lacks material fit with beat function, emotion, POV, or prose duties.");
            }

            if (string.IsNullOrWhiteSpace(beat.NarrationStrategy))
            {
                narrationErrors.Add($"Beat {beat.BeatIndex} is missing narration strategy.");
            }
        }

        foreach (var forbidden in blueprint.ForbiddenFacts.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            if (ContainsForbidden(blueprint.FinalHook, forbidden) ||
                blueprint.Beats.Any(beat => beat.SceneFacts.Any(fact => ContainsForbidden(fact, forbidden))))
            {
                forbiddenFactErrors.Add($"Forbidden fact appears in blueprint: {forbidden}");
            }
        }

        if (blueprint.RiskFlags.Any(flag => flag.Contains("ai", StringComparison.OrdinalIgnoreCase)))
        {
            aiRisks.Add("Blueprint already carries AI prose risk flags.");
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
