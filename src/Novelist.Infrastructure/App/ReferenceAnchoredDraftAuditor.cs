using System.Text.RegularExpressions;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceAnchoredDraftAuditor
{
    public static ReferenceAnchoredDraftAuditPayload BuildDraftAudit(
        ReferenceChapterBlueprintPayload blueprint,
        IReadOnlyList<ReferenceDraftParagraphCandidatePayload> candidates,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(candidates);

        var provenanceErrors = new List<string>();
        var blueprintErrors = new List<string>();
        var unsupportedFactErrors = new List<string>();
        var povErrors = new List<string>();
        var aiRisks = new List<string>();
        var requiredFixes = new List<string>();

        foreach (var candidate in candidates)
        {
            var beat = blueprint.Beats.FirstOrDefault(item => string.Equals(item.BeatId, candidate.BeatId, StringComparison.Ordinal));
            if (beat is null)
            {
                blueprintErrors.Add($"Candidate {candidate.CandidateId} references a missing blueprint beat.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(candidate.MaterialId))
            {
                provenanceErrors.Add($"Candidate {candidate.CandidateId} is missing material provenance.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Text))
            {
                blueprintErrors.Add($"Candidate {candidate.CandidateId} text is empty.");
            }

            if (string.Equals(candidate.AuditStatus, "failed", StringComparison.Ordinal))
            {
                requiredFixes.Add($"Candidate {candidate.CandidateId} failed reference reuse audit.");
            }

            if ((string.Equals(beat.BeatType, ReferenceBlueprintBeatTypes.DialogueExchange, StringComparison.Ordinal) ||
                    beat.ProseDuties.All(duty => string.Equals(duty, "dialogue", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(duty, "action", StringComparison.OrdinalIgnoreCase))) &&
                string.IsNullOrWhiteSpace(beat.AntiScreenplayDuty))
            {
                blueprintErrors.Add($"Beat {beat.BeatIndex} lacks anti-screenplay execution duty.");
            }

            foreach (var forbidden in beat.ViewpointForbiddenKnowledge.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                if (candidate.Text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    povErrors.Add($"Candidate {candidate.CandidateId} leaks forbidden POV knowledge: {forbidden}");
                }
            }

            foreach (var leakedCharacter in FindNonPovInteriorKnowledgeLeaks(beat, candidate.Text))
            {
                povErrors.Add($"Candidate {candidate.CandidateId} leaks non-POV interior knowledge for {leakedCharacter}.");
                requiredFixes.Add($"Keep candidate {candidate.CandidateId} inside POV boundary; show {leakedCharacter}'s state through external evidence instead of direct interior knowledge.");
            }

            foreach (var forbidden in blueprint.ForbiddenFacts
                .Concat(beat.ForbiddenFacts)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (candidate.Text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    unsupportedFactErrors.Add($"Candidate {candidate.CandidateId} contains forbidden fact: {forbidden}");
                    requiredFixes.Add($"Remove forbidden fact from candidate {candidate.CandidateId}: {forbidden}");
                }
            }

            foreach (var unsupportedFact in FindUnsupportedHighRiskFacts(blueprint, beat, candidate))
            {
                unsupportedFactErrors.Add($"Candidate {candidate.CandidateId} introduces unsupported fact: {unsupportedFact}");
                requiredFixes.Add($"Remove unsupported fact from candidate {candidate.CandidateId} or add it to approved scene facts: {unsupportedFact}");
            }

            foreach (var requiredPhrase in ExtractRequiredProsePhrases(beat))
            {
                if (!candidate.Text.Contains(requiredPhrase, StringComparison.OrdinalIgnoreCase))
                {
                    blueprintErrors.Add($"Candidate {candidate.CandidateId} misses required prose target: {requiredPhrase}");
                    requiredFixes.Add($"Add required prose target to candidate {candidate.CandidateId}: {requiredPhrase}");
                }
            }

            foreach (var requiredEvidence in ExtractRequiredEmotionEvidence(beat))
            {
                if (!candidate.Text.Contains(requiredEvidence, StringComparison.OrdinalIgnoreCase))
                {
                    blueprintErrors.Add($"Candidate {candidate.CandidateId} misses required emotion evidence: {requiredEvidence}");
                    requiredFixes.Add($"Add required emotion evidence to candidate {candidate.CandidateId}: {requiredEvidence}");
                }
            }

            if (RequiresNovelisticExecution(beat) && IsDialogueOnly(candidate.Text))
            {
                aiRisks.Add($"Candidate {candidate.CandidateId} has screenplay drift: dialogue-only prose despite anti-screenplay duty.");
                requiredFixes.Add($"Add non-dialogue narration, interiority, sensory pressure, or transition work to candidate {candidate.CandidateId}.");
            }

            if (RequiresNovelisticExecution(beat) && IsActionOnly(candidate.Text))
            {
                aiRisks.Add($"Candidate {candidate.CandidateId} has action-only screenplay drift despite novelistic prose duties.");
                requiredFixes.Add($"Add interiority, sensory pressure, external evidence, transition work, or subtext to candidate {candidate.CandidateId}.");
            }
        }

        var status = provenanceErrors.Count == 0 &&
            blueprintErrors.Count == 0 &&
            unsupportedFactErrors.Count == 0 &&
            povErrors.Count == 0 &&
            requiredFixes.Count == 0
                ? "passed"
                : "failed";
        var rewriteLevel = candidates
            .Select(candidate => candidate.RewriteLevel)
            .OrderByDescending(RewriteLevelRank)
            .FirstOrDefault() ?? ReferenceRewriteLevels.L0;
        return new ReferenceAnchoredDraftAuditPayload(
            "draft-audit-" + Guid.NewGuid().ToString("N"),
            blueprint.BlueprintId,
            status,
            rewriteLevel,
            provenanceErrors,
            blueprintErrors,
            unsupportedFactErrors,
            povErrors,
            aiRisks,
            requiredFixes,
            now);
    }

    internal static IReadOnlyList<string> ExtractRequiredProsePhrases(ReferenceChapterBlueprintBeatPayload beat)
    {
        return new[]
            {
                beat.ExternalEvidence,
                beat.SensoryAnchorTarget,
                beat.SourceBackedDetailTarget,
                beat.CandidateRejectionRule
            }
            .SelectMany(ExtractRequiredPhrases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> FindNonPovInteriorKnowledgeLeaks(
        ReferenceChapterBlueprintBeatPayload beat,
        string candidateText)
    {
        if (string.IsNullOrWhiteSpace(beat.PovCharacter) || string.IsNullOrWhiteSpace(candidateText))
        {
            return [];
        }

        var povCharacter = beat.PovCharacter.Trim();
        return ExtractCandidateCharacterNames(beat)
            .Where(character => !string.Equals(character, povCharacter, StringComparison.OrdinalIgnoreCase))
            .Where(character => ContainsDirectInteriorKnowledge(candidateText, character))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractCandidateCharacterNames(ReferenceChapterBlueprintBeatPayload beat)
    {
        return beat.CharacterStatesBefore
            .Concat(beat.CharacterStatesAfter)
            .Concat(beat.CharacterGoals)
            .Concat(beat.CharacterMisbeliefs)
            .Concat(beat.RelationshipPressure)
            .SelectMany(ExtractCharacterNameCandidates)
            .Where(name => name.Length >= 2 && name.Length <= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ExtractCharacterNameCandidates(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var normalized = value.Trim();
        var labelled = Regex.Match(normalized, @"(?:角色|人物|character)\s*[:：]\s*([\u4e00-\u9fff]{2,4})", RegexOptions.IgnoreCase);
        if (labelled.Success)
        {
            yield return labelled.Groups[1].Value;
            yield break;
        }

        var prefix = Regex.Match(normalized, @"^([\u4e00-\u9fff]{2,4})(?:\s|:|：|，|,|。|$)");
        if (prefix.Success)
        {
            yield return prefix.Groups[1].Value;
        }
    }

    private static bool ContainsDirectInteriorKnowledge(string text, string character)
    {
        var escaped = Regex.Escape(character);
        return Regex.IsMatch(
            text,
            escaped + @"[^。！？!?；;\n]{0,12}(心里|心中|知道|明白|意识到|想到|想起|觉得|以为|察觉|记得|确信|后悔|害怕|担心|怀疑)",
            RegexOptions.IgnoreCase);
    }

    private static IReadOnlyList<string> FindUnsupportedHighRiskFacts(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat,
        ReferenceDraftParagraphCandidatePayload candidate)
    {
        var allowedFacts = blueprint.KnownFacts
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Concat(candidate.ChangedSlots.Select(slot => slot.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unsupported = new List<string>();
        foreach (var fact in ExtractHighRiskFactPhrases(candidate.Text))
        {
            if (allowedFacts.Any(allowed => allowed.Contains(fact, StringComparison.OrdinalIgnoreCase) ||
                    fact.Contains(allowed, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            unsupported.Add(fact);
        }

        return unsupported.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> ExtractHighRiskFactPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        const string pattern = @"[\u4e00-\u9fff]{1,8}(钥匙|身份|编号|名单|坐标|密码|证据|真相|实验|档案|密令|血样|账本|芯片|令牌|地图|遗书|录音|录像|照片|报告|坐标点)";
        return Regex.Matches(text, pattern)
            .Select(match => match.Value)
            .Where(value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ExtractRequiredEmotionEvidence(ReferenceChapterBlueprintBeatPayload beat)
    {
        return ExtractRequiredMarkers(
            beat.ExternalEvidence,
            ["required external evidence:", "required emotion evidence:"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool RequiresNovelisticExecution(ReferenceChapterBlueprintBeatPayload beat)
    {
        return !string.IsNullOrWhiteSpace(beat.AntiScreenplayDuty) ||
            beat.ProseDuties.Any(duty =>
                string.Equals(duty, "interiority", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(duty, "external_evidence", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(duty, "transition", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(duty, "sensory", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(duty, "subtext", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDialogueOnly(string text)
    {
        var lines = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        return lines.Length > 0 && lines.All(IsDialogueLine);
    }

    private static bool IsDialogueLine(string line)
    {
        return (line.StartsWith('“') && line.Contains("”", StringComparison.Ordinal)) ||
            (line.StartsWith('"') && line.LastIndexOf("\"", StringComparison.Ordinal) > 0) ||
            (line.StartsWith('「') && line.Contains("」", StringComparison.Ordinal)) ||
            line.StartsWith("-", StringComparison.Ordinal);
    }

    private static bool IsActionOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsDialogueOnly(text) || HasNovelisticNarrationEvidence(text))
        {
            return false;
        }

        var sentences = SplitSentences(text);
        if (sentences.Length < 2)
        {
            return false;
        }

        var actionLikeCount = sentences.Count(IsActionBlockingSentence);
        var shortSentenceCount = sentences.Count(sentence => sentence.Length <= 14);
        return actionLikeCount == sentences.Length && shortSentenceCount >= sentences.Length - 1;
    }

    private static string[] SplitSentences(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(new[] { '。', '！', '？', '!', '?', '.', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(sentence => sentence.Length > 0)
            .ToArray();
    }

    private static bool IsActionBlockingSentence(string sentence)
    {
        return ContainsAny(sentence,
        [
            "推门", "进门", "进去", "进来", "出来", "离开", "转身", "回头", "抬头", "低头",
            "点头", "摇头", "停住", "停下", "站住", "站起", "站着", "坐下", "坐着", "起身",
            "走", "跑", "退", "靠近", "避开", "伸手", "抬手", "放下", "拿起", "递", "接过",
            "打开", "关上", "合上", "盯", "看", "望", "问", "说", "开口", "沉默", "皱眉"
        ]);
    }

    private static bool HasNovelisticNarrationEvidence(string text)
    {
        return ContainsAny(text,
        [
            "心里", "心口", "心头", "胸口", "喉咙", "指尖", "背脊", "呼吸", "气息",
            "想起", "想到", "觉得", "意识到", "明白", "知道", "记得", "不敢", "忍住",
            "仿佛", "像是", "好像", "似乎", "分明", "因为", "所以", "只是", "却", "仍然",
            "雨声", "风声", "气味", "光影", "阴影", "冷意", "热意", "疼痛", "刺痛",
            "压低", "压住", "发紧", "发涩", "发凉", "发烫", "迟疑", "犹豫"
        ]);
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> markers)
    {
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExtractRequiredPhrases(string value)
    {
        return ExtractRequiredMarkers(value, ["required phrase:", "required:"]);
    }

    private static IEnumerable<string> ExtractRequiredMarkers(string value, IReadOnlyList<string> markers)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var marker in markers)
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var required = value[(index + marker.Length)..].Trim();
            if (required.Length == 0)
            {
                continue;
            }

            var separators = new[] { '\n', '\r', ';', '；' };
            var separatorIndex = required.IndexOfAny(separators);
            if (separatorIndex >= 0)
            {
                required = required[..separatorIndex].Trim();
            }

            required = required.Trim(' ', '\t', '"', '\'', '“', '”', '‘', '’', '.', '。');
            if (required.Length > 0)
            {
                yield return required;
            }
        }
    }

    private static int RewriteLevelRank(string rewriteLevel)
    {
        return rewriteLevel switch
        {
            ReferenceRewriteLevels.L0 => 0,
            ReferenceRewriteLevels.L1 => 1,
            ReferenceRewriteLevels.L2 => 2,
            ReferenceRewriteLevels.L3 => 3,
            ReferenceRewriteLevels.L4 => 4,
            _ => 99
        };
    }
}
