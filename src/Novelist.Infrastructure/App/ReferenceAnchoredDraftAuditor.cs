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
            else if (ReferenceDraftProvenanceIds.IsNoReuseMaterialId(candidate.MaterialId) &&
                string.IsNullOrWhiteSpace(beat.NoReuseReason))
            {
                provenanceErrors.Add($"Candidate {candidate.CandidateId} uses no-reuse provenance but the blueprint beat has no approved no-reuse reason.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Text))
            {
                blueprintErrors.Add($"Candidate {candidate.CandidateId} text is empty.");
            }

            if (string.Equals(candidate.AuditStatus, "failed", StringComparison.Ordinal))
            {
                requiredFixes.Add($"Candidate {candidate.CandidateId} failed reference reuse audit.");
            }

            var allowsShortDialogueExchange = AllowsExplicitShortDialogueExchange(beat, candidate.Text);
            if ((string.Equals(beat.BeatType, ReferenceBlueprintBeatTypes.DialogueExchange, StringComparison.Ordinal) ||
                    beat.ProseDuties.All(duty => string.Equals(duty, "dialogue", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(duty, "action", StringComparison.OrdinalIgnoreCase))) &&
                string.IsNullOrWhiteSpace(beat.AntiScreenplayDuty) &&
                !allowsShortDialogueExchange)
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

            foreach (var distanceViolation in FindNarrativeDistanceViolations(beat, candidate.Text))
            {
                povErrors.Add($"Candidate {candidate.CandidateId} violates narrative distance: {distanceViolation}");
                requiredFixes.Add($"Keep candidate {candidate.CandidateId} inside the approved narrative distance: {distanceViolation}");
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

            foreach (var unsupportedFact in FindUnsupportedFacts(blueprint, beat, candidate))
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

            var emotionMechanics = ExtractPlannedEmotionMechanics(beat);
            if (emotionMechanics.Count > 0 &&
                !HasPlannedEmotionMechanicEvidence(candidate.Text, emotionMechanics))
            {
                var requiredMechanics = string.Join(", ", emotionMechanics);
                blueprintErrors.Add($"Candidate {candidate.CandidateId} misses planned emotion mechanic evidence: {requiredMechanics}");
                requiredFixes.Add($"Ground candidate {candidate.CandidateId} in at least one approved emotion mechanic: {requiredMechanics}");
            }

            var missingProseDuties = FindMissingProseDutyEvidence(beat, candidate.Text);
            if (missingProseDuties.Count > 0)
            {
                var duties = string.Join(", ", missingProseDuties);
                blueprintErrors.Add($"Candidate {candidate.CandidateId} misses prose duty evidence for: {duties}");
                requiredFixes.Add($"Add observable prose duty evidence to candidate {candidate.CandidateId}: {duties}");
            }

            foreach (var violation in FindExecutionContractViolations(beat, candidate.Text))
            {
                blueprintErrors.Add($"Candidate {candidate.CandidateId} violates blueprint execution contract: {violation}");
                requiredFixes.Add($"Revise candidate {candidate.CandidateId} to satisfy paragraph intention, execution mode, and rejection rule: {violation}");
            }

            if (RequiresNovelisticExecution(beat) &&
                IsDialogueOnly(candidate.Text) &&
                !allowsShortDialogueExchange)
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
                beat.SubtextPlan,
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
            .Where(character => ContainsDirectInteriorKnowledge(candidateText, character) ||
                ContainsNamedHiddenState(candidateText, character))
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

    private static bool ContainsNamedHiddenState(string text, string character)
    {
        var escaped = Regex.Escape(character);
        return Regex.IsMatch(
            text,
            escaped + @"的(恐惧|害怕|惧意|歉意|愧疚|后悔|悔意|怒意|愤怒|嫉妒|犹豫|迟疑|怀疑|算计|念头|想法|决心|恶意|杀意|贪念|不甘|绝望|希望|得意|慌乱)",
            RegexOptions.IgnoreCase);
    }

    private static IReadOnlyList<string> FindNarrativeDistanceViolations(
        ReferenceChapterBlueprintBeatPayload beat,
        string candidateText)
    {
        if (string.IsNullOrWhiteSpace(candidateText) ||
            !ContainsAny(beat.NarrativeDistance, ["close", "limited", "贴近", "近距离", "有限视角"]))
        {
            return [];
        }

        var violations = new List<string>();
        if (ContainsAny(
            candidateText,
            ["镜头", "画面切", "画面拉", "上帝视角", "全知", "读者可以看到", "we see", "camera"]))
        {
            violations.Add("close narrative distance cannot use camera or omniscient framing.");
        }

        violations.AddRange(FindUnperceivedPovFactReveals(beat, candidateText));
        violations.AddRange(FindHiddenPositionBehindPovReveals(beat, candidateText));
        return violations;
    }

    private static IEnumerable<string> FindUnperceivedPovFactReveals(
        ReferenceChapterBlueprintBeatPayload beat,
        string candidateText)
    {
        if (string.IsNullOrWhiteSpace(beat.PovCharacter))
        {
            yield break;
        }

        var povCharacter = Regex.Escape(beat.PovCharacter.Trim());
        const string negativePerception = "(?:看不见|没看见|没有看见|看不到|不知道|并不知道|没有察觉|未曾发现|没有发现|无从知道)";
        var pattern = povCharacter + @"[^。！？!?；;\n]{0,8}" + negativePerception + @"(?<reveal>[^。！？!?；;\n]{2,30})";
        foreach (Match match in Regex.Matches(candidateText, pattern, RegexOptions.IgnoreCase))
        {
            var reveal = match.Groups["reveal"].Value.Trim(' ', '\t', '，', ',', '。', '.', '；', ';');
            if (ContainsAny(
                reveal,
                ["身后", "背后", "门后", "屋内", "暗处", "袖口", "口袋", "抽屉", "里", "内", "藏", "已经", "正", "正在", "真相", "身份", "钥匙", "证据", "文件", "血迹", "暗门"]))
            {
                yield return "limited POV cannot reveal unperceived facts to the reader.";
            }
        }
    }

    private static IEnumerable<string> FindHiddenPositionBehindPovReveals(
        ReferenceChapterBlueprintBeatPayload beat,
        string candidateText)
    {
        if (string.IsNullOrWhiteSpace(beat.PovCharacter))
        {
            yield break;
        }

        var povCharacter = Regex.Escape(beat.PovCharacter.Trim());
        var povCannotSeeBehind = Regex.IsMatch(
            candidateText,
            povCharacter + @"[^。！？!?；;\n]{0,18}(背对着|背对|没有回头|没回头|未回头|不曾回头|背身)",
            RegexOptions.IgnoreCase);
        if (!povCannotSeeBehind)
        {
            yield break;
        }

        if (Regex.IsMatch(
            candidateText,
            @"(门后|身后|背后|暗处|阴影里|走廊尽头)[^。！？!?；;\n]{0,20}(站着|藏|躲|握|看着|盯着|等着|无声|出现)",
            RegexOptions.IgnoreCase))
        {
            yield return "limited POV cannot reveal hidden position behind the POV character.";
        }
    }

    private static IReadOnlyList<string> FindUnsupportedFacts(
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
        foreach (var fact in ExtractAuditableFactPhrases(candidate.Text))
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

    internal static IReadOnlyList<string> ExtractAuditableFactPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var facts = new List<string>();
        const string pattern = @"[\u4e00-\u9fff]{1,8}(钥匙|身份|编号|名单|坐标|密码|证据|真相|实验|档案|密令|血样|账本|芯片|令牌|地图|遗书|录音|录像|照片|报告|坐标点|尸体|血迹|密道|暗门|标记|信件|纸条|伤口|弹孔|药瓶|匕首)";
        facts.AddRange(Regex.Matches(text, pattern)
            .Select(match => NormalizeAuditableFactPhrase(match.Value))
            .Where(value => value.Length >= 2));
        facts.AddRange(ExtractAccessCredentialFacts(text));
        facts.AddRange(ExtractIdentityRevealFacts(text));
        facts.AddRange(ExtractRelationshipRevealFacts(text));
        facts.AddRange(ExtractDeathOrDisappearanceRevealFacts(text));
        facts.AddRange(ExtractPastEventRevealFacts(text));
        return facts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeAuditableFactPhrase(string value)
    {
        var normalized = value.Trim(' ', '\t', '\r', '\n', '，', ',', '。', '.', '；', ';', '：', ':');
        foreach (var marker in new[] { "后面有", "里面有", "里有", "藏着", "放着", "压着", "写着", "记录着", "发现了", "发现", "看到", "看见" })
        {
            var index = normalized.LastIndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                normalized = normalized[(index + marker.Length)..].Trim();
            }
        }

        return normalized;
    }

    private static IEnumerable<string> ExtractAccessCredentialFacts(string text)
    {
        const string credentialTerms = "门禁卡|门卡|房卡|通行证|工牌|胸牌|证件|钥匙卡|身份卡|ID卡|id卡|U盘|硬盘|存储卡";
        foreach (Match match in Regex.Matches(
            text,
            @"(?:^|[，。！？；;,\s、]|和|与)(?<fact>[\u4e00-\u9fffA-Za-z0-9]{0,8}(?:" + credentialTerms + @"))"))
        {
            var fact = match.Groups["fact"].Value.Trim('，', ',', '。', '.', '；', ';', '！', '!', '？', '?', '、');
            if (fact.Length >= 2)
            {
                yield return fact;
            }
        }
    }

    private static IEnumerable<string> ExtractIdentityRevealFacts(string text)
    {
        const string identityRoles = "卧底|内鬼|凶手|叛徒|实验体|继承人|线人|替身|死者|嫌疑人|共犯|主谋";
        foreach (Match match in Regex.Matches(
            text,
            @"(?<name>[\u4e00-\u9fff]{2,4}?)(?:其实|原来|竟然|真正|真实)是(?<role>" + identityRoles + ")"))
        {
            var name = match.Groups["name"].Value;
            if (IsValidIdentitySubject(name))
            {
                yield return name + "是" + match.Groups["role"].Value;
            }
        }

        foreach (Match match in Regex.Matches(
            text,
            @"(?<name>[\u4e00-\u9fff]{2,4})是(?<role>" + identityRoles + ")"))
        {
            var name = match.Groups["name"].Value;
            if (IsValidIdentitySubject(name))
            {
                yield return name + "是" + match.Groups["role"].Value;
            }
        }

        foreach (Match match in Regex.Matches(
            text,
            @"(?<name>[\u4e00-\u9fff]{2,4})(?:的)?(?:真实身份|真正身份|身份)(?:是|为)(?<role>" + identityRoles + ")"))
        {
            var name = match.Groups["name"].Value;
            if (IsValidIdentitySubject(name))
            {
                yield return name + "是" + match.Groups["role"].Value;
            }
        }
    }

    private static bool IsValidIdentitySubject(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            !ContainsAny(name, ["其实", "原来", "竟然", "真正", "真实"]);
    }

    private static IEnumerable<string> ExtractRelationshipRevealFacts(string text)
    {
        const string relationshipRoles = "父亲|母亲|哥哥|姐姐|弟弟|妹妹|儿子|女儿|师父|老师|学生|徒弟|未婚夫|未婚妻|丈夫|妻子|恋人|旧友|仇人|上司|下属|同伴|盟友";
        foreach (Match match in Regex.Matches(
            text,
            @"(?<name>[\u4e00-\u9fff]{2,4}?)(?:其实|原来|竟然|真正|真实)?是(?<target>[\u4e00-\u9fff]{2,4}?)的(?<role>" + relationshipRoles + ")"))
        {
            var name = match.Groups["name"].Value;
            var target = match.Groups["target"].Value;
            if (IsValidRelationshipSubject(name, target))
            {
                yield return name + "是" + target + "的" + match.Groups["role"].Value;
            }
        }

        foreach (Match match in Regex.Matches(
            text,
            @"(?<target>[\u4e00-\u9fff]{2,4}?)的(?<role>" + relationshipRoles + @")(?:其实|原来|竟然|真正|真实)?是(?<name>[\u4e00-\u9fff]{2,4}?)"))
        {
            var name = match.Groups["name"].Value;
            var target = match.Groups["target"].Value;
            if (IsValidRelationshipSubject(name, target))
            {
                yield return name + "是" + target + "的" + match.Groups["role"].Value;
            }
        }
    }

    private static bool IsValidRelationshipSubject(string name, string target)
    {
        return IsValidIdentitySubject(name) &&
            IsValidIdentitySubject(target) &&
            !string.Equals(name, target, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractDeathOrDisappearanceRevealFacts(string text)
    {
        const string revealMarkers = @"(?:其实|原来|竟然|早就|已经|早已|三年前|两年前|一年前|多年前|多年以前|当年|那年|昨夜|昨晚|当晚|此前|过去|曾经|曾)";
        const string states = "死了|死亡|遇害|被杀|失踪|消失";
        foreach (Match match in Regex.Matches(
            text,
            @"(?:^|[，。！？；;,\s])(?<name>[\u4e00-\u9fff]{2,4}?)(?:" + revealMarkers + @"){0,3}(?<state>" + states + ")"))
        {
            var name = match.Groups["name"].Value;
            if (!IsValidIdentitySubject(name))
            {
                continue;
            }

            var state = match.Groups["state"].Value switch
            {
                "死亡" or "遇害" or "被杀" => "死了",
                "消失" => "失踪",
                var value => value
            };
            yield return name + state;
        }
    }

    private static IEnumerable<string> ExtractPastEventRevealFacts(string text)
    {
        const string timeMarkers = "三年前|两年前|一年前|十年前|多年前|多年以前|当年|那年|昨夜|昨晚|当晚|此前|过去|曾经|曾|早年";
        const string actions = "放火烧了|杀死|害死|杀了|绑架|袭击|刺伤|烧毁|烧了|偷走|盗走|藏起|出卖|背叛|救过|见过|带走|送走|伪造";
        foreach (Match match in Regex.Matches(
            text,
            @"(?:^|[，。！？；;,\s])(?<subject>[\u4e00-\u9fff]{2,4})(?<time>" + timeMarkers + ")(?<action>" + actions + @")(?<object>[\u4e00-\u9fff]{2,8})"))
        {
            var subject = match.Groups["subject"].Value;
            var target = match.Groups["object"].Value.Trim('，', ',', '。', '.', '；', ';', '！', '!', '？', '?');
            if (IsValidIdentitySubject(subject) &&
                target.Length is >= 2 and <= 8 &&
                !string.Equals(subject, target, StringComparison.OrdinalIgnoreCase))
            {
                yield return subject + match.Groups["time"].Value + match.Groups["action"].Value + target;
            }
        }
    }

    internal static IReadOnlyList<string> ExtractRequiredEmotionEvidence(ReferenceChapterBlueprintBeatPayload beat)
    {
        return ExtractRequiredMarkers(
            beat.ExternalEvidence,
            ["required external evidence:", "required emotion evidence:"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ExtractPlannedEmotionMechanics(ReferenceChapterBlueprintBeatPayload beat)
    {
        if (string.Equals(beat.EmotionBefore, beat.EmotionAfter, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return new[] { beat.EmotionTrigger, beat.SuppressedReaction, beat.ExternalEvidence, beat.EmotionAfter }
            .SelectMany(ExtractChineseMechanicPhrases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ExtractChineseMechanicPhrases(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("required", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(value, @"[\u4e00-\u9fff]{2,12}"))
        {
            var phrase = match.Value.Trim();
            if (phrase.Length >= 2 && !IsGenericEmotionMechanicPhrase(phrase))
            {
                yield return phrase;
            }
        }
    }

    private static bool IsGenericEmotionMechanicPhrase(string phrase)
    {
        return phrase is "情绪" or "反应" or "外部证据" or "触发" or "压抑反应" or "可见证据" or "变化";
    }

    private static bool HasPlannedEmotionMechanicEvidence(string candidateText, IReadOnlyList<string> mechanics)
    {
        return mechanics.Any(mechanic => candidateText.Contains(mechanic, StringComparison.OrdinalIgnoreCase) ||
            HasEquivalentChineseEmotionMechanicEvidence(mechanic, candidateText));
    }

    private static bool HasEquivalentChineseEmotionMechanicEvidence(string mechanic, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(mechanic) || string.IsNullOrWhiteSpace(candidateText))
        {
            return false;
        }

        if (ContainsAny(mechanic, ["指尖", "手指", "指节", "掌心", "手心", "拳"]) &&
            ContainsAny(candidateText, ["指尖", "手指", "指节", "掌心", "手心", "拳"]) &&
            ContainsAny(candidateText, ["发紧", "收紧", "绷紧", "攥紧", "捏紧", "蜷紧", "僵住", "发僵", "发颤", "颤", "抖"]))
        {
            return true;
        }

        if (ContainsAny(mechanic, ["喉咙", "嗓子", "声音", "呼吸", "气息"]) &&
            ContainsAny(candidateText, ["喉咙", "嗓子", "声音", "呼吸", "气息"]) &&
            ContainsAny(candidateText, ["发紧", "发涩", "发哑", "压低", "停住", "滞住", "咽", "堵"]))
        {
            return true;
        }

        if (ContainsAny(mechanic, ["咽下", "咽回", "忍住", "压下", "憋住", "没有回答", "没回答"]) &&
            ContainsAny(candidateText, ["咽下", "咽回", "忍住", "压下", "憋住", "没有回答", "没回答", "沉默", "停顿"]))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> FindMissingProseDutyEvidence(
        ReferenceChapterBlueprintBeatPayload beat,
        string candidateText)
    {
        var duties = beat.ProseDuties
            .Select(NormalizeProseDuty)
            .Where(IsAuditableProseDuty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duties.Length == 0 || string.IsNullOrWhiteSpace(candidateText))
        {
            return [];
        }

        return duties.Any(duty => HasProseDutyEvidence(duty, beat, candidateText))
            ? []
            : duties;
    }

    private static string NormalizeProseDuty(string duty)
    {
        return string.IsNullOrWhiteSpace(duty)
            ? string.Empty
            : duty.Trim().Replace('-', '_').ToLowerInvariant();
    }

    private static bool IsAuditableProseDuty(string duty)
    {
        return duty is "interiority" or "external_evidence" or "transition" or "causality" or
            "sensory" or "sensory_anchor" or "subtext" or "source_detail" or "source_backed_detail";
    }

    private static bool HasProseDutyEvidence(
        string duty,
        ReferenceChapterBlueprintBeatPayload beat,
        string candidateText)
    {
        return duty switch
        {
            "interiority" => HasInteriorityEvidence(candidateText),
            "external_evidence" => HasExternalEvidence(beat, candidateText),
            "transition" or "causality" => HasTransitionEvidence(candidateText),
            "sensory" or "sensory_anchor" => HasSensoryEvidence(candidateText),
            "subtext" => HasSubtextEvidence(candidateText),
            "source_detail" or "source_backed_detail" => HasSourceDetailEvidence(beat, candidateText),
            _ => false
        };
    }

    private static IReadOnlyList<string> FindExecutionContractViolations(
        ReferenceChapterBlueprintBeatPayload beat,
        string candidateText)
    {
        var violations = new List<string>();
        if (RequiresDwellExecutionEvidence(beat) && !HasDwellExecutionEvidence(beat, candidateText))
        {
            violations.Add("paragraph intention or execution mode requires dwell/linger evidence before action.");
        }

        if (ViolatesCandidateRejectionRule(beat.CandidateRejectionRule, candidateText))
        {
            violations.Add("candidate matches the beat candidate rejection rule.");
        }

        return violations;
    }

    private static bool RequiresDwellExecutionEvidence(ReferenceChapterBlueprintBeatPayload beat)
    {
        return ContainsAny(
                beat.ParagraphIntention,
                ["dwell", "linger", "hold", "slow", "threshold", "停留", "放慢", "迟疑", "犹豫", "门槛"]) ||
            ContainsAny(
                beat.ExecutionMode,
                ["dwell", "linger", "hold", "slow", "停留", "放慢", "迟疑", "犹豫"]);
    }

    private static bool HasDwellExecutionEvidence(ReferenceChapterBlueprintBeatPayload beat, string candidateText)
    {
        return HasInteriorityEvidence(candidateText) ||
            HasExternalEvidence(beat, candidateText) ||
            HasSensoryEvidence(candidateText) ||
            HasSubtextEvidence(candidateText) ||
            HasTransitionEvidence(candidateText);
    }

    private static bool ViolatesCandidateRejectionRule(string rejectionRule, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(rejectionRule))
        {
            return false;
        }

        var normalized = rejectionRule.Trim()
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal)
            .ToLowerInvariant();
        if ((ContainsAny(normalized, ["action_only", "movement_only"]) ||
                ContainsAny(rejectionRule, ["纯动作", "只有动作", "只有走位", "动作流水"])) &&
            IsActionOnly(candidateText))
        {
            return true;
        }

        if ((ContainsAny(normalized, ["dialogue_only"]) ||
                ContainsAny(rejectionRule, ["纯对话", "只有对话"])) &&
            IsDialogueOnly(candidateText))
        {
            return true;
        }

        return false;
    }

    private static bool HasInteriorityEvidence(string text)
    {
        return ContainsAny(text,
        [
            "心里", "心中", "心口", "心头", "想到", "想起", "觉得", "意识到", "明白",
            "知道", "记得", "以为", "怀疑", "确信", "后悔", "害怕", "担心", "不敢", "忍住"
        ]);
    }

    private static bool HasExternalEvidence(ReferenceChapterBlueprintBeatPayload beat, string text)
    {
        return ContainsNonMarkerTarget(text, beat.ExternalEvidence) ||
            ContainsAny(text,
            [
                "指尖", "手指", "掌心", "喉咙", "胸口", "肩", "背脊", "呼吸", "气息",
                "目光", "眼神", "声音", "唇", "停顿", "沉默", "发紧", "发涩", "发凉",
                "颤", "抖", "僵", "避开", "咽下", "攥", "雨声", "风声", "冷意", "压低", "压住"
            ]);
    }

    private static bool HasTransitionEvidence(string text)
    {
        return ContainsAny(text,
        [
            "因为", "所以", "于是", "但", "但是", "可", "可是", "却", "只是", "仍然",
            "直到", "这才", "下一刻", "先前", "刚才", "后来", "随即", "转而"
        ]);
    }

    private static bool HasSensoryEvidence(string text)
    {
        return ContainsAny(text,
        [
            "雨声", "风声", "脚步声", "气味", "光", "影", "阴影", "冷意", "热意",
            "疼", "刺痛", "发凉", "发烫", "潮", "湿", "灰尘", "血腥", "灯", "门缝"
        ]);
    }

    private static bool HasSubtextEvidence(string text)
    {
        return ContainsAny(text,
        [
            "没说", "没有说", "不说", "沉默", "停顿", "避开", "咽下", "忍住",
            "压下", "装作", "像没听见", "没有回答"
        ]);
    }

    private static bool HasSourceDetailEvidence(ReferenceChapterBlueprintBeatPayload beat, string text)
    {
        return ContainsNonMarkerTarget(text, beat.SourceBackedDetailTarget) ||
            ContainsNonMarkerTarget(text, beat.SensoryAnchorTarget);
    }

    private static bool ContainsNonMarkerTarget(string text, string target)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var normalizedTarget = target.Trim();
        if (normalizedTarget.Contains("required", StringComparison.OrdinalIgnoreCase) ||
            normalizedTarget.Contains(":", StringComparison.Ordinal) ||
            normalizedTarget.Contains("：", StringComparison.Ordinal))
        {
            return false;
        }

        return normalizedTarget.Length >= 2 &&
            text.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase);
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

    private static bool AllowsExplicitShortDialogueExchange(
        ReferenceChapterBlueprintBeatPayload beat,
        string candidateText)
    {
        if (!IsShortDialogueExchange(candidateText) ||
            !IsDialogueBeat(beat) ||
            !HasShortExchangeAllowance(beat))
        {
            return false;
        }

        return true;
    }

    private static bool IsDialogueBeat(ReferenceChapterBlueprintBeatPayload beat)
    {
        return string.Equals(beat.BeatType, ReferenceBlueprintBeatTypes.DialogueExchange, StringComparison.Ordinal) ||
            beat.ProseDuties.Any(duty => string.Equals(duty, "dialogue", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasShortExchangeAllowance(ReferenceChapterBlueprintBeatPayload beat)
    {
        return new[]
            {
                beat.ParagraphIntention,
                beat.ExecutionMode,
                beat.CandidateRejectionRule,
                beat.NarrationStrategy,
                beat.RhythmStrategy,
                beat.AntiScreenplayDuty
            }
            .Concat(beat.ProseDuties)
            .Any(ContainsShortExchangeMarker);
    }

    private static bool ContainsShortExchangeMarker(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim()
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("short_exchange", StringComparison.Ordinal) ||
            normalized.Contains("brief_exchange", StringComparison.Ordinal) ||
            normalized.Contains("short_dialogue", StringComparison.Ordinal) ||
            normalized.Contains("brief_dialogue", StringComparison.Ordinal) ||
            ContainsAny(value, ["短交流", "短对话", "简短对话"]);
    }

    private static bool IsShortDialogueExchange(string text)
    {
        var lines = DialogueCandidateLines(text);
        return lines.Length is > 0 and <= 2 && lines.All(IsDialogueLine);
    }

    private static bool IsDialogueOnly(string text)
    {
        var lines = DialogueCandidateLines(text);
        return lines.Length > 0 && lines.All(IsDialogueLine);
    }

    private static string[] DialogueCandidateLines(string text)
    {
        return (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
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
