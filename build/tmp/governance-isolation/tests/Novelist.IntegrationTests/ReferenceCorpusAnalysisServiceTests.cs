using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusAnalysisServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartFeatureAnalysisRunsDefaultSentenceFamiliesAndPersistsStatus()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        var analyzer = new EmptyObservationFeatureFamilyAnalyzer(tokensPerCall: 2);
        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            new FixedAppSettingsService("fake/fake-model", "medium"),
            analyzer);

        var started = await service.StartFeatureAnalysisAsync(
            new StartReferenceCorpusFeatureAnalysisPayload(
                NovelId: 42,
                AnchorId: 101,
                Scope: ReferenceCorpusNodeTypes.Sentence,
                TokenBudget: null,
                Resume: false,
                RunId: "analysis-service-run-1"),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, started.Status);
        Assert.Equal("analysis-service-run-1", started.RunId);
        Assert.Equal(42, started.NovelId);
        Assert.Equal(101, started.AnchorId);
        Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies, started.Families);
        Assert.Equal(10, started.ProcessedWorkItems);
        Assert.Equal(20, started.TokensSpent);
        Assert.Equal("fake", started.ModelProvider);
        Assert.Equal("fake-model", started.ModelId);
        Assert.NotNull(started.CompletedAt);
        Assert.Equal(10, analyzer.Calls.Count);
        Assert.All(analyzer.Calls, call => Assert.Equal(ReferenceCorpusNodeTypes.Sentence, call.NodeType));

        var status = await service.GetFeatureAnalysisRunAsync(
            new GetReferenceCorpusFeatureAnalysisRunPayload(
                NovelId: 42,
                RunId: "analysis-service-run-1"),
            CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, status.Status);
        Assert.Equal(20, status.TokensSpent);
        Assert.Equal(0, status.ObservationCount);
        Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies, status.Families);
    }

    [Fact]
    public async Task StartFeatureAnalysisMarksRunFailedWhenAnalyzerThrows()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            new FixedAppSettingsService("fake/fake-model", "medium"),
            new ThrowingFeatureFamilyAnalyzer());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.StartFeatureAnalysisAsync(
                new StartReferenceCorpusFeatureAnalysisPayload(
                    NovelId: 42,
                    AnchorId: 101,
                    Scope: ReferenceCorpusNodeTypes.Sentence,
                    TokenBudget: null,
                    Resume: false,
                    RunId: "analysis-service-failed-run-1"),
                CancellationToken.None));

        var status = await service.GetFeatureAnalysisRunAsync(
            new GetReferenceCorpusFeatureAnalysisRunPayload(
                NovelId: 42,
                RunId: "analysis-service-failed-run-1"),
            CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Failed, status.Status);
        Assert.NotNull(status.CompletedAt);
        Assert.Contains(status.Diagnostics, item => string.Equals(item, "analysis_failed:InvalidOperationException", StringComparison.Ordinal));
        var diagnosticsJson = string.Join('\n', status.Diagnostics);
        Assert.DoesNotContain("node_text", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_text", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model_output_json", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("D:\\private", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartTechniqueSpecimenAnalysisPersistsSpecimensAndSafeStatus()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        var analyzer = new RecordingTechniqueSpecimenAnalyzer(tokensPerCall: 19);
        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            new FixedAppSettingsService("fake/fake-model", "medium"),
            new EmptyObservationFeatureFamilyAnalyzer(tokensPerCall: 1),
            techniqueSpecimenAnalyzer: analyzer);

        var started = await service.StartTechniqueSpecimenAnalysisAsync(
            new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
                NovelId: 42,
                AnchorId: 101,
                SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
                MinObservationConfidence: 0.70,
                RunId: "technique-service-run-1"),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, started.Status);
        Assert.Equal("technique-service-run-1", started.RunId);
        Assert.Equal(42, started.NovelId);
        Assert.Equal(101, started.AnchorId);
        Assert.Equal("technique_specimen", started.Scope);
        Assert.Equal(19, started.TokensSpent);
        Assert.Equal(1, started.SpecimenCount);
        Assert.Equal(1, started.ProcessedNodes);
        Assert.Equal("fake", started.ModelProvider);
        Assert.Equal("fake-model", started.ModelId);
        Assert.Equal(ReferenceCorpusTechniqueSpecimenSchemaVersions.V1, started.SchemaVersion);
        Assert.NotNull(started.CompletedAt);
        Assert.Single(analyzer.Calls);
        Assert.Equal("node-b", analyzer.Calls[0].NodeId);
        Assert.Equal(3, analyzer.Calls[0].Observations.Count);
        AssertRunDiagnosticsDoNotLeakSensitiveFields(started.Diagnostics);

        var status = await service.GetTechniqueSpecimenAnalysisRunAsync(
            new GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
                NovelId: 42,
                RunId: "technique-service-run-1"),
            CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, status.Status);
        Assert.Equal(19, status.TokensSpent);
        Assert.Equal(1, status.SpecimenCount);
        Assert.Equal(0, status.ProcessedNodes);
        AssertRunDiagnosticsDoNotLeakSensitiveFields(status.Diagnostics);
    }

    [Fact]
    public async Task StartTechniqueSpecimenAnalysisBudgetExhaustsAndResumesFromPersistedCursor()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        await SeedAdditionalTechniqueNodeAsync(options);
        var analyzer = new RecordingTechniqueSpecimenAnalyzer(tokensPerCall: 19);
        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            new FixedAppSettingsService("fake/fake-model", "medium"),
            new EmptyObservationFeatureFamilyAnalyzer(tokensPerCall: 1),
            techniqueSpecimenAnalyzer: analyzer);

        var first = await service.StartTechniqueSpecimenAnalysisAsync(
            new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
                NovelId: 42,
                AnchorId: 101,
                SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
                MinObservationConfidence: 0.70,
                TokenBudget: 19,
                Resume: false,
                RunId: "technique-service-budget-run-1"),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, first.Status);
        Assert.Equal(19, first.TokenBudget);
        Assert.Equal(19, first.TokensSpent);
        Assert.Equal("node-b", first.ResumeCursor);
        Assert.Equal(1, first.SpecimenCount);
        Assert.Equal(1, first.ProcessedNodes);
        Assert.Null(first.CompletedAt);
        Assert.Single(analyzer.Calls);
        Assert.Equal("node-b", analyzer.Calls[0].NodeId);
        Assert.Equal(1, await ReadTechniqueSpecimenCountAsync(options, "technique-service-budget-run-1"));
        Assert.Equal(2, await ReadTechniqueSpecimenEvidenceCountAsync(options, "technique-service-budget-run-1"));

        var nodeBSpecimenId = await ReadTechniqueSpecimenIdByNodeAsync(options, "technique-service-budget-run-1", "node-b");
        await UpdateTechniqueSpecimenReviewStateAsync(options, nodeBSpecimenId, "confirmed");

        var second = await service.StartTechniqueSpecimenAnalysisAsync(
            new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
                NovelId: 42,
                AnchorId: 101,
                SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
                MinObservationConfidence: 0.70,
                TokenBudget: 38,
                Resume: true,
                RunId: "technique-service-budget-run-1"),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, second.Status);
        Assert.Equal(38, second.TokenBudget);
        Assert.Equal(38, second.TokensSpent);
        Assert.Equal("node-c", second.ResumeCursor);
        Assert.Equal(2, second.SpecimenCount);
        Assert.Equal(1, second.ProcessedNodes);
        Assert.NotNull(second.CompletedAt);
        Assert.Equal(["node-b", "node-c"], analyzer.Calls.Select(call => call.NodeId).ToArray());
        Assert.Equal(2, await ReadTechniqueSpecimenCountAsync(options, "technique-service-budget-run-1"));
        Assert.Equal(4, await ReadTechniqueSpecimenEvidenceCountAsync(options, "technique-service-budget-run-1"));
        Assert.Equal("confirmed", await ReadTechniqueSpecimenReviewStateAsync(options, nodeBSpecimenId));

        var status = await service.GetTechniqueSpecimenAnalysisRunAsync(
            new GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
                NovelId: 42,
                RunId: "technique-service-budget-run-1"),
            CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, status.Status);
        Assert.Equal(38, status.TokenBudget);
        Assert.Equal(38, status.TokensSpent);
        Assert.Equal("node-c", status.ResumeCursor);
        Assert.Equal(2, status.SpecimenCount);
        Assert.Equal(0, status.ProcessedNodes);
    }

    [Fact]
    public async Task StartTechniqueSpecimenAnalysisInvalidResumeDoesNotOverwriteCompletedRun()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        var analyzer = new RecordingTechniqueSpecimenAnalyzer(tokensPerCall: 19);
        var service = new SqliteReferenceCorpusAnalysisService(
        options,
        new FixedAppSettingsService("fake/fake-model", "medium"),
        new EmptyObservationFeatureFamilyAnalyzer(tokensPerCall: 1),
        techniqueSpecimenAnalyzer: analyzer);

        var completed = await service.StartTechniqueSpecimenAnalysisAsync(
        new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
        NovelId: 42,
        AnchorId: 101,
        SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
        MinObservationConfidence: 0.70,
        RunId: "technique-service-completed-run-1"),
        CancellationToken.None);

 await Assert.ThrowsAsync<ReferenceCorpusTechniqueSpecimenRunner.ReferenceCorpusAnalysisRunPreconditionException>(async () =>
await service.StartTechniqueSpecimenAnalysisAsync(
        new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
        NovelId: 42,
        AnchorId: 101,
        SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
        MinObservationConfidence: 0.70,
        RunId: completed.RunId,
        TokenBudget: 64,
        Resume: true),
        CancellationToken.None));

        var status = await service.GetTechniqueSpecimenAnalysisRunAsync(
        new GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload(42, completed.RunId),
        CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, status.Status);
        Assert.Equal(completed.TokensSpent, status.TokensSpent);
        Assert.Equal(completed.ResumeCursor, status.ResumeCursor);
        Assert.Single(analyzer.Calls);
    }

    [Fact]
    public async Task StartTechniqueSpecimenAnalysisMarksRunFailedWhenAnalyzerThrows()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            new FixedAppSettingsService("fake/fake-model", "medium"),
            new EmptyObservationFeatureFamilyAnalyzer(tokensPerCall: 1),
            techniqueSpecimenAnalyzer: new ThrowingTechniqueSpecimenAnalyzer());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.StartTechniqueSpecimenAnalysisAsync(
                new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
                    NovelId: 42,
                    AnchorId: 101,
                    SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
                    MinObservationConfidence: 0.70,
                    RunId: "technique-service-failed-run-1"),
                CancellationToken.None));

        var status = await service.GetTechniqueSpecimenAnalysisRunAsync(
            new GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
                NovelId: 42,
                RunId: "technique-service-failed-run-1"),
            CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Failed, status.Status);
        Assert.NotNull(status.CompletedAt);
        Assert.Equal(0, status.SpecimenCount);
        Assert.Contains(status.Diagnostics, item => string.Equals(item, "analysis_failed:InvalidOperationException", StringComparison.Ordinal));
        AssertRunDiagnosticsDoNotLeakSensitiveFields(status.Diagnostics);
    }

    [Fact]
    public async Task ListFeatureObservationsReturnsSafePaginatedNodeAnalysis()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            new FixedAppSettingsService("fake/fake-model", "medium"),
            new EmptyObservationFeatureFamilyAnalyzer(tokensPerCall: 1));

        var firstPage = await service.ListFeatureObservationsAsync(
            new ListReferenceCorpusFeatureObservationsPayload(
                NovelId: 42,
                AnchorId: 101,
                NodeId: "node-b",
                PageRequest: new PageRequestPayload(
                    Cursor: null,
                    PageSize: 1,
                    SortBy: "created_at",
                    SortDir: "asc",
                    Filters: new Dictionary<string, string> { ["feature_family"] = ReferenceCorpusFeatureFamilies.Emotion })),
            CancellationToken.None);

        Assert.Single(firstPage.Items);
        Assert.Equal(2, firstPage.Total);
        Assert.True(firstPage.HasMore);
        Assert.NotNull(firstPage.NextCursor);
        var observation = firstPage.Items[0];
        Assert.Equal("node-b", observation.NodeId);
        Assert.Equal(ReferenceCorpusFeatureFamilies.Emotion, observation.FeatureFamily);
        Assert.Equal("emotion_state", observation.FeatureKey);
        Assert.Equal("active", observation.ValidityState);
        Assert.Equal("feature-run-technique-service", observation.RunId);
        Assert.Equal("restrained", observation.ValuePreview);
        Assert.Equal("hash-node-b", observation.TextHash);
        Assert.NotNull(observation.EvidencePreview);
        AssertFeatureObservationPayloadDoesNotLeakSourceFields(observation);

        var secondPage = await service.ListFeatureObservationsAsync(
            new ListReferenceCorpusFeatureObservationsPayload(
                NovelId: 42,
                AnchorId: 101,
                NodeId: "node-b",
                PageRequest: new PageRequestPayload(
                    Cursor: firstPage.NextCursor,
                    PageSize: 1,
                    SortBy: "created_at",
                    SortDir: "asc",
                    Filters: new Dictionary<string, string> { ["feature_family"] = ReferenceCorpusFeatureFamilies.Emotion })),
            CancellationToken.None);

        Assert.Single(secondPage.Items);
        Assert.False(secondPage.HasMore);
        Assert.Null(secondPage.NextCursor);
        Assert.NotEqual(firstPage.Items[0].ObservationId, secondPage.Items[0].ObservationId);
    }

    [Fact]
    public async Task StartFeatureAnalysisRoutesLowConfidenceObservationsToReviewList()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            new FixedAppSettingsService("fake/fake-model", "medium"),
            new ConfidenceSplitFeatureFamilyAnalyzer(tokensPerCall: 1));

        var started = await service.StartFeatureAnalysisAsync(
            new StartReferenceCorpusFeatureAnalysisPayload(
                NovelId: 42,
                AnchorId: 101,
                Scope: ReferenceCorpusNodeTypes.Sentence,
                TokenBudget: null,
                Resume: false,
                RunId: "analysis-service-confidence-routing-run-1"),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, started.Status);
        Assert.Equal(2, started.ObservationCount);

        var lowConfidencePage = await service.ListFeatureObservationsAsync(
            new ListReferenceCorpusFeatureObservationsPayload(
                NovelId: 42,
                AnchorId: 101,
                NodeId: null,
                PageRequest: new PageRequestPayload(
                    Cursor: null,
                    PageSize: 10,
                    SortBy: "created_at",
                    SortDir: "asc",
                    Filters: new Dictionary<string, string>
                    {
                        ["run_id"] = "analysis-service-confidence-routing-run-1",
                        ["feature_family"] = ReferenceCorpusFeatureFamilies.Syntax,
                        ["review_state"] = "low_confidence"
                    })),
            CancellationToken.None);

        var lowConfidence = Assert.Single(lowConfidencePage.Items);
        Assert.Equal("node-a", lowConfidence.NodeId);
        Assert.Equal("low_confidence", lowConfidence.ReviewState);
        Assert.True(lowConfidence.Confidence < 0.70);
        AssertFeatureObservationPayloadDoesNotLeakSourceFields(lowConfidence);

        var unverifiedPage = await service.ListFeatureObservationsAsync(
            new ListReferenceCorpusFeatureObservationsPayload(
                NovelId: 42,
                AnchorId: 101,
                NodeId: null,
                PageRequest: new PageRequestPayload(
                    Cursor: null,
                    PageSize: 10,
                    SortBy: "created_at",
                    SortDir: "asc",
                    Filters: new Dictionary<string, string>
                    {
                        ["run_id"] = "analysis-service-confidence-routing-run-1",
                        ["feature_family"] = ReferenceCorpusFeatureFamilies.Syntax,
                        ["review_state"] = "unverified"
                    })),
            CancellationToken.None);

        var unverified = Assert.Single(unverifiedPage.Items);
        Assert.Equal("node-b", unverified.NodeId);
        Assert.Equal("unverified", unverified.ReviewState);
        Assert.True(unverified.Confidence >= 0.70);
        AssertFeatureObservationPayloadDoesNotLeakSourceFields(unverified);
    }

    [Fact]
    public async Task ListTechniqueSpecimensReturnsSafeEvidenceTrace()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        await SeedAnalysisFixtureAsync(options);
        var service = new SqliteReferenceCorpusAnalysisService(
            options,
            new FixedAppSettingsService("fake/fake-model", "medium"),
            new EmptyObservationFeatureFamilyAnalyzer(tokensPerCall: 1),
            techniqueSpecimenAnalyzer: new RecordingTechniqueSpecimenAnalyzer(tokensPerCall: 19));
        await service.StartTechniqueSpecimenAnalysisAsync(
            new StartReferenceCorpusTechniqueSpecimenAnalysisPayload(
                NovelId: 42,
                AnchorId: 101,
                SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
                MinObservationConfidence: 0.70,
                RunId: "technique-service-list-run-1"),
            CancellationToken.None);

        var specimens = await service.ListTechniqueSpecimensAsync(
            new ListReferenceCorpusTechniqueSpecimensPayload(
                NovelId: 42,
                AnchorId: 101,
                SourceNodeId: "node-b",
                PageRequest: new PageRequestPayload(
                    Cursor: null,
                    PageSize: 20,
                    SortBy: "created_at",
                    SortDir: "desc",
                    Filters: new Dictionary<string, string> { ["technique_family"] = "action_as_emotion" })),
            CancellationToken.None);

        Assert.Single(specimens.Items);
        Assert.Equal(1, specimens.Total);
        var specimen = specimens.Items[0];
        Assert.Equal("node-b", specimen.SourceNodeId);
        Assert.Equal("technique-service-list-run-1", specimen.AnalysisRunId);
        Assert.Equal("action_as_emotion", specimen.TechniqueFamily);
        Assert.NotEmpty(specimen.TransferSlots);
        Assert.NotEmpty(specimen.WhyItWorks.ContributingFactors);
        Assert.True(specimen.WhyItWorks.TraceComplete);
        Assert.Equal(2, specimen.Evidence.Count);
        Assert.Contains(specimen.Evidence, item =>
            item.FeatureFamily == ReferenceCorpusFeatureFamilies.Emotion &&
            item.TextHash == "hash-node-b" &&
            !string.IsNullOrWhiteSpace(item.EvidencePreview));
        Assert.Contains(specimen.Evidence, item => item.FeatureFamily == ReferenceCorpusFeatureFamilies.Rhetoric);
        Assert.All(specimen.WhyItWorks.ContributingFactors, factor => Assert.NotEmpty(factor.Evidence));
        AssertTechniqueSpecimenPayloadDoesNotLeakSourceFields(specimen);
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

    private static async ValueTask SeedAnalysisFixtureAsync(AppInitializationOptions options)
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
                  (101, 42, '雨门小样', '', 'rain-doorway.md', 'markdown', 'user_provided',
                   'source-hash-101', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO reference_text_nodes
                  (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
                   chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
                VALUES
                  ('node-a', 101, NULL, 'sentence', 1, 1,
                   1, 0, 10, 10, 'hash-node-a', '雨声贴着门缝往里挤。', '2026-07-09T00:00:00Z'),
                  ('node-b', 101, NULL, 'sentence', 2, 1,
                   1, 11, 25, 14, 'hash-node-b', '她没有开口，只扣紧钥匙。', '2026-07-09T00:00:00Z');
            """;
            await command.ExecuteNonQueryAsync();
        }

        await SeedTechniqueObservationFixtureAsync(connection);
    }

    private static async ValueTask SeedTechniqueObservationFixtureAsync(SqliteConnection connection)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO reference_analysis_runs
                  (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
                   scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
                VALUES
                  ('feature-run-technique-service', 101, 'feature-v1', 'reference-corpus-feature-family-v1', 'fake', 'fake-model',
                   'sentence', 'completed', NULL, 22, 'node-b|emotion', '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 2);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var createdAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            new ReferenceCorpusFeatureObservation(
                NodeId: "node-b",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                RunId: "feature-run-technique-service",
                AnchorId: 101,
                FeatureFamily: ReferenceCorpusFeatureFamilies.Emotion,
                FeatureKey: "emotion_state",
                ValueKind: "enum",
                ValueText: "restrained",
                ValueNum: 7,
                ValueBool: null,
                ValueJson: """{"surface":"calm","subtext":"restrained"}""",
                Intensity: 7,
                Confidence: 0.86,
                EvidenceStart: 0,
                EvidenceEnd: 12,
                Explanation: "动作和沉默共同显示压抑情绪。",
                ReviewState: "unverified",
                ValidityState: "active",
                SupersededByRunId: null,
                CreatedAt: createdAt),
            CancellationToken.None);
        await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            new ReferenceCorpusFeatureObservation(
                NodeId: "node-b",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                RunId: "feature-run-technique-service",
                AnchorId: 101,
                FeatureFamily: ReferenceCorpusFeatureFamilies.Emotion,
                FeatureKey: "emotion_direction",
                ValueKind: "enum",
                ValueText: "inward",
                ValueNum: 6,
                ValueBool: null,
                ValueJson: """{"direction":"inward"}""",
                Intensity: 6,
                Confidence: 0.84,
                EvidenceStart: 0,
                EvidenceEnd: 12,
                Explanation: "反应向内收束。",
                ReviewState: "unverified",
                ValidityState: "active",
                SupersededByRunId: null,
                CreatedAt: createdAt.AddSeconds(1)),
            CancellationToken.None);
        await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            new ReferenceCorpusFeatureObservation(
                NodeId: "node-b",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                RunId: "feature-run-technique-service",
                AnchorId: 101,
                FeatureFamily: ReferenceCorpusFeatureFamilies.Rhetoric,
                FeatureKey: "devices",
                ValueKind: "array",
                ValueText: "ellipsis",
                ValueNum: null,
                ValueBool: null,
                ValueJson: """[{"type":"ellipsis","narrative_function":"withhold_direct_emotion"}]""",
                Intensity: null,
                Confidence: 0.82,
                EvidenceStart: 0,
                EvidenceEnd: 12,
                Explanation: "省略直接情绪词，保留读者补全空间。",
                ReviewState: "unverified",
                ValidityState: "active",
                SupersededByRunId: null,
                CreatedAt: createdAt),
            CancellationToken.None);
    }

    private static async ValueTask SeedAdditionalTechniqueNodeAsync(AppInitializationOptions options)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO reference_text_nodes
                  (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
                   chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
                VALUES
                  ('node-c', 101, NULL, 'sentence', 3, 1,
                   1, 26, 42, 16, 'hash-node-c', '她垂下眼，把钥匙藏回袖中。', '2026-07-09T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var createdAt = DateTimeOffset.Parse("2026-07-09T00:00:03Z");
        await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            new ReferenceCorpusFeatureObservation(
                NodeId: "node-c",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                RunId: "feature-run-technique-service",
                AnchorId: 101,
                FeatureFamily: ReferenceCorpusFeatureFamilies.Emotion,
                FeatureKey: "emotion_state",
                ValueKind: "enum",
                ValueText: "withheld",
                ValueNum: 6,
                ValueBool: null,
                ValueJson: """{"surface":"quiet","subtext":"withheld"}""",
                Intensity: 6,
                Confidence: 0.85,
                EvidenceStart: 0,
                EvidenceEnd: 14,
                Explanation: "低头和藏钥匙共同显示情绪收束。",
                ReviewState: "unverified",
                ValidityState: "active",
                SupersededByRunId: null,
                CreatedAt: createdAt),
            CancellationToken.None);
        await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            new ReferenceCorpusFeatureObservation(
                NodeId: "node-c",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                RunId: "feature-run-technique-service",
                AnchorId: 101,
                FeatureFamily: ReferenceCorpusFeatureFamilies.Rhetoric,
                FeatureKey: "devices",
                ValueKind: "array",
                ValueText: "substitution",
                ValueNum: null,
                ValueBool: null,
                ValueJson: """[{"type":"substitution","narrative_function":"replace_direct_emotion"}]""",
                Intensity: null,
                Confidence: 0.83,
                EvidenceStart: 0,
                EvidenceEnd: 14,
                Explanation: "用动作替代直接情绪陈述。",
                ReviewState: "unverified",
                ValidityState: "active",
                SupersededByRunId: null,
                CreatedAt: createdAt),
            CancellationToken.None);
    }

    private static async ValueTask<int> ReadTechniqueSpecimenCountAsync(
        AppInitializationOptions options,
        string runId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_technique_specimens
            WHERE analysis_run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask<int> ReadTechniqueSpecimenEvidenceCountAsync(
        AppInitializationOptions options,
        string runId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_specimen_evidence e
            INNER JOIN reference_technique_specimens s ON s.specimen_id = e.specimen_id
            WHERE s.analysis_run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask<string> ReadTechniqueSpecimenIdByNodeAsync(
        AppInitializationOptions options,
        string runId,
        string sourceNodeId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT specimen_id
            FROM reference_technique_specimens
            WHERE analysis_run_id = $run_id
              AND source_node_id = $source_node_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$source_node_id", sourceNodeId);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async ValueTask UpdateTechniqueSpecimenReviewStateAsync(
        AppInitializationOptions options,
        string specimenId,
        string reviewState)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
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

    private static async ValueTask<string> ReadTechniqueSpecimenReviewStateAsync(
        AppInitializationOptions options,
        string specimenId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ReferenceDatabasePath(options), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT review_state
            FROM reference_technique_specimens
            WHERE specimen_id = $specimen_id;
            """;
        command.Parameters.AddWithValue("$specimen_id", specimenId);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static string ReferenceDatabasePath(AppInitializationOptions options)
    {
        return Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
    }

    private sealed class EmptyObservationFeatureFamilyAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
    {
        private readonly int _tokensPerCall;

        public EmptyObservationFeatureFamilyAnalyzer(int tokensPerCall)
        {
            _tokensPerCall = tokensPerCall;
        }

        public List<ReferenceCorpusFeatureFamilyAnalysisInput> Calls { get; } = [];

        public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusFeatureFamilyAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(input);
            return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
                $$"""{"schema_version":"reference-corpus-feature-family-v1","family":"{{input.Family}}","node_type":"{{input.NodeType}}","observations":[]}""",
                _tokensPerCall));
        }
    }

    private sealed class ThrowingFeatureFamilyAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
    {
        public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusFeatureFamilyAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                @"Analyzer failed in fixture; node_text: hidden source_text: secret raw_text: secret prompt: hidden model_output_json: secret embedding: [1] D:\private\reference.md");
        }
    }

    private sealed class ConfidenceSplitFeatureFamilyAnalyzer(int tokensPerCall) : IReferenceCorpusFeatureFamilyAnalyzer
    {
        public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusFeatureFamilyAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Family != ReferenceCorpusFeatureFamilies.Syntax)
            {
                return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
                    $$"""{"schema_version":"reference-corpus-feature-family-v1","family":"{{input.Family}}","node_type":"{{input.NodeType}}","observations":[]}""",
                    tokensPerCall));
            }

            var confidence = input.NodeId == "node-a" ? 0.42 : 0.82;
            return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
                $$"""
                {
                  "schema_version": "reference-corpus-feature-family-v1",
                  "family": "syntax",
                  "node_type": "sentence",
                  "observations": [
                    {
                      "feature_key": "sentence_pattern",
                      "label": "subject_predicate",
                      "complexity": "simple",
                      "confidence": {{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                      "evidence_start": 0,
                      "evidence_end": 4,
                      "explanation": "fixture confidence split for review routing."
                    }
                  ]
                }
                """,
                tokensPerCall));
        }
    }

    private sealed class RecordingTechniqueSpecimenAnalyzer(int tokensPerCall) : IReferenceCorpusTechniqueSpecimenAnalyzer
    {
        public List<ReferenceCorpusTechniqueSpecimenAnalysisInput> Calls { get; } = [];

        public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusTechniqueSpecimenAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(input);
            var emotionId = input.Observations
                .First(item => item.FeatureFamily == ReferenceCorpusFeatureFamilies.Emotion)
                .ObservationId;
            var rhetoricId = input.Observations
                .First(item => item.FeatureFamily == ReferenceCorpusFeatureFamilies.Rhetoric)
                .ObservationId;
            return ValueTask.FromResult(new ReferenceCorpusTechniqueSpecimenAnalysisOutput(
                ValidTechniqueSpecimenJson(input.NodeId, [emotionId, rhetoricId]),
                tokensPerCall));
        }
    }

    private sealed class ThrowingTechniqueSpecimenAnalyzer : IReferenceCorpusTechniqueSpecimenAnalyzer
    {
        public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusTechniqueSpecimenAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                @"Technique failed in fixture; node_text: hidden source_text: secret raw_source: secret raw_text: secret prompt: hidden model_output_json: secret value_json: secret embedding: [1] D:\private\reference.md");
        }
    }

    private static string ValidTechniqueSpecimenJson(string sourceNodeId, IReadOnlyList<string> observationIds)
    {
        return $$"""
        {
          "schema_version": "reference-corpus-technique-specimen-v1",
          "source_node_id": "{{sourceNodeId}}",
          "technique_family": "action_as_emotion",
          "technique_abstract": "用细节动作承载压抑情绪，以沉默留白放大张力",
          "trigger_context": "角色有强烈情绪但不能直接说破的短句节点",
          "transfer_template": "[角色] [外化细节动作]，随后留出沉默。",
          "transfer_slots": [
            { "slot_name": "role", "purpose": "当前承压角色", "constraints": "必须处在情绪压抑状态" }
          ],
          "effect_on_reader": "让读者从动作和空白中自行补全情绪，压迫感更稳",
          "applicability_conditions": ["角色需要压住反应"],
          "failure_modes": ["动作与情境没有因果时会显得装饰化"],
          "anti_patterns": ["直接解释角色情绪"],
          "world_context_dependencies": [],
          "why_it_works": [
            {
              "factor": "外化动作提供可见证据",
              "observation_ids": ["{{observationIds[0]}}"],
              "explanation": "情绪 observation 证明该节点的压力来自外化细节，而不是直白说明。"
            },
            {
              "factor": "留白让读者补全未说出口的反应",
              "observation_ids": ["{{observationIds[1]}}"],
              "explanation": "修辞 observation 证明省略和沉默承担了叙事作用。"
            }
          ],
          "confidence": 0.86,
          "mastery_notes": "适合短句，不适合需要大量信息交代的段落。"
        }
        """;
    }

    private static void AssertRunDiagnosticsDoNotLeakSensitiveFields(IReadOnlyList<string> diagnostics)
    {
        var diagnosticsJson = string.Join('\n', diagnostics);
        Assert.DoesNotContain("node_text", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_source", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_text", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model_output_json", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("value_json", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("D:\\private", diagnosticsJson, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertFeatureObservationPayloadDoesNotLeakSourceFields(ReferenceCorpusFeatureObservationPayload payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        AssertAnalysisPayloadDoesNotLeakSourceFields(json);
    }

    private static void AssertTechniqueSpecimenPayloadDoesNotLeakSourceFields(ReferenceCorpusTechniqueSpecimenPayload payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        AssertAnalysisPayloadDoesNotLeakSourceFields(json);
    }

    private static void AssertAnalysisPayloadDoesNotLeakSourceFields(string json)
    {
        Assert.DoesNotContain("node_text", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_source", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_text", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model_output_json", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("value_json", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("why_it_works_json", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rain-doorway.md", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedAppSettingsService(string selectedModelKey, string reasoningEffort) : IAppSettingsService
    {
        public ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AppSettingsPayload(
                1,
                0,
                selectedModelKey,
                reasoningEffort,
                "manual",
                360,
                string.Empty,
                string.Empty));
        }

        public ValueTask SaveSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SaveAvatarAsync(byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SaveUserNameAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetApprovalModeAsync(string mode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetChatPanelWidthAsync(int width, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetLastNovelAsync(long novelId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetLastSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetReasoningEffortAsync(string reasoningEffort, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetSelectedModelAsync(
            string selectedModelKey,
            string reasoningEffort,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
