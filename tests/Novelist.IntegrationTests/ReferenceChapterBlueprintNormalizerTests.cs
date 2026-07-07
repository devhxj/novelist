using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceChapterBlueprintNormalizerTests
{
    [Fact]
    public void ComputeContextHashIgnoresEquivalentWhitespaceAndNullLists()
    {
        var baseline = new ReferenceChapterBlueprintContextPack(
            NovelId: 10,
            ChapterNumber: 3,
            SourcePlanScope: "next",
            SourcePlanContent: "rain plan",
            ChapterGoal: "pressure the protagonist",
            AnchorIds: [7],
            KnownFacts: ["rain pressure"],
            ForbiddenFacts: ["killer identity"]);
        var equivalent = new ReferenceChapterBlueprintContextPack(
            NovelId: 10,
            ChapterNumber: 3,
            SourcePlanScope: "  next  ",
            SourcePlanContent: "  rain plan\r\n",
            ChapterGoal: "  pressure the protagonist  ",
            AnchorIds: [7],
            KnownFacts: ["  rain pressure  ", ""],
            ForbiddenFacts: ["  killer identity  "]);
        var empty = new ReferenceChapterBlueprintContextPack(
            NovelId: 10,
            ChapterNumber: 3,
            SourcePlanScope: "next",
            SourcePlanContent: "rain plan",
            ChapterGoal: "pressure the protagonist",
            AnchorIds: null,
            KnownFacts: null,
            ForbiddenFacts: null);
        var emptyEquivalent = new ReferenceChapterBlueprintContextPack(
            NovelId: 10,
            ChapterNumber: 3,
            SourcePlanScope: "next",
            SourcePlanContent: "rain plan",
            ChapterGoal: "pressure the protagonist",
            AnchorIds: [],
            KnownFacts: [],
            ForbiddenFacts: []);

        Assert.Equal(
            ReferenceChapterBlueprintNormalizer.ComputeContextHash(baseline),
            ReferenceChapterBlueprintNormalizer.ComputeContextHash(equivalent));
        Assert.Equal(
            ReferenceChapterBlueprintNormalizer.ComputeContextHash(empty),
            ReferenceChapterBlueprintNormalizer.ComputeContextHash(emptyEquivalent));
    }

    [Fact]
    public void ComputeContextHashChangesWhenContextInputChanges()
    {
        var baseline = new ReferenceChapterBlueprintContextPack(
            NovelId: 10,
            ChapterNumber: 3,
            SourcePlanScope: "next",
            SourcePlanContent: "rain plan",
            ChapterGoal: "pressure the protagonist",
            AnchorIds: [7],
            KnownFacts: ["rain pressure"],
            ForbiddenFacts: ["killer identity"]);
        var changed = baseline with
        {
            KnownFacts = ["door is locked"]
        };

        Assert.NotEqual(
            ReferenceChapterBlueprintNormalizer.ComputeContextHash(baseline),
            ReferenceChapterBlueprintNormalizer.ComputeContextHash(changed));
    }

    [Fact]
    public void ComputeSourcePlanHashChangesWhenPlanContentChanges()
    {
        var baseline = ReferenceChapterBlueprintNormalizer.ComputeSourcePlanHash("next", "rain plan");
        var sameDefaultScope = ReferenceChapterBlueprintNormalizer.ComputeSourcePlanHash("", "rain plan");
        var changed = ReferenceChapterBlueprintNormalizer.ComputeSourcePlanHash("next", "door plan");

        Assert.Equal(baseline, sameDefaultScope);
        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void ComputeAnalysisContractHashIgnoresEquivalentWhitespace()
    {
        var baseline = Blueprint();
        var withWhitespace = Blueprint(
            logicSummary: "  logic  ",
            knownFacts: ["  rain pressure  "],
            configureBeat: beat => beat with
            {
                ParagraphIntention = "  dwell before action  ",
                ReferenceQuery = beat.ReferenceQuery with
                {
                    Query = "  rain pressure  ",
                    FunctionTags = ["  environment  "]
                },
                ProseDuties = ["  interiority  ", "  external_evidence  "]
            });

        var baselineHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(baseline);
        var whitespaceHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(withWhitespace);

        Assert.Equal(baselineHash, whitespaceHash);
    }

    [Fact]
    public void ComputeAnalysisContractHashChangesWhenReviewedFieldChanges()
    {
        var baseline = Blueprint();
        var changed = Blueprint(configureBeat: beat => beat with
        {
            ParagraphIntention = "move directly into action"
        });

        var baselineHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(baseline);
        var changedHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(changed);

        Assert.NotEqual(baselineHash, changedHash);
    }

    [Fact]
    public void ComputeAnalysisContractHashChangesWhenStyleContractChanges()
    {
        var baseline = Blueprint(configureBeat: beat => beat with
        {
            StyleContract = new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [1],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Moderate,
                MinStyleFit: 0.75,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["source_leak"])
        });
        var changed = Blueprint(configureBeat: beat => beat with
        {
            StyleContract = new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [1],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                MinStyleFit: 1.25,
                AllowedCloseness: "low",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["source_leak"])
        });

        var baselineHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(baseline);
        var changedHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(changed);

        Assert.NotEqual(baselineHash, changedHash);
    }

    [Fact]
    public void ComputeAnalysisContractHashPreservesOrderedArraySemantics()
    {
        var baseline = Blueprint();
        var reordered = Blueprint(configureBeat: beat => beat with
        {
            ProseDuties = ["external_evidence", "interiority"]
        });

        var baselineHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(baseline);
        var reorderedHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(reordered);

        Assert.NotEqual(baselineHash, reorderedHash);
    }

    [Fact]
    public void ComputeAnalysisContractHashTreatsNullListsAsEmptyLists()
    {
        var empty = Blueprint(knownFacts: []);
        var nullList = empty with { KnownFacts = null! };

        var emptyHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(empty);
        var nullHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(nullList);

        Assert.Equal(emptyHash, nullHash);
    }

    private static ReferenceChapterBlueprintPayload Blueprint(
        string logicSummary = "logic",
        IReadOnlyList<string>? knownFacts = null,
        Func<ReferenceChapterBlueprintBeatPayload, ReferenceChapterBlueprintBeatPayload>? configureBeat = null)
    {
        var beat = configureBeat?.Invoke(Beat()) ?? Beat();
        return new ReferenceChapterBlueprintPayload(
            1,
            10,
            1,
            "Blueprint",
            ReferenceBlueprintStates.Draft,
            "next",
            "source-hash",
            "context-hash",
            "analysis-hash",
            1,
            0,
            1,
            "chapter function",
            new ReferenceChapterBlueprintAnalysisTrackPayload("logic", logicSummary, ["point"]),
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
                ["source detail"],
                ["reject"]),
            "previous",
            "final",
            "hook",
            "pov",
            "close",
            knownFacts ?? ["rain pressure"],
            [],
            [],
            [beat],
            LatestReview: null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static ReferenceChapterBlueprintBeatPayload Beat()
    {
        return new ReferenceChapterBlueprintBeatPayload(
            "1:beat:1",
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
            "pov",
            "close",
            ["allowed"],
            [],
            ["controlled"],
            ["pressured"],
            ["goal"],
            ["misbelief"],
            ["pressure"],
            "trigger",
            "before",
            "after",
            "suppressed",
            "external",
            "strategy",
            "rhythm",
            "dwell before action",
            "dwell",
            "anti-screenplay",
            "rain detail",
            "subtext",
            "source detail",
            "reject",
            ["rain pressure"],
            [],
            new ReferenceMaterialQueryPayload(
                "rain pressure",
                [ReferenceMaterialTypes.Sentence],
                ["strained"],
                ["environment"],
                ["close"],
                ["interiority"],
                3),
            [ReferenceMaterialTypes.Sentence],
            ReferenceRewriteLevels.L1,
            [new ReferenceSlotValuePayload("subject", "street")],
            "preserve order",
            string.Empty,
            ["interiority", "external_evidence"],
            []);
    }
}
