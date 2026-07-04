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
        var noReuse = Beat("1:beat:2") with { NoReuseReason = "transition carries no reusable source material" };

        var required = ReferenceAnchoredDraftPreflight.RequiredMaterialBeatIds([reusable, noReuse]);

        Assert.Equal(["1:beat:1"], required);
    }

    [Fact]
    public void EnsureSelectedMaterialLinksForTargetBeatsRejectsMissingRequiredLinks()
    {
        var reusable = Beat("1:beat:1");
        var noReuse = Beat("1:beat:2") with { NoReuseReason = "transition carries no reusable source material" };

        var exception = Assert.Throws<ArgumentException>(() =>
            ReferenceAnchoredDraftPreflight.EnsureSelectedMaterialLinksForTargetBeats([reusable, noReuse], new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>()));

        Assert.Contains("current blueprint analysis contract", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSelectedMaterialLinksForTargetBeatsAllowsNoReuseOnlyTargets()
    {
        var noReuse = Beat("1:beat:2") with { NoReuseReason = "transition carries no reusable source material" };

        var links = ReferenceAnchoredDraftPreflight.EnsureSelectedMaterialLinksForTargetBeats(
            [noReuse],
            new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>());

        Assert.Empty(links);
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
        string status = ReferenceBlueprintReviewStatuses.Passed)
    {
        return new ReferenceChapterBlueprintReviewPayload(
            "review-1",
            blueprintId,
            contextHash,
            sourceHash,
            analysisHash,
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
}
