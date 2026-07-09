using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;
using Novelist.IntegrationTests.TestDoubles;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusServiceTests : IDisposable
{
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

        Assert.Equal(2, result.Total);
        Assert.Equal(10, result.Size);
        Assert.False(result.HasMore);
        Assert.Equal(["node-rain-doorway-s1", "node-rain-doorway-s2"], result.Items.Select(item => item.NodeId).ToArray());
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
        Assert.Equal("obs-rain-doorway-sensory", result.Items[0].Evidence.Single().ObservationId);

        var allEmbeddingInputs = embeddings.Calls.SelectMany(call => call.Inputs).ToArray();
        Assert.Contains("雨声贴着门缝往里挤。", allEmbeddingInputs);
        Assert.Contains("她没有立刻开口，只把钥匙扣在掌心。", allEmbeddingInputs);
        Assert.Contains("林岚停在门里，指尖还按着锁。", allEmbeddingInputs);
        Assert.Contains(embeddings.Calls, call => call.Options.InputKind == BuiltinOnnxEmbeddingModel.DocumentInputKind);
        Assert.Contains(embeddings.Calls, call => call.Options.InputKind == BuiltinOnnxEmbeddingModel.QueryInputKind);
        Assert.Equal(2, await ReadNodeEmbeddingCountAsync(options));
        Assert.Equal(1, await ReadCurrentChapterEmbeddingCacheCountAsync(options));

        var firstCallCount = embeddings.CallCount;
        var second = await service.SearchCandidatesAsync(BuildSearchPayload(), CancellationToken.None);

        Assert.Equal(result.Items.Select(item => item.NodeId), second.Items.Select(item => item.NodeId));
        Assert.Equal(firstCallCount + 1, embeddings.CallCount);
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
                   'source-hash-102', 'corpus-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace');
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
                  ('library-rain-doorway', 102, 1, 'trusted', 'hidden-reveal');

                INSERT OR IGNORE INTO reference_source_license
                  (anchor_id, license_state, authorization_evidence, reuse_policy,
                   max_verbatim_ratio, cleared_for_insertion, reviewed_at)
                VALUES
                  (101, 'authorized', 'fixture', 'adapted_only', 0.42, 1, '2026-07-09T00:00:00Z'),
                  (102, 'forbidden', 'fixture', 'forbidden', 0.00, 0, '2026-07-09T00:00:00Z');

                INSERT OR IGNORE INTO reference_text_nodes
                  (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
                   chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
                VALUES
                  ('node-rain-doorway-s1', 101, NULL, 'sentence', 1, 1,
                   1, 0, 11, 11, 'sha256-fixture-s1', '雨声贴着门缝往里挤。', '2026-07-09T00:00:00Z'),
                  ('node-rain-doorway-s2', 101, NULL, 'sentence', 2, 1,
                   1, 12, 31, 19, 'sha256-fixture-s2', '她没有立刻开口，只把钥匙扣在掌心。', '2026-07-09T00:00:00Z'),
                  ('node-hidden-reveal', 102, NULL, 'sentence', 1, 1,
                   1, 0, 13, 13, 'sha256-hidden-reveal', '周鸣的真实目的终于暴露。', '2026-07-09T00:00:00Z');

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
                   0, 18, '不开口用动作压住答案。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async ValueTask<int> ReadNodeEmbeddingCountAsync(AppInitializationOptions options)
    {
        return await ReadCountAsync(options, "reference_text_node_embeddings");
    }

    private static async ValueTask<int> ReadCurrentChapterEmbeddingCacheCountAsync(AppInitializationOptions options)
    {
        return await ReadCountAsync(options, "reference_current_chapter_embedding_cache");
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
}
