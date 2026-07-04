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
    public void ReferenceMaterialPayloadSearchScoresUseStableSnakeCaseJsonNames()
    {
        var material = new ReferenceMaterialPayload(
            MaterialId: "material-1",
            AnchorId: 7,
            SourceSegmentId: "segment-1",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "environment",
            EmotionTag: "reflective",
            SceneTag: "scene",
            PovTag: "close",
            TechniqueTag: "sensory_detail",
            FunctionConfidence: 0.8,
            EmotionConfidence: 0.7,
            PovConfidence: 0.6,
            Text: "雨声压低了门口。",
            SourceHash: "hash",
            ExtractorVersion: "deterministic-v1",
            UserVerified: false,
            CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            ScoreComponents: new Dictionary<string, double>
            {
                ["lexical"] = 12.0,
                ["material_type"] = 1.5,
                ["confidence"] = 1.0
            });

        using var scoredJson = JsonDocument.Parse(JsonSerializer.Serialize(material, BridgeJson.SerializerOptions));
        var root = scoredJson.RootElement;
        Assert.Equal(12.0, root.GetProperty("score_components").GetProperty("lexical").GetDouble());
        Assert.Equal(1.5, root.GetProperty("score_components").GetProperty("material_type").GetDouble());
        Assert.False(root.TryGetProperty("ScoreComponents", out _));

        using var unscoredJson = JsonDocument.Parse(JsonSerializer.Serialize(material with { ScoreComponents = null }, BridgeJson.SerializerOptions));
        Assert.False(unscoredJson.RootElement.TryGetProperty("score_components", out _));
    }

    [Fact]
    public void SearchReferenceMaterialsPayloadUsesStableNarrativeFilterJsonNames()
    {
        var payload = new SearchReferenceMaterialsPayload(
            NovelId: 42,
            AnchorIds: [7],
            Query: "rain pressure",
            MaterialTypes: [ReferenceMaterialTypes.Sentence],
            EmotionTags: ["heightened"],
            FunctionTags: ["environment"],
            PovTags: ["unknown"],
            TechniqueTags: ["sensory_detail"],
            Page: 1,
            Size: 10,
            NarrativeDuties: ["external_evidence"],
            EmotionTransitions: ["neutral->heightened"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal("external_evidence", root.GetProperty("narrative_duties")[0].GetString());
        Assert.Equal("neutral->heightened", root.GetProperty("emotion_transitions")[0].GetString());
        Assert.False(root.TryGetProperty("NarrativeDuties", out _));
        Assert.False(root.TryGetProperty("EmotionTransitions", out _));
    }

    [Fact]
    public void BindReferenceBlueprintMaterialsPayloadUsesStableSelectionJsonName()
    {
        var payload = new BindReferenceBlueprintMaterialsPayload(
            NovelId: 42,
            BlueprintId: 10,
            MaxResultsPerBeat: 3,
            SelectTopCandidate: true);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal(3, root.GetProperty("max_results_per_beat").GetInt32());
        Assert.True(root.GetProperty("select_top_candidate").GetBoolean());
        Assert.False(root.TryGetProperty("SelectTopCandidate", out _));

        var defaulted = JsonSerializer.Deserialize<BindReferenceBlueprintMaterialsPayload>(
            """{"novel_id":42,"blueprint_id":10,"max_results_per_beat":3}""",
            BridgeJson.SerializerOptions);
        Assert.NotNull(defaulted);
        Assert.False(defaulted.SelectTopCandidate);
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
            ReviewVersion: 1,
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
        Assert.Equal(1, root.GetProperty("latest_review").GetProperty("review_version").GetInt32());
        Assert.Equal("beat 2 lacks causality_in", root.GetProperty("latest_review").GetProperty("causality_errors")[0].GetString());
        Assert.Equal("emotion shift lacks external evidence", root.GetProperty("latest_review").GetProperty("emotion_errors")[0].GetString());
        Assert.Equal("dialogue beat lacks anti-screenplay duty", root.GetProperty("latest_review").GetProperty("narration_errors")[0].GetString());
        Assert.Equal("paragraph intention missing", root.GetProperty("latest_review").GetProperty("execution_errors")[0].GetString());
        Assert.Equal("role-state delta missing", root.GetProperty("latest_review").GetProperty("character_state_errors")[0].GetString());
        Assert.Equal("pov leak", root.GetProperty("latest_review").GetProperty("pov_errors")[0].GetString());
        Assert.Equal("state mismatch", root.GetProperty("latest_review").GetProperty("continuity_errors")[0].GetString());
        Assert.Equal("scene jump lacks reason", root.GetProperty("latest_review").GetProperty("transition_errors")[0].GetString());
        Assert.Equal("forbidden fact appears", root.GetProperty("latest_review").GetProperty("forbidden_fact_errors")[0].GetString());
        Assert.Equal("reference query missing", root.GetProperty("latest_review").GetProperty("reference_binding_errors")[0].GetString());
        Assert.Equal("semantic match lacks function fit", root.GetProperty("latest_review").GetProperty("material_fit_errors")[0].GetString());
        Assert.Equal("action dialogue only", root.GetProperty("latest_review").GetProperty("screenplay_drift_risks")[0].GetString());
        Assert.Equal("generic emotion label", root.GetProperty("latest_review").GetProperty("ai_prose_risks")[0].GetString());
        Assert.Equal("beat reads like blocking", root.GetProperty("latest_review").GetProperty("novelistic_narration_errors")[0].GetString());
        Assert.Equal("add external evidence", root.GetProperty("latest_review").GetProperty("required_fixes")[0].GetString());
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
    public void ReferenceReuseAuditPayloadsExposeNonSlotEditsAsSnakeCase()
    {
        var payload = new ReferenceReuseAuditPayload(
            AuditId: "audit-1",
            Status: "passed",
            RewriteLevel: ReferenceRewriteLevels.L2,
            ProvenanceErrors: [],
            UnsupportedFactErrors: [],
            AiProseRisks: [],
            NonSlotEdits: ["Inserted non-slot text '却' at offset 1."],
            RequiredFixes: [],
            AuditedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal("audit-1", root.GetProperty("audit_id").GetString());
        Assert.Equal("L2", root.GetProperty("rewrite_level").GetString());
        Assert.Equal("Inserted non-slot text '却' at offset 1.", root.GetProperty("non_slot_edits")[0].GetString());
        Assert.False(root.TryGetProperty("NonSlotEdits", out _));
    }

    [Fact]
    public void ReferenceUserFeedbackPayloadsUseStableSnakeCaseJsonNames()
    {
        var input = new RecordReferenceUserFeedbackPayload(
            NovelId: 42,
            TargetType: ReferenceFeedbackTargetTypes.ReuseCandidate,
            TargetId: "candidate-1",
            Decision: ReferenceFeedbackDecisions.Edited,
            MaterialId: "material-1",
            CandidateId: "candidate-1",
            BlueprintId: 10,
            BeatId: "beat-1",
            FeedbackTags: ["too_ai_flavored", "introduced_fact"],
            Note: "kept only the pressure image",
            EditedText: "edited candidate text",
            Origin: "user");

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var inputRoot = inputJson.RootElement;

        Assert.Equal(42, inputRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal("reuse_candidate", inputRoot.GetProperty("target_type").GetString());
        Assert.Equal("candidate-1", inputRoot.GetProperty("target_id").GetString());
        Assert.Equal("edited", inputRoot.GetProperty("decision").GetString());
        Assert.Equal("material-1", inputRoot.GetProperty("material_id").GetString());
        Assert.Equal("candidate-1", inputRoot.GetProperty("candidate_id").GetString());
        Assert.Equal(10, inputRoot.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("beat-1", inputRoot.GetProperty("beat_id").GetString());
        Assert.Equal("too_ai_flavored", inputRoot.GetProperty("feedback_tags")[0].GetString());
        Assert.Equal("kept only the pressure image", inputRoot.GetProperty("note").GetString());
        Assert.Equal("edited candidate text", inputRoot.GetProperty("edited_text").GetString());
        Assert.Equal("user", inputRoot.GetProperty("origin").GetString());
        Assert.False(inputRoot.TryGetProperty("NovelId", out _));

        var result = new ReferenceUserFeedbackPayload(
            FeedbackId: "feedback-1",
            NovelId: 42,
            TargetType: ReferenceFeedbackTargetTypes.ReuseCandidate,
            TargetId: "candidate-1",
            Decision: ReferenceFeedbackDecisions.Edited,
            MaterialId: "material-1",
            CandidateId: "candidate-1",
            BlueprintId: 10,
            BeatId: "beat-1",
            FeedbackTags: ["too_ai_flavored"],
            Note: "kept only the pressure image",
            EditedTextHash: "edited-hash",
            Origin: "user",
            CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"));

        using var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeJson.SerializerOptions));
        var resultRoot = resultJson.RootElement;

        Assert.Equal("feedback-1", resultRoot.GetProperty("feedback_id").GetString());
        Assert.Equal("edited-hash", resultRoot.GetProperty("edited_text_hash").GetString());
        Assert.Equal("too_ai_flavored", resultRoot.GetProperty("feedback_tags")[0].GetString());
        Assert.False(resultRoot.TryGetProperty("EditedTextHash", out _));
    }

    [Fact]
    public void ReferenceMaterialTagUpdatePayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new UpdateReferenceMaterialTagsPayload(
            NovelId: 42,
            MaterialId: "material-1",
            FunctionTag: "interiority",
            EmotionTag: "unease",
            SceneTag: "threshold",
            PovTag: "close",
            TechniqueTag: "afterbeat",
            Origin: "user",
            Note: "manual correction after reviewing search results");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal("material-1", root.GetProperty("material_id").GetString());
        Assert.Equal("interiority", root.GetProperty("function_tag").GetString());
        Assert.Equal("unease", root.GetProperty("emotion_tag").GetString());
        Assert.Equal("threshold", root.GetProperty("scene_tag").GetString());
        Assert.Equal("close", root.GetProperty("pov_tag").GetString());
        Assert.Equal("afterbeat", root.GetProperty("technique_tag").GetString());
        Assert.Equal("user", root.GetProperty("origin").GetString());
        Assert.Equal("manual correction after reviewing search results", root.GetProperty("note").GetString());
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
        Assert.Contains(ReferenceFeedbackDecisions.Accepted, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackDecisions.Rejected, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackDecisions.Edited, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackTargetTypes.ReuseCandidate, ReferenceFeedbackTargetTypes.All);
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
            "RecordReferenceUserFeedback",
            "GetReferenceUserFeedback",
            "UpdateReferenceMaterialTags",
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
