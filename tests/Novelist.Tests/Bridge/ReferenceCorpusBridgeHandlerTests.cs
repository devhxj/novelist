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
        Assert.DoesNotContain("source_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", raw, StringComparison.OrdinalIgnoreCase);
        Assert.True(json.RootElement.GetProperty("result").GetProperty("ready_for_insertion").GetBoolean());
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
    public async Task SearchReferenceCorpusCandidatesDoesNotExposeEmbeddingOrSourceText()
    {
        var service = new RecordingReferenceCorpusService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusHandlers(service);

        using var json = await AssertOkJsonAsync(
            dispatcher,
            "SearchReferenceCorpusCandidates",
            BuildSearchPayload(pageSize: 20));

        var raw = json.RootElement.GetProperty("result").GetRawText();
        Assert.DoesNotContain("embedding", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_path", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", raw, StringComparison.OrdinalIgnoreCase);
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

    private static void AssertReferenceCorpusFeatureAnalysisRunDoesNotLeakSourceFields(string raw)
    {
        Assert.DoesNotContain("node_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model_output_json", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", raw, StringComparison.OrdinalIgnoreCase);
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

        public ValueTask<PageResultPayload<ReferenceCorpusCandidatePayload>> SearchCandidatesAsync(
            SearchReferenceCorpusCandidatesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    }

    private sealed class RecordingReferenceCorpusWritingService : IReferenceCorpusWritingService
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ReferenceCorpusInsertionDraftPayload> GenerateInsertionDraftAsync(
            GenerateReferenceCorpusInsertionDraftPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                $"GenerateInsertionDraft:{input.NaturalLanguageGoal}:{input.ChapterContext.NovelId}:{input.ChapterContext.ChapterNumber}");
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
            return ValueTask.FromResult(new ReferenceCorpusInsertionDraftPayload(
                QueryContext: query,
                Blueprint: new ReferenceCorpusInsertionBlueprintPayload(
                    BlueprintId: "blueprint-1",
                    QueryContextHash: "query-hash",
                    Strategy: "single_beat_m1",
                    Beats:
                    [
                        new ReferenceCorpusInsertionBlueprintBeatPayload(
                            BeatId: "beat-1",
                            BeatIndex: 0,
                            RoleInBeat: "source_sentence",
                            NarrativeFunction: "raise_pressure",
                            NodeIds: ["node-1"])
                    ]),
                Pieces:
                [
                    new ReferenceCorpusInsertionPiecePayload(
                        PieceId: "piece-1",
                        BeatId: "beat-1",
                        CandidateId: "candidate-1",
                        NodeId: "node-1",
                        AnchorId: 101,
                        LibraryId: "library-rain-doorway",
                        SourceTextHash: "source-hash",
                        ReusePolicy: ReferenceCorpusReusePolicies.AdaptedOnly,
                        LicenseState: ReferenceCorpusLicenseStates.Authorized,
                        OutputText: "林岚没有立刻开口。",
                        PreservedTextHash: "preserved-hash",
                        PreservedHashMatches: true,
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
                            NodeId: "node-1",
                            ShouldBlock: false,
                            FourGramContainmentRatio: 0.1,
                            LongestCommonSubstringRatio: 0.1,
                            Violations: [])
                    ])));
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
    }
}
