using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceCorpusBridgeHandlerTests
{
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
}
