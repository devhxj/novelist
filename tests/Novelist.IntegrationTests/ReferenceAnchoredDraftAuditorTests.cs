using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceAnchoredDraftAuditorTests
{
    [Fact]
    public void BuildDraftAuditFailsWhenCandidateContainsForbiddenFact()
    {
        var blueprint = Blueprint(
            beat => beat,
            forbiddenFacts: ["凶手身份"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，凶手身份在门后闪了一下。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("凶手身份", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove forbidden fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnsupportedHighRiskFact()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，密室钥匙在门后闪了一下。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("密室钥匙", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove unsupported fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditAllowsHighRiskFactWhenItIsSceneFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SceneFacts = [.. beat.SceneFacts, "密室钥匙"]
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，密室钥匙在门后闪了一下。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("密室钥匙", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateLeaksNonPovCharacterInteriorKnowledge()
    {
        var blueprint = Blueprint(beat => beat with
        {
            PovCharacter = "林岚",
            CharacterStatesBefore = ["林岚 controlled", "周鸣 guarded"]
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣心里明白她已经看穿了他。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.PovErrors, item => item.Contains("周鸣", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("POV", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditAllowsPovCharacterInteriorKnowledge()
    {
        var blueprint = Blueprint(beat => beat with
        {
            PovCharacter = "林岚",
            CharacterStatesBefore = ["林岚 controlled", "周鸣 guarded"]
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚心里明白自己不能退。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.Empty(audit.PovErrors);
    }

    [Fact]
    public void BuildDraftAuditFailsWhenRequiredProseTargetIsMissing()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SourceBackedDetailTarget = "required phrase: 门口停住"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("required prose target", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("门口停住", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIsDialogueOnlyDespiteAntiScreenplayDuty()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(
            blueprint,
            """
            “你来了？”
            “我来了。”
            """);

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.AiProseRisks, item => item.Contains("screenplay", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("non-dialogue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIsActionOnlyDespiteNovelisticDuties()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ProseDuties = ["interiority", "external_evidence", "transition"],
            AntiScreenplayDuty = "show pressure beyond action"
        });
        var candidate = Candidate(blueprint, "他推门进去。她转身。两人沉默。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.AiProseRisks, item => item.Contains("action-only", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("interiority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenRequiredEmotionEvidenceIsMissing()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ExternalEvidence = "required external evidence: 指尖发紧"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，他在门口停了一会儿。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("emotion evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("指尖发紧", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractRequiredProsePhrasesReadsExplicitMarkersOnly()
    {
        var beat = Beat("1:beat:1") with
        {
            ExternalEvidence = "visible pause without hard target",
            SensoryAnchorTarget = "required: 雨声",
            SourceBackedDetailTarget = "required phrase: 门口停住；then keep cadence",
            CandidateRejectionRule = "reject action only"
        };

        var phrases = ReferenceAnchoredDraftAuditor.ExtractRequiredProsePhrases(beat);

        Assert.Equal(["雨声", "门口停住"], phrases);
    }

    [Fact]
    public void ExtractRequiredEmotionEvidenceReadsEvidenceMarkersOnly()
    {
        var beat = Beat("1:beat:1") with
        {
            ExternalEvidence = "required external evidence: 指尖发紧；visible pressure"
        };

        var evidence = ReferenceAnchoredDraftAuditor.ExtractRequiredEmotionEvidence(beat);

        Assert.Equal(["指尖发紧"], evidence);
    }

    private static ReferenceChapterBlueprintPayload Blueprint(
        Func<ReferenceChapterBlueprintBeatPayload, ReferenceChapterBlueprintBeatPayload> configureBeat,
        IReadOnlyList<string>? forbiddenFacts = null)
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
            "hook",
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
