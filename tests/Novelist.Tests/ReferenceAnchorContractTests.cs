using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.Tests;

public sealed class ReferenceAnchorContractTests
{
    [Fact]
    public void ReferenceAnchorPayloadsUseStableSnakeCaseJsonNames()
    {
        var input = new CreateReferenceAnchorPayload(
            NovelId: 42,
            Title: "Anchor Book",
            Author: "Reference Author",
            SourcePath: @"D:\books\anchor.md",
            SourceKind: "markdown",
            LicenseStatus: "user_provided");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal("Anchor Book", root.GetProperty("title").GetString());
        Assert.Equal("Reference Author", root.GetProperty("author").GetString());
        Assert.Equal(@"D:\books\anchor.md", root.GetProperty("source_path").GetString());
        Assert.Equal("markdown", root.GetProperty("source_kind").GetString());
        Assert.Equal("user_provided", root.GetProperty("license_status").GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
    }

    [Fact]
    public void ReferenceChapterBlueprintPayloadsUseStableSnakeCaseJsonNames()
    {
        var beat = new ReferenceChapterBlueprintBeatPayload(
            BeatId: "beat-1",
            BeatIndex: 1,
            SceneIndex: 1,
            BeatType: ReferenceBlueprintBeatTypes.Interiority,
            NarrativeFunction: "reveal pressure through restrained interiority",
            LogicPremise: "the clue contradicts the previous chapter state",
            ConflictPressure: "the protagonist must decide whether to confront the witness",
            CausalityIn: "previous clue forces the protagonist to reconsider",
            CausalityOut: "the reconsideration motivates the next confrontation",
            TransitionIn: "camera stays with the protagonist after the clue is found",
            TransitionOut: "private unease pushes the next scene",
            PovCharacter: "protagonist",
            NarrativeDistance: "close",
            ViewpointAllowedKnowledge: ["known clue"],
            ViewpointForbiddenKnowledge: ["culprit identity"],
            CharacterStatesBefore: ["guarded"],
            CharacterStatesAfter: ["uneasy"],
            CharacterGoals: ["protect the clue"],
            CharacterMisbeliefs: ["the witness is still available"],
            RelationshipPressure: ["trust in the witness weakens"],
            EmotionTrigger: "a detail contradicts the protagonist's assumption",
            EmotionBefore: "controlled suspicion",
            EmotionAfter: "private unease",
            SuppressedReaction: "does not voice the fear",
            ExternalEvidence: "hand pauses before opening the door",
            NarrationStrategy: "brief close interiority followed by physical afterbeat",
            RhythmStrategy: "short sentence after a longer reflective sentence",
            ParagraphIntention: "linger on hesitation before the next action",
            ExecutionMode: "dwell",
            AntiScreenplayDuty: "carry the beat through interiority and physical afterbeat, not dialogue blocking",
            SensoryAnchorTarget: "the locked door under the protagonist's hand",
            SubtextPlan: "the pause implies fear without naming it directly",
            SourceBackedDetailTarget: "physical hesitation detail",
            CandidateRejectionRule: "reject dialogue-only or action-only prose",
            SceneFacts: ["door is locked"],
            ForbiddenFacts: ["culprit identity"],
            ReferenceQuery: new ReferenceMaterialQueryPayload(
                Query: "close interiority hesitation",
                MaterialTypes: [ReferenceMaterialTypes.Passage],
                EmotionTags: ["unease"],
                FunctionTags: ["interiority"],
                PovTags: ["close"],
                TechniqueTags: ["afterbeat"],
                MaxResults: 5),
            RequiredMaterialTypes: [ReferenceMaterialTypes.Passage],
            MaxRewriteLevel: ReferenceRewriteLevels.L2,
            SlotPlan: [new ReferenceSlotValuePayload("object", "door")],
            LockedPhrasePolicy: "preserve physical afterbeat cadence",
            NoReuseReason: "",
            ProseDuties: ["interiority", "physical_afterbeat"],
            RiskFlags: ["fake_emotion"]);

        var review = new ReferenceChapterBlueprintReviewPayload(
            ReviewId: "review-1",
            BlueprintId: 10,
            ContextHash: "context-hash",
            SourcePlanHash: "plan-hash",
            AnalysisContractHash: "analysis-hash",
            Status: ReferenceBlueprintReviewStatuses.Failed,
            Score: 0.45,
            LogicErrors: ["missing payoff"],
            CausalityErrors: ["beat 2 lacks causality_in"],
            EmotionErrors: ["emotion shift lacks external evidence"],
            NarrationErrors: ["dialogue beat lacks anti-screenplay duty"],
            ExecutionErrors: ["paragraph intention missing"],
            CharacterStateErrors: ["role-state delta missing"],
            PovErrors: ["pov leak"],
            ContinuityErrors: ["state mismatch"],
            TransitionErrors: ["scene jump lacks reason"],
            ForbiddenFactErrors: ["forbidden fact appears"],
            ReferenceBindingErrors: ["reference query missing"],
            MaterialFitErrors: ["semantic match lacks function fit"],
            ScreenplayDriftRisks: ["action dialogue only"],
            AiProseRisks: ["generic emotion label"],
            NovelisticNarrationErrors: ["beat reads like blocking"],
            RequiredFixes: ["add external evidence"],
            ReviewedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"));

        var payload = new ReferenceChapterBlueprintPayload(
            BlueprintId: 10,
            NovelId: 42,
            ChapterNumber: 7,
            Title: "Chapter 7 Blueprint",
            Status: ReferenceBlueprintStates.ReviewPassed,
            SourcePlanScope: "chapter",
            SourcePlanHash: "plan-hash",
            ContextHash: "context-hash",
            AnalysisContractHash: "analysis-hash",
            BlueprintVersion: 1,
            ParentBlueprintId: 0,
            PrimaryAnchorId: 3,
            ChapterFunction: "turn suspicion into commitment",
            LogicAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("logic", "cause to hook", ["premise", "turn"]),
            EmotionAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("emotion", "suspicion to unease", ["trigger", "evidence"]),
            NarrationAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("narration", "close controlled interiority", ["distance", "rhythm"]),
            CharacterAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("character", "guarded to committed", ["goal", "misbelief"]),
            ReferenceAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("reference", "bind material by function, emotion, POV, and prose duty", ["query", "rewrite budget"]),
            TransitionPlan: new ReferenceChapterBlueprintAnalysisTrackPayload("transition", "pressure-driven scene movement", ["door to witness"]),
            ExecutionContract: new ReferenceChapterBlueprintExecutionTrackPayload(
                Track: "execution",
                Summary: "novelistic paragraph execution before prose drafting",
                ParagraphIntentions: ["linger on hesitation"],
                ExecutionModes: ["dwell"],
                AntiScreenplayDuties: ["interiority before action"],
                SourceBackedDetailTargets: ["physical hesitation detail"],
                CandidateRejectionRules: ["reject dialogue-only prose"]),
            PreviousState: "protagonist doubts the clue",
            FinalState: "protagonist decides to confront a witness",
            FinalHook: "the witness is missing",
            GlobalPov: "protagonist",
            GlobalNarrativeDistance: "close",
            KnownFacts: ["the clue exists"],
            ForbiddenFacts: ["culprit identity"],
            RiskFlags: ["screenplay_drift"],
            Beats: [beat],
            LatestReview: review,
            CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal(7, root.GetProperty("chapter_number").GetInt32());
        Assert.Equal("review_passed", root.GetProperty("status").GetString());
        Assert.Equal("plan-hash", root.GetProperty("source_plan_hash").GetString());
        Assert.Equal("analysis-hash", root.GetProperty("analysis_contract_hash").GetString());
        Assert.Equal(1, root.GetProperty("blueprint_version").GetInt32());
        Assert.Equal("logic", root.GetProperty("logic_analysis").GetProperty("track").GetString());
        Assert.Equal("emotion", root.GetProperty("emotion_analysis").GetProperty("track").GetString());
        Assert.Equal("narration", root.GetProperty("narration_analysis").GetProperty("track").GetString());
        Assert.Equal("character", root.GetProperty("character_analysis").GetProperty("track").GetString());
        Assert.Equal("reference", root.GetProperty("reference_analysis").GetProperty("track").GetString());
        Assert.Equal("transition", root.GetProperty("transition_plan").GetProperty("track").GetString());
        Assert.Equal("execution", root.GetProperty("execution_contract").GetProperty("track").GetString());
        Assert.Equal("close", root.GetProperty("global_narrative_distance").GetString());
        Assert.Equal("beat-1", root.GetProperty("beats")[0].GetProperty("beat_id").GetString());
        Assert.Equal("the clue contradicts the previous chapter state", root.GetProperty("beats")[0].GetProperty("logic_premise").GetString());
        Assert.Equal("camera stays with the protagonist after the clue is found", root.GetProperty("beats")[0].GetProperty("transition_in").GetString());
        Assert.Equal("does not voice the fear", root.GetProperty("beats")[0].GetProperty("suppressed_reaction").GetString());
        Assert.Equal("linger on hesitation before the next action", root.GetProperty("beats")[0].GetProperty("paragraph_intention").GetString());
        Assert.Equal("dwell", root.GetProperty("beats")[0].GetProperty("execution_mode").GetString());
        Assert.Equal("preserve physical afterbeat cadence", root.GetProperty("beats")[0].GetProperty("locked_phrase_policy").GetString());
        Assert.Equal("close interiority hesitation", root.GetProperty("beats")[0].GetProperty("reference_query").GetProperty("query").GetString());
        Assert.Equal("missing payoff", root.GetProperty("latest_review").GetProperty("logic_errors")[0].GetString());
        Assert.Equal("paragraph intention missing", root.GetProperty("latest_review").GetProperty("execution_errors")[0].GetString());
        Assert.Equal("semantic match lacks function fit", root.GetProperty("latest_review").GetProperty("material_fit_errors")[0].GetString());
        Assert.Equal("beat reads like blocking", root.GetProperty("latest_review").GetProperty("novelistic_narration_errors")[0].GetString());
        Assert.False(root.TryGetProperty("BlueprintId", out _));
    }

    [Fact]
    public void ReferenceBlueprintRevisionPayloadsUseStableSnakeCaseJsonNames()
    {
        var payload = new ReviseReferenceChapterBlueprintPayload(
            NovelId: 42,
            BlueprintId: 10,
            Changes: [new ReferenceBlueprintRevisionChangePayload("beat:beat-1:paragraph_intention", "linger on the threshold")],
            Origin: "user",
            RevisionReason: "tighten novelistic execution");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("beat:beat-1:paragraph_intention", root.GetProperty("changes")[0].GetProperty("field_path").GetString());
        Assert.Equal("linger on the threshold", root.GetProperty("changes")[0].GetProperty("new_value").GetString());
        Assert.Equal("user", root.GetProperty("origin").GetString());
        Assert.Equal("tighten novelistic execution", root.GetProperty("revision_reason").GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
    }

    [Fact]
    public void ReferenceConstantsDocumentInitialStateAndRewriteVocabulary()
    {
        Assert.Equal("L0", ReferenceRewriteLevels.L0);
        Assert.Equal("L4", ReferenceRewriteLevels.L4);
        Assert.Contains(ReferenceAnchorBuildStates.Ready, ReferenceAnchorBuildStates.All);
        Assert.Contains(ReferenceAnchorBuildStates.FailedEmbedding, ReferenceAnchorBuildStates.All);
        Assert.Contains(ReferenceBlueprintStates.Approved, ReferenceBlueprintStates.All);
        Assert.Contains(ReferenceBlueprintStates.Stale, ReferenceBlueprintStates.All);
        Assert.Contains(ReferenceBlueprintStates.Normalized, ReferenceBlueprintStates.All);
        Assert.Contains(ReferenceBlueprintStates.MaterialBound, ReferenceBlueprintStates.All);
        Assert.Contains(ReferenceBlueprintBeatTypes.Interiority, ReferenceBlueprintBeatTypes.All);
        Assert.Contains(ReferenceBlueprintReviewStatuses.Failed, ReferenceBlueprintReviewStatuses.All);
    }

    [Fact]
    public void CompatibilityRegistryIncludesReferenceAnchorMethods()
    {
        string[] expected =
        [
            "CreateReferenceAnchor",
            "GetReferenceAnchors",
            "DeleteReferenceAnchor",
            "RebuildReferenceAnchor",
            "GetReferenceAnchorBuildStatus",
            "SearchReferenceMaterials",
            "AdaptReferenceMaterial",
            "AuditReferenceReuse",
            "GenerateReferenceChapterBlueprint",
            "GetReferenceChapterBlueprints",
            "GetReferenceChapterBlueprint",
            "ReviewReferenceChapterBlueprint",
            "ReviseReferenceChapterBlueprint",
            "ApproveReferenceChapterBlueprint",
            "BindReferenceBlueprintMaterials",
            "GenerateReferenceAnchoredDraft",
            "AuditReferenceAnchoredDraft"
        ];

        foreach (var method in expected)
        {
            Assert.Contains(method, BridgeCompatibilityAppMethods.MethodNames);
        }
    }
}
