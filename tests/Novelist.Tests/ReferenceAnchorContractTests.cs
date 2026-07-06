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

        var anchor = new ReferenceAnchorPayload(
            AnchorId: 7,
            NovelId: 0,
            Title: "Shared Anchor",
            Author: "",
            SourcePath: @"D:\books\shared.md",
            SourceKind: "markdown",
            LicenseStatus: "licensed",
            SourceFileHash: "hash",
            BuildVersion: "reference-anchor-v1",
            Status: ReferenceAnchorBuildStates.Ready,
            CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            Visibility: ReferenceCorpusVisibilities.Workspace,
            SourceTrust: ReferenceSourceTrustLevels.UserVerified,
            UserTags: ["rain", "threshold"]);

        using var anchorJson = JsonDocument.Parse(JsonSerializer.Serialize(anchor, BridgeJson.SerializerOptions));
        var anchorRoot = anchorJson.RootElement;
        Assert.Equal("workspace", anchorRoot.GetProperty("visibility").GetString());
        Assert.Equal("user_verified", anchorRoot.GetProperty("source_trust").GetString());
        Assert.Equal("rain", anchorRoot.GetProperty("user_tags")[0].GetString());
        Assert.Equal("workspace_corpus", anchorRoot.GetProperty("owner_scope").GetString());
        Assert.False(anchorRoot.TryGetProperty("owner_novel_id", out _));
        Assert.False(anchorRoot.TryGetProperty("SourceTrust", out _));

        var novelAnchor = anchor with
        {
            NovelId = 42,
            OwnerScope = ReferenceAnchorOwnerScopes.Novel,
            OwnerNovelId = 42
        };
        using var novelAnchorJson = JsonDocument.Parse(JsonSerializer.Serialize(novelAnchor, BridgeJson.SerializerOptions));
        var novelAnchorRoot = novelAnchorJson.RootElement;
        Assert.Equal(42, novelAnchorRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal("novel", novelAnchorRoot.GetProperty("owner_scope").GetString());
        Assert.Equal(42, novelAnchorRoot.GetProperty("owner_novel_id").GetInt64());
    }

    [Fact]
    public void PromoteReferenceAnchorToWorkspaceCorpusPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new PromoteReferenceAnchorToWorkspaceCorpusPayload(
            NovelId: 42,
            AnchorId: 7,
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["migrated", "shared"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, root.GetProperty("anchor_id").GetInt64());
        Assert.Equal("imported", root.GetProperty("source_trust").GetString());
        Assert.Equal("migrated", root.GetProperty("user_tags")[0].GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("AnchorId", out _));
    }

    [Fact]
    public void PromoteReferenceAnchorsToWorkspaceCorpusPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new PromoteReferenceAnchorsToWorkspaceCorpusPayload(
            NovelId: 42,
            AnchorIds: [7, 8],
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["migrated", "shared"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal([7, 8], root.GetProperty("anchor_ids").EnumerateArray().Select(item => item.GetInt64()).ToArray());
        Assert.Equal("imported", root.GetProperty("source_trust").GetString());
        Assert.Equal("migrated", root.GetProperty("user_tags")[0].GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("AnchorIds", out _));
    }

    [Fact]
    public void DeleteReferenceAnchorsPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new DeleteReferenceAnchorsPayload(
            NovelId: 42,
            AnchorIds: [7, 8]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal([7, 8], root.GetProperty("anchor_ids").EnumerateArray().Select(item => item.GetInt64()).ToArray());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("AnchorIds", out _));
    }

    [Fact]
    public void UpdateReferenceAnchorMetadataPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new UpdateReferenceAnchorMetadataPayload(
            NovelId: 42,
            AnchorId: 7,
            Title: "Updated Anchor",
            Author: "Reference Author",
            LicenseStatus: "user_provided",
            Visibility: ReferenceCorpusVisibilities.Workspace,
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["curated", "rain"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, root.GetProperty("anchor_id").GetInt64());
        Assert.Equal("Updated Anchor", root.GetProperty("title").GetString());
        Assert.Equal("Reference Author", root.GetProperty("author").GetString());
        Assert.Equal("user_provided", root.GetProperty("license_status").GetString());
        Assert.Equal("workspace", root.GetProperty("visibility").GetString());
        Assert.Equal("imported", root.GetProperty("source_trust").GetString());
        Assert.Equal("curated", root.GetProperty("user_tags")[0].GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("AnchorId", out _));
        Assert.False(root.TryGetProperty("SourceTrust", out _));
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
            EmotionTransitions: ["neutral->heightened"],
            ProseDuties: ["source_backed_detail"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal("external_evidence", root.GetProperty("narrative_duties")[0].GetString());
        Assert.Equal("neutral->heightened", root.GetProperty("emotion_transitions")[0].GetString());
        Assert.Equal("source_backed_detail", root.GetProperty("prose_duties")[0].GetString());
        Assert.False(root.TryGetProperty("NarrativeDuties", out _));
        Assert.False(root.TryGetProperty("EmotionTransitions", out _));
        Assert.False(root.TryGetProperty("ProseDuties", out _));
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
    public void ReferenceBlueprintMaterialLinkPayloadUsesStableFitExplanationJsonName()
    {
        var link = new ReferenceBlueprintMaterialLinkPayload(
            LinkId: "link-1",
            BlueprintId: 10,
            BeatId: "beat-1",
            MaterialId: "material-1",
            IntendedUse: "show restrained pressure",
            MaxRewriteLevel: ReferenceRewriteLevels.L1,
            Selected: true,
            Score: 9.5,
            ScoreComponents: new Dictionary<string, double>
            {
                ["function"] = 3.0,
                ["emotion"] = 2.0
            },
            FitExplanation: "function and emotion match the beat reference query.",
            CreatedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(link, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal("function and emotion match the beat reference query.", root.GetProperty("fit_explanation").GetString());
        Assert.False(root.TryGetProperty("FitExplanation", out _));
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
            Defects:
            [
                new ReferenceChapterBlueprintReviewDefectPayload(
                    Category: "emotion",
                    FieldPath: "beat:beat-1:external_evidence",
                    BeatId: "beat-1",
                    Severity: "error",
                    Reason: "emotion shift lacks external evidence",
                    RequiredFix: "Add concrete observable evidence for the emotion shift.")
            ],
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
            UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"))
        {
            BuildVersion = "reference-blueprint-v1"
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal(7, root.GetProperty("chapter_number").GetInt32());
        Assert.Equal("review_passed", root.GetProperty("status").GetString());
        Assert.Equal("plan-hash", root.GetProperty("source_plan_hash").GetString());
        Assert.Equal("analysis-hash", root.GetProperty("analysis_contract_hash").GetString());
        Assert.Equal(1, root.GetProperty("blueprint_version").GetInt32());
        Assert.Equal("reference-blueprint-v1", root.GetProperty("build_version").GetString());
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
        var defect = root.GetProperty("latest_review").GetProperty("defects")[0];
        Assert.Equal("emotion", defect.GetProperty("category").GetString());
        Assert.Equal("beat:beat-1:external_evidence", defect.GetProperty("field_path").GetString());
        Assert.Equal("beat-1", defect.GetProperty("beat_id").GetString());
        Assert.Equal("error", defect.GetProperty("severity").GetString());
        Assert.Equal("emotion shift lacks external evidence", defect.GetProperty("reason").GetString());
        Assert.Equal("Add concrete observable evidence for the emotion shift.", defect.GetProperty("required_fix").GetString());
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
    public void ApproveReferenceChapterBlueprintPayloadUsesStableSnakeCaseJsonNames()
    {
        var payload = new ApproveReferenceChapterBlueprintPayload(
            NovelId: 42,
            BlueprintId: 10,
            ReviewId: "review-1",
            ApproverOrigin: "user");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("review-1", root.GetProperty("review_id").GetString());
        Assert.Equal("user", root.GetProperty("approver_origin").GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));

        var legacyPayload = new ApproveReferenceChapterBlueprintPayload(42, 10, "review-1");
        Assert.Equal("user", legacyPayload.ApproverOrigin);
    }

    [Fact]
    public void ReferenceOrchestrationPayloadsUseStableSnakeCaseJsonNames()
    {
        var policy = new ReferenceCorpusSearchPolicyPayload(
            Mode: "story_context",
            MaxResultsPerBeat: 4,
            LicenseStatuses: ["user_provided"],
            IncludeAnchorIds: [7],
            ExcludeAnchorIds: [9]);
        var input = new StartReferenceOrchestrationRunPayload(
            NovelId: 42,
            ChapterNumber: 7,
            ChapterGoal: "rain-night confrontation",
            KnownFacts: ["林岚在门口"],
            ForbiddenFacts: ["凶手身份"],
            AnchorIds: null,
            CorpusSearchPolicy: policy,
            SourceConfirmed: false);

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var inputRoot = inputJson.RootElement;

        Assert.Equal(42, inputRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, inputRoot.GetProperty("chapter_number").GetInt32());
        Assert.Equal("rain-night confrontation", inputRoot.GetProperty("chapter_goal").GetString());
        Assert.Equal("林岚在门口", inputRoot.GetProperty("known_facts")[0].GetString());
        Assert.Equal("凶手身份", inputRoot.GetProperty("forbidden_facts")[0].GetString());
        Assert.Equal(JsonValueKind.Null, inputRoot.GetProperty("anchor_ids").ValueKind);
        Assert.False(inputRoot.GetProperty("source_confirmed").GetBoolean());
        var policyRoot = inputRoot.GetProperty("corpus_search_policy");
        Assert.Equal("story_context", policyRoot.GetProperty("mode").GetString());
        Assert.Equal(4, policyRoot.GetProperty("max_results_per_beat").GetInt32());
        Assert.Equal("user_provided", policyRoot.GetProperty("license_statuses")[0].GetString());
        Assert.Equal(7, policyRoot.GetProperty("include_anchor_ids")[0].GetInt64());
        Assert.Equal(9, policyRoot.GetProperty("exclude_anchor_ids")[0].GetInt64());
        Assert.False(inputRoot.TryGetProperty("NovelId", out _));

        var run = new ReferenceOrchestrationRunPayload(
            RunId: "run-1",
            NovelId: 42,
            ChapterNumber: 7,
            Status: ReferenceOrchestrationRunStatuses.WaitingForUser,
            Stage: ReferenceOrchestrationStages.SourceConfirmation,
            ChapterGoal: "rain-night confrontation",
            KnownFacts: ["林岚在门口"],
            ForbiddenFacts: ["凶手身份"],
            AnchorIds: [],
            CorpusSearchPolicy: policy,
            BlueprintId: 0,
            ReviewId: "",
            CandidateIds: [],
            CurrentDecision: new ReferenceOrchestrationRequiredDecisionPayload(
                DecisionType: ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts,
                StopReason: ReferenceOrchestrationStopReasons.SourceConfirmationRequired,
                Summary: "Confirm sources and fact boundaries before automation.",
                RequiredActions: ["confirm_source", "confirm_license_status", "confirm_known_facts", "confirm_forbidden_facts"],
                ApprovalSummary: new ReferenceOrchestrationApprovalSummaryPayload(
                    ChapterFunction: "turn hesitation into action",
                    Pov: "林岚 close",
                    FactBoundaryChanges: [],
                    EmotionalTrajectory: "guarded -> resolved",
                    MaterialUsePlan: "bind by beat function",
                    RewriteBudget: "L2",
                    HighRiskFindings: []),
                ProposedBlueprintRevision: new ReferenceOrchestrationBlueprintRevisionProposalPayload(
                    BlueprintId: 501,
                    ReviewId: "review-1",
                    Origin: "orchestrator",
                    RevisionReason: "deterministic fix proposal",
                    Changes: [new ReferenceBlueprintRevisionChangePayload("final_hook", "approved hook")])),
            LastStopReason: ReferenceOrchestrationStopReasons.SourceConfirmationRequired,
            ErrorMessage: "",
            CreatedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"));

        using var runJson = JsonDocument.Parse(JsonSerializer.Serialize(run, BridgeJson.SerializerOptions));
        var runRoot = runJson.RootElement;

        Assert.Equal("run-1", runRoot.GetProperty("run_id").GetString());
        Assert.Equal("waiting_for_user", runRoot.GetProperty("status").GetString());
        Assert.Equal("source_confirmation", runRoot.GetProperty("stage").GetString());
        Assert.Equal(0, runRoot.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("", runRoot.GetProperty("review_id").GetString());
        Assert.Equal("source_confirmation_required", runRoot.GetProperty("last_stop_reason").GetString());
        var decision = runRoot.GetProperty("current_decision");
        Assert.Equal("confirm_source_and_facts", decision.GetProperty("decision_type").GetString());
        Assert.Equal("confirm_source", decision.GetProperty("required_actions")[0].GetString());
        Assert.Equal("confirm_license_status", decision.GetProperty("required_actions")[1].GetString());
        Assert.Equal("turn hesitation into action", decision.GetProperty("approval_summary").GetProperty("chapter_function").GetString());
        var proposal = decision.GetProperty("proposed_blueprint_revision");
        Assert.Equal(501, proposal.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("review-1", proposal.GetProperty("review_id").GetString());
        Assert.Equal("final_hook", proposal.GetProperty("changes")[0].GetProperty("field_path").GetString());
        Assert.False(runRoot.TryGetProperty("RunId", out _));

        var runEvent = new ReferenceOrchestrationRunEventPayload(
            EventId: 12,
            RunId: "run-1",
            NovelId: 42,
            EventType: "decision_resumed",
            Stage: ReferenceOrchestrationStages.BlueprintApproval,
            Status: ReferenceOrchestrationRunStatuses.Running,
            StopReason: ReferenceOrchestrationStopReasons.BlueprintApprovalRequired,
            DecisionType: ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
            Summary: "user approved blueprint review-1",
            CreatedAt: DateTimeOffset.Parse("2026-07-05T00:01:00Z"));

        using var eventJson = JsonDocument.Parse(JsonSerializer.Serialize(runEvent, BridgeJson.SerializerOptions));
        var eventRoot = eventJson.RootElement;
        Assert.Equal(12, eventRoot.GetProperty("event_id").GetInt64());
        Assert.Equal("run-1", eventRoot.GetProperty("run_id").GetString());
        Assert.Equal("decision_resumed", eventRoot.GetProperty("event_type").GetString());
        Assert.Equal("blueprint_approval", eventRoot.GetProperty("stage").GetString());
        Assert.Equal("blueprint_approval_required", eventRoot.GetProperty("stop_reason").GetString());
        Assert.Equal("approve_blueprint", eventRoot.GetProperty("decision_type").GetString());
        Assert.Equal("user approved blueprint review-1", eventRoot.GetProperty("summary").GetString());
        Assert.False(eventRoot.TryGetProperty("EventId", out _));
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
        Assert.Contains(ReferenceOrchestrationRunStatuses.WaitingForUser, ReferenceOrchestrationRunStatuses.All);
        Assert.Contains(ReferenceOrchestrationStages.SourceConfirmation, ReferenceOrchestrationStages.All);
        Assert.Contains(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, ReferenceOrchestrationDecisionTypes.All);
        Assert.Contains(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, ReferenceOrchestrationDecisionTypes.All);
        Assert.Contains(ReferenceOrchestrationStopReasons.HighRiskGateBlocked, ReferenceOrchestrationStopReasons.All);
        Assert.Contains(ReferenceOrchestrationStopReasons.FinalInsertionRequired, ReferenceOrchestrationStopReasons.All);
        Assert.Contains(ReferenceOrchestrationStopReasons.DraftAuditFailed, ReferenceOrchestrationStopReasons.All);
        Assert.Contains(ReferenceFeedbackDecisions.Accepted, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackDecisions.Rejected, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackDecisions.Edited, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackTargetTypes.ReuseCandidate, ReferenceFeedbackTargetTypes.All);
    }

    [Fact]
    public void AnchoredDraftPayloadSerializesBeatCandidatesWithoutFullChapterAssembly()
    {
        var payload = new ReferenceAnchoredDraftPayload(
            BlueprintId: 10,
            Candidates:
            [
                new ReferenceDraftParagraphCandidatePayload(
                    CandidateId: "candidate-1",
                    BlueprintId: 10,
                    BeatId: "beat-1",
                    MaterialId: "material-1",
                    RewriteLevel: ReferenceRewriteLevels.L1,
                    Text: "候选段落",
                    ChangedSlots: [new ReferenceSlotValuePayload("object", "门")],
                    NonSlotEdits: [],
                    AuditStatus: "passed",
                    CreatedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"))
            ],
            Audit: null);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        var candidate = Assert.Single(root.GetProperty("candidates").EnumerateArray());
        Assert.Equal("beat-1", candidate.GetProperty("beat_id").GetString());
        Assert.Equal("候选段落", candidate.GetProperty("text").GetString());
        Assert.False(root.TryGetProperty("chapter_text", out _));
        Assert.False(root.TryGetProperty("assembled_text", out _));
        Assert.False(root.TryGetProperty("full_chapter", out _));
    }

    [Fact]
    public void CompatibilityRegistryIncludesReferenceAnchorMethods()
    {
        string[] expected =
        [
            "CreateReferenceAnchor",
            "GetReferenceAnchors",
            "DeleteReferenceAnchor",
            "DeleteReferenceAnchors",
            "PromoteReferenceAnchorsToWorkspaceCorpus",
            "PromoteReferenceAnchorToWorkspaceCorpus",
            "UpdateReferenceAnchorMetadata",
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
            "AuditReferenceAnchoredDraft",
            "StartReferenceOrchestrationRun",
            "GetReferenceOrchestrationRuns",
            "GetReferenceOrchestrationRun",
            "ResumeReferenceOrchestrationRun",
            "CancelReferenceOrchestrationRun"
        ];

        foreach (var method in expected)
        {
            Assert.Contains(method, BridgeCompatibilityAppMethods.MethodNames);
        }
    }
}
