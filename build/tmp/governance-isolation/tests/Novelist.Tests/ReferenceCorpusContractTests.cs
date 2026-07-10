using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;

namespace Novelist.Tests;

public sealed class ReferenceCorpusContractTests
{
    [Fact]
    public void TechniqueSpecimenAnalysisStartPayloadUsesBudgetAndResumeSnakeCaseFields()
    {
        var payload = new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
        NovelId: 42,
        AnchorId: 101,
        SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
        MinObservationConfidence: 0.70,
        RunId: "technique-run-1",
        TokenBudget: 64,
        Resume: true);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(64, root.GetProperty("token_budget").GetInt32());
        Assert.True(root.GetProperty("resume").GetBoolean());
        Assert.False(root.TryGetProperty("TokenBudget", out _));
        Assert.False(root.TryGetProperty("Resume", out _));
    }

    [Fact]
    public void TechniqueSpecimenAnalysisRunPayloadUsesBudgetAndResumeCursorSnakeCaseFields()
    {
        var payload = new ReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
        RunId: "technique-run-1",
        NovelId: 42,
        AnchorId: 101,
        Scope: "technique_specimen",
 Status: "budget_exhausted",
        TokenBudget: 64,
        TokensSpent: 64,
        ResumeCursor: "node-b",
        SpecimenCount: 2,
        ProcessedNodes: 2,
        AnalyzerVersion: "reference-corpus-technique-specimen-llm-v1",
 SchemaVersion: "reference-corpus-technique-specimen-v1",
        ModelProvider: "fake",
        ModelId: "fake-model",
        StartedAt: DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        CompletedAt: null,
        Diagnostics: ["budget_exhausted"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(64, root.GetProperty("token_budget").GetInt32());
        Assert.Equal("node-b", root.GetProperty("resume_cursor").GetString());
        Assert.False(root.TryGetProperty("TokenBudget", out _));
        Assert.False(root.TryGetProperty("ResumeCursor", out _));
    }

    [Fact]
    public void PageRequestAndPageResultUseStableCursorCapableSnakeCaseShape()
    {
        var request = new PageRequestPayload(
            Cursor: "cursor-1",
            PageSize: 40,
            SortBy: "score",
            SortDir: "desc",
            Filters: new Dictionary<string, string>
            {
                ["feature_family"] = "sensory"
            });

        using var requestJson = JsonDocument.Parse(JsonSerializer.Serialize(request, BridgeJson.SerializerOptions));
        var requestRoot = requestJson.RootElement;
        Assert.Equal("cursor-1", requestRoot.GetProperty("cursor").GetString());
        Assert.Equal(40, requestRoot.GetProperty("page_size").GetInt32());
        Assert.Equal("score", requestRoot.GetProperty("sort_by").GetString());
        Assert.Equal("desc", requestRoot.GetProperty("sort_dir").GetString());
        Assert.Equal("sensory", requestRoot.GetProperty("filters").GetProperty("feature_family").GetString());
        Assert.False(requestRoot.TryGetProperty("PageSize", out _));

        var result = new PageResultPayload<string>(
            Items: ["node-1"],
            Total: 100,
            Page: 1,
            Size: 40,
            TotalPages: 3,
            NextCursor: "cursor-2",
            HasMore: true,
            TotalEstimate: 100);

        using var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeJson.SerializerOptions));
        var resultRoot = resultJson.RootElement;
        Assert.Equal("node-1", resultRoot.GetProperty("items")[0].GetString());
        Assert.Equal("cursor-2", resultRoot.GetProperty("next_cursor").GetString());
        Assert.True(resultRoot.GetProperty("has_more").GetBoolean());
        Assert.Equal(100, resultRoot.GetProperty("total_estimate").GetInt32());
        Assert.False(resultRoot.TryGetProperty("NextCursor", out _));
    }

    [Fact]
    public void SearchReferenceCorpusCandidatesPayloadKeepsChapterEmbeddingBackendOnly()
    {
        var payload = new SearchReferenceCorpusCandidatesPayload(
            QueryContext: BuildQueryContext(),
            PageRequest: new PageRequestPayload(
                Cursor: null,
                PageSize: 20,
                SortBy: "score",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["node_type"] = "sentence" }));

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;
        var query = root.GetProperty("query_context");
        var chapter = query.GetProperty("chapter_context");
        var scope = query.GetProperty("scope");

        Assert.Equal("doorway_confrontation", query.GetProperty("scene_type").GetString());
        Assert.Equal(42, chapter.GetProperty("novel_id").GetInt64());
        Assert.Equal("林岚停在门里。", chapter.GetProperty("current_draft_text").GetString());
        Assert.Equal(3, chapter.GetProperty("insertion_offset").GetInt32());
        Assert.Equal("林岚", chapter.GetProperty("character_snapshots")[0].GetProperty("character").GetString());
        Assert.Equal("project:42:default", scope.GetProperty("session_id").GetString());
        AssertJsonDoesNotExposeProperties(root, "embedding");
        Assert.False(root.TryGetProperty("QueryContext", out _));
    }

    [Fact]
    public void ReferenceCorpusTechniqueVectorBackfillPayloadUsesStableSnakeCaseDiagnostics()
    {
        var request = new BackfillReferenceCorpusTechniqueVectorIndexPayload(
            BuildQueryContext(),
            ReferenceCorpusNodeTypes.Sentence);

        var requestJson = JsonSerializer.Serialize(request, BridgeJson.SerializerOptions);
        using var requestDocument = JsonDocument.Parse(requestJson);
        var requestRoot = requestDocument.RootElement;

        Assert.Equal("sentence", requestRoot.GetProperty("node_type").GetString());
        Assert.Equal(42, requestRoot.GetProperty("query_context").GetProperty("chapter_context").GetProperty("novel_id").GetInt64());
        Assert.False(requestRoot.TryGetProperty("NodeType", out _));
        AssertJsonDoesNotExposeProperties(requestRoot, "embedding", "source_text", "raw_text", "prompt");

        var response = new ReferenceCorpusTechniqueVectorIndexBackfillPayload(
            ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Ready,
            IndexScopeKey: "idx-scope-1",
            TableName: "vec_reference_technique_fixture_8",
            ProviderKey: "fake",
            ModelId: "hash-model",
            Dimensions: 8,
            SourceCount: 2,
            VectorCount: 2,
            SkippedVectorCount: 0,
            Rebuilt: true,
            Diagnostics: ["native_technique_index_rebuilt"]);

        var responseJson = JsonSerializer.Serialize(response, BridgeJson.SerializerOptions);
        using var responseDocument = JsonDocument.Parse(responseJson);
        var responseRoot = responseDocument.RootElement;

        Assert.Equal("ready", responseRoot.GetProperty("status").GetString());
        Assert.Equal("idx-scope-1", responseRoot.GetProperty("index_scope_key").GetString());
        Assert.Equal("vec_reference_technique_fixture_8", responseRoot.GetProperty("table_name").GetString());
        Assert.Equal("fake", responseRoot.GetProperty("provider_key").GetString());
        Assert.Equal("hash-model", responseRoot.GetProperty("model_id").GetString());
        Assert.Equal(8, responseRoot.GetProperty("dimensions").GetInt32());
        Assert.Equal(2, responseRoot.GetProperty("source_count").GetInt32());
        Assert.True(responseRoot.GetProperty("rebuilt").GetBoolean());
        Assert.Equal("native_technique_index_rebuilt", responseRoot.GetProperty("diagnostics")[0].GetString());
        Assert.False(responseRoot.TryGetProperty("IndexScopeKey", out _));
        AssertJsonDoesNotExposeProperties(responseRoot, "embedding", "source_text", "raw_text", "prompt");
    }

    [Fact]
    public void ReferenceCorpusCandidatePayloadReturnsPreviewAndEvidenceWithoutSourceLeakFields()
    {
        var payload = new ReferenceCorpusCandidatePayload(
            CandidateId: "candidate-node-1",
            NodeId: "node-rain-doorway-s1",
            AnchorId: 101,
            LibraryId: "library-rain-doorway",
            NodeType: ReferenceCorpusNodeTypes.Sentence,
            TextPreview: "雨声贴着门缝往里挤。",
            TextHash: "sha256-fixture-s1",
            LicenseState: ReferenceCorpusLicenseStates.Authorized,
            ReusePolicy: ReferenceCorpusReusePolicies.AdaptedOnly,
            Score: 0.91,
            ScoreComponents: new Dictionary<string, double>
            {
                ["semantic"] = 0.58,
                ["current_chapter_fit"] = 0.33
            },
            FitExplanation: "sensory doorway pressure matches insertion context",
            Evidence:
            [
                new ReferenceCorpusCandidateEvidencePayload(
                    ObservationId: "obs-rain-doorway-sensory",
                    FeatureFamily: "sensory",
                    FeatureKey: "auditory_pressure",
                    Confidence: 0.92)
            ]);

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal("candidate-node-1", root.GetProperty("candidate_id").GetString());
        Assert.Equal("node-rain-doorway-s1", root.GetProperty("node_id").GetString());
        Assert.Equal("雨声贴着门缝往里挤。", root.GetProperty("text_preview").GetString());
        Assert.Equal("authorized", root.GetProperty("license_state").GetString());
        Assert.Equal("adapted_only", root.GetProperty("reuse_policy").GetString());
        Assert.Equal(0.58, root.GetProperty("score_components").GetProperty("semantic").GetDouble());
        Assert.Equal("obs-rain-doorway-sensory", root.GetProperty("evidence")[0].GetProperty("observation_id").GetString());
        Assert.False(root.TryGetProperty("raw_text", out _));
        Assert.False(root.TryGetProperty("source_text", out _));
        Assert.False(root.TryGetProperty("source_path", out _));
        Assert.False(root.TryGetProperty("prompt", out _));
        Assert.False(root.TryGetProperty("embedding", out _));
    }

    [Fact]
    public void ReferenceCorpusBlueprintCandidatesPayloadUsesStableFeedbackFieldsWithoutSourceLeakFields()
    {
        var payload = new ReferenceCorpusBlueprintCandidatesPayload(
            QueryContext: BuildQueryContext(),
            Candidates:
            [
                new ReferenceCorpusBlueprintCandidatePayload(
                    Blueprint: BuildBlueprint(),
                    SourceDistribution:
                    [
                        new ReferenceCorpusBlueprintSourcePayload(
                            LibraryId: "library-rain-doorway",
                            AnchorId: 101,
                            NodeCount: 1)
                    ],
                    CoverageScore: 0.86,
                    GapReasons: ["single_library_source"],
                    FeedbackReason: "rejected_nodes:1",
                    GapPositions:
                    [
                        new ReferenceCorpusBlueprintGapPositionPayload(
                            BeatId: "corpus-beat-1",
                            BeatIndex: 0,
                            RoleInBeat: "source_sentence",
                            NarrativeFunction: "raise_pressure",
                            NodeIds: ["node-rain-doorway-s1"],
                            CoveredDimensions: ["emotion"],
                            MissingDimensions: ["rhythm", "narrative", "technique"],
                            GapReasons:
                            [
                                "missing_rhythm_evidence",
                                "missing_narrative_evidence",
                                "missing_technique_coverage"
                            ])
                    ])
            ],
            FeedbackApplied: true,
            FeedbackSummary: "rejected_blueprints:1;rejected_nodes:1");

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.True(root.GetProperty("feedback_applied").GetBoolean());
        Assert.Equal(JsonValueKind.True, root.GetProperty("feedback_applied").ValueKind);
        Assert.Equal("rejected_blueprints:1;rejected_nodes:1", root.GetProperty("feedback_summary").GetString());
        Assert.Equal("corpus-blueprint-1", root.GetProperty("candidates")[0].GetProperty("blueprint").GetProperty("blueprint_id").GetString());
        Assert.Equal("library-rain-doorway", root.GetProperty("candidates")[0].GetProperty("source_distribution")[0].GetProperty("library_id").GetString());
        var gapPosition = root.GetProperty("candidates")[0].GetProperty("gap_positions")[0];
        Assert.Equal("corpus-beat-1", gapPosition.GetProperty("beat_id").GetString());
        Assert.Equal(0, gapPosition.GetProperty("beat_index").GetInt32());
        Assert.Equal("emotion", gapPosition.GetProperty("covered_dimensions")[0].GetString());
        Assert.Equal("rhythm", gapPosition.GetProperty("missing_dimensions")[0].GetString());
        Assert.Equal("missing_rhythm_evidence", gapPosition.GetProperty("gap_reasons")[0].GetString());
        Assert.False(root.TryGetProperty("FeedbackApplied", out _));
        AssertJsonDoesNotExposeProperties(root, "source_text", "raw_text", "embedding");
    }

    [Fact]
    public void ReferenceCorpusInsertionDraftInputSerializesSelectedBlueprintAsSnakeCase()
    {
        var payload = new GenerateReferenceCorpusInsertionDraftPayload(
            NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口。",
            ChapterContext: BuildQueryContext().ChapterContext,
            Scope: BuildQueryContext().Scope,
            SlotValues: new Dictionary<string, string>
            {
                ["她"] = "林岚"
            },
            SelectedBlueprint: BuildBlueprint());

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;
        var selectedBlueprint = root.GetProperty("selected_blueprint");

        Assert.Equal("corpus-blueprint-1", selectedBlueprint.GetProperty("blueprint_id").GetString());
        Assert.Equal("corpus-beat-1", selectedBlueprint.GetProperty("beats")[0].GetProperty("beat_id").GetString());
        Assert.False(root.TryGetProperty("SelectedBlueprint", out _));
        AssertJsonDoesNotExposeProperties(root, "source_text", "raw_text", "embedding");
    }

    [Fact]
    public void ReferenceCorpusInsertionDraftCandidatesPayloadUsesStableCandidateWrapperWithoutSourceLeakFields()
    {
        var request = new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
            NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口。",
            ChapterContext: BuildQueryContext().ChapterContext,
            Scope: BuildQueryContext().Scope,
            SlotValues: new Dictionary<string, string>
            {
                ["她"] = "林岚"
            },
            SelectedBlueprint: BuildBlueprint(),
            RequestedCount: 3,
            SlotValueVariants:
            [
                new ReferenceCorpusDraftSlotValueVariantPayload(
                    VariantId: "strict-current-scene",
                    Label: "门内对峙",
                    SlotValues: new Dictionary<string, string>
                    {
                        ["character:她"] = "林岚",
                        ["place:门口"] = "暗廊"
                    })
            ]);

        var requestJson = JsonSerializer.Serialize(request, BridgeJson.SerializerOptions);
        using var requestDocument = JsonDocument.Parse(requestJson);
        var requestRoot = requestDocument.RootElement;

        Assert.Equal(3, requestRoot.GetProperty("requested_count").GetInt32());
        Assert.Equal("corpus-blueprint-1", requestRoot.GetProperty("selected_blueprint").GetProperty("blueprint_id").GetString());
        var slotVariant = requestRoot.GetProperty("slot_value_variants")[0];
        Assert.Equal("strict-current-scene", slotVariant.GetProperty("variant_id").GetString());
        Assert.Equal("门内对峙", slotVariant.GetProperty("label").GetString());
        Assert.Equal("林岚", slotVariant.GetProperty("slot_values").GetProperty("character:她").GetString());
        Assert.Equal("暗廊", slotVariant.GetProperty("slot_values").GetProperty("place:门口").GetString());
        Assert.False(requestRoot.TryGetProperty("RequestedCount", out _));
        Assert.False(requestRoot.TryGetProperty("SlotValueVariants", out _));
        AssertJsonDoesNotExposeProperties(requestRoot, "source_text", "raw_text", "embedding");

        var response = new ReferenceCorpusInsertionDraftCandidatesPayload(
            QueryContext: BuildQueryContext(),
            SelectedBlueprint: BuildBlueprint(),
            Candidates:
            [
                new ReferenceCorpusInsertionDraftCandidatePayload(
                    CandidateId: "corpus-draft-candidate-1",
                    Strategy: "source_variant_1",
                    Explanation: "Uses selected source nodes.",
                    Draft: BuildDraft(),
                    NextAction: new ReferenceCorpusDraftCandidateNextActionPayload(
                        Action: ReferenceCorpusDraftCandidateNextActions.RegenerateBlueprint,
                        ReasonCode: "transition_replacement_outside_selected_blueprint",
                        Message: "Regenerate blueprint candidates with the rejected source removed.",
                        TransitionId: "transition-1",
                        RejectedPieceId: "piece-1",
                        RejectedNodeId: "node-1",
                        ReplacementNodeId: "node-2",
                        Feedback: new ReferenceCorpusBlueprintFeedbackPayload(
                            RejectedBlueprintIds: ["corpus-blueprint-1"],
                            RejectedNodeIds: ["node-1"],
                            AvoidLibraryIds: [],
                            AvoidAnchorIds: [],
                            ProblemTags: ["transition_replacement_required"],
                            Notes: "Transition replacement requested.")))
            ]);

        var responseJson = JsonSerializer.Serialize(response, BridgeJson.SerializerOptions);
        using var responseDocument = JsonDocument.Parse(responseJson);
        var responseRoot = responseDocument.RootElement;
        var candidate = responseRoot.GetProperty("candidates")[0];

        Assert.Equal("corpus-blueprint-1", responseRoot.GetProperty("selected_blueprint").GetProperty("blueprint_id").GetString());
        Assert.Equal("corpus-draft-candidate-1", candidate.GetProperty("candidate_id").GetString());
        Assert.Equal("source_variant_1", candidate.GetProperty("strategy").GetString());
        Assert.Equal("林岚没有立刻开口。", candidate.GetProperty("draft").GetProperty("assembled_text").GetString());
        var nextAction = candidate.GetProperty("next_action");
        Assert.Equal("regenerate_blueprint", nextAction.GetProperty("action").GetString());
        Assert.Equal("transition_replacement_outside_selected_blueprint", nextAction.GetProperty("reason_code").GetString());
        Assert.Equal("transition-1", nextAction.GetProperty("transition_id").GetString());
        Assert.Equal("piece-1", nextAction.GetProperty("rejected_piece_id").GetString());
        Assert.Equal("node-1", nextAction.GetProperty("rejected_node_id").GetString());
        Assert.Equal("node-2", nextAction.GetProperty("replacement_node_id").GetString());
        Assert.Equal("node-1", nextAction.GetProperty("feedback").GetProperty("rejected_node_ids")[0].GetString());
        Assert.Equal("transition_replacement_required", nextAction.GetProperty("feedback").GetProperty("problem_tags")[0].GetString());
        Assert.False(nextAction.TryGetProperty("ReasonCode", out _));
        Assert.Equal(JsonValueKind.Array, candidate.GetProperty("draft").GetProperty("transitions").ValueKind);
        var piece = candidate.GetProperty("draft").GetProperty("pieces")[0];
        var span = piece.GetProperty("preserved_spans")[0];
        Assert.Equal("preserved-span-1", span.GetProperty("span_id").GetString());
        Assert.Equal("span-hash", span.GetProperty("source_text_hash").GetString());
        Assert.True(span.GetProperty("matches").GetBoolean());
        var lockedSpan = piece.GetProperty("locked_spans")[0];
        Assert.Equal("locked-span-1", lockedSpan.GetProperty("span_id").GetString());
        Assert.Equal("quoted_text", lockedSpan.GetProperty("reason").GetString());
        Assert.True(lockedSpan.GetProperty("matches").GetBoolean());
        var audit = candidate.GetProperty("draft").GetProperty("audit");
        Assert.True(audit.GetProperty("passed").GetBoolean());
        Assert.Equal("passed", audit.GetProperty("status").GetString());
        Assert.Equal("piece-1", audit.GetProperty("pieces")[0].GetProperty("piece_id").GetString());
        Assert.Equal(JsonValueKind.Array, audit.GetProperty("transitions").ValueKind);
        Assert.False(responseRoot.TryGetProperty("SelectedBlueprint", out _));
        AssertJsonDoesNotExposeProperties(responseRoot, "source_text", "raw_text", "embedding");
    }

    [Fact]
    public void ReferenceCorpusTransitionPayloadUsesAuditableSnakeCaseShape()
    {
        var payload = new ReferenceCorpusTransitionPayload(
            TransitionId: "transition-1",
            GapId: "gap-1",
            AfterPieceId: "piece-1",
            BeforePieceId: "piece-2",
            Decision: ReferenceCorpusTransitionDecisions.InsertTransition,
            Strategy: "bridge_sentence",
            Text: "雨声压近了一寸。",
            TextHash: "transition-hash",
            OutputStart: 8,
            OutputEnd: 17,
            Approved: true,
            Reason: "Bridge two selected source-backed pieces.");

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal("transition-1", root.GetProperty("transition_id").GetString());
        Assert.Equal("gap-1", root.GetProperty("gap_id").GetString());
        Assert.Equal("piece-1", root.GetProperty("after_piece_id").GetString());
        Assert.Equal("piece-2", root.GetProperty("before_piece_id").GetString());
        Assert.Equal("insert_transition", root.GetProperty("decision").GetString());
        Assert.Equal("bridge_sentence", root.GetProperty("strategy").GetString());
        Assert.Equal(8, root.GetProperty("output_start").GetInt32());
        Assert.Equal(17, root.GetProperty("output_end").GetInt32());
        Assert.True(root.GetProperty("approved").GetBoolean());
        Assert.False(root.TryGetProperty("replacement_piece_id", out _));
        Assert.False(root.TryGetProperty("replacement_node_id", out _));
        Assert.False(root.TryGetProperty("TransitionId", out _));
        AssertJsonDoesNotExposeProperties(root, "source_text", "raw_text", "embedding");
    }

    [Fact]
    public void ReferenceCorpusReplacePieceTransitionSerializesReplacementIdsAsSnakeCase()
    {
        var payload = new ReferenceCorpusTransitionPayload(
            TransitionId: "transition-replace-1",
            GapId: "gap-1",
            AfterPieceId: "piece-1",
            BeforePieceId: "piece-2",
            Decision: ReferenceCorpusTransitionDecisions.ReplacePiece,
            Strategy: "replace_piece",
            Text: string.Empty,
            TextHash: "empty-hash",
            OutputStart: 0,
            OutputEnd: 0,
            Approved: false,
            Reason: "Replacement required inside selected blueprint beat.",
            ReplacementPieceId: "piece-1",
            ReplacementNodeId: "node-alternative-1");

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal("replace_piece", root.GetProperty("decision").GetString());
        Assert.Equal("piece-1", root.GetProperty("replacement_piece_id").GetString());
        Assert.Equal("node-alternative-1", root.GetProperty("replacement_node_id").GetString());
        Assert.False(root.GetProperty("approved").GetBoolean());
        Assert.False(root.TryGetProperty("ReplacementPieceId", out _));
        Assert.False(root.TryGetProperty("ReplacementNodeId", out _));
        AssertJsonDoesNotExposeProperties(root, "source_text", "raw_text", "embedding");
    }

    [Fact]
    public void ReferenceCorpusConstantsDocumentLicenseAndNodeVocabulary()
    {
        Assert.Contains(ReferenceCorpusNodeTypes.Sentence, ReferenceCorpusNodeTypes.All);
        Assert.Contains(ReferenceCorpusNodeTypes.Passage, ReferenceCorpusNodeTypes.All);
        Assert.Contains(ReferenceCorpusReusePolicies.AdaptedOnly, ReferenceCorpusReusePolicies.All);
        Assert.Contains(ReferenceCorpusLicenseStates.Authorized, ReferenceCorpusLicenseStates.All);
    }

    private static ReferenceCorpusQueryContextPayload BuildQueryContext()
    {
        return new ReferenceCorpusQueryContextPayload(
            SceneType: "doorway_confrontation",
            EmotionTarget: "restrained_pressure",
            PacingTarget: "slow_tension",
            NarrativePosition: "pre-reveal",
            CommercialMechanic: "withheld-answer-hook",
            CharacterStates: ["林岚 guarded"],
            RequiredNarrativeFunctions: ["raise_pressure", "avoid_reveal"],
            ChapterContext: new CurrentChapterContextPayload(
                NovelId: 42,
                ChapterNumber: 3,
                CurrentDraftText: "林岚停在门里。",
                InsertionOffset: 3,
                PreviousChapterSummary: "门外有人靠近。",
                CharacterSnapshots:
                [
                    new CharacterStateSnapshotPayload(
                        Character: "林岚",
                        State: "guarded",
                        AllowedKnowledge: ["门外有人靠近"],
                        ForbiddenKnowledge: ["周鸣的真实目的"])
                ]),
            Scope: new ReferenceCorpusScopePayload(
                LibraryIds: ["library-rain-doorway"],
                ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                IncludeAnchorIds: [101],
                ExcludeAnchorIds: [],
                SessionId: "project:42:default"));
    }

    private static ReferenceCorpusInsertionBlueprintPayload BuildBlueprint()
    {
        return new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "corpus-blueprint-1",
            QueryContextHash: "query-context-hash-1",
            Strategy: "score_focus_m1",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "corpus-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-rain-doorway-s1"])
            ]);
    }

    private static ReferenceCorpusInsertionDraftPayload BuildDraft()
    {
        return new ReferenceCorpusInsertionDraftPayload(
            QueryContext: BuildQueryContext(),
            Blueprint: BuildBlueprint(),
            Pieces:
            [
                new ReferenceCorpusInsertionPiecePayload(
                    PieceId: "piece-1",
                    BeatId: "corpus-beat-1",
                    CandidateId: "candidate-1",
                    NodeId: "node-rain-doorway-s1",
                    AnchorId: 101,
                    LibraryId: "library-rain-doorway",
                    SourceTextHash: "sha256-fixture-s1",
                    ReusePolicy: ReferenceCorpusReusePolicies.AdaptedOnly,
                    LicenseState: ReferenceCorpusLicenseStates.Authorized,
                    OutputText: "林岚没有立刻开口。",
                    PreservedTextHash: "preserved-hash",
                    PreservedHashMatches: true,
                    PreservedSpans:
                    [
                        new ReferenceCorpusPreservedSpanPayload(
                            SpanId: "preserved-span-1",
                            SourceStart: 1,
                            SourceEnd: 8,
                            OutputStart: 2,
                            OutputEnd: 9,
                            SourceTextHash: "span-hash",
                            OutputTextHash: "span-hash",
                            Matches: true)
                    ],
                    LockedSpans:
                    [
                        new ReferenceCorpusLockedSpanPayload(
                            SpanId: "locked-span-1",
                            SourceStart: 9,
                            SourceEnd: 15,
                            OutputStart: 10,
                            OutputEnd: 16,
                            SourceTextHash: "locked-hash",
                            OutputTextHash: "locked-hash",
                            Matches: true,
                            Reason: "quoted_text")
                    ],
                    SlotReplacements:
                    [
                        new ReferenceCorpusSlotReplacementPayload(
                            SlotName: "character",
                            SourceValue: "她",
                            ReplacementValue: "林岚",
                            SourceStart: 0,
                            SourceEnd: 1,
                            OutputStart: 0,
                            OutputEnd: 2)
                    ])
            ],
            SlotReplacements:
            [
                new ReferenceCorpusSlotReplacementPayload(
                    SlotName: "character",
                    SourceValue: "她",
                    ReplacementValue: "林岚",
                    SourceStart: 0,
                    SourceEnd: 1,
                            OutputStart: 0,
                            OutputEnd: 2)
            ],
            Transitions: [],
            AssembledText: "林岚没有立刻开口。",
            ChapterTextAfterInsertion: "林岚停在门里。\n林岚没有立刻开口。",
            ReadyForInsertion: true,
            Gate: new ReferenceCorpusInsertionGatePayload(
                Passed: true,
                Status: "passed",
                Errors: [],
                Pieces:
                [
                    new ReferenceCorpusInsertionGatePiecePayload(
                        PieceId: "piece-1",
                        NodeId: "node-rain-doorway-s1",
                        ShouldBlock: false,
                        FourGramContainmentRatio: 0.1,
                        LongestCommonSubstringRatio: 0.1,
                        Violations: [])
                    ]),
            Audit: new ReferenceCorpusDraftAuditPayload(
                Passed: true,
                Status: "passed",
                Errors: [],
                Pieces:
                [
                    new ReferenceCorpusDraftAuditPiecePayload(
                        PieceId: "piece-1",
                        NodeId: "node-rain-doorway-s1",
                        Passed: true,
                        PreservedSpanCount: 1,
                        MismatchedSpanCount: 0,
                        Violations: [])
                ],
                Transitions: []));
    }

    private static void AssertJsonDoesNotExposeProperties(JsonElement element, params string[] propertyNames)
    {
        AssertJsonDoesNotExposeProperties(element, propertyNames.ToHashSet(StringComparer.OrdinalIgnoreCase), "$");
    }

    private static void AssertJsonDoesNotExposeProperties(
        JsonElement element,
        ISet<string> propertyNames,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.False(
                    propertyNames.Contains(property.Name),
                    $"JSON property '{property.Name}' must not be exposed at {path}.");
                AssertJsonDoesNotExposeProperties(property.Value, propertyNames, path + "." + property.Name);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                AssertJsonDoesNotExposeProperties(item, propertyNames, path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]");
                index++;
            }
        }
    }
}
