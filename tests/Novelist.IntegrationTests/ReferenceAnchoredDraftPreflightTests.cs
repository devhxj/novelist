using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceAnchoredDraftPreflightTests
{
    [Fact]
    public void EnsureDraftGenerationAllowedRejectsStaleBlueprint()
    {
        var blueprint = Blueprint(status: ReferenceBlueprintStates.Stale);

        var exception = Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureDraftGenerationAllowed(blueprint));

        Assert.Contains("Stale blueprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureDraftGenerationAllowedRejectsReviewHashMismatch()
    {
        var blueprint = Blueprint(review: Review(analysisHash: "old-analysis-hash"));

        var exception = Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureDraftGenerationAllowed(blueprint));

        Assert.Contains("current passing blueprint review", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewMatchesBlueprintRejectsReviewFromDifferentBlueprint()
    {
        var blueprint = Blueprint();
        var otherBlueprintReview = Review(blueprintId: blueprint.BlueprintId + 1);

        Assert.False(ReferenceAnchoredDraftPreflight.ReviewMatchesBlueprint(blueprint, otherBlueprintReview));
        Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureCurrentPassingReview(
                blueprint with { LatestReview = otherBlueprintReview },
                "Material binding"));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("source")]
    [InlineData("analysis")]
    public void ReviewMatchesBlueprintRejectsReviewHashMismatch(string hashName)
    {
        var blueprint = Blueprint();
        var mismatchedReview = hashName switch
        {
            "context" => Review(contextHash: "old-context-hash"),
            "source" => Review(sourceHash: "old-source-hash"),
            "analysis" => Review(analysisHash: "old-analysis-hash"),
            _ => throw new ArgumentOutOfRangeException(nameof(hashName), hashName, "Unsupported hash name.")
        };

        Assert.False(ReferenceAnchoredDraftPreflight.ReviewMatchesBlueprint(blueprint, mismatchedReview));
        Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureCurrentPassingReview(
                blueprint with { LatestReview = mismatchedReview },
                "Material binding"));
    }

    [Fact]
    public void ReviewMatchesBlueprintRejectsReviewFromDifferentReviewVersion()
    {
        var blueprint = Blueprint();
        var oldVersionReview = Review(reviewVersion: ReferenceChapterBlueprintReviewer.CurrentReviewVersion + 1);

        Assert.False(ReferenceAnchoredDraftPreflight.ReviewMatchesBlueprint(blueprint, oldVersionReview));
        Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureCurrentPassingReview(
                blueprint with { LatestReview = oldVersionReview },
                "Material binding"));
    }

    [Fact]
    public void SelectTargetBeatsTrimsRequestedIdsAndRejectsMissingTargets()
    {
        var blueprint = Blueprint();

        var selected = ReferenceAnchoredDraftPreflight.SelectTargetBeats(blueprint, [" 1:beat:1 "]);

        var beat = Assert.Single(selected);
        Assert.Equal("1:beat:1", beat.BeatId);
        Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.SelectTargetBeats(blueprint, ["missing-beat"]));
    }

    [Fact]
    public void RequiredMaterialBeatIdsSkipsApprovedNoReuseBeats()
    {
        var reusable = Beat("1:beat:1");
        var noReuse = Beat("1:beat:2") with
        {
            SourceBackedDetailTarget = string.Empty,
            NoReuseReason = "transition carries no reusable source material"
        };

        var required = ReferenceAnchoredDraftPreflight.RequiredMaterialBeatIds([reusable, noReuse]);

        Assert.Equal(["1:beat:1"], required);
    }

    [Fact]
    public void RequiredMaterialBeatIdsStillRequiresSourceBackedNoReuseBeats()
    {
        var sourceBackedNoReuse = Beat("1:beat:2") with
        {
            SourceBackedDetailTarget = "source-backed rain pressure detail",
            NoReuseReason = "transition carries no reusable source material"
        };

        var required = ReferenceAnchoredDraftPreflight.RequiredMaterialBeatIds([sourceBackedNoReuse]);

        Assert.Equal(["1:beat:2"], required);
    }

    [Fact]
    public void EnsureSelectedMaterialLinksForTargetBeatsRejectsMissingRequiredLinks()
    {
        var reusable = Beat("1:beat:1");
        var noReuse = Beat("1:beat:2") with
        {
            SourceBackedDetailTarget = string.Empty,
            NoReuseReason = "transition carries no reusable source material"
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureSelectedMaterialLinksForTargetBeats([reusable, noReuse], new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>()));

        Assert.Contains("current blueprint analysis contract", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSelectedMaterialLinksForTargetBeatsAllowsNoReuseOnlyTargets()
    {
        var noReuse = Beat("1:beat:2") with
        {
            SourceBackedDetailTarget = string.Empty,
            NoReuseReason = "transition carries no reusable source material"
        };

        var links = ReferenceAnchoredDraftPreflight.EnsureSelectedMaterialLinksForTargetBeats(
            [noReuse],
            new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>());

        Assert.Empty(links);
    }

    [Fact]
    public void EnsureCandidateProvenanceAcceptsCurrentSelectedLinksAndApprovedNoReuse()
    {
        var reusable = Beat("1:beat:1");
        var noReuse = Beat("1:beat:2") with
        {
            SourceBackedDetailTarget = string.Empty,
            NoReuseReason = "transition carries no reusable source material"
        };
        var selectedLinks = new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal)
        {
            [reusable.BeatId] = Link(reusable.BeatId, "material-1")
        };

        ReferenceAnchoredDraftPreflight.EnsureCandidateProvenance(
            [reusable, noReuse],
            selectedLinks,
            [
                Candidate(reusable.BeatId, "material-1"),
                Candidate(noReuse.BeatId, ReferenceDraftProvenanceIds.BuildNoReuseMaterialId(noReuse.BeatId))
            ]);
    }

    [Fact]
    public void EnsureCandidateProvenanceRejectsNoReuseForSourceBackedBeat()
    {
        var sourceBackedNoReuse = Beat("1:beat:1") with
        {
            SourceBackedDetailTarget = "source-backed rain pressure detail",
            NoReuseReason = "transition carries no reusable source material"
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureCandidateProvenance(
                [sourceBackedNoReuse],
                new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal),
                [Candidate(sourceBackedNoReuse.BeatId, ReferenceDraftProvenanceIds.BuildNoReuseMaterialId(sourceBackedNoReuse.BeatId))]));

        Assert.Contains("source-backed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureCandidateProvenanceRejectsMaterialMismatch()
    {
        var beat = Beat("1:beat:1");
        var selectedLinks = new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal)
        {
            [beat.BeatId] = Link(beat.BeatId, "material-1")
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureCandidateProvenance(
                [beat],
                selectedLinks,
                [Candidate(beat.BeatId, "material-2")]));

        Assert.Contains("selected material link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureCandidateProvenanceRejectsUnapprovedNoReuse()
    {
        var beat = Beat("1:beat:1");

        var exception = Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureCandidateProvenance(
                [beat],
                new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal),
                [Candidate(beat.BeatId, ReferenceDraftProvenanceIds.BuildNoReuseMaterialId(beat.BeatId))]));

        Assert.Contains("no-reuse", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ReferenceChapterBlueprintPayload Blueprint(
        string status = ReferenceBlueprintStates.MaterialBound,
        ReferenceChapterBlueprintReviewPayload? review = null)
    {
        return new ReferenceChapterBlueprintPayload(
            1,
            10,
            1,
            "测试蓝图",
            status,
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
            [],
            [],
            [Beat("1:beat:1")],
            review ?? Review(),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static ReferenceChapterBlueprintReviewPayload Review(
        long blueprintId = 1,
        string contextHash = "context-hash",
        string sourceHash = "source-hash",
        string analysisHash = "analysis-hash",
        int reviewVersion = ReferenceChapterBlueprintReviewer.CurrentReviewVersion,
        string status = ReferenceBlueprintReviewStatuses.Passed)
    {
        return new ReferenceChapterBlueprintReviewPayload(
            "review-1",
            blueprintId,
            contextHash,
            sourceHash,
            analysisHash,
            reviewVersion,
            status,
            1.0,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
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

    private static ReferenceBlueprintMaterialLinkPayload Link(string beatId, string materialId)
    {
        return new ReferenceBlueprintMaterialLinkPayload(
            "link-1",
            1,
            beatId,
            materialId,
            "intended use",
            ReferenceRewriteLevels.L1,
            Selected: true,
            Score: 1.0,
            ScoreComponents: new Dictionary<string, double>(),
            FitExplanation: "test fit",
            DateTimeOffset.UnixEpoch);
    }

    private static ReferenceDraftParagraphCandidatePayload Candidate(string beatId, string materialId)
    {
        return new ReferenceDraftParagraphCandidatePayload(
            "candidate-1",
            1,
            beatId,
            materialId,
            ReferenceRewriteLevels.L1,
            "candidate text",
            ChangedSlots: [],
            NonSlotEdits: [],
            AuditStatus: "passed",
            DateTimeOffset.UnixEpoch);
    }
}
