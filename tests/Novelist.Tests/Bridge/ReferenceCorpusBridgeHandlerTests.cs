using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceCorpusBridgeHandlerTests
{
    [Fact]
    public async Task StartReferenceCorpusFeatureAnalysisRoutesToAnalysisServiceWithoutSourceLeakFields()
    {
        var service = new RecordingReferenceCorpusAnalysisService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusAnalysisHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "StartReferenceCorpusFeatureAnalysis",
            BuildStartAnalysisPayload());

        Assert.Equal(["StartFeatureAnalysis:42:101:sentence:50:False"], service.Calls);
        var raw = json.RootElement.GetProperty("result").GetRawText();
        Assert.Contains("run_id", raw, StringComparison.Ordinal);
        Assert.Contains("observation_count", raw, StringComparison.Ordinal);
        AssertReferenceCorpusFeatureAnalysisRunDoesNotLeakSourceFields(raw);
    }

    [Fact]
    public async Task GetReferenceCorpusFeatureAnalysisRunRoutesToAnalysisService()
    {
        var service = new RecordingReferenceCorpusAnalysisService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusAnalysisHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "GetReferenceCorpusFeatureAnalysisRun",
            BuildGetAnalysisPayload());

        Assert.Equal(["GetFeatureAnalysisRun:42:analysis-run-1"], service.Calls);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("analysis-run-1", result.GetProperty("run_id").GetString());
        Assert.Equal("completed", result.GetProperty("status").GetString());
        AssertReferenceCorpusFeatureAnalysisRunDoesNotLeakSourceFields(result.GetRawText());
    }

    [Fact]
    public async Task StartReferenceCorpusTechniqueSpecimenAnalysisRoutesToAnalysisServiceWithoutSourceLeakFields()
    {
        var service = new RecordingReferenceCorpusAnalysisService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusAnalysisHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "StartReferenceCorpusTechniqueSpecimenAnalysis",
            BuildStartTechniqueSpecimenAnalysisPayload());

        Assert.Equal(["StartTechniqueSpecimenAnalysis:42:101:sentence:0.7:64:True"], service.Calls);
        var result = json.RootElement.GetProperty("result");
        var raw = result.GetRawText();
        Assert.Contains("specimen_count", raw, StringComparison.Ordinal);
        Assert.Contains("processed_nodes", raw, StringComparison.Ordinal);
        Assert.Equal(64, result.GetProperty("token_budget").GetInt32());
        Assert.Equal("node-b", result.GetProperty("resume_cursor").GetString());
        AssertReferenceCorpusAnalysisRunDoesNotLeakSourceFields(raw);
    }

    [Fact]
    public async Task GetReferenceCorpusTechniqueSpecimenAnalysisRunRoutesToAnalysisService()
    {
        var service = new RecordingReferenceCorpusAnalysisService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusAnalysisHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "GetReferenceCorpusTechniqueSpecimenAnalysisRun",
            BuildGetTechniqueSpecimenAnalysisPayload());

        Assert.Equal(["GetTechniqueSpecimenAnalysisRun:42:technique-run-1"], service.Calls);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("technique-run-1", result.GetProperty("run_id").GetString());
        Assert.Equal("completed", result.GetProperty("status").GetString());
        AssertReferenceCorpusAnalysisRunDoesNotLeakSourceFields(result.GetRawText());
    }

    [Fact]
    public async Task ListReferenceCorpusFeatureObservationsRoutesToAnalysisServiceAndReturnsPageResult()
    {
        var service = new RecordingReferenceCorpusAnalysisService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusAnalysisHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "ListReferenceCorpusFeatureObservations",
            BuildListFeatureObservationsPayload(pageSize: 20));

        Assert.Equal(["ListFeatureObservations:42:101:node-b:20:created_at:desc"], service.Calls);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("obs-emotion", result.GetProperty("items")[0].GetProperty("observation_id").GetString());
        Assert.Equal("emotion", result.GetProperty("items")[0].GetProperty("feature_family").GetString());
        Assert.Equal("cursor-next", result.GetProperty("next_cursor").GetString());
        Assert.True(result.GetProperty("has_more").GetBoolean());
        AssertReferenceCorpusAnalysisListDoesNotLeakSourceFields(result.GetRawText());
    }

    [Fact]
    public async Task ListReferenceCorpusTechniqueSpecimensRoutesToAnalysisServiceAndReturnsEvidenceTrace()
    {
        var service = new RecordingReferenceCorpusAnalysisService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusAnalysisHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "ListReferenceCorpusTechniqueSpecimens",
            BuildListTechniqueSpecimensPayload(pageSize: 20));

        Assert.Equal(["ListTechniqueSpecimens:42:101:node-b:20:created_at:desc"], service.Calls);
        var result = json.RootElement.GetProperty("result");
        var specimen = result.GetProperty("items")[0];
        Assert.Equal("spec-1", specimen.GetProperty("specimen_id").GetString());
        Assert.Equal("action_as_emotion", specimen.GetProperty("technique_family").GetString());
        Assert.Equal("obs-emotion", specimen.GetProperty("evidence")[0].GetProperty("observation_id").GetString());
        Assert.True(specimen.GetProperty("why_it_works").GetProperty("trace_complete").GetBoolean());
        Assert.Equal("role", specimen.GetProperty("transfer_slots")[0].GetProperty("slot_name").GetString());
        AssertReferenceCorpusAnalysisListDoesNotLeakSourceFields(result.GetRawText());
    }

    [Fact]
    public async Task ListReferenceCorpusFeatureObservationsRejectsPageSizeAboveLimitAsValidationError()
    {
        var service = new RecordingReferenceCorpusAnalysisService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusAnalysisHandlers(service);

        using var json = await DispatchAsync(
            dispatcher,
            "ListReferenceCorpusFeatureObservations",
            BuildListFeatureObservationsPayload(pageSize: 201));

        var root = json.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Contains(
            PageRequestErrorCodes.PageSizeOutOfRange,
            error.GetProperty("details").GetProperty("page_request").GetString(),
            StringComparison.Ordinal);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task ListReferenceCorpusFeatureObservationsRejectsUnsupportedFilterBeforeServiceCall()
    {
        var service = new RecordingReferenceCorpusAnalysisService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusAnalysisHandlers(service);

        var payload = BuildListFeatureObservationsPayload(pageSize: 20) with
        {
            PageRequest = new PageRequestPayload(
                Cursor: null,
                PageSize: 20,
                SortBy: "created_at",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["unsupported_filter"] = "x" })
        };
        using var json = await DispatchAsync(
            dispatcher,
            "ListReferenceCorpusFeatureObservations",
            payload);

        var root = json.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Contains(
            PageRequestErrorCodes.InvalidFilterKey,
            error.GetProperty("details").GetProperty("page_request").GetString(),
            StringComparison.Ordinal);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task GenerateReferenceCorpusBlueprintCandidatesRoutesToWritingServiceWithoutSourceLeakFields()
    {
        var service = new RecordingReferenceCorpusWritingService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusWritingHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "GenerateReferenceCorpusBlueprintCandidates",
            BuildBlueprintCandidatesPayload());

        Assert.Equal(["GenerateBlueprintCandidates:doorway goal:42:3:3"], service.Calls);
        var raw = json.RootElement.GetProperty("result").GetRawText();
        Assert.Contains("candidates", raw, StringComparison.Ordinal);
        Assert.Contains("source_distribution", raw, StringComparison.Ordinal);
        Assert.False(json.RootElement.GetProperty("result").GetProperty("feedback_applied").GetBoolean());
        Assert.Equal("none", json.RootElement.GetProperty("result").GetProperty("feedback_summary").GetString());
        AssertJsonDoesNotExposeProperties(raw, "source_text", "raw_text", "embedding");
        Assert.Equal(2, json.RootElement.GetProperty("result").GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public async Task GenerateReferenceCorpusInsertionDraftRoutesToWritingServiceWithoutSourceLeakFields()
    {
        var service = new RecordingReferenceCorpusWritingService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusWritingHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "GenerateReferenceCorpusInsertionDraft",
            BuildInsertionDraftPayload());

        Assert.Equal(["GenerateInsertionDraft:doorway goal:42:3"], service.Calls);
        var raw = json.RootElement.GetProperty("result").GetRawText();
        Assert.Contains("assembled_text", raw, StringComparison.Ordinal);
        Assert.Contains("preserved_spans", raw, StringComparison.Ordinal);
        AssertJsonDoesNotExposeProperties(raw, "source_text", "raw_text", "embedding");
        Assert.True(json.RootElement.GetProperty("result").GetProperty("ready_for_insertion").GetBoolean());
    }

    [Fact]
    public async Task GenerateReferenceCorpusInsertionDraftCandidatesRoutesToWritingServiceWithoutSourceLeakFields()
    {
        var service = new RecordingReferenceCorpusWritingService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusWritingHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "GenerateReferenceCorpusInsertionDraftCandidates",
            BuildInsertionDraftCandidatesPayload());

        Assert.Equal(["GenerateInsertionDraftCandidates:doorway goal:42:3:3"], service.Calls);
        var raw = json.RootElement.GetProperty("result").GetRawText();
        var result = json.RootElement.GetProperty("result");
        Assert.Contains("selected_blueprint", raw, StringComparison.Ordinal);
        Assert.Contains("candidates", raw, StringComparison.Ordinal);
        Assert.Contains("assembled_text", raw, StringComparison.Ordinal);
        Assert.Equal("selected-blueprint-1", result.GetProperty("selected_blueprint").GetProperty("blueprint_id").GetString());
        Assert.Equal(2, result.GetProperty("candidates").GetArrayLength());
        Assert.True(result.GetProperty("candidates")[0].GetProperty("draft").GetProperty("ready_for_insertion").GetBoolean());
        Assert.Contains("preserved_spans", raw, StringComparison.Ordinal);
        AssertJsonDoesNotExposeProperties(raw, "source_text", "raw_text", "embedding");
    }

    [Fact]
    public async Task SearchReferenceCorpusCandidatesRoutesToServiceAndReturnsPageResult()
    {
        var service = new RecordingReferenceCorpusService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "SearchReferenceCorpusCandidates",
            BuildSearchPayload(pageSize: 20));

        Assert.Equal(
            ["SearchCandidates:doorway_confrontation:42:3:20:score:desc"],
            service.Calls);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("candidate-node-1", result.GetProperty("items")[0].GetProperty("candidate_id").GetString());
        Assert.Equal("cursor-next", result.GetProperty("next_cursor").GetString());
        Assert.True(result.GetProperty("has_more").GetBoolean());
        Assert.Equal(1, result.GetProperty("total_estimate").GetInt32());
    }

    [Fact]
    public async Task BackfillReferenceCorpusTechniqueVectorIndexRoutesToService()
    {
        var service = new RecordingReferenceCorpusService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "BackfillReferenceCorpusTechniqueVectorIndex",
            new BackfillReferenceCorpusTechniqueVectorIndexPayload(
                BuildSearchPayload(pageSize: 20).QueryContext,
                ReferenceCorpusNodeTypes.Sentence));

        Assert.Equal(
            ["BackfillTechniqueVectorIndex:doorway_confrontation:42:sentence"],
            service.Calls);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("ready", result.GetProperty("status").GetString());
        Assert.Equal("idx-scope-1", result.GetProperty("index_scope_key").GetString());
        Assert.Equal("vec_reference_technique_fixture_8", result.GetProperty("table_name").GetString());
        Assert.Equal(1, result.GetProperty("source_count").GetInt32());
        Assert.Equal(1, result.GetProperty("vector_count").GetInt32());
        AssertJsonDoesNotExposeProperties(result.GetRawText(), "embedding", "source_text", "raw_text", "prompt");
    }

    [Fact]
public async Task SearchReferenceCorpusCandidatesRejectsPageSizeAboveLimitAsValidationError()
    {
        var service = new RecordingReferenceCorpusService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusHandlers(service);

        using var json = await DispatchAsync(
            dispatcher,
            "SearchReferenceCorpusCandidates",
            BuildSearchPayload(pageSize: 201));

        var root = json.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Contains(
            PageRequestErrorCodes.PageSizeOutOfRange,
            error.GetProperty("details").GetProperty("page_request").GetString(),
            StringComparison.Ordinal);
Assert.Empty(service.Calls);
}

 [Fact]
 public async Task SearchReferenceCorpusCandidatesMapsInvalidFeedbackToValidationError()
 {
 var service = new RecordingReferenceCorpusService { ThrowOnSearch = new ArgumentException("Unsupported retrieval route 'unknown'.") };
 var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusHandlers(service);

 using var json = await DispatchAsync(dispatcher, "SearchReferenceCorpusCandidates", BuildSearchPayload(pageSize: 20));

 Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
 Assert.Equal("VALIDATION_ERROR", json.RootElement.GetProperty("error").GetProperty("code").GetString());
 Assert.Contains("Unsupported retrieval route",
 json.RootElement.GetProperty("error").GetProperty("details").GetProperty("input").GetString());
 }

    [Fact]
    public async Task SearchReferenceCorpusCandidatesDoesNotExposeEmbeddingOrSourceText()
    {
        var service = new RecordingReferenceCorpusService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "SearchReferenceCorpusCandidates",
            BuildSearchPayload(pageSize: 20));

        var raw = json.RootElement.GetProperty("result").GetRawText();
        AssertJsonDoesNotExposeProperties(raw, "embedding", "source_text", "raw_text", "source_path", "prompt");
    }

    private static SearchReferenceCorpusCandidatesPayload BuildSearchPayload(int pageSize)
    {
        return new SearchReferenceCorpusCandidatesPayload(
            new ReferenceCorpusQueryContextPayload(
                SceneType: "doorway_confrontation",
                EmotionTarget: "restrained_pressure",
                PacingTarget: "slow_tension",
                NarrativePosition: "pre-reveal",
                CommercialMechanic: "withheld-answer-hook",
                CharacterStates: ["林岚 guarded"],
                RequiredNarrativeFunctions: ["raise_pressure"],
                ChapterContext: new CurrentChapterContextPayload(
                    NovelId: 42,
                    ChapterNumber: 3,
                    CurrentDraftText: "林岚停在门里。",
                    InsertionOffset: 3,
                    PreviousChapterSummary: "门外有人靠近。",
                    CharacterSnapshots:
                    [
                        new CharacterStateSnapshotPayload(
                            "林岚",
                            "guarded",
                            ["门外有人靠近"],
                            ["周鸣的真实目的"])
                    ]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: ["library-rain-doorway"],
                    ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                    IncludeAnchorIds: [101],
                    ExcludeAnchorIds: [])),
            new PageRequestPayload(
                Cursor: null,
                PageSize: pageSize,
                SortBy: "score",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["node_type"] = ReferenceCorpusNodeTypes.Sentence }));
    }

    private static GenerateReferenceCorpusInsertionDraftPayload BuildInsertionDraftPayload()
    {
        return new GenerateReferenceCorpusInsertionDraftPayload(
            NaturalLanguageGoal: "doorway goal",
            ChapterContext: new CurrentChapterContextPayload(
                NovelId: 42,
                ChapterNumber: 3,
                CurrentDraftText: "林岚停在门里。",
                InsertionOffset: 3,
                PreviousChapterSummary: "门外有人靠近。",
                CharacterSnapshots:
                [
                    new CharacterStateSnapshotPayload(
                        "林岚",
                        "guarded",
                        ["门外有人靠近"],
                        ["周鸣的真实目的"])
                ]),
            Scope: new ReferenceCorpusScopePayload(
                LibraryIds: ["library-rain-doorway"],
                ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                IncludeAnchorIds: [101],
                ExcludeAnchorIds: []),
            SlotValues: new Dictionary<string, string>());
    }

    private static GenerateReferenceCorpusInsertionDraftCandidatesPayload BuildInsertionDraftCandidatesPayload()
    {
        return new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
            NaturalLanguageGoal: "doorway goal",
            ChapterContext: new CurrentChapterContextPayload(
                NovelId: 42,
                ChapterNumber: 3,
                CurrentDraftText: "林岚停在门里。",
                InsertionOffset: 3,
                PreviousChapterSummary: "门外有人靠近。",
                CharacterSnapshots:
                [
                    new CharacterStateSnapshotPayload(
                        "林岚",
                        "guarded",
                        ["门外有人靠近"],
                        ["周鸣的真实目的"])
                ]),
            Scope: new ReferenceCorpusScopePayload(
                LibraryIds: [],
                ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                IncludeAnchorIds: [],
                ExcludeAnchorIds: [],
                SessionId: "project:42:default"),
            SlotValues: new Dictionary<string, string>(),
            SelectedBlueprint: new ReferenceCorpusInsertionBlueprintPayload(
                BlueprintId: "selected-blueprint-1",
                QueryContextHash: "query-hash",
                Strategy: "score_focus_m1",
                Beats:
                [
                    new ReferenceCorpusInsertionBlueprintBeatPayload(
                        BeatId: "selected-blueprint-1-beat-1",
                        BeatIndex: 0,
                        RoleInBeat: "source_sentence",
                        NarrativeFunction: "raise_pressure",
                        NodeIds: ["node-1", "node-2"])
                ]),
            RequestedCount: 3);
    }

    private static GenerateReferenceCorpusBlueprintCandidatesPayload BuildBlueprintCandidatesPayload()
    {
        return new GenerateReferenceCorpusBlueprintCandidatesPayload(
            NaturalLanguageGoal: "doorway goal",
            ChapterContext: new CurrentChapterContextPayload(
                NovelId: 42,
                ChapterNumber: 3,
                CurrentDraftText: "林岚停在门里。",
                InsertionOffset: 3,
                PreviousChapterSummary: "门外有人靠近。",
                CharacterSnapshots:
                [
                    new CharacterStateSnapshotPayload(
                        "林岚",
                        "guarded",
                        ["门外有人靠近"],
                        ["周鸣的真实目的"])
                ]),
            Scope: new ReferenceCorpusScopePayload(
                LibraryIds: [],
                ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                IncludeAnchorIds: [],
                ExcludeAnchorIds: [],
                SessionId: "project:42:default"),
            RequestedCount: 3,
            Feedback: null);
    }

    private static StartReferenceCorpusFeatureAnalysisPayload BuildStartAnalysisPayload()
    {
        return new StartReferenceCorpusFeatureAnalysisPayload(
            NovelId: 42,
            AnchorId: 101,
            Scope: ReferenceCorpusNodeTypes.Sentence,
            TokenBudget: 50,
            Resume: false,
            RunId: "analysis-run-1");
    }

    private static GetReferenceCorpusFeatureAnalysisRunPayload BuildGetAnalysisPayload()
    {
        return new GetReferenceCorpusFeatureAnalysisRunPayload(
            NovelId: 42,
            RunId: "analysis-run-1");
    }

    private static StartReferenceCorpusTechniqueSpecimenAnalysisPayload BuildStartTechniqueSpecimenAnalysisPayload()
    {
        return new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
            NovelId: 42,
            AnchorId: 101,
            SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
            MinObservationConfidence: 0.70,
 RunId: "technique-run-1",
 TokenBudget: 64,
 Resume: true);
    }

    private static GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload BuildGetTechniqueSpecimenAnalysisPayload()
    {
        return new GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
            NovelId: 42,
            RunId: "technique-run-1");
    }

    private static ListReferenceCorpusFeatureObservationsPayload BuildListFeatureObservationsPayload(int pageSize)
    {
        return new ListReferenceCorpusFeatureObservationsPayload(
            NovelId: 42,
            AnchorId: 101,
            NodeId: "node-b",
            PageRequest: new PageRequestPayload(
                Cursor: null,
                PageSize: pageSize,
                SortBy: "created_at",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["feature_family"] = ReferenceCorpusFeatureFamilies.Emotion }));
    }

    private static ListReferenceCorpusTechniqueSpecimensPayload BuildListTechniqueSpecimensPayload(int pageSize)
    {
        return new ListReferenceCorpusTechniqueSpecimensPayload(
            NovelId: 42,
            AnchorId: 101,
            SourceNodeId: "node-b",
            PageRequest: new PageRequestPayload(
                Cursor: null,
                PageSize: pageSize,
                SortBy: "created_at",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["technique_family"] = "action_as_emotion" }));
    }

    private static void AssertReferenceCorpusFeatureAnalysisRunDoesNotLeakSourceFields(string raw)
    {
        AssertReferenceCorpusAnalysisRunDoesNotLeakSourceFields(raw);
    }

    private static void AssertReferenceCorpusAnalysisRunDoesNotLeakSourceFields(string raw)
    {
        AssertJsonDoesNotExposeProperties(
            raw,
            "node_text",
            "source_text",
            "raw_text",
            "raw_source",
            "prompt",
            "model_output_json",
            "embedding",
            "value_json");
    }

    private static void AssertReferenceCorpusAnalysisListDoesNotLeakSourceFields(string raw)
    {
        AssertJsonDoesNotExposeProperties(
            raw,
            "node_text",
            "source_text",
            "raw_text",
            "raw_source",
            "prompt",
            "model_output_json",
            "embedding",
            "source_path",
            "value_json",
            "why_it_works_json");
    }

    private static void AssertJsonDoesNotExposeProperties(string raw, params string[] propertyNames)
    {
        using var document = JsonDocument.Parse(raw);
        AssertJsonDoesNotExposeProperties(document.RootElement, propertyNames.ToHashSet(StringComparer.OrdinalIgnoreCase), "$");
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

    private static async Task<JsonDocument> AssertOkJsonAsync(
        BridgeDispatcher dispatcher,
        string method,
        params object?[] args)
    {
        var json = await DispatchAsync(dispatcher, method, args);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean(), json.RootElement.GetRawText());
        return json;
    }

    private static async Task<JsonDocument> DispatchAsync(
        BridgeDispatcher dispatcher,
        string method,
        params object?[] args)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                kind = "request",
                id = "request-1",
                method,
                payload = new { args }
            },
            BridgeJson.SerializerOptions);
        var response = await dispatcher.DispatchAsync(payload);
        return ParseOutbound(response);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

private sealed class RecordingReferenceCorpusService : IReferenceCorpusService
{
public List<string> Calls { get; } = [];
 public Exception? ThrowOnSearch { get; init; }

        public ValueTask<PageResultPayload<ReferenceCorpusCandidatePayload>> SearchCandidatesAsync(
            SearchReferenceCorpusCandidatesPayload input,
            CancellationToken cancellationToken)
{
cancellationToken.ThrowIfCancellationRequested();
 if (ThrowOnSearch is not null) throw ThrowOnSearch;
            Calls.Add(
                $"SearchCandidates:{input.QueryContext.SceneType}:{input.QueryContext.ChapterContext.NovelId}:{input.QueryContext.ChapterContext.ChapterNumber}:{input.PageRequest.PageSize}:{input.PageRequest.SortBy}:{input.PageRequest.SortDir}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceCorpusCandidatePayload>(
                Items:
                [
                    new ReferenceCorpusCandidatePayload(
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
                        ScoreComponents: new Dictionary<string, double> { ["semantic"] = 0.58 },
                        FitExplanation: "sensory doorway pressure matches insertion context",
                        Evidence:
                        [
                            new ReferenceCorpusCandidateEvidencePayload(
                                ObservationId: "obs-rain-doorway-sensory",
                                FeatureFamily: "sensory",
                                FeatureKey: "auditory_pressure",
                                Confidence: 0.92)
                        ])
                ],
                Total: 1,
                Page: 1,
                Size: input.PageRequest.PageSize,
                TotalPages: 1,
                NextCursor: "cursor-next",
                HasMore: true,
                TotalEstimate: 1));
        }

        public ValueTask<ReferenceCorpusTechniqueVectorIndexBackfillPayload> BackfillTechniqueVectorIndexAsync(
            BackfillReferenceCorpusTechniqueVectorIndexPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"BackfillTechniqueVectorIndex:{input.QueryContext.SceneType}:{input.QueryContext.ChapterContext.NovelId}:{input.NodeType}");
            return ValueTask.FromResult(new ReferenceCorpusTechniqueVectorIndexBackfillPayload(
                ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Ready,
                IndexScopeKey: "idx-scope-1",
                TableName: "vec_reference_technique_fixture_8",
                ProviderKey: "fake-provider",
                ModelId: "fake-model",
                Dimensions: 8,
                SourceCount: 1,
                VectorCount: 1,
                SkippedVectorCount: 0,
                Rebuilt: true,
                Diagnostics: ["native_technique_index_rebuilt"]));
        }
    }

    private sealed class RecordingReferenceCorpusWritingService : IReferenceCorpusWritingService
    {
public List<string> Calls { get; } = [];

 public async ValueTask<ReferenceCorpusBlueprintCandidatePayload> GenerateChapterBlueprintAsync(
 GenerateReferenceCorpusBlueprintCandidatesPayload input,
 CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 var result = await GenerateBlueprintCandidatesAsync(
 input with { RequestedCount = Math.Max(1, input.RequestedCount) },
 cancellationToken);
 return result.Candidates[0];
 }

public ValueTask<ReferenceCorpusBlueprintCandidatesPayload> GenerateBlueprintCandidatesAsync(
            GenerateReferenceCorpusBlueprintCandidatesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                $"GenerateBlueprintCandidates:{input.NaturalLanguageGoal}:{input.ChapterContext.NovelId}:{input.ChapterContext.ChapterNumber}:{input.RequestedCount}");
            var query = new ReferenceCorpusQueryContextPayload(
                SceneType: "doorway_confrontation",
                EmotionTarget: "restrained_pressure",
                PacingTarget: "slow_tension",
                NarrativePosition: "current_insertion",
                CommercialMechanic: "withheld-answer-hook",
                CharacterStates: ["林岚 guarded"],
                RequiredNarrativeFunctions: ["raise_pressure"],
                ChapterContext: input.ChapterContext,
                Scope: input.Scope);
            return ValueTask.FromResult(new ReferenceCorpusBlueprintCandidatesPayload(
                query,
                Candidates:
                [
                    BuildBlueprintCandidate("blueprint-1", "score_focus_m1", "library-rain-doorway", 101, "node-1"),
                    BuildBlueprintCandidate("blueprint-2", "source_diversity_m1", "library-fire-market", 102, "node-2")
                ],
                FeedbackApplied: false,
                FeedbackSummary: "none"));
        }

        public ValueTask<ReferenceCorpusInsertionDraftPayload> GenerateInsertionDraftAsync(
            GenerateReferenceCorpusInsertionDraftPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                $"GenerateInsertionDraft:{input.NaturalLanguageGoal}:{input.ChapterContext.NovelId}:{input.ChapterContext.ChapterNumber}");
            return ValueTask.FromResult(BuildInsertionDraftResult(
                input.ChapterContext,
                input.Scope,
                "blueprint-1",
                "node-1",
                "林岚没有立刻开口。"));
        }

        public ValueTask<ReferenceCorpusInsertionDraftCandidatesPayload> GenerateInsertionDraftCandidatesAsync(
            GenerateReferenceCorpusInsertionDraftCandidatesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                $"GenerateInsertionDraftCandidates:{input.NaturalLanguageGoal}:{input.ChapterContext.NovelId}:{input.ChapterContext.ChapterNumber}:{input.RequestedCount}");
            var query = BuildQuery(input.ChapterContext, input.Scope);
            return ValueTask.FromResult(new ReferenceCorpusInsertionDraftCandidatesPayload(
                QueryContext: query,
                SelectedBlueprint: input.SelectedBlueprint,
                Candidates:
                [
                    new ReferenceCorpusInsertionDraftCandidatePayload(
                        CandidateId: "draft-candidate-1",
                        Strategy: "source_variant_1",
                        Explanation: "Uses selected source nodes.",
                        Draft: BuildInsertionDraftResult(
                            input.ChapterContext,
                            input.Scope,
                            "selected-blueprint-1:draft-1",
                            "node-1",
                            "林岚没有立刻开口。")),
                    new ReferenceCorpusInsertionDraftCandidatePayload(
                        CandidateId: "draft-candidate-2",
                        Strategy: "source_variant_2",
                        Explanation: "Rotates selected source nodes.",
                        Draft: BuildInsertionDraftResult(
                            input.ChapterContext,
                            input.Scope,
                            "selected-blueprint-1:draft-2",
                            "node-2",
                            "林岚把钥匙扣在掌心。"))
                ]));
        }

        private static ReferenceCorpusInsertionDraftPayload BuildInsertionDraftResult(
            CurrentChapterContextPayload chapterContext,
            ReferenceCorpusScopePayload scope,
            string blueprintId,
            string nodeId,
            string outputText)
        {
            var beatId = blueprintId + "-beat-1";
            var pieceId = "piece-" + nodeId;
            return new ReferenceCorpusInsertionDraftPayload(
                QueryContext: BuildQuery(chapterContext, scope),
                Blueprint: new ReferenceCorpusInsertionBlueprintPayload(
                    BlueprintId: blueprintId,
                    QueryContextHash: "query-hash",
                    Strategy: "single_beat_m1",
                    Beats:
                    [
                        new ReferenceCorpusInsertionBlueprintBeatPayload(
                            BeatId: beatId,
                            BeatIndex: 0,
                            RoleInBeat: "source_sentence",
                            NarrativeFunction: "raise_pressure",
                            NodeIds: [nodeId])
                    ]),
                Pieces:
                [
                    new ReferenceCorpusInsertionPiecePayload(
                        PieceId: pieceId,
                        BeatId: beatId,
                        CandidateId: "candidate-" + nodeId,
                        NodeId: nodeId,
                        AnchorId: 101,
                        LibraryId: "library-rain-doorway",
                        SourceTextHash: "source-hash",
                        ReusePolicy: ReferenceCorpusReusePolicies.AdaptedOnly,
                        LicenseState: ReferenceCorpusLicenseStates.Authorized,
                        OutputText: outputText,
                        PreservedTextHash: "preserved-hash",
                        PreservedHashMatches: true,
                        PreservedSpans:
                        [
                            new ReferenceCorpusPreservedSpanPayload(
                                SpanId: "preserved-span-" + nodeId,
                                SourceStart: 1,
                                SourceEnd: 8,
                                OutputStart: 2,
                                OutputEnd: 9,
                                SourceTextHash: "span-hash",
                                OutputTextHash: "span-hash",
                                Matches: true)
                        ],
                        LockedSpans: [],
                        SlotReplacements:
                        [
                            new ReferenceCorpusSlotReplacementPayload(
                                "character",
                                "她",
                                "林岚",
                                0,
                                1,
                                0,
                                2)
                        ])
                ],
                SlotReplacements:
                [
                    new ReferenceCorpusSlotReplacementPayload(
                        "character",
                        "她",
                        "林岚",
                        0,
                        1,
                        0,
                        2)
                ],
                Transitions: [],
                AssembledText: outputText,
                ChapterTextAfterInsertion: "林岚停在门里。\n" + outputText,
                ReadyForInsertion: true,
                Gate: new ReferenceCorpusInsertionGatePayload(
                    Passed: true,
                    Status: "passed",
                    Errors: [],
                    Pieces:
                    [
                        new ReferenceCorpusInsertionGatePiecePayload(
                            PieceId: pieceId,
                            NodeId: nodeId,
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
                            PieceId: pieceId,
                            NodeId: nodeId,
                            Passed: true,
                            PreservedSpanCount: 1,
                            MismatchedSpanCount: 0,
                            Violations: [])
                    ],
                    Transitions: []));
        }

        private static ReferenceCorpusQueryContextPayload BuildQuery(
            CurrentChapterContextPayload chapterContext,
            ReferenceCorpusScopePayload scope)
        {
            return new ReferenceCorpusQueryContextPayload(
                SceneType: "doorway_confrontation",
                EmotionTarget: "restrained_pressure",
                PacingTarget: "slow_tension",
                NarrativePosition: "current_insertion",
                CommercialMechanic: "withheld-answer-hook",
                CharacterStates: ["林岚 guarded"],
                RequiredNarrativeFunctions: ["raise_pressure"],
                ChapterContext: chapterContext,
                Scope: scope);
        }

        private static ReferenceCorpusBlueprintCandidatePayload BuildBlueprintCandidate(
            string blueprintId,
            string strategy,
            string libraryId,
            long anchorId,
            string nodeId)
        {
            return new ReferenceCorpusBlueprintCandidatePayload(
                Blueprint: new ReferenceCorpusInsertionBlueprintPayload(
                    blueprintId,
                    "query-hash",
                    strategy,
                    [
                        new ReferenceCorpusInsertionBlueprintBeatPayload(
                            BeatId: blueprintId + "-beat-1",
                            BeatIndex: 0,
                            RoleInBeat: "source_sentence",
                            NarrativeFunction: "raise_pressure",
                            NodeIds: [nodeId])
                    ]),
                SourceDistribution:
                [
                    new ReferenceCorpusBlueprintSourcePayload(libraryId, anchorId, 1)
                ],
                CoverageScore: 0.8,
                GapReasons: [],
                FeedbackReason: "initial_candidate");
        }
    }

    private sealed class RecordingReferenceCorpusAnalysisService : IReferenceCorpusAnalysisService
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ReferenceCorpusFeatureAnalysisRunPayload> StartFeatureAnalysisAsync(
            StartReferenceCorpusFeatureAnalysisPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"StartFeatureAnalysis:{input.NovelId}:{input.AnchorId}:{input.Scope}:{input.TokenBudget}:{input.Resume}");
            return ValueTask.FromResult(BuildRun(input.RunId ?? "analysis-run-1", input.NovelId, input.AnchorId, input.Scope));
        }

        public ValueTask<ReferenceCorpusFeatureAnalysisRunPayload?> GetFeatureAnalysisRunAsync(
            GetReferenceCorpusFeatureAnalysisRunPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetFeatureAnalysisRun:{input.NovelId}:{input.RunId}");
            return ValueTask.FromResult<ReferenceCorpusFeatureAnalysisRunPayload?>(
                BuildRun(input.RunId, input.NovelId, anchorId: 101, ReferenceCorpusNodeTypes.Sentence));
        }

        public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisRunPayload> StartTechniqueSpecimenAnalysisAsync(
            StartReferenceCorpusTechniqueSpecimenAnalysisPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"StartTechniqueSpecimenAnalysis:{input.NovelId}:{input.AnchorId}:{input.SourceNodeType}:{input.MinObservationConfidence}:{input.TokenBudget}:{input.Resume}");
            return ValueTask.FromResult(BuildTechniqueRun(input.RunId ?? "technique-run-1", input.NovelId, input.AnchorId));
        }

        public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisRunPayload?> GetTechniqueSpecimenAnalysisRunAsync(
            GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetTechniqueSpecimenAnalysisRun:{input.NovelId}:{input.RunId}");
            return ValueTask.FromResult<ReferenceCorpusTechniqueSpecimenAnalysisRunPayload?>(
                BuildTechniqueRun(input.RunId, input.NovelId, anchorId: 101));
        }

        public ValueTask<PageResultPayload<ReferenceCorpusFeatureObservationPayload>> ListFeatureObservationsAsync(
            ListReferenceCorpusFeatureObservationsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"ListFeatureObservations:{input.NovelId}:{input.AnchorId}:{input.NodeId}:{input.PageRequest.PageSize}:{input.PageRequest.SortBy}:{input.PageRequest.SortDir}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceCorpusFeatureObservationPayload>(
                Items:
                [
                    new ReferenceCorpusFeatureObservationPayload(
                        ObservationId: "obs-emotion",
                        NodeId: input.NodeId ?? "node-b",
                        AnchorId: input.AnchorId,
                        NodeType: ReferenceCorpusNodeTypes.Sentence,
                        TextHash: "hash-node-b",
                        FeatureFamily: ReferenceCorpusFeatureFamilies.Emotion,
                        FeatureKey: "emotion_state",
                        ValueKind: "enum",
                        ValuePreview: "restrained",
                        ValueText: "restrained",
                        ValueNum: 7,
                        ValueBool: null,
                        Intensity: 7,
                        Confidence: 0.86,
                        EvidenceStart: 0,
                        EvidenceEnd: 12,
                        EvidencePreview: "她没有开口",
                        Explanation: "动作和沉默共同显示压抑情绪。",
                        ReviewState: "unverified",
                        ValidityState: "active",
                        RunId: "feature-run-1",
                        CreatedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z"))
                ],
                Total: 2,
                Page: 1,
                Size: input.PageRequest.PageSize,
                TotalPages: 1,
                NextCursor: "cursor-next",
                HasMore: true,
                TotalEstimate: 2));
        }

        public ValueTask<PageResultPayload<ReferenceCorpusTechniqueSpecimenPayload>> ListTechniqueSpecimensAsync(
            ListReferenceCorpusTechniqueSpecimensPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"ListTechniqueSpecimens:{input.NovelId}:{input.AnchorId}:{input.SourceNodeId}:{input.PageRequest.PageSize}:{input.PageRequest.SortBy}:{input.PageRequest.SortDir}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceCorpusTechniqueSpecimenPayload>(
                Items:
                [
                    new ReferenceCorpusTechniqueSpecimenPayload(
                        SpecimenId: "spec-1",
                        SourceNodeId: input.SourceNodeId ?? "node-b",
                        SourceAnchorId: input.AnchorId,
                        AnalysisRunId: "technique-run-1",
                        TechniqueFamily: "action_as_emotion",
                        TechniqueAbstract: "用细节动作承载压抑情绪，以沉默留白放大张力",
                        TriggerContext: "角色承压但不能直接说破",
                        TransferTemplate: "[角色] [外化细节动作]，随后留出沉默。",
                        TransferSlots:
                        [
                            new ReferenceCorpusTechniqueTransferSlotPayload(
                                SlotName: "role",
                                Purpose: "当前承压角色",
                                Constraints: "必须承压")
                        ],
                        EffectOnReader: "让读者从动作和空白中补全情绪。",
                        ApplicabilityConditions: ["角色需要压住反应"],
                        FailureModes: ["动作无因果会显得装饰化"],
                        AntiPatterns: ["直接解释角色情绪"],
                        WorldContextDependencies: [],
                        WhyItWorks: new ReferenceCorpusTechniqueWhyItWorksPayload(
                            ContributingFactors:
                            [
                                new ReferenceCorpusTechniqueWhyFactorPayload(
                                    Factor: "外化动作",
                                    ObservationIds: ["obs-emotion"],
                                    Explanation: "有证据。",
                                    Evidence:
                                    [
                                        new ReferenceCorpusTechniqueSpecimenEvidencePayload(
                                            ObservationId: "obs-emotion",
                                            NodeId: input.SourceNodeId ?? "node-b",
                                            NodeType: ReferenceCorpusNodeTypes.Sentence,
                                            TextHash: "hash-node-b",
                                            FeatureFamily: ReferenceCorpusFeatureFamilies.Emotion,
                                            FeatureKey: "emotion_state",
                                            Confidence: 0.86,
                                            EvidenceStart: 0,
                                            EvidenceEnd: 12,
                                            EvidencePreview: "她没有开口",
                                            ValuePreview: "restrained",
                                            Explanation: "动作和沉默共同显示压抑情绪。")
                                    ])
                            ],
                            TraceComplete: true),
                        Confidence: 0.86,
                        ReviewState: "unverified",
                        ValidityState: "active",
                        MasteryNotes: "适合短句。",
                        CreatedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z"),
                        Evidence:
                        [
                            new ReferenceCorpusTechniqueSpecimenEvidencePayload(
                                ObservationId: "obs-emotion",
                                NodeId: input.SourceNodeId ?? "node-b",
                                NodeType: ReferenceCorpusNodeTypes.Sentence,
                                TextHash: "hash-node-b",
                                FeatureFamily: ReferenceCorpusFeatureFamilies.Emotion,
                                FeatureKey: "emotion_state",
                                Confidence: 0.86,
                                EvidenceStart: 0,
                                EvidenceEnd: 12,
                                EvidencePreview: "她没有开口",
                                ValuePreview: "restrained",
                                Explanation: "动作和沉默共同显示压抑情绪。")
                        ])
                ],
                Total: 1,
                Page: 1,
                Size: input.PageRequest.PageSize,
                TotalPages: 1,
                NextCursor: null,
                HasMore: false,
                TotalEstimate: 1));
        }

        private static ReferenceCorpusFeatureAnalysisRunPayload BuildRun(
            string runId,
            long novelId,
            long anchorId,
            string scope)
        {
            return new ReferenceCorpusFeatureAnalysisRunPayload(
                RunId: runId,
                NovelId: novelId,
                AnchorId: anchorId,
                Scope: scope,
                Families: ReferenceCorpusFeatureFamilies.SentenceFamilies,
                Status: ReferenceCorpusAnalysisRunStatuses.Completed,
                TokenBudget: 50,
                TokensSpent: 12,
                ResumeCursor: "node-a|syntax",
                ObservationCount: 2,
                ProcessedWorkItems: 2,
                AnalyzerVersion: "reference-corpus-feature-llm-v1",
                SchemaVersion: ReferenceCorpusFeatureFamilySchemaVersions.V1,
                ModelProvider: "fake",
                ModelId: "fake-model",
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z"),
                CompletedAt: DateTimeOffset.Parse("2026-07-09T00:00:01Z"),
                Diagnostics: ["accepted"]);
        }

        private static ReferenceCorpusTechniqueSpecimenAnalysisRunPayload BuildTechniqueRun(
            string runId,
            long novelId,
            long anchorId)
        {
            return new ReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
                RunId: runId,
                NovelId: novelId,
                AnchorId: anchorId,
                Scope: "technique_specimen",
Status: ReferenceCorpusAnalysisRunStatuses.Completed,
 TokenBudget: 64,
TokensSpent: 21,
 ResumeCursor: "node-b",
                SpecimenCount: 1,
                ProcessedNodes: 1,
                AnalyzerVersion: "reference-corpus-technique-specimen-llm-v1",
                SchemaVersion: ReferenceCorpusTechniqueSpecimenSchemaVersions.V1,
                ModelProvider: "fake",
                ModelId: "fake-model",
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z"),
                CompletedAt: DateTimeOffset.Parse("2026-07-09T00:00:01Z"),
                Diagnostics: ["accepted"]);
        }
    }
}
