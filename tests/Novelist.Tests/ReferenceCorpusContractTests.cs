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
