using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceAnchoredDraftAuditorTests
{
    [Fact]
    public void BuildDraftAuditFailsWhenCandidateMissingMaterialProvenance()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸。") with
        {
            MaterialId = ""
        };

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.ProvenanceErrors, item => item.Contains("missing material provenance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenNoReuseProvenanceLacksBeatReason()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚心里一紧，指尖停住。") with
        {
            MaterialId = ReferenceDraftProvenanceIds.BuildNoReuseMaterialId(blueprint.Beats[0].BeatId)
        };

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.ProvenanceErrors, item => item.Contains("no-reuse provenance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenSourceBackedBeatUsesNoReuseProvenance()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SourceBackedDetailTarget = "source-backed rain pressure detail",
            NoReuseReason = "transition carries no reusable source material"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚心里一紧，指尖停住。") with
        {
            MaterialId = ReferenceDraftProvenanceIds.BuildNoReuseMaterialId(blueprint.Beats[0].BeatId)
        };

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.ProvenanceErrors, item => item.Contains("source-backed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.ProvenanceErrors, item => item.Contains("selected reference material", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenSelectedMaterialLinkIsLowConfidenceWeakMatch()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚心里一紧，指尖停住。");
        var link = new ReferenceBlueprintMaterialLinkPayload(
            "link-low-confidence",
            blueprint.BlueprintId,
            blueprint.Beats[0].BeatId,
            candidate.MaterialId,
            "show pressure",
            ReferenceRewriteLevels.L1,
            Selected: true,
            Score: 0.25,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["low_confidence"] = -1.0,
                ["function"] = 1.0
            },
            "Beat 1 fit: expanded query fallback weak match.",
            DateTimeOffset.UnixEpoch);

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal)
            {
                [blueprint.Beats[0].BeatId] = link
            });

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.ProvenanceErrors, item => item.Contains("low-confidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.ProvenanceErrors, item => item.Contains("weak match", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("stronger reference material", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenL2CandidateCopiesSelectedSourceMaterial()
    {
        const string sourceText = "雨声压低了整条街的呼吸，林岚在门口停住，指尖慢慢发紧，心里一紧。";
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, sourceText) with
        {
            RewriteLevel = ReferenceRewriteLevels.L2,
            AuditStatus = "passed"
        };
        var link = new ReferenceBlueprintMaterialLinkPayload(
            "link-source-leak",
            blueprint.BlueprintId,
            blueprint.Beats[0].BeatId,
            candidate.MaterialId,
            "show pressure",
            ReferenceRewriteLevels.L2,
            Selected: true,
            Score: 1,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["function"] = 1.0
            },
            "Beat 1 fit: selected material.",
            DateTimeOffset.UnixEpoch);

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal)
            {
                [blueprint.Beats[0].BeatId] = link
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [candidate.MaterialId] = sourceText
            });

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("source-leak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            audit.RequiredFixes,
            item => item.Contains("n-gram", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("source-span", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsForExactPhraseReuseWithoutLeakingCopiedPhraseIntoReport()
    {
        const string copiedPhrase = "雨声压低了整条街的呼吸，林岚在门口停住";
        const string sourceText = "她避开灯光，雨声压低了整条街的呼吸，林岚在门口停住，直到钥匙碰到掌心。";
        const string candidateText = "她先整理桌上的旧照片，把所有线索按时间放回抽屉，又绕到窗边确认楼下没有人影。雨声压低了整条街的呼吸，林岚在门口停住。随后她才把话题转回那封信，没有提任何人的名字。";
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, candidateText) with
        {
            RewriteLevel = ReferenceRewriteLevels.L2,
            AuditStatus = "passed"
        };
        var link = new ReferenceBlueprintMaterialLinkPayload(
            "link-exact-phrase-source-leak",
            blueprint.BlueprintId,
            blueprint.Beats[0].BeatId,
            candidate.MaterialId,
            "show pressure",
            ReferenceRewriteLevels.L2,
            Selected: true,
            Score: 1,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["function"] = 1.0
            },
            "Beat 1 fit: selected material.",
            DateTimeOffset.UnixEpoch);

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal)
            {
                [blueprint.Beats[0].BeatId] = link
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [candidate.MaterialId] = sourceText
            });

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("exact phrase", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(audit.ReadableReport);
        var report = audit.ReadableReport!;
        Assert.Contains(report.Findings, finding => finding.Message.Contains("exact phrase", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Findings, finding => finding.Message.Contains(copiedPhrase, StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, finding => finding.RequiredAction.Contains(copiedPhrase, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditUsesStrongStyleContractSourceLeakThresholds()
    {
        const string sourceText = "雨声压低了整条街的呼吸，林岚在门口停住，指节慢慢发紧，心里一紧。";
        const string candidateText = "雨声压低了街的呼吸，林岚却在门口停了一下，指节发紧，心里仍然发沉。";
        var blueprint = Blueprint(beat => beat with
        {
            StyleContract = new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [99],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                MinStyleFit: 1.0,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["source_leak"])
        });
        var candidate = Candidate(blueprint, candidateText) with
        {
            RewriteLevel = ReferenceRewriteLevels.L2,
            AuditStatus = "passed"
        };
        var link = new ReferenceBlueprintMaterialLinkPayload(
            "link-strong-source-leak",
            blueprint.BlueprintId,
            blueprint.Beats[0].BeatId,
            candidate.MaterialId,
            "show pressure",
            ReferenceRewriteLevels.L2,
            Selected: true,
            Score: 1,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["style_fit"] = 1.25
            },
            "Beat 1 fit: selected material with strong style fit.",
            DateTimeOffset.UnixEpoch);

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal)
            {
                [blueprint.Beats[0].BeatId] = link
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [candidate.MaterialId] = sourceText
            });

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("source-leak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("strong", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenSelectedMaterialStyleFitFallsBelowStyleContractMinimum()
    {
        var blueprint = Blueprint(beat => beat with
        {
            StyleContract = new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [99],
                StyleDimensions: ["sensory_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Moderate,
                MinStyleFit: 0.8,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["sentence"],
                ForbiddenStyleRisks: ["style_distance"])
        });
        var candidate = Candidate(
            blueprint,
            "雨声压低了整条街的呼吸，林岚心里一紧，指尖在杯沿发紧，却仍然没有后退。") with
        {
            RewriteLevel = ReferenceRewriteLevels.L2,
            AuditStatus = "passed"
        };
        var link = new ReferenceBlueprintMaterialLinkPayload(
            "link-low-style-fit",
            blueprint.BlueprintId,
            blueprint.Beats[0].BeatId,
            candidate.MaterialId,
            "show pressure",
            ReferenceRewriteLevels.L2,
            Selected: true,
            Score: 1,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["style_fit"] = 0.5
            },
            "Beat 1 fit: selected material with weak style fit.",
            DateTimeOffset.UnixEpoch);

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal)
            {
                [blueprint.Beats[0].BeatId] = link
            });

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("style-distance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("min_style_fit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIsFarFromRequiredProfileStyleFeature()
    {
        var blueprint = Blueprint(beat => beat with
        {
            StyleContract = new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [99],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                MinStyleFit: 0.8,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["style_distance"])
        });
        var candidate = Candidate(
            blueprint,
            "雨声压低了整条街的呼吸，林岚心里一紧，指尖在杯沿发紧，却仍然没有后退。") with
        {
            RewriteLevel = ReferenceRewriteLevels.L2,
            AuditStatus = "passed"
        };
        var link = new ReferenceBlueprintMaterialLinkPayload(
            "link-strong-style-fit",
            blueprint.BlueprintId,
            blueprint.Beats[0].BeatId,
            candidate.MaterialId,
            "show pressure",
            ReferenceRewriteLevels.L2,
            Selected: true,
            Score: 1,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["style_fit"] = 1.25
            },
            "Beat 1 fit: selected material with strong style fit.",
            DateTimeOffset.UnixEpoch);
        var profileFeatures = StyleProfiles(
            99,
            NumericStyleFeature("dialogue_ratio", 0.8, "ratio"));

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal)
            {
                [blueprint.Beats[0].BeatId] = link
            },
            selectedMaterialTextByMaterialId: null,
            profileFeatures);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("style-distance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("dialogue_ratio", StringComparison.OrdinalIgnoreCase));
    }

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
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnsupportedAccessCredential()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，旧楼门禁卡在抽屉底下露出一角。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("旧楼门禁卡", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove unsupported fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnsupportedLocationIdentifier()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，旧楼地址写在纸背面。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("旧楼地址", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove unsupported fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnsupportedSensitiveIdentifier()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚银行卡号622202、案号A17-42和病历号B91都写在纸背面。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("林岚银行卡号622202", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("案号A17-42", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("病历号B91", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove unsupported fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnsupportedLegalDocument()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，旧宅股权转让协议和地下室产权证明被压在账本下面。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("旧宅股权转让协议", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("地下室产权证明", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove unsupported fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnsupportedDangerousArtifact()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，消音手枪、炸药包、毒剂配方和伪造处方都被压在账本下面。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("消音手枪", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("炸药包", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("毒剂配方", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("伪造处方", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove unsupported fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnsupportedForensicEvidence()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，刀柄上的指纹、袖口纤维和DNA报告都被压在账本下面。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("刀柄上的指纹", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("袖口纤维", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("DNA报告", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove unsupported fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnsupportedCommunicationEvidence()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，手机里的聊天记录、通话记录和转账记录都被压在账本下面。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("聊天记录", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("通话记录", StringComparison.Ordinal));
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("转账记录", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("Remove unsupported fact", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditAllowsSensitiveIdentifierWhenItIsSceneFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SceneFacts = [.. beat.SceneFacts, "林岚银行卡号622202"]
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚银行卡号622202写在纸背面。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("林岚银行卡号622202", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditAllowsLegalDocumentWhenItIsSceneFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SceneFacts = [.. beat.SceneFacts, "旧宅股权转让协议"]
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，旧宅股权转让协议被塞进抽屉夹层。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("旧宅股权转让协议", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditAllowsDangerousArtifactWhenItIsSceneFact()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SceneFacts = [.. beat.SceneFacts, "消音手枪", "毒剂配方"]
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，消音手枪和毒剂配方被塞进抽屉夹层。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("消音手枪", StringComparison.Ordinal));
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("毒剂配方", StringComparison.Ordinal));
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
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnapprovedIdentityReveal()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣其实是卧底。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("周鸣是卧底", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("approved scene facts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditAllowsIdentityRevealWhenItIsKnownFact()
    {
        var blueprint = Blueprint(
            beat => beat,
            knownFacts: ["周鸣是卧底"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣其实是卧底。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("周鸣是卧底", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnapprovedRelationshipReveal()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣其实是林岚的哥哥。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("周鸣是林岚的哥哥", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditAllowsRelationshipRevealWhenItIsKnownFact()
    {
        var blueprint = Blueprint(
            beat => beat,
            knownFacts: ["周鸣是林岚的哥哥"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣其实是林岚的哥哥。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("周鸣是林岚的哥哥", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnapprovedDeathReveal()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，赵启其实早就死了。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("赵启死了", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditAllowsDeathRevealWhenItIsKnownFact()
    {
        var blueprint = Blueprint(
            beat => beat,
            knownFacts: ["赵启死了"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，赵启其实早就死了。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("赵启死了", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnapprovedDisappearanceReveal()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，陈砚三年前失踪。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("陈砚失踪", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateIntroducesUnapprovedPastEventReveal()
    {
        var blueprint = Blueprint(beat => beat);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣三年前杀了赵启。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.UnsupportedFactErrors, item => item.Contains("周鸣三年前杀了赵启", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditAllowsPastEventRevealWhenItIsKnownFact()
    {
        var blueprint = Blueprint(
            beat => beat,
            knownFacts: ["周鸣三年前杀了赵启"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣三年前杀了赵启。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("周鸣三年前杀了赵启", StringComparison.Ordinal));
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
    public void BuildDraftAuditFailsWhenCandidateNamesNonPovCharacterHiddenEmotion()
    {
        var blueprint = Blueprint(beat => beat with
        {
            PovCharacter = "林岚",
            CharacterStatesBefore = ["林岚 controlled", "周鸣 guarded"]
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣的恐惧终于从眼底漫了上来。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.PovErrors, item => item.Contains("周鸣", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("external evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateStatesNonPovCharacterHiddenIntention()
    {
        var blueprint = Blueprint(beat => beat with
        {
            PovCharacter = "林岚",
            CharacterStatesBefore = ["林岚 controlled", "周鸣 guarded"]
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，周鸣打算把她引开，却只把手按在门把上。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.PovErrors, item => item.Contains("周鸣", StringComparison.Ordinal));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("external evidence", StringComparison.OrdinalIgnoreCase));
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
    public void BuildDraftAuditFailsWhenCandidateBreaksCloseNarrativeDistance()
    {
        var blueprint = Blueprint(beat => beat with
        {
            NarrativeDistance = "close",
            PovCharacter = "林岚"
        });
        var candidate = Candidate(blueprint, "镜头拉远，读者可以看到周鸣藏在门后。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.PovErrors, item => item.Contains("narrative distance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("approved narrative distance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenLimitedPovRevealsUnperceivedFact()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                NarrativeDistance = "limited",
                PovCharacter = "林岚"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "周鸣袖口里的钥匙"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚心里一紧，却没有看见周鸣袖口里的钥匙。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.PovErrors, item => item.Contains("unperceived", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("钥匙", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenLimitedPovRevealsUnheardOffstageFact()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                NarrativeDistance = "limited",
                PovCharacter = "林岚"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "周鸣握住那把钥匙", "那把钥匙"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚听不见门外的动静，门外周鸣已经握住那把钥匙。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.PovErrors, item => item.Contains("offstage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(audit.UnsupportedFactErrors, item => item.Contains("钥匙", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenLimitedPovRevealsHiddenPositionBehindPovCharacter()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                NarrativeDistance = "limited",
                PovCharacter = "林岚"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "周鸣在门后"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚背对着门，周鸣在门后无声地站着。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.PovErrors, item => item.Contains("hidden position", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(audit.UnsupportedFactErrors);
    }

    [Fact]
    public void BuildDraftAuditFailsWhenLimitedPovRevealsBarrierSeparatedOffstageAction()
    {
        var blueprint = Blueprint(
            beat => beat with
            {
                NarrativeDistance = "limited",
                PovCharacter = "林岚"
            },
            knownFacts: ["雨声压低了整条街的呼吸", "周鸣按住门把"]);
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚隔着门，只听见雨声，门外周鸣正在抬手按住门把。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.PovErrors, item => item.Contains("barrier-separated", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(audit.UnsupportedFactErrors);
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
    public void BuildDraftAuditFailsWhenRequiredSubtextTargetIsMissing()
    {
        var blueprint = Blueprint(beat => beat with
        {
            SubtextPlan = "required: 没有回答"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚心里一紧，指尖停住。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("required prose target", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("没有回答", StringComparison.Ordinal));
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
    public void BuildDraftAuditAllowsExplicitShortDialogueExchange()
    {
        var blueprint = Blueprint(beat => beat with
        {
            BeatType = ReferenceBlueprintBeatTypes.DialogueExchange,
            ParagraphIntention = "allow short exchange before narration resumes",
            ExecutionMode = "short_exchange",
            AntiScreenplayDuty = "",
            ProseDuties = ["dialogue"],
            CandidateRejectionRule = "allow short exchange"
        });
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

        Assert.Equal("passed", audit.Status);
        Assert.Empty(audit.AiProseRisks);
    }

    [Fact]
    public void BuildDraftAuditRejectsLongDialogueDespiteShortExchangeAllowance()
    {
        var blueprint = Blueprint(beat => beat with
        {
            BeatType = ReferenceBlueprintBeatTypes.DialogueExchange,
            ParagraphIntention = "allow short exchange before narration resumes",
            ExecutionMode = "short_exchange",
            AntiScreenplayDuty = "",
            ProseDuties = ["dialogue"],
            CandidateRejectionRule = "allow short exchange"
        });
        var candidate = Candidate(
            blueprint,
            """
            “你来了？”
            “我来了。”
            “那就开始吧。”
            """);

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("anti-screenplay", StringComparison.OrdinalIgnoreCase));
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
    public void BuildDraftAuditFailsWhenCandidateIsBlockingOnlyDespiteNovelisticDuties()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ProseDuties = ["interiority", "external_evidence", "transition"],
            AntiScreenplayDuty = "show pressure beyond dialogue tags and blocking"
        });
        var candidate = Candidate(blueprint, "他说了一句。她转身。两人看着门。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.AiProseRisks, item => item.Contains("blocking-only", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("interiority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateHasNoEvidenceForDeclaredProseDuties()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ProseDuties = ["interiority", "external_evidence", "transition"],
            AntiScreenplayDuty = "show pressure through prose duties"
        });
        var candidate = Candidate(blueprint, "门开着，灯亮着，桌边有一只杯子。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("prose duty evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("interiority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenDelayedReactionDutyHasNoEvidence()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ProseDuties = ["delayed_reaction"],
            AntiScreenplayDuty = "show delayed reaction instead of direct action"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，门口的灯光照在他的肩上，冷意贴着袖口。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("delayed_reaction", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("delayed_reaction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditAllowsDelayedReactionDutyWhenCandidateShowsWithheldReaction()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ProseDuties = ["delayed_reaction"],
            AntiScreenplayDuty = "show delayed reaction instead of direct action"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，他到了门口，话到嘴边又咽了回去。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.BlueprintErrors, item => item.Contains("delayed_reaction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditFailsWhenCandidateViolatesParagraphExecutionContract()
    {
        var blueprint = Blueprint(beat => beat with
        {
            ParagraphIntention = "linger on the threshold before action",
            ExecutionMode = "dwell",
            CandidateRejectionRule = "reject movement-only prose",
            ProseDuties = ["interiority", "external_evidence", "transition"]
        });
        var candidate = Candidate(blueprint, "他推门进去。她转身。两人走开。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("paragraph intention", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("rejection rule", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("execution mode", StringComparison.OrdinalIgnoreCase));
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
    public void BuildDraftAuditFailsWhenEmotionChangesWithoutPlannedMechanicEvidence()
    {
        var blueprint = Blueprint(beat => beat with
        {
            EmotionBefore = "克制",
            EmotionAfter = "紧张",
            EmotionTrigger = "门缝里的血迹",
            SuppressedReaction = "咽下回答",
            ExternalEvidence = "指尖发紧"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，他在门口停了一会儿。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.BlueprintErrors, item => item.Contains("emotion mechanic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("指尖发紧", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDraftAuditAllowsEquivalentExternalEmotionEvidence()
    {
        var blueprint = Blueprint(beat => beat with
        {
            EmotionBefore = "克制",
            EmotionAfter = "紧张",
            EmotionTrigger = "门缝里的血迹",
            SuppressedReaction = "咽下回答",
            ExternalEvidence = "指尖发紧"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚心里一紧，手指在杯沿蜷紧，却仍然没有后退。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.BlueprintErrors, item => item.Contains("emotion mechanic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDraftAuditAllowsApprovedEmotionAfterStateEvidence()
    {
        var blueprint = Blueprint(beat => beat with
        {
            EmotionBefore = "克制",
            EmotionAfter = "紧张",
            EmotionTrigger = "门缝里的血迹",
            SuppressedReaction = "咽下回答",
            ExternalEvidence = "指尖发紧"
        });
        var candidate = Candidate(blueprint, "雨声压低了整条街的呼吸，林岚心里紧张，仍然没有后退。");

        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(
            blueprint,
            [candidate],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("passed", audit.Status);
        Assert.DoesNotContain(audit.BlueprintErrors, item => item.Contains("emotion mechanic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractRequiredProsePhrasesReadsExplicitMarkersOnly()
    {
        var beat = Beat("1:beat:1") with
        {
            ExternalEvidence = "visible pause without hard target",
            SensoryAnchorTarget = "required: 雨声",
            SubtextPlan = "required: 没有回答",
            SourceBackedDetailTarget = "required phrase: 门口停住；then keep cadence",
            CandidateRejectionRule = "reject action only"
        };

        var phrases = ReferenceAnchoredDraftAuditor.ExtractRequiredProsePhrases(beat);

        Assert.Equal(["雨声", "没有回答", "门口停住"], phrases);
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

    [Fact]
    public void ExtractAuditableFactPhrasesReadsRelationshipReveal()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，周鸣其实是林岚的哥哥。");

        Assert.Contains("周鸣是林岚的哥哥", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsConcealedSceneEvidence()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，旧宅暗门后面有一只药瓶。");

        Assert.Contains("旧宅暗门", facts);
        Assert.Contains("一只药瓶", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsAccessCredentialFacts()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，旧楼门禁卡和地下室通行证被压在账本下面。");

        Assert.Contains("旧楼门禁卡", facts);
        Assert.Contains("地下室通行证", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsLocationIdentifierFacts()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，旧楼地址和地下室房间号都写在纸背面。");

        Assert.Contains("旧楼地址", facts);
        Assert.Contains("地下室房间号", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsSensitiveIdentifierFacts()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，林岚银行卡号622202、案号A17-42、病历号B91和车牌号A12345都写在纸背面。");

        Assert.Contains("林岚银行卡号622202", facts);
        Assert.Contains("案号A17-42", facts);
        Assert.Contains("病历号B91", facts);
        Assert.Contains("车牌号A12345", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsLegalDocumentFacts()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，旧宅股权转让协议和地下室产权证明被压在账本下面。");

        Assert.Contains("旧宅股权转让协议", facts);
        Assert.Contains("地下室产权证明", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsDangerousArtifactFacts()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，消音手枪、炸药包、毒剂配方和伪造处方都被压在账本下面。");

        Assert.Contains("消音手枪", facts);
        Assert.Contains("炸药包", facts);
        Assert.Contains("毒剂配方", facts);
        Assert.Contains("伪造处方", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsForensicEvidenceFacts()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，刀柄上的指纹、袖口纤维、鞋印和DNA报告都被压在账本下面。");

        Assert.Contains("刀柄上的指纹", facts);
        Assert.Contains("袖口纤维", facts);
        Assert.Contains("鞋印", facts);
        Assert.Contains("DNA报告", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsCommunicationEvidenceFacts()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，手机里的聊天记录、通话记录和转账记录都被压在账本下面。");

        Assert.Contains("聊天记录", facts);
        Assert.Contains("通话记录", facts);
        Assert.Contains("转账记录", facts);
    }

    [Fact]
    public void ExtractAuditableFactPhrasesReadsDeathDisappearanceAndPastEventReveals()
    {
        var facts = ReferenceAnchoredDraftAuditor.ExtractAuditableFactPhrases(
            "雨声压低了整条街的呼吸，赵启其实早就死了，陈砚三年前失踪，周鸣三年前杀了赵启。");

        Assert.Contains("赵启死了", facts);
        Assert.Contains("陈砚失踪", facts);
        Assert.Contains("周鸣三年前杀了赵启", facts);
    }

    private static ReferenceChapterBlueprintPayload Blueprint(
        Func<ReferenceChapterBlueprintBeatPayload, ReferenceChapterBlueprintBeatPayload> configureBeat,
        IReadOnlyList<string>? forbiddenFacts = null,
        IReadOnlyList<string>? knownFacts = null)
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

    private static IReadOnlyDictionary<long, ReferenceStyleFeatureVectorPayload> StyleProfiles(
        long profileId,
        params ReferenceStyleNumericFeaturePayload[] numericFeatures)
    {
        return new Dictionary<long, ReferenceStyleFeatureVectorPayload>
        {
            [profileId] = new ReferenceStyleFeatureVectorPayload(
                numericFeatures,
                [],
                [])
        };
    }

    private static ReferenceStyleNumericFeaturePayload NumericStyleFeature(string featureKey, double value, string unit)
    {
        return new ReferenceStyleNumericFeaturePayload(
            featureKey,
            value,
            unit,
            1,
            []);
    }
}
