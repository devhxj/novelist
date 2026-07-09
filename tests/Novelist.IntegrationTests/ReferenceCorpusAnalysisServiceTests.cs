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
