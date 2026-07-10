using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;
using Novelist.IntegrationTests.TestDoubles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions GoldenJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SearchCandidatesReturnsLicensedScopedNodesAndCachesBackendEmbeddings()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        var embeddings = new DeterministicHashEmbeddingClient(defaultDimensions: 8);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            embeddings);

        var result = await service.SearchCandidatesAsync(BuildSearchPayload(), CancellationToken.None);

        Assert.Equal(4, result.Total);
        Assert.Equal(10, result.Size);
        Assert.False(result.HasMore);
        Assert.Equal(
            ["node-rain-doorway-s1", "node-rain-doorway-s2", "node-rain-doorway-s3", "node-rain-doorway-s4"],
            result.Items.Select(item => item.NodeId).Order(StringComparer.Ordinal).ToArray());
        Assert.All(result.Items, item =>
        {
            Assert.Equal("library-rain-doorway", item.LibraryId);
            Assert.Equal(ReferenceCorpusLicenseStates.Authorized, item.LicenseState);
            Assert.Equal(ReferenceCorpusReusePolicies.AdaptedOnly, item.ReusePolicy);
            Assert.True(item.Score > 0);
            Assert.True(item.ScoreComponents.ContainsKey("semantic"));
            Assert.True(item.ScoreComponents.ContainsKey("chapter_fit"));
            Assert.DoesNotContain("hidden", item.CandidateId, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(result.Items, item => item.NodeId == "node-hidden-reveal");
        var sensoryCandidate = Assert.Single(result.Items, item => item.NodeId == "node-rain-doorway-s1");
        Assert.Equal("obs-rain-doorway-sensory", sensoryCandidate.Evidence.Single().ObservationId);

        var allEmbeddingInputs = embeddings.Calls.SelectMany(call => call.Inputs).ToArray();
        Assert.Contains("雨声贴着门缝往里挤。", allEmbeddingInputs);
        Assert.Contains("她没有立刻开口，只把钥匙扣在掌心。", allEmbeddingInputs);
        Assert.Contains("林岚停在门里，指尖还按着锁。", allEmbeddingInputs);
        Assert.Contains(embeddings.Calls, call => call.Options.InputKind == BuiltinOnnxEmbeddingModel.DocumentInputKind);
        Assert.Contains(embeddings.Calls, call => call.Options.InputKind == BuiltinOnnxEmbeddingModel.QueryInputKind);
        Assert.Equal(4, await ReadNodeEmbeddingCountAsync(options));
        Assert.Equal(1, await ReadCurrentChapterEmbeddingCacheCountAsync(options));

        var firstCallCount = embeddings.CallCount;
        var second = await service.SearchCandidatesAsync(BuildSearchPayload(), CancellationToken.None);

        Assert.Equal(result.Items.Select(item => item.NodeId), second.Items.Select(item => item.NodeId));
        Assert.Equal(firstCallCount + 1, embeddings.CallCount);
    }

    [Fact]
    public async Task SearchCandidatesRainDoorwayMatchesM3RetrievalGoldenJson()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildSearchPayload(), CancellationToken.None);

        AssertMatchesM3RetrievalGolden(
            "m3-rain-doorway-basic-search",
            result,
            await ReadNodeEmbeddingCountAsync(options),
            await ReadCurrentChapterEmbeddingCacheCountAsync(options),
            await ReadTechniqueVectorCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesFourWayRecallMatchesM3RetrievalGoldenJson()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedFourWayRecallFixtureAsync(options);
        await SeedFourWayUnsafeRouteFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new FourWayRecallEmbeddingClient(defaultDimensions: 4));
        var request = BuildFourWayRecallPayload() with
        {
            QueryContext = BuildFourWayRecallPayload().QueryContext with
            {
                Scope = BuildFourWayRecallPayload().QueryContext.Scope with
                {
                    ExcludeAnchorIds = [203]
                }
            }
        };

        var result = await service.SearchCandidatesAsync(request, CancellationToken.None);

        AssertMatchesM3RetrievalGolden(
            "m3-four-way-recall-diagnostics",
            result,
            await ReadNodeEmbeddingCountAsync(options),
            await ReadCurrentChapterEmbeddingCacheCountAsync(options),
            await ReadTechniqueVectorCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesRejectsForbiddenAndUnknownLicensesEvenWhenExplicitlyIncluded()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildSearchPayload() with
        {
            QueryContext = BuildSearchPayload().QueryContext with
            {
                Scope = BuildSearchPayload().QueryContext.Scope with
                {
                    IncludeAnchorIds = [102, 104]
                }
            }
        }, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
        Assert.DoesNotContain(result.Items, item => item.LicenseState is ReferenceCorpusLicenseStates.Forbidden or ReferenceCorpusLicenseStates.Unknown);
    }

    [Fact]
    public async Task SearchCandidatesUsesSessionBoundLibrariesBeforeScoring()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCrossLibrarySessionFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));

        var result = await service.SearchCandidatesAsync(BuildCrossLibrarySearchPayload(), CancellationToken.None);

        Assert.Equal(2, result.Size);
        Assert.Contains(result.Items, item => item.NodeId == "node-fire-market-s1");
        Assert.True(
            result.Items.Select(item => item.LibraryId).Distinct(StringComparer.Ordinal).Count() >= 2,
            "Session-bound corpus search must keep candidates from more than one enabled library before final scoring.");
        Assert.True(
            result.Items.Select(item => item.AnchorId).Distinct().Count() >= 2,
            "Session-bound corpus search must not collapse the chapter use path to a single anchor.");
        Assert.All(result.Items, item =>
        {
            Assert.Contains(item.LibraryId, new[] { "library-rain-doorway", "library-fire-market" });
            Assert.Equal(ReferenceCorpusLicenseStates.Authorized, item.LicenseState);
            Assert.Equal(ReferenceCorpusReusePolicies.AdaptedOnly, item.ReusePolicy);
        });
    }

    [Fact]
public async Task SearchCandidatesHonorsDisabledSessionLibraries()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCrossLibrarySessionFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));

        var before = await service.SearchCandidatesAsync(BuildCrossLibrarySearchPayload(), CancellationToken.None);

        Assert.Contains(before.Items, item => item.LibraryId == "library-fire-market");

        await SetLibraryEnabledAsync(options, "library-fire-market", enabled: false);
        var after = await service.SearchCandidatesAsync(BuildCrossLibrarySearchPayload(), CancellationToken.None);

        Assert.DoesNotContain(after.Items, item => item.LibraryId == "library-fire-market");
        Assert.All(after.Items, item => Assert.Equal("library-rain-doorway", item.LibraryId));
Assert.NotEmpty(after.Items);
}

 [Fact]
 public async Task SearchCandidatesDoesNotRestoreLibrariesAfterExplicitSessionUnbind()
 {
 var options = CreateOptions();
 await InitializeAsync(options);
 await SeedCrossLibrarySessionFixtureAsync(options);
 await using (var connection = await OpenReferenceConnectionAsync(options))
 await using (var command = connection.CreateCommand())
 {
 command.CommandText = """
 DELETE FROM reference_session_library_binding WHERE session_id='session-cross-corpus';
 INSERT INTO reference_session_library_scope_state(session_id,is_explicit,updated_at)
 VALUES('session-cross-corpus',1,'2026-07-10T00:00:00Z');
 """;
 await command.ExecuteNonQueryAsync();
 }
 var service = new SqliteReferenceCorpusService(
 options,
 new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
 new TopicEmbeddingClient(defaultDimensions: 3));

 var result = await service.SearchCandidatesAsync(BuildCrossLibrarySearchPayload(), CancellationToken.None);

 Assert.Empty(result.Items);
 Assert.Equal(0, result.Total);
 }

    [Fact]
    public async Task SearchCandidatesFoldsCrossLibraryDedupGroupsBeforeScoring()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCrossLibrarySessionFixtureAsync(options);
        await SetSharedDedupGroupAsync(options, "shared-pressure-scene");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));

        var result = await service.SearchCandidatesAsync(BuildCrossLibrarySearchPayload() with
        {
            PageRequest = BuildCrossLibrarySearchPayload().PageRequest with { PageSize = 10 }
        }, CancellationToken.None);

        Assert.NotEmpty(result.Items);
        Assert.DoesNotContain(result.Items, item => item.LibraryId == "library-rain-doorway");
        Assert.Contains(result.Items, item => item.LibraryId == "library-fire-market");
        Assert.Single(result.Items.Select(item => item.AnchorId).Distinct());
    }

    [Fact]
    public async Task SearchCandidatesFiltersByStructuredObservationAndSensoryProjection()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildSearchPayload() with
        {
            PageRequest = BuildSearchPayload().PageRequest with
            {
                PageSize = 10,
                Filters = new Dictionary<string, string>
                {
                    ["node_type"] = ReferenceCorpusNodeTypes.Sentence,
                    ["feature_family"] = "action",
                    ["feature_key"] = "emotion_carrier",
                    ["feature_value_text"] = "action_over_psychology",
                    ["sensory_sense"] = "tactile",
                    ["sensory_min_intensity"] = "0.8"
                }
            }
        }, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("node-rain-doorway-s3", item.NodeId);
        Assert.Contains(item.Evidence, evidence =>
            evidence.FeatureFamily == "action" &&
            evidence.FeatureKey == "emotion_carrier" &&
            evidence.ObservationId == "obs-rain-doorway-action-carrier");
        Assert.Contains(item.Evidence, evidence =>
            evidence.FeatureFamily == "sensory" &&
            evidence.FeatureKey == "senses" &&
            evidence.ObservationId == "obs-rain-doorway-touch");
    }

    [Fact]
    public async Task SearchCandidatesRequiresAllIndexedStructuredObservationFilters()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await AddRhythmLengthObservationAsync(options, "node-rain-doorway-s2", "obs-rain-doorway-rhythm-medium-s2", 19);
        await AddRhythmLengthObservationAsync(options, "node-rain-doorway-s3", "obs-rain-doorway-rhythm-medium-s3", 19);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildSearchPayload() with
        {
            PageRequest = BuildSearchPayload().PageRequest with
            {
                PageSize = 10,
                Filters = new Dictionary<string, string>
                {
                    ["node_type"] = ReferenceCorpusNodeTypes.Sentence,
                    ["feature_filter_0_family"] = "action",
                    ["feature_filter_0_key"] = "emotion_carrier",
                    ["feature_filter_0_value_text"] = "action_over_psychology",
                    ["feature_filter_1_family"] = "rhythm",
                    ["feature_filter_1_key"] = "length_band",
                    ["feature_filter_1_value_num_min"] = "16"
                }
            }
        }, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("node-rain-doorway-s3", item.NodeId);
        Assert.Contains(item.Evidence, evidence =>
            evidence.FeatureFamily == "action" &&
            evidence.FeatureKey == "emotion_carrier");
        Assert.Contains(item.Evidence, evidence =>
            evidence.FeatureFamily == "rhythm" &&
            evidence.FeatureKey == "length_band");
    }

    [Fact]
    public async Task SearchCandidatesRanksTechniqueAbstractEmbeddingSeparatelyFromRawText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedTechniqueSpecimensAsync(options);
        var embeddings = new TechniqueIntentEmbeddingClient(defaultDimensions: 8);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            embeddings);

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload(), CancellationToken.None);

        var top = Assert.Single(result.Items.Take(1));
        Assert.Equal("node-rain-doorway-s3", top.NodeId);
        Assert.True(top.ScoreComponents.TryGetValue("technique_fit", out var techniqueFit));
        Assert.True(techniqueFit > top.ScoreComponents["semantic"]);
        Assert.Equal(2, await ReadTechniqueVectorCountAsync(options));

        var firstCallCount = embeddings.CallCount;
        var second = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload(), CancellationToken.None);

        Assert.Equal(result.Items.Select(item => item.NodeId), second.Items.Select(item => item.NodeId));
        Assert.Equal(firstCallCount + 1, embeddings.CallCount);
    }

    [Fact]
    public async Task SearchCandidatesRecallsTechniqueSpecimenBeyondPerSourcePrefetchWindow()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        var top = Assert.Single(result.Items.Take(1));
        Assert.Equal("node-rain-doorway-far-technique", top.NodeId);
        Assert.Equal("library-rain-doorway", top.LibraryId);
        Assert.True(top.ScoreComponents["technique_fit"] > 0.99);
        Assert.Equal(1, await ReadTechniqueVectorCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesUsesNativeTechniqueTopKWhenAvailable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        await SeedUnrankedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new FakeSqliteVecTechniqueProvider(
            record => record.ChunkId == "specimen-far-action-over-psychology");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 10 }
        }, CancellationToken.None);

        Assert.NotEmpty(nativeProvider.ProvisionCalls);
        Assert.NotEmpty(nativeProvider.SearchCalls);
        var top = Assert.Single(result.Items.Take(1));
        Assert.Equal("node-rain-doorway-far-technique", top.NodeId);
        Assert.True(top.ScoreComponents.TryGetValue("recall_technique_semantic", out var recallRoute));
        Assert.Equal(1, recallRoute);
        Assert.DoesNotContain(result.Items, item => item.NodeId == "node-rain-doorway-far-technique-unranked");
        Assert.Equal(2, await ReadTechniqueVectorCountAsync(options));
        Assert.Equal(2, await ReadTechniqueVectorRowCountAsync(options));
    }

    [Fact]
    public async Task BackfillTechniqueVectorIndexPrewarmsNativeRowsAndSearchDoesNotReprovision()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new FakeSqliteVecTechniqueProvider(
            record => record.ChunkId == "specimen-far-action-over-psychology");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var backfill = await service.BackfillTechniqueVectorIndexAsync(
            new BackfillReferenceCorpusTechniqueVectorIndexPayload(
                BuildTechniqueSearchPayload().QueryContext,
                ReferenceCorpusNodeTypes.Sentence),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Ready, backfill.Status);
        Assert.False(string.IsNullOrWhiteSpace(backfill.IndexScopeKey));
        Assert.False(string.IsNullOrWhiteSpace(backfill.TableName));
        Assert.Equal("fake", backfill.ProviderKey);
        Assert.Equal("hash-model", backfill.ModelId);
        Assert.Equal(8, backfill.Dimensions);
        Assert.Equal(1, backfill.SourceCount);
        Assert.Equal(1, backfill.VectorCount);
        Assert.Equal(0, backfill.SkippedVectorCount);
        Assert.True(backfill.Rebuilt);
        Assert.Contains("native_technique_index_rebuilt", backfill.Diagnostics);
        Assert.Single(nativeProvider.ProvisionCalls);
        Assert.Empty(nativeProvider.SearchCalls);
        Assert.Equal(1, await ReadTechniqueVectorCountAsync(options));
        Assert.Equal(1, await ReadTechniqueVectorRowCountAsync(options));
        Assert.Equal(1, await ReadTechniqueVectorIndexStateCountAsync(options));

        var provisionCountAfterBackfill = nativeProvider.ProvisionCalls.Count;
        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 10 }
        }, CancellationToken.None);

        Assert.Equal(provisionCountAfterBackfill, nativeProvider.ProvisionCalls.Count);
        Assert.NotEmpty(nativeProvider.SearchCalls);
        var top = Assert.Single(result.Items.Take(1));
        Assert.Equal("node-rain-doorway-far-technique", top.NodeId);
        Assert.True(top.ScoreComponents.TryGetValue("recall_technique_semantic", out var recallRoute));
        Assert.Equal(1, recallRoute);
        Assert.Equal(1, await ReadTechniqueVectorRowCountAsync(options));
        Assert.Equal(1, await ReadTechniqueVectorIndexStateCountAsync(options));
    }

    [Fact]
    public async Task BackfillTechniqueVectorIndexFailureReturnsDiagnosticsAndSearchStillFallsBack()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new ThrowingSqliteVecTechniqueProvider();
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var backfill = await service.BackfillTechniqueVectorIndexAsync(
            new BackfillReferenceCorpusTechniqueVectorIndexPayload(
                BuildTechniqueSearchPayload().QueryContext,
                ReferenceCorpusNodeTypes.Sentence),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Failed, backfill.Status);
        Assert.Contains(backfill.Diagnostics, item => item.Contains("native_technique_index_backfill_failed", StringComparison.Ordinal));
        Assert.Equal(1, nativeProvider.ProvisionAttempts);
        Assert.Equal(0, await ReadTechniqueVectorRowCountAsync(options));
        Assert.Equal(0, await ReadTechniqueVectorIndexStateCountAsync(options));

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        var top = Assert.Single(result.Items.Take(1));
        Assert.Equal("node-rain-doorway-far-technique", top.NodeId);
        Assert.True(top.ScoreComponents["technique_fit"] > 0.99);
        Assert.Equal(1, await ReadTechniqueVectorCountAsync(options));
        Assert.Equal(0, await ReadTechniqueVectorRowCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesFallsBackWhenNativeTechniqueTopKFails()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new ThrowingSqliteVecTechniqueProvider();
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        Assert.True(nativeProvider.ProvisionAttempts > 0);
        var top = Assert.Single(result.Items.Take(1));
        Assert.Equal("node-rain-doorway-far-technique", top.NodeId);
        Assert.Equal("library-rain-doorway", top.LibraryId);
        Assert.True(top.ScoreComponents["technique_fit"] > 0.99);
        Assert.Equal(1, await ReadTechniqueVectorCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesNativeTechniqueTopKHonorsExcludedAnchors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new FakeSqliteVecTechniqueProvider(
            record => record.ChunkId == "specimen-far-action-over-psychology");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            QueryContext = BuildTechniqueSearchPayload().QueryContext with
            {
                Scope = BuildTechniqueSearchPayload().QueryContext.Scope with
                {
                    ExcludeAnchorIds = [101]
                }
            },
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 10 }
        }, CancellationToken.None);

        Assert.Empty(nativeProvider.ProvisionCalls);
        Assert.Empty(nativeProvider.SearchCalls);
        Assert.Empty(result.Items);
        Assert.Equal(0, await ReadTechniqueVectorCountAsync(options));
        Assert.Equal(0, await ReadTechniqueVectorRowCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesNativeTechniqueTopKClearsRowsForRejectedSpecimens()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new FakeSqliteVecTechniqueProvider(
            record => record.ChunkId == "specimen-far-action-over-psychology");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var first = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);
        Assert.Contains(first.Items, item => item.NodeId == "node-rain-doorway-far-technique");
        Assert.Equal(1, await ReadTechniqueVectorRowCountAsync(options));

        await SetTechniqueSpecimenReviewStateAsync(
            options,
            "specimen-far-action-over-psychology",
            "rejected");
        var second = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        Assert.DoesNotContain(second.Items, item => item.NodeId == "node-rain-doorway-far-technique");
        Assert.Equal(0, await ReadTechniqueVectorRowCountAsync(options));
        Assert.Equal(0, await ReadTechniqueVectorIndexStateCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesNativeTechniqueTopKRebuildsStaleRowHash()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new FakeSqliteVecTechniqueProvider(
            record => record.ChunkId == "specimen-far-action-over-psychology");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var first = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);
        Assert.Contains(first.Items, item => item.NodeId == "node-rain-doorway-far-technique");
        Assert.Equal(1, await ReadTechniqueVectorRowCountAsync(options));
        Assert.Single(nativeProvider.ProvisionCalls);

        await CorruptNativeTechniqueVectorRowHashAsync(options);
        var second = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        Assert.Contains(second.Items, item => item.NodeId == "node-rain-doorway-far-technique");
        Assert.True(nativeProvider.ProvisionCalls.Count >= 2);
        Assert.Equal(1, await ReadTechniqueVectorRowCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesNativeTechniqueTopKRejectsForgedRowMappingWhenIndexStateMatches()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new FakeSqliteVecTechniqueProvider(
            record => record.ChunkId == "specimen-far-action-over-psychology");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var first = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);
        Assert.Contains(first.Items, item => item.NodeId == "node-rain-doorway-far-technique");
        Assert.Single(nativeProvider.ProvisionCalls);

        await CorruptNativeTechniqueVectorRowSourceNodeAsync(options, "node-rain-doorway-s3", 101);
        var second = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        Assert.Contains(second.Items, item => item.NodeId == "node-rain-doorway-far-technique");
        Assert.DoesNotContain(second.Items, item => item.NodeId == "node-rain-doorway-s3" &&
            item.ScoreComponents.ContainsKey("recall_technique_semantic"));
        Assert.True(nativeProvider.ProvisionCalls.Count >= 2);
    }

    [Fact]
    public async Task SearchCandidatesNativeTechniqueTopKExcludesActiveSpecimenWithSupersededRunId()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        await SetTechniqueSpecimenSupersededRunIdAsync(
            options,
            "specimen-far-action-over-psychology",
            "run-stage1-rain-doorway-next");
        var nativeProvider = new FakeSqliteVecTechniqueProvider(
            record => record.ChunkId == "specimen-far-action-over-psychology");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        Assert.DoesNotContain(result.Items, item => item.NodeId == "node-rain-doorway-far-technique");
        Assert.Empty(nativeProvider.ProvisionCalls);
        Assert.Empty(nativeProvider.SearchCalls);
        Assert.Equal(0, await ReadTechniqueVectorCountAsync(options));
        Assert.Equal(0, await ReadTechniqueVectorRowCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesTechniqueSpecimenUnionDoesNotBypassStructuredFilters()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with
            {
                PageSize = 10,
                Filters = new Dictionary<string, string>
                {
                    ["node_type"] = ReferenceCorpusNodeTypes.Sentence,
                    ["feature_family"] = "action",
                    ["feature_key"] = "emotion_carrier",
                    ["feature_value_text"] = "action_over_psychology",
                    ["sensory_sense"] = "tactile",
                    ["sensory_min_intensity"] = "0.8"
                }
            }
        }, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("node-rain-doorway-s3", item.NodeId);
        Assert.DoesNotContain(result.Items, candidate => candidate.NodeId == "node-rain-doorway-far-technique");
    }

    [Fact]
    public async Task SearchCandidatesNativeTechniqueTopKDoesNotBypassStructuredFilters()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var nativeProvider = new FakeSqliteVecTechniqueProvider(
            record => record.ChunkId == "specimen-far-action-over-psychology");
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8),
            nativeProvider,
            nativeProvider);

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            PageRequest = BuildTechniqueSearchPayload().PageRequest with
            {
                PageSize = 10,
                Filters = new Dictionary<string, string>
                {
                    ["node_type"] = ReferenceCorpusNodeTypes.Sentence,
                    ["feature_family"] = "action",
                    ["feature_key"] = "emotion_carrier",
                    ["feature_value_text"] = "action_over_psychology",
                    ["sensory_sense"] = "tactile",
                    ["sensory_min_intensity"] = "0.8"
                }
            }
        }, CancellationToken.None);

        Assert.NotEmpty(nativeProvider.SearchCalls);
        var item = Assert.Single(result.Items);
        Assert.Equal("node-rain-doorway-s3", item.NodeId);
        Assert.DoesNotContain(result.Items, candidate => candidate.NodeId == "node-rain-doorway-far-technique");
    }

    [Fact]
    public async Task SearchCandidatesTechniqueSpecimenUnionHonorsExcludedAnchors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedFarTechniqueSpecimenFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            QueryContext = BuildTechniqueSearchPayload().QueryContext with
            {
                Scope = BuildTechniqueSearchPayload().QueryContext.Scope with
                {
                    ExcludeAnchorIds = [101]
                }
            },
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, await ReadTechniqueVectorCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesTechniqueSpecimenUnionHonorsForbiddenLicense()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedCorpusFixtureAsync(options);
        await SeedForbiddenTechniqueSpecimenAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildTechniqueSearchPayload() with
        {
            QueryContext = BuildTechniqueSearchPayload().QueryContext with
            {
                Scope = BuildTechniqueSearchPayload().QueryContext.Scope with
                {
                    IncludeAnchorIds = [102]
                }
            },
            PageRequest = BuildTechniqueSearchPayload().PageRequest with { PageSize = 3 }
        }, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, await ReadTechniqueVectorCountAsync(options));
    }

    [Fact]
    public async Task SearchCandidatesRanksInsertionWindowAndAllowedKnowledgeContext()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedLocalContextFitFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new ConstantEmbeddingClient(defaultDimensions: 8));

        var result = await service.SearchCandidatesAsync(BuildLocalContextFitPayload(), CancellationToken.None);

        Assert.Equal(3, result.Total);
        var top = Assert.Single(result.Items.Take(1));
        Assert.Equal("node-local-context-match", top.NodeId);
        Assert.True(top.ScoreComponents.TryGetValue("local_context_fit", out var localContextFit));
        Assert.True(localContextFit > 0.65);

        var generic = Assert.Single(result.Items, item => item.NodeId == "node-local-context-generic");
        Assert.True(top.ScoreComponents["local_context_fit"] > generic.ScoreComponents["local_context_fit"]);

        var forbiddenOnly = Assert.Single(result.Items, item => item.NodeId == "node-local-context-forbidden");
        Assert.True(
            forbiddenOnly.ScoreComponents["local_context_fit"] < 0.20,
            "ForbiddenKnowledge must not be used as a positive context retrieval signal.");
    }

    [Fact]
    public async Task SearchCandidatesMergesFourRecallRoutesWithDiagnostics()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedFourWayRecallFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new FourWayRecallEmbeddingClient(defaultDimensions: 4));

        var result = await service.SearchCandidatesAsync(BuildFourWayRecallPayload(), CancellationToken.None);

        Assert.True(result.Total > result.Items.Count);
        Assert.Equal(4, result.Items.Count);
        Assert.Contains(result.Items, item =>
            item.NodeId == "node-fourway-semantic" &&
            item.ScoreComponents.TryGetValue("recall_text_semantic", out var route) &&
            route == 1);
        Assert.Contains(result.Items, item =>
            item.NodeId == "node-fourway-technique" &&
            item.ScoreComponents.TryGetValue("recall_technique_semantic", out var route) &&
            route == 1);
        Assert.Contains(result.Items, item =>
            item.NodeId == "node-fourway-observation" &&
            item.ScoreComponents.TryGetValue("recall_structured_observation", out var route) &&
            route == 1);
        Assert.Contains(result.Items, item =>
            item.NodeId == "node-fourway-context" &&
            item.ScoreComponents.TryGetValue("recall_chapter_context", out var route) &&
            route == 1);
    }

    [Fact]
    public async Task SearchCandidatesStructuredObservationRecallDoesNotDependOnBasePrefetchWindow()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedFourWayRecallFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new FourWayRecallEmbeddingClient(defaultDimensions: 4));

        var result = await service.SearchCandidatesAsync(BuildFourWayRecallPayload(), CancellationToken.None);

        var recalled = Assert.Single(result.Items, item => item.NodeId == "node-fourway-observation");
        Assert.True(recalled.ScoreComponents.TryGetValue("recall_structured_observation", out var route));
        Assert.Equal(1, route);
        Assert.Contains(recalled.Evidence, item =>
            item.ObservationId == "obs-fourway-observation-route" &&
            item.FeatureFamily == "narrative_function" &&
            item.FeatureKey == "function");
    }

    [Fact]
    public async Task SearchCandidatesStructuredObservationRecallUsesExplicitFeatureFiltersBeyondPrefetchWindow()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedFourWayRecallFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new FourWayRecallEmbeddingClient(defaultDimensions: 4));
        var baseRequest = BuildFourWayRecallPayload();
        var request = baseRequest with
        {
            QueryContext = baseRequest.QueryContext with
            {
                SceneType = "unrelated_scene",
                EmotionTarget = "unrelated_emotion",
                PacingTarget = "unrelated_pacing",
                NarrativePosition = "unrelated_position",
                CommercialMechanic = "unrelated_mechanic",
                RequiredNarrativeFunctions = []
            },
            PageRequest = baseRequest.PageRequest with
            {
                Filters = new Dictionary<string, string>
                {
                    ["node_type"] = ReferenceCorpusNodeTypes.Sentence,
                    ["feature_filter_0_family"] = "narrative_function",
                    ["feature_filter_0_key"] = "function",
                    ["feature_filter_0_value_text"] = "observation_route_marker"
                }
            }
        };

        var result = await service.SearchCandidatesAsync(request, CancellationToken.None);

        var recalled = Assert.Single(result.Items, item => item.NodeId == "node-fourway-observation");
        Assert.True(recalled.ScoreComponents.TryGetValue("recall_structured_observation", out var route));
        Assert.Equal(1, route);
    }

    [Fact]
    public async Task SearchCandidatesChapterContextRecallMarksEveryRouteHitBeyondScoreWinner()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedFourWayRecallFixtureAsync(options);
        await SeedAdditionalChapterContextRouteNodeAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new FourWayRecallEmbeddingClient(defaultDimensions: 4));
        var request = BuildFourWayRecallPayload() with
        {
            PageRequest = BuildFourWayRecallPayload().PageRequest with { PageSize = 32 }
        };

        var result = await service.SearchCandidatesAsync(request, CancellationToken.None);

        var primary = Assert.Single(result.Items, item => item.NodeId == "node-fourway-context");
        var secondary = Assert.Single(result.Items, item => item.NodeId == "node-fourway-context-secondary");
        Assert.True(primary.ScoreComponents["local_context_fit"] > secondary.ScoreComponents["local_context_fit"]);
        Assert.True(primary.ScoreComponents.TryGetValue("recall_chapter_context", out var primaryRoute));
        Assert.Equal(1, primaryRoute);
        Assert.True(secondary.ScoreComponents.TryGetValue("recall_chapter_context", out var route));
        Assert.Equal(1, route);
    }

    [Fact]
    public async Task SearchCandidatesChapterContextRecallHonorsStructuredFiltersBeforeRouteLimit()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedFourWayRecallFixtureAsync(options);
        await SeedChapterContextFilterLimitFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new FourWayRecallEmbeddingClient(defaultDimensions: 4));
        var baseRequest = BuildFourWayRecallPayload();
        var request = baseRequest with
        {
            PageRequest = baseRequest.PageRequest with
            {
                Filters = new Dictionary<string, string>
                {
                    ["node_type"] = ReferenceCorpusNodeTypes.Sentence,
                    ["feature_filter_0_family"] = "rhythm",
                    ["feature_filter_0_key"] = "length_band",
                    ["feature_filter_0_value_text"] = "context_filter_match"
                }
            }
        };

        var result = await service.SearchCandidatesAsync(request, CancellationToken.None);

        var target = Assert.Single(result.Items, item => item.NodeId == "node-fourway-context-filter-target");
        Assert.True(target.ScoreComponents.TryGetValue("recall_chapter_context", out var contextRoute));
        Assert.Equal(1, contextRoute);
        Assert.True(target.ScoreComponents.TryGetValue("recall_structured_observation", out var observationRoute));
        Assert.Equal(1, observationRoute);
    }

    [Fact]
    public async Task SearchCandidatesMergedRecallRoutesHonorScopeLicenseAndDedup()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedFourWayRecallFixtureAsync(options);
        await SeedFourWayUnsafeRouteFixtureAsync(options);
        var service = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new FourWayRecallEmbeddingClient(defaultDimensions: 4));

        var request = BuildFourWayRecallPayload() with
        {
            QueryContext = BuildFourWayRecallPayload().QueryContext with
            {
                Scope = BuildFourWayRecallPayload().QueryContext.Scope with
                {
                    ExcludeAnchorIds = [203]
                }
            },
            PageRequest = BuildFourWayRecallPayload().PageRequest with { PageSize = 16 }
        };
        var result = await service.SearchCandidatesAsync(request, CancellationToken.None);

        Assert.NotEmpty(result.Items);
        Assert.DoesNotContain(result.Items, item => item.AnchorId == 203);
        Assert.DoesNotContain(result.Items, item => item.AnchorId == 204);
        Assert.DoesNotContain(result.Items, item => item.AnchorId == 205);
        Assert.DoesNotContain(result.Items, item => item.NodeId.StartsWith("node-fourway-excluded-", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items, item => item.NodeId.StartsWith("node-fourway-forbidden-", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items, item => item.NodeId.StartsWith("node-fourway-dedup-loser-", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static EmbeddingRequestOptions CreateEmbeddingOptions()
    {
        return new EmbeddingRequestOptions(
            ProviderKey: "fake",
            EndpointUrl: string.Empty,
            ApiKey: string.Empty,
            ModelId: "hash-model",
            Dimensions: 8,
            User: null,
            NormalizeEmbeddings: true);
    }

    private static SearchReferenceCorpusCandidatesPayload BuildSearchPayload()
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
                    NovelId: 3001,
                    ChapterNumber: 3,
                    CurrentDraftText: "林岚停在门里，指尖还按着锁。",
                    InsertionOffset: 8,
                    PreviousChapterSummary: "周鸣失约，林岚只知道有人在雨夜靠近。",
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
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: [])),
            new PageRequestPayload(
                Cursor: null,
                PageSize: 10,
                SortBy: "score",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["node_type"] = ReferenceCorpusNodeTypes.Sentence }));
    }

    private static SearchReferenceCorpusCandidatesPayload BuildCrossLibrarySearchPayload()
    {
        return new SearchReferenceCorpusCandidatesPayload(
            new ReferenceCorpusQueryContextPayload(
                SceneType: "fire_market_confrontation",
                EmotionTarget: "restrained_pressure",
                PacingTarget: "slow_tension",
                NarrativePosition: "pre-reveal",
                CommercialMechanic: "withheld-answer-hook",
                CharacterStates: ["秦砚 guarded"],
                RequiredNarrativeFunctions: ["raise_pressure", "withhold_answer"],
                ChapterContext: new CurrentChapterContextPayload(
                    NovelId: 3001,
                    ChapterNumber: 3,
                    CurrentDraftText: "秦砚停在火光外，指尖还按着旧伤。",
                    InsertionOffset: 10,
                    PreviousChapterSummary: "旧市集起火，有人在火光里回头。",
                    CharacterSnapshots:
                    [
                        new CharacterStateSnapshotPayload(
                            "秦砚",
                            "guarded",
                            ["市集起火"],
                            ["对方真实目的"])
                    ]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: [],
                    SessionId: "session-cross-corpus")),
            new PageRequestPayload(
                Cursor: null,
                PageSize: 2,
                SortBy: "score",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["node_type"] = ReferenceCorpusNodeTypes.Sentence }));
    }

    private static SearchReferenceCorpusCandidatesPayload BuildTechniqueSearchPayload()
    {
        return BuildSearchPayload() with
        {
            QueryContext = BuildSearchPayload().QueryContext with
            {
                EmotionTarget = "动作替代心理描写表现愤怒",
                RequiredNarrativeFunctions = ["用身体细节承载压抑怒意"],
                ChapterContext = BuildSearchPayload().QueryContext.ChapterContext with
                {
                    CurrentDraftText = "林岚站在门边，话到嘴边又压了回去。"
                }
            }
        };
    }

    private static SearchReferenceCorpusCandidatesPayload BuildLocalContextFitPayload()
    {
        return new SearchReferenceCorpusCandidatesPayload(
            new ReferenceCorpusQueryContextPayload(
                SceneType: "tower_gate_standoff",
                EmotionTarget: "restrained_pressure",
                PacingTarget: "slow_tension",
                NarrativePosition: "pre-reveal",
                CommercialMechanic: "withheld-answer-hook",
                CharacterStates: ["秦砚 guarded"],
                RequiredNarrativeFunctions: ["raise_pressure"],
                ChapterContext: new CurrentChapterContextPayload(
                    NovelId: 3001,
                    ChapterNumber: 7,
                    CurrentDraftText: "秦砚停在黑塔门前，掌心压着铜令。",
                    InsertionOffset: 9,
                    PreviousChapterSummary: "队长提醒秦砚，铜令不能落到外人手里。",
                    CharacterSnapshots:
                    [
                        new CharacterStateSnapshotPayload(
                            "秦砚",
                            "guarded",
                            ["队长在场", "铜令真正用途"],
                            ["叛徒身份"])
                    ]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: ["library-local-context"],
                    ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: [])),
            new PageRequestPayload(
                Cursor: null,
                PageSize: 3,
                SortBy: "score",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["node_type"] = ReferenceCorpusNodeTypes.Sentence }));
    }

    private static SearchReferenceCorpusCandidatesPayload BuildFourWayRecallPayload()
    {
        return new SearchReferenceCorpusCandidatesPayload(
            new ReferenceCorpusQueryContextPayload(
                SceneType: "four_way_recall_scene",
                EmotionTarget: "四路召回",
                PacingTarget: "slow_tension",
                NarrativePosition: "pre-reveal",
                CommercialMechanic: "withheld-answer-hook",
                CharacterStates: ["秦砚 guarded"],
                RequiredNarrativeFunctions: ["observation_route_marker"],
                ChapterContext: new CurrentChapterContextPayload(
                    NovelId: 3001,
                    ChapterNumber: 8,
                    CurrentDraftText: "秦砚停在黑塔门前，掌心压着铜令。",
                    InsertionOffset: 9,
                    PreviousChapterSummary: "队长提醒秦砚，铜令不能落到外人手里。",
                    CharacterSnapshots:
                    [
                        new CharacterStateSnapshotPayload(
                            "秦砚",
                            "guarded",
                            ["队长在场", "铜令真正用途"],
                            ["叛徒身份"])
                    ]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: ["library-fourway-recall"],
                    ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: [])),
            new PageRequestPayload(
                Cursor: null,
                PageSize: 4,
                SortBy: "score",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["node_type"] = ReferenceCorpusNodeTypes.Sentence }));
    }

    private static async ValueTask SeedCorpusFixtureAsync(AppInitializationOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReferenceDatabasePath(options))!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS reference_anchors (
                  anchor_id INTEGER PRIMARY KEY,
                  novel_id INTEGER,
                  title TEXT NOT NULL,
                  author TEXT NOT NULL,
                  source_path TEXT NOT NULL,
                  source_kind TEXT NOT NULL,
                  license_status TEXT NOT NULL,
                  source_file_hash TEXT NOT NULL,
                  build_version TEXT NOT NULL,
                  status TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  corpus_visibility TEXT NOT NULL DEFAULT 'private',
                  source_trust TEXT NOT NULL DEFAULT 'user_verified',
                  user_tags_json TEXT NOT NULL DEFAULT '[]'
                );

                INSERT OR IGNORE INTO reference_anchors
                  (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
                VALUES
                  (101, NULL, '雨门小样', '', 'rain-doorway.md', 'markdown', 'user_provided',
                   'source-hash-101', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace'),
                  (102, NULL, '隐藏揭示样本', '', 'hidden-reveal.md', 'markdown', 'user_provided',
                   'source-hash-102', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace'),
                  (104, NULL, '未授权样本', '', 'unknown-license.md', 'markdown', 'unknown',
                   'source-hash-104', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO reference_corpus_libraries
                  (library_id, scope, novel_id, name, created_at)
                VALUES
                  ('library-rain-doorway', 'project', 3001, '雨门语料库', '2026-07-09T00:00:00Z');

                INSERT OR IGNORE INTO reference_library_members
                  (library_id, anchor_id, enabled, source_quality, dedup_group_id)
                VALUES
                  ('library-rain-doorway', 101, 1, 'trusted', 'rain-doorway'),
                  ('library-rain-doorway', 102, 1, 'trusted', 'hidden-reveal'),
                  ('library-rain-doorway', 104, 1, 'trusted', 'unknown-license');

                INSERT OR IGNORE INTO reference_source_license
                  (anchor_id, license_state, authorization_evidence, reuse_policy,
                   max_verbatim_ratio, cleared_for_insertion, reviewed_at)
                VALUES
                  (101, 'authorized', 'fixture', 'adapted_only', 0.42, 1, '2026-07-09T00:00:00Z'),
                  (102, 'forbidden', 'fixture', 'forbidden', 0.00, 0, '2026-07-09T00:00:00Z'),
                  (104, 'unknown', 'fixture', 'adapted_only', 0.00, 0, '2026-07-09T00:00:00Z');

                INSERT OR IGNORE INTO reference_text_nodes
                  (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
                   chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
                VALUES
                  ('node-rain-doorway-s1', 101, NULL, 'sentence', 1, 1,
                   1, 0, 11, 11, 'sha256-fixture-s1', '雨声贴着门缝往里挤。', '2026-07-09T00:00:00Z'),
                  ('node-rain-doorway-s2', 101, NULL, 'sentence', 2, 1,
                   1, 12, 31, 19, 'sha256-fixture-s2', '她没有立刻开口，只把钥匙扣在掌心。', '2026-07-09T00:00:00Z'),
                  ('node-rain-doorway-s3', 101, NULL, 'sentence', 3, 1,
                   1, 32, 51, 19, 'sha256-fixture-s3', '她捏紧拳骨，没有把怒意说出口。', '2026-07-09T00:00:00Z'),
                  ('node-rain-doorway-s4', 101, NULL, 'sentence', 4, 1,
                   1, 52, 68, 16, 'sha256-fixture-s4', '她盯着雨幕，心里很生气。', '2026-07-09T00:00:00Z'),
                  ('node-hidden-reveal', 102, NULL, 'sentence', 1, 1,
                   1, 0, 13, 13, 'sha256-hidden-reveal', '周鸣的真实目的终于暴露。', '2026-07-09T00:00:00Z'),
                  ('node-unknown-license', 104, NULL, 'sentence', 1, 1,
                   1, 0, 14, 14, 'sha256-unknown-license', '未授权文本不能进入候选。', '2026-07-09T00:00:00Z');

                INSERT OR IGNORE INTO reference_analysis_runs
                  (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
                   scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
                VALUES
                  ('run-stage1-rain-doorway', 101, 'fake-stage1-v1', 'corpus-v1', 'fake', 'fake-model',
                   'sentence', 'completed', 100, 12, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 2);

                INSERT OR IGNORE INTO reference_feature_observations
                  (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
                   value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
                   evidence_start, evidence_end, explanation, review_state, validity_state,
                   superseded_by_run_id, created_at)
                VALUES
                  ('obs-rain-doorway-sensory', 'node-rain-doorway-s1', 'sentence',
                   'run-stage1-rain-doorway', 101, 'sensory', 'senses',
                   'array', 'auditory', NULL, NULL, '[{"sense":"auditory","intensity":0.8}]', 0.80, 0.92,
                   0, 10, '雨声门缝形成阈值压迫。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
                  ('obs-rain-doorway-emotion', 'node-rain-doorway-s2', 'sentence',
                   'run-stage1-rain-doorway', 101, 'emotion', 'emotion_state',
                   'enum', 'calm', NULL, NULL, '{"surface":"calm","subtext":"restrained","direction":"stable","mode":"suppressed"}', 0.72, 0.89,
                   0, 18, '不开口用动作压住答案。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
                  ('obs-rain-doorway-action-carrier', 'node-rain-doorway-s3', 'sentence',
                   'run-stage1-rain-doorway', 101, 'action', 'emotion_carrier',
                   'enum', 'action_over_psychology', NULL, NULL, '{"action":"fist_clench","emotion":"suppressed_anger"}', 0.88, 0.94,
                   0, 5, '用捏紧拳骨替代直接心理说明。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
                  ('obs-rain-doorway-touch', 'node-rain-doorway-s3', 'sentence',
                   'run-stage1-rain-doorway', 101, 'sensory', 'senses',
                   'array', 'tactile', NULL, NULL, '[{"sense":"tactile","intensity":0.9}]', 0.90, 0.93,
                   0, 5, '拳骨触觉承载压抑怒意。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
                  ('obs-rain-doorway-direct-anger', 'node-rain-doorway-s4', 'sentence',
                   'run-stage1-rain-doorway', 101, 'emotion', 'emotion_state',
                   'enum', 'direct_anger', NULL, NULL, '{"surface":"angry","mode":"direct"}', 0.60, 0.91,
                   6, 12, '直接说明生气，不是动作承载。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z');

                INSERT OR IGNORE INTO reference_obs_sensory
                  (observation_id, node_id, anchor_id, sense, intensity)
                VALUES
                  ('obs-rain-doorway-sensory', 'node-rain-doorway-s1', 101, 'auditory', 0.80),
                  ('obs-rain-doorway-touch', 'node-rain-doorway-s3', 101, 'tactile', 0.90);
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async ValueTask SeedTechniqueSpecimensAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ('specimen-action-over-psychology', 'node-rain-doorway-s3', 101, 'run-stage1-rain-doorway',
               'emotion_carrier',
               '用细节动作承载压抑愤怒，以沉默留白替代直接心理描写',
               '角色必须压住怒意，场面需要避免直接说明情绪',
               '[角色] [身体细节动作]，不直接说明情绪。',
               '["角色","身体细节动作"]',
               '让读者从动作压力里自行读出怒意，情绪更克制也更紧',
               '["已有明确冲突压力","角色有克制动机"]',
               '["动作没有叙事压力时会显得空"]',
               '["补一句他很生气","把动作写成无意义肢体描写"]',
               NULL,
               '{"contributing_factors":[{"summary":"动作替代心理描写","observation_ids":["obs-rain-doorway-action-carrier"]}]}',
               0.96, 'confirmed', 'active', NULL,
               '动作应短，情绪词应少。',
               '2026-07-09T00:00:00Z'),
              ('specimen-direct-anger', 'node-rain-doorway-s4', 101, 'run-stage1-rain-doorway',
               'direct_emotion',
               '直接陈述角色愤怒，让情绪信息马上被读者看见',
               '需要迅速交代角色情绪，且不追求压抑留白',
               '[角色] 直接说明 [情绪]。',
               '["角色","情绪"]',
               '降低理解成本，但会削弱留白和张力',
               '["节奏需要快速交代"]',
               '["情绪显得浅白"]',
               '["在需要克制张力时直接说出生气"]',
               NULL,
               '{"contributing_factors":[{"summary":"直接情绪陈述","observation_ids":["obs-rain-doorway-direct-anger"]}]}',
               0.91, 'confirmed', 'active', NULL,
               '只适合快速交代，不适合作为压抑怒意的核心技法。',
               '2026-07-09T00:00:00Z'),
              ('specimen-rejected-high-match', 'node-rain-doorway-s1', 101, 'run-stage1-rain-doorway',
               'emotion_carrier',
               '用细节动作承载压抑愤怒，以沉默留白替代直接心理描写',
               '这个标本被人工拒绝，不能影响章节使用路径',
               '[角色] [身体细节动作]，不直接说明情绪。',
               '["角色","身体细节动作"]',
               '不应参与检索排序',
               '[]',
               '[]',
               '[]',
               NULL,
               '{"contributing_factors":[{"summary":"rejected","observation_ids":["obs-rain-doorway-sensory"]}]}',
               0.99, 'rejected', 'active', NULL,
               'rejected specimen must not be embedded.',
               '2026-07-09T00:00:00Z'),
              ('specimen-superseded-high-match', 'node-rain-doorway-s2', 101, 'run-stage1-rain-doorway',
               'emotion_carrier',
               '用细节动作承载压抑愤怒，以沉默留白替代直接心理描写',
               '这个标本已失效，不能影响章节使用路径',
               '[角色] [身体细节动作]，不直接说明情绪。',
               '["角色","身体细节动作"]',
               '不应参与检索排序',
               '[]',
               '[]',
               '[]',
               NULL,
               '{"contributing_factors":[{"summary":"superseded","observation_ids":["obs-rain-doorway-emotion"]}]}',
               0.99, 'confirmed', 'superseded', 'run-stage1-rain-doorway-next',
               'superseded specimen must not be embedded.',
               '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedForbiddenTechniqueSpecimenAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ('run-stage1-hidden-reveal', 102, 'fake-stage1-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 8, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 0);

            INSERT OR IGNORE INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ('specimen-forbidden-high-match', 'node-hidden-reveal', 102, 'run-stage1-hidden-reveal',
               'emotion_carrier',
               '用细节动作承载压抑愤怒，以沉默留白替代直接心理描写',
               '这个来源未获授权，不能进入章节使用路径',
               '[角色] [身体细节动作]，[场面留白]。',
               '["角色","身体细节动作","场面留白"]',
               '不应参与检索排序',
               '[]',
               '[]',
               '[]',
               NULL,
               '{"contributing_factors":[]}',
               0.99, 'confirmed', 'active', NULL,
               'forbidden source must not be embedded.',
               '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedFarTechniqueSpecimenFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        var fillerNodes = string.Join(
            "," + Environment.NewLine,
            Enumerable.Range(5, 60).Select(index =>
                $"('node-rain-doorway-long-{index}', 101, NULL, 'sentence', {index}, 1, 1, {index * 20}, {index * 20 + 12}, 12, 'sha256-long-{index}', '门廊里的第{index}个低压句。', '2026-07-09T00:00:00Z')"));
        command.CommandText = $$"""
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              {{fillerNodes}},
              ('node-rain-doorway-far-technique', 101, NULL, 'sentence', 80, 1,
               1, 1600, 1623, 23, 'sha256-far-technique', '她把杯沿转了半圈，屋里谁都没有作声。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ('specimen-far-action-over-psychology', 'node-rain-doorway-far-technique', 101, 'run-stage1-rain-doorway',
               'emotion_carrier',
               '用细节动作承载压抑愤怒，以沉默留白替代直接心理描写',
               '角色不能直接爆发，但场面需要让读者感到怒意已经压到临界点',
               '[角色] [身体细节动作]，[场面留白]。',
               '["角色","身体细节动作","场面留白"]',
               '把情绪判断交给读者完成，减少直白解释，增强压抑张力',
               '["已有冲突压力","动作能承载角色欲望或克制"]',
               '["动作与冲突无关时会显得空泛"]',
               '["直接补一句他很愤怒"]',
               NULL,
               '{"contributing_factors":[{"summary":"远位置技法标本","observation_ids":["obs-rain-doorway-action-carrier"]}]}',
               0.97, 'confirmed', 'active', NULL,
               '远位置技法标本必须能通过技法语义补进候选池。',
               '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedUnrankedFarTechniqueSpecimenFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-rain-doorway-far-technique-unranked', 101, NULL, 'sentence', 81, 1,
               1, 1624, 1646, 22, 'sha256-far-technique-unranked', '她直接说自己很生气，声音压得很低。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ('specimen-far-direct-anger-unranked', 'node-rain-doorway-far-technique-unranked', 101, 'run-stage1-rain-doorway',
               'direct_emotion',
               '直接陈述角色愤怒，让读者立即知道情绪结论',
               '需要快速交代情绪，不追求动作留白',
               '[角色] 直接说明 [情绪]。',
               '["角色","情绪"]',
               '理解成本低，但会削弱压抑张力',
               '["需要快速说明"]',
               '["张力变直白"]',
               '["在克制场景里直接解释愤怒"]',
               NULL,
               '{"contributing_factors":[]}',
               0.90, 'confirmed', 'active', NULL,
               '该标本用于证明 native topK 不应退化为全量 active specimen 补池。',
               '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedCrossLibrarySessionFixtureAsync(AppInitializationOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReferenceDatabasePath(options))!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        var rainNodes = string.Join(
            "," + Environment.NewLine,
            Enumerable.Range(1, 12).Select(index =>
                $"('node-rain-doorway-filler-{index}', 101, NULL, 'sentence', {index}, 1, 1, {index * 10}, {index * 10 + 8}, 8, 'sha256-rain-filler-{index}', '雨声沿着门槛拖慢第{index}步。', '2026-07-09T00:00:00Z')"));
        command.CommandText = $$"""
            INSERT OR IGNORE INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
            VALUES
              (101, NULL, '雨门低相关样本', '', 'rain-doorway.md', 'markdown', 'user_provided',
               'source-hash-101', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace'),
              (103, NULL, '火市高相关样本', '', 'fire-market.md', 'markdown', 'user_provided',
               'source-hash-103', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace');

            INSERT OR IGNORE INTO reference_corpus_libraries
              (library_id, scope, novel_id, name, created_at)
            VALUES
              ('library-rain-doorway', 'project', 3001, '雨门语料库', '2026-07-09T00:00:00Z'),
              ('library-fire-market', 'project', 3001, '火市语料库', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_session_library_binding
              (session_id, library_id)
            VALUES
              ('session-cross-corpus', 'library-rain-doorway'),
              ('session-cross-corpus', 'library-fire-market');

            INSERT OR IGNORE INTO reference_library_members
              (library_id, anchor_id, enabled, source_quality, dedup_group_id)
            VALUES
              ('library-rain-doorway', 101, 1, 'trusted', 'rain-doorway'),
              ('library-fire-market', 103, 1, 'trusted', 'fire-market');

            INSERT OR IGNORE INTO reference_source_license
              (anchor_id, license_state, authorization_evidence, reuse_policy,
               max_verbatim_ratio, cleared_for_insertion, reviewed_at)
            VALUES
              (101, 'authorized', 'fixture', 'adapted_only', 1.00, 1, '2026-07-09T00:00:00Z'),
              (103, 'authorized', 'fixture', 'adapted_only', 1.00, 1, '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              {{rainNodes}},
              ('node-fire-market-s1', 103, NULL, 'sentence', 1, 1,
               1, 0, 21, 21, 'sha256-fire-market-s1', '火光在旧市集尽头一压，他没有立刻回头。', '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedLocalContextFitFixtureAsync(AppInitializationOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReferenceDatabasePath(options))!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        var fillerNodes = string.Join(
            "," + Environment.NewLine,
            Enumerable.Range(1, 24).Select(index =>
                $"('node-fourway-filler-{index}', 202, NULL, 'sentence', {index}, 1, 1, {index * 12}, {index * 12 + 10}, 10, 'sha256-fourway-filler-{index}', '走廊里只剩第{index}盏灯。', '2026-07-09T00:00:00Z')"));
        command.CommandText = $$"""
            INSERT OR IGNORE INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
            VALUES
              (201, NULL, '局部上下文样本', '', 'local-context.md', 'markdown', 'user_provided',
               'source-hash-201', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace');

            INSERT OR IGNORE INTO reference_corpus_libraries
              (library_id, scope, novel_id, name, created_at)
            VALUES
              ('library-local-context', 'project', 3001, '局部上下文语料库', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_library_members
              (library_id, anchor_id, enabled, source_quality, dedup_group_id)
            VALUES
              ('library-local-context', 201, 1, 'trusted', 'local-context');

            INSERT OR IGNORE INTO reference_source_license
              (anchor_id, license_state, authorization_evidence, reuse_policy,
               max_verbatim_ratio, cleared_for_insertion, reviewed_at)
            VALUES
              (201, 'authorized', 'fixture', 'adapted_only', 1.00, 1, '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-local-context-generic', 201, NULL, 'sentence', 1, 1,
               1, 0, 19, 19, 'sha256-local-context-generic', '雨声贴着门缝往里挤，她没有立刻开口。', '2026-07-09T00:00:00Z'),
              ('node-local-context-forbidden', 201, NULL, 'sentence', 2, 1,
               1, 20, 32, 12, 'sha256-local-context-forbidden', '叛徒身份已经露出破绽。', '2026-07-09T00:00:00Z'),
              ('node-local-context-match', 201, NULL, 'sentence', 3, 1,
               1, 33, 58, 25, 'sha256-local-context-match', '队长在黑塔门前停住，秦砚把铜令收进袖口。', '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedFourWayRecallFixtureAsync(AppInitializationOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReferenceDatabasePath(options))!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        var fillerNodes = string.Join(
            "," + Environment.NewLine,
            Enumerable.Range(1, 24).Select(index =>
                $"('node-fourway-filler-{index}', 202, NULL, 'sentence', {index}, 1, 1, {index * 12}, {index * 12 + 10}, 10, 'sha256-fourway-filler-{index}', '走廊里只剩第{index}盏灯。', '2026-07-09T00:00:00Z')"));
        command.CommandText = $$"""
            INSERT OR IGNORE INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
            VALUES
              (202, NULL, '四路召回样本', '', 'fourway-recall.md', 'markdown', 'user_provided',
               'source-hash-202', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace');

            INSERT OR IGNORE INTO reference_corpus_libraries
              (library_id, scope, novel_id, name, created_at)
            VALUES
              ('library-fourway-recall', 'project', 3001, '四路召回语料库', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_library_members
              (library_id, anchor_id, enabled, source_quality, dedup_group_id)
            VALUES
              ('library-fourway-recall', 202, 1, 'trusted', 'fourway-recall');

            INSERT OR IGNORE INTO reference_source_license
              (anchor_id, license_state, authorization_evidence, reuse_policy,
               max_verbatim_ratio, cleared_for_insertion, reviewed_at)
            VALUES
              (202, 'authorized', 'fixture', 'adapted_only', 1.00, 1, '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              {{fillerNodes}},
              ('node-fourway-semantic', 202, NULL, 'sentence', 40, 1,
               1, 480, 495, 15, 'sha256-fourway-semantic', '语义锚点直指四路召回目标。', '2026-07-09T00:00:00Z'),
              ('node-fourway-technique', 202, NULL, 'sentence', 41, 1,
               1, 496, 515, 19, 'sha256-fourway-technique', '她把杯沿转了半圈，屋里谁都没作声。', '2026-07-09T00:00:00Z'),
              ('node-fourway-observation', 202, NULL, 'sentence', 42, 1,
               1, 516, 529, 13, 'sha256-fourway-observation', '他停在门边，没有回答。', '2026-07-09T00:00:00Z'),
              ('node-fourway-context', 202, NULL, 'sentence', 43, 1,
               1, 530, 555, 25, 'sha256-fourway-context', '队长在黑塔门前停住，秦砚把铜令收进袖口。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ('run-fourway-recall', 202, 'fake-stage1-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 12, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 1);

            INSERT OR IGNORE INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ('obs-fourway-observation-route', 'node-fourway-observation', 'sentence',
               'run-fourway-recall', 202, 'narrative_function', 'function',
               'enum', 'observation_route_marker', NULL, NULL, '{"function":"observation_route_marker"}', NULL, 0.99,
               0, 12, '用于证明结构化 observation 可作为独立召回路。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ('specimen-fourway-technique', 'node-fourway-technique', 202, 'run-fourway-recall',
               'emotion_carrier',
               '四路技法锚点：用细节动作承载压抑愤怒，以沉默留白替代直接心理描写',
               '角色不能直接爆发，但场面需要让读者感到怒意已经压到临界点',
               '[角色] [身体细节动作]，[场面留白]。',
               '["角色","身体细节动作","场面留白"]',
               '把情绪判断交给读者完成，减少直白解释，增强压抑张力',
               '["已有冲突压力","动作能承载角色欲望或克制"]',
               '["动作与冲突无关时会显得空泛"]',
               '["直接补一句他很愤怒"]',
               NULL,
               '{"contributing_factors":[{"summary":"四路技法标本","observation_ids":["obs-fourway-observation-route"]}]}',
               0.97, 'confirmed', 'active', NULL,
               '技法召回必须能独立进入候选页。',
               '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedFourWayUnsafeRouteFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
            VALUES
              (203, NULL, '被排除四路样本', '', 'fourway-excluded.md', 'markdown', 'user_provided',
               'source-hash-203', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace'),
              (204, NULL, '未授权四路样本', '', 'fourway-forbidden.md', 'markdown', 'user_provided',
               'source-hash-204', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace'),
              (205, NULL, '去重失败者四路样本', '', 'fourway-dedup-loser.md', 'markdown', 'user_provided',
               'source-hash-205', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace');

            INSERT OR IGNORE INTO reference_library_members
              (library_id, anchor_id, enabled, source_quality, dedup_group_id)
            VALUES
              ('library-fourway-recall', 203, 1, 'trusted', 'fourway-excluded'),
              ('library-fourway-recall', 204, 1, 'trusted', 'fourway-forbidden'),
              ('library-fourway-recall', 205, 1, 'low', 'fourway-recall');

            INSERT OR IGNORE INTO reference_source_license
              (anchor_id, license_state, authorization_evidence, reuse_policy,
               max_verbatim_ratio, cleared_for_insertion, reviewed_at)
            VALUES
              (203, 'authorized', 'fixture', 'adapted_only', 1.00, 1, '2026-07-09T00:00:00Z'),
              (204, 'forbidden', 'fixture', 'forbidden', 0.00, 0, '2026-07-09T00:00:00Z'),
              (205, 'authorized', 'fixture', 'adapted_only', 1.00, 1, '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-fourway-excluded-semantic', 203, NULL, 'sentence', 1, 1,
               1, 0, 15, 15, 'sha256-fourway-excluded-semantic', '语义锚点直指四路召回目标。', '2026-07-09T00:00:00Z'),
              ('node-fourway-forbidden-context', 204, NULL, 'sentence', 1, 1,
               1, 0, 25, 25, 'sha256-fourway-forbidden-context', '队长在黑塔门前停住，秦砚把铜令收进袖口。', '2026-07-09T00:00:00Z'),
              ('node-fourway-dedup-loser-context', 205, NULL, 'sentence', 1, 1,
               1, 0, 25, 25, 'sha256-fourway-dedup-loser-context', '队长在黑塔门前停住，秦砚把铜令收进袖口。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ('run-fourway-excluded', 203, 'fake-stage1-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 2, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 1),
              ('run-fourway-forbidden', 204, 'fake-stage1-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 2, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 1);

            INSERT OR IGNORE INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ('obs-fourway-excluded-route', 'node-fourway-excluded-semantic', 'sentence',
               'run-fourway-excluded', 203, 'narrative_function', 'function',
               'enum', 'observation_route_marker', NULL, NULL, '{"function":"observation_route_marker"}', NULL, 0.99,
               0, 12, '被排除 anchor 不能通过 route union 返回。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-fourway-forbidden-route', 'node-fourway-forbidden-context', 'sentence',
               'run-fourway-forbidden', 204, 'narrative_function', 'function',
               'enum', 'observation_route_marker', NULL, NULL, '{"function":"observation_route_marker"}', NULL, 0.99,
               0, 12, '未授权来源不能通过 route union 返回。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedAdditionalChapterContextRouteNodeAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-fourway-context-secondary', 202, NULL, 'sentence', 44, 1,
               1, 556, 572, 16, 'sha256-fourway-context-secondary',
               '秦砚把铜令收进袖口。', '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedChapterContextFilterLimitFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        var distractorNodes = string.Join(
            "," + Environment.NewLine,
            Enumerable.Range(0, 20).Select(index =>
            {
                var sequence = 44 + index;
                return $"('node-fourway-context-filter-distractor-{index}', 202, NULL, 'sentence', {sequence}, 1, 1, {600 + index * 20}, {618 + index * 20}, 18, 'sha256-fourway-context-filter-distractor-{index}', '队长在黑塔门前停住，秦砚把铜令收进袖口。', '2026-07-09T00:00:00Z')";
            }));
        command.CommandText = $$"""
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              {{distractorNodes}},
              ('node-fourway-context-filter-target', 202, NULL, 'sentence', 90, 1,
               1, 1200, 1220, 20, 'sha256-fourway-context-filter-target',
               '队长在黑塔门前停住，秦砚把铜令收进袖口。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ('obs-fourway-context-filter-target', 'node-fourway-context-filter-target', 'sentence',
               'run-fourway-recall', 202, 'rhythm', 'length_band',
               'enum', 'context_filter_match', NULL, NULL, '{"length_band":"context_filter_match"}', NULL, 0.98,
               0, 20, '用于证明章节上下文 route 在 route limit 前先应用结构化过滤。', 'confirmed', 'active', NULL,
               '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SetLibraryEnabledAsync(
        AppInitializationOptions options,
        string libraryId,
        bool enabled)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_library_members
            SET enabled = $enabled,
                disabled_reason = CASE WHEN $enabled = 0 THEN 'test disabled' ELSE NULL END
            WHERE library_id = $library_id;
            """;
        command.Parameters.AddWithValue("$library_id", libraryId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        Assert.True(await command.ExecuteNonQueryAsync() > 0);
    }

    private static async ValueTask SetSharedDedupGroupAsync(
        AppInitializationOptions options,
        string dedupGroupId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_library_members
            SET dedup_group_id = $dedup_group_id
            WHERE library_id IN ('library-rain-doorway', 'library-fire-market');
            """;
        command.Parameters.AddWithValue("$dedup_group_id", dedupGroupId);
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask AddRhythmLengthObservationAsync(
        AppInitializationOptions options,
        string nodeId,
        string observationId,
        int charCount)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ($observation_id, $node_id, 'sentence',
               'run-stage1-rain-doorway', 101, 'rhythm', 'length_band',
               'number', 'medium', $char_count, NULL, $value_json, NULL, 0.95,
               0, $char_count, 'Medium-length rhythm used for indexed feature filter tests.',
               'confirmed', 'active', NULL, '2026-07-09T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$observation_id", observationId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$char_count", charCount);
        command.Parameters.AddWithValue("$value_json", $$"""{"feature_key":"length_band","label":"medium","char_count":{{charCount}},"cadence":"flowing"}""");
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<int> ReadNodeEmbeddingCountAsync(AppInitializationOptions options)
    {
        return await ReadCountAsync(options, "reference_text_node_embeddings");
    }

    private static async ValueTask<int> ReadCurrentChapterEmbeddingCacheCountAsync(AppInitializationOptions options)
    {
        return await ReadCountAsync(options, "reference_current_chapter_embedding_cache");
    }

    private static async ValueTask<int> ReadTechniqueVectorCountAsync(AppInitializationOptions options)
    {
        return await ReadCountAsync(options, "reference_technique_vectors");
    }

    private static async ValueTask<int> ReadTechniqueVectorRowCountAsync(AppInitializationOptions options)
    {
        return await ReadCountAsync(options, "reference_technique_vector_rows");
    }

    private static async ValueTask<int> ReadTechniqueVectorIndexStateCountAsync(AppInitializationOptions options)
    {
        return await ReadCountAsync(options, "reference_technique_vector_index_state");
    }

    private static async ValueTask SetTechniqueSpecimenReviewStateAsync(
        AppInitializationOptions options,
        string specimenId,
        string reviewState)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_technique_specimens
            SET review_state = $review_state
            WHERE specimen_id = $specimen_id;
            """;
        command.Parameters.AddWithValue("$review_state", reviewState);
        command.Parameters.AddWithValue("$specimen_id", specimenId);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SetTechniqueSpecimenSupersededRunIdAsync(
        AppInitializationOptions options,
        string specimenId,
        string supersededByRunId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_technique_specimens
            SET superseded_by_run_id = $superseded_by_run_id
            WHERE specimen_id = $specimen_id;
            """;
        command.Parameters.AddWithValue("$superseded_by_run_id", supersededByRunId);
        command.Parameters.AddWithValue("$specimen_id", specimenId);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask CorruptNativeTechniqueVectorRowHashAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE reference_technique_vector_rows SET technique_hash = 'stale-technique-hash';";
        Assert.True(await command.ExecuteNonQueryAsync() > 0);
    }

    private static async ValueTask CorruptNativeTechniqueVectorRowSourceNodeAsync(
        AppInitializationOptions options,
        string sourceNodeId,
        long sourceAnchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_technique_vector_rows
            SET source_node_id = $source_node_id,
                source_anchor_id = $source_anchor_id;
            """;
        command.Parameters.AddWithValue("$source_node_id", sourceNodeId);
        command.Parameters.AddWithValue("$source_anchor_id", sourceAnchorId);
        Assert.True(await command.ExecuteNonQueryAsync() > 0);
    }

    private static void AssertMatchesM3RetrievalGolden(
        string fixtureId,
        PageResultPayload<ReferenceCorpusCandidatePayload> result,
        int nodeEmbeddingCount,
        int currentChapterEmbeddingCacheCount,
        int techniqueVectorCount)
    {
        using var document = LoadCorpusDrivenWritingFixture("m3-retrieval-golden.json");
        var fixture = document.RootElement
            .GetProperty("fixtures")
            .EnumerateArray()
            .Single(item => item.GetProperty("fixture_id").GetString() == fixtureId);
        var expected = JsonNode.Parse(fixture.GetProperty("expected_retrieval").GetRawText());
        Assert.NotNull(expected);
        var expectedExcludedNodeIds = fixture
            .GetProperty("expected_retrieval")
            .GetProperty("excluded_node_ids")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
        foreach (var excludedNodeId in expectedExcludedNodeIds)
        {
            Assert.DoesNotContain(result.Items, item => item.NodeId == excludedNodeId);
        }

        var actual = NormalizeRetrievalForGolden(
            result,
            expectedExcludedNodeIds,
            nodeEmbeddingCount,
            currentChapterEmbeddingCacheCount,
            techniqueVectorCount);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            "M3 corpus-driven-writing retrieval golden mismatch." +
            Environment.NewLine +
            actual.ToJsonString(GoldenJsonOptions));
    }

    private static JsonObject NormalizeRetrievalForGolden(
        PageResultPayload<ReferenceCorpusCandidatePayload> result,
        IReadOnlyList<string> excludedNodeIds,
        int nodeEmbeddingCount,
        int currentChapterEmbeddingCacheCount,
        int techniqueVectorCount)
    {
        return new JsonObject
        {
            ["page"] = new JsonObject
            {
                ["total"] = result.Total,
                ["size"] = result.Size,
                ["has_more"] = result.HasMore,
                ["total_estimate"] = result.TotalEstimate
            },
            ["ranked_node_ids"] = new JsonArray(result.Items.Select(item => JsonValue.Create(item.NodeId)).ToArray<JsonNode?>()),
            ["candidates"] = new JsonArray(result.Items.Select(NormalizeCandidateForGolden).ToArray<JsonNode?>()),
            ["excluded_node_ids"] = new JsonArray(excludedNodeIds.Select(item => JsonValue.Create(item)).ToArray<JsonNode?>()),
            ["cache_expectations"] = new JsonObject
            {
                ["node_embedding_count"] = nodeEmbeddingCount,
                ["current_chapter_embedding_cache_count"] = currentChapterEmbeddingCacheCount,
                ["technique_vector_count"] = techniqueVectorCount
            }
        };
    }

    private static JsonObject NormalizeCandidateForGolden(ReferenceCorpusCandidatePayload item, int index)
    {
        var scoreComponentKeys = item.ScoreComponents
            .Keys
            .Order(StringComparer.Ordinal)
            .Select(key => JsonValue.Create(key))
            .ToArray<JsonNode?>();
        var routeComponents = new JsonObject();
        foreach (var routeComponent in item.ScoreComponents
            .Where(pair => pair.Key.StartsWith("recall_", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            routeComponents[routeComponent.Key] = routeComponent.Value;
        }

        return new JsonObject
        {
            ["rank"] = index + 1,
            ["candidate_id"] = item.CandidateId,
            ["node_id"] = item.NodeId,
            ["anchor_id"] = item.AnchorId,
            ["library_id"] = item.LibraryId,
            ["node_type"] = item.NodeType,
            ["text_hash"] = item.TextHash,
            ["text_preview_hash"] = Sha256Hex(item.TextPreview),
            ["text_preview_char_len"] = item.TextPreview.Length,
            ["license_state"] = item.LicenseState,
            ["reuse_policy"] = item.ReusePolicy,
            ["fit_explanation"] = item.FitExplanation,
            ["score_component_keys"] = new JsonArray(scoreComponentKeys),
            ["route_components"] = routeComponents,
            ["evidence"] = new JsonArray(item.Evidence.Select(NormalizeEvidenceForGolden).ToArray<JsonNode?>())
        };
    }

    private static JsonObject NormalizeEvidenceForGolden(ReferenceCorpusCandidateEvidencePayload item)
    {
        return new JsonObject
        {
            ["observation_id"] = item.ObservationId,
            ["feature_family"] = item.FeatureFamily,
            ["feature_key"] = item.FeatureKey,
            ["confidence"] = Math.Round(item.Confidence, 6)
        };
    }

    private static JsonDocument LoadCorpusDrivenWritingFixture(string fileName)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            fileName);
        return JsonDocument.Parse(File.ReadAllText(fixturePath));
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder("sha256:", 7 + bytes.Length * 2);
        foreach (var valueByte in bytes)
        {
            builder.Append(valueByte.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static async ValueTask<int> ReadCountAsync(AppInitializationOptions options, string tableName)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + tableName + ";";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static string ReferenceDatabasePath(AppInitializationOptions options)
    {
        return Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
    }

    private static async ValueTask<SqliteConnection> OpenReferenceConnectionAsync(AppInitializationOptions options)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();
        return connection;
    }

    private sealed class StaticEmbeddingConfigurationService : IEmbeddingConfigurationService
    {
        private readonly EmbeddingRequestOptions _options;

        public StaticEmbeddingConfigurationService(EmbeddingRequestOptions options)
        {
            _options = options;
        }

        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<EmbeddingRequestOptions?>(_options);
        }
    }

    private sealed class FakeSqliteVecTechniqueProvider : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
    {
        private readonly Func<SqliteVecVectorRecord, bool> _match;
        private readonly List<SqliteVecVectorRecord> _vectors = [];

        public FakeSqliteVecTechniqueProvider(Func<SqliteVecVectorRecord, bool> match)
        {
            _match = match;
        }

        public List<SqliteVecProvisionRequest> ProvisionCalls { get; } = [];

        public List<SqliteVecSearchRequest> SearchCalls { get; } = [];

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProvisionCalls.Add(request);
            _vectors.Clear();
            _vectors.AddRange(request.Vectors);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
            string databasePath,
            SqliteVecSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCalls.Add(request);
            IReadOnlyList<SqliteVecSearchRecord> result = _vectors
                .Where(_match)
                .Select(vector => new SqliteVecSearchRecord(vector.RowId, Distance: 0.01))
                .Take(request.TopK)
                .ToArray();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingSqliteVecTechniqueProvider : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
    {
        public int ProvisionAttempts { get; private set; }

        public int SearchAttempts { get; private set; }

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProvisionAttempts++;
            throw new InvalidOperationException("native sqlite-vec unavailable in fixture");
        }

        public ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
            string databasePath,
            SqliteVecSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchAttempts++;
            throw new InvalidOperationException("native sqlite-vec query unavailable in fixture");
        }
    }

    private sealed class TopicEmbeddingClient : IEmbeddingClient
    {
        private readonly int _defaultDimensions;

        public TopicEmbeddingClient(int defaultDimensions)
        {
            _defaultDimensions = defaultDimensions;
        }

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dimensions = options.Dimensions ?? _defaultDimensions;
            var items = inputs
                .Select((input, index) => new EmbeddingItemResult(index, VectorFor(input, dimensions)))
                .ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                dimensions,
                items,
                new EmbeddingUsage(inputs.Count, inputs.Count)));
        }

        private static IReadOnlyList<float> VectorFor(string input, int dimensions)
        {
            var vector = new float[dimensions];
            if (dimensions == 0)
            {
                return vector;
            }

            var topic = input.Contains("火光", StringComparison.Ordinal) ||
                input.Contains("旧市集", StringComparison.Ordinal) ||
                input.Contains("fire_market", StringComparison.Ordinal)
                    ? 0
                    : Math.Min(1, dimensions - 1);
            vector[topic] = 1f;
            return vector;
        }
    }

    private sealed class ConstantEmbeddingClient : IEmbeddingClient
    {
        private readonly int _defaultDimensions;

        public ConstantEmbeddingClient(int defaultDimensions)
        {
            _defaultDimensions = defaultDimensions;
        }

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dimensions = options.Dimensions ?? _defaultDimensions;
            var vector = new float[dimensions];
            if (dimensions > 0)
            {
                vector[0] = 1f;
            }

            var items = inputs
                .Select((_, index) => new EmbeddingItemResult(index, vector))
                .ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                dimensions,
                items,
                new EmbeddingUsage(inputs.Count, inputs.Count)));
        }
    }

    private sealed class FourWayRecallEmbeddingClient : IEmbeddingClient
    {
        private readonly int _defaultDimensions;

        public FourWayRecallEmbeddingClient(int defaultDimensions)
        {
            _defaultDimensions = defaultDimensions;
        }

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dimensions = options.Dimensions ?? _defaultDimensions;
            var items = inputs
                .Select((input, index) => new EmbeddingItemResult(index, VectorFor(input, dimensions)))
                .ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                dimensions,
                items,
                new EmbeddingUsage(inputs.Count, inputs.Count)));
        }

        private static IReadOnlyList<float> VectorFor(string input, int dimensions)
        {
            var vector = new float[dimensions];
            if (dimensions <= 0)
            {
                return vector;
            }

            if (input.Contains("four_way_recall", StringComparison.Ordinal) ||
                input.Contains("四路召回", StringComparison.Ordinal))
            {
                vector[0] = 1.0f;
                vector[Math.Min(1, dimensions - 1)] = 0.80f;
            }
            else if (input.Contains("语义锚点", StringComparison.Ordinal))
            {
                vector[0] = 1.0f;
            }
            else if (input.Contains("四路技法锚点", StringComparison.Ordinal))
            {
                vector[Math.Min(1, dimensions - 1)] = 1.0f;
            }
            else if (input.Contains("黑塔门前", StringComparison.Ordinal) ||
                input.Contains("队长", StringComparison.Ordinal) ||
                input.Contains("铜令", StringComparison.Ordinal))
            {
                vector[Math.Min(2, dimensions - 1)] = 1.0f;
            }
            else
            {
                vector[Math.Min(3, dimensions - 1)] = 1.0f;
            }

            return vector;
        }
    }

    private sealed class TechniqueIntentEmbeddingClient : IEmbeddingClient
    {
        private readonly object _gate = new();
        private readonly int _defaultDimensions;
        private int _callCount;

        public TechniqueIntentEmbeddingClient(int defaultDimensions)
        {
            _defaultDimensions = defaultDimensions;
        }

        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _callCount;
                }
            }
        }

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dimensions = options.Dimensions ?? _defaultDimensions;
            lock (_gate)
            {
                _callCount++;
            }

            var items = inputs
                .Select((input, index) => new EmbeddingItemResult(index, VectorFor(input, dimensions)))
                .ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                dimensions,
                items,
                new EmbeddingUsage(inputs.Count, inputs.Count)));
        }

        private static IReadOnlyList<float> VectorFor(string input, int dimensions)
        {
            var vector = new float[dimensions];
            if (dimensions <= 0)
            {
                return vector;
            }

            var topic = 1;
            if (input.Contains("动作替代心理", StringComparison.Ordinal) ||
                input.Contains("细节动作承载压抑愤怒", StringComparison.Ordinal) ||
                input.Contains("身体细节动作", StringComparison.Ordinal) ||
                input.Contains("不直接说明情绪", StringComparison.Ordinal))
            {
                topic = 0;
            }
            else if (input.Contains("直接陈述", StringComparison.Ordinal) ||
                input.Contains("心里很生气", StringComparison.Ordinal) ||
                input.Contains("直接说明生气", StringComparison.Ordinal))
            {
                topic = Math.Min(2, dimensions - 1);
            }

            vector[Math.Min(topic, dimensions - 1)] = 1f;
            return vector;
        }
    }
}
