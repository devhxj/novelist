using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusFeatureAnalysisRunnerTests
{
 [Fact]
 public async Task RunAsyncPausesAtWorkItemBoundaryWithoutCallingNextAnalysis()
 {
 await using var connection = await OpenFixtureConnectionAsync();
 var analyzer = new RecordingFeatureFamilyAnalyzer(tokensPerCall: 10);
 var runner = new ReferenceCorpusFeatureAnalysisRunner(analyzer);

 var result = await runner.RunAsync(
 connection,
 new ReferenceCorpusFeatureAnalysisRunRequest(
 RunId: "llm-pause-run-1",
 AnchorId: 101,
 NodeType: ReferenceCorpusNodeTypes.Sentence,
 Families: [ReferenceCorpusFeatureFamilies.Syntax],
 AnalyzerVersion: "llm-feature-v1",
 ModelProvider: "fake",
 ModelId: "fake-model",
 TokenBudget: null,
 Resume: false,
 StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z"))
 {
 ExecutionControl = new SequenceExecutionControl(
 ReferenceCorpusAnalysisExecutionActions.Proceed,
 ReferenceCorpusAnalysisExecutionActions.Pause)
 },
 CancellationToken.None);

 Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Paused, result.Status);
 Assert.Equal(1, result.ProcessedWorkItems);
 Assert.Single(analyzer.Calls);
 Assert.Equal("node-a|syntax", result.ResumeCursor);
 }

    [Fact]
    public async Task RunAsyncPersistsValidatedObservationsAndResumesAfterBudgetCursor()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var analyzer = new RecordingFeatureFamilyAnalyzer(tokensPerCall: 10);
        var runner = new ReferenceCorpusFeatureAnalysisRunner(analyzer);
        var startedAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");

        var first = await runner.RunAsync(
            connection,
            new ReferenceCorpusFeatureAnalysisRunRequest(
                RunId: "llm-sentence-run-1",
                AnchorId: 101,
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                Families:
                [
                    ReferenceCorpusFeatureFamilies.Syntax,
                    ReferenceCorpusFeatureFamilies.Emotion
                ],
                AnalyzerVersion: "llm-feature-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                TokenBudget: 20,
                Resume: false,
                StartedAt: startedAt),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, first.Status);
        Assert.Equal(2, first.ProcessedWorkItems);
        Assert.Equal("node-a|emotion", first.ResumeCursor);
        Assert.Equal(2, await ReadObservationCountAsync(connection));
        Assert.Equal(2, analyzer.Calls.Count);

        var second = await runner.RunAsync(
            connection,
            new ReferenceCorpusFeatureAnalysisRunRequest(
                RunId: "llm-sentence-run-1",
                AnchorId: 101,
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                Families:
                [
                    ReferenceCorpusFeatureFamilies.Syntax,
                    ReferenceCorpusFeatureFamilies.Emotion
                ],
                AnalyzerVersion: "llm-feature-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                TokenBudget: 40,
                Resume: true,
                StartedAt: startedAt.AddMinutes(1)),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, second.Status);
        Assert.Equal(2, second.ProcessedWorkItems);
        Assert.Equal("node-b|emotion", second.ResumeCursor);
        Assert.Equal(4, await ReadObservationCountAsync(connection));
        Assert.Equal(4, analyzer.Calls.Count);
        Assert.All(analyzer.Calls, call => Assert.Same(ReferenceCorpusFeatureAnalysisContext.Empty, call.Context));

        var run = await ReadRunAsync(connection);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, run.Status);
        Assert.Equal(40, run.TokensSpent);
        Assert.Equal("node-b|emotion", run.ResumeCursor);
        Assert.Equal(4, run.ObservationCount);
    }

    [Fact]
    public async Task RunAsyncSyncsSensoryArrayObservationProjection()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var runner = new ReferenceCorpusFeatureAnalysisRunner(new RecordingFeatureFamilyAnalyzer(tokensPerCall: 5));

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusFeatureAnalysisRunRequest(
                RunId: "llm-sensory-run-1",
                AnchorId: 101,
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                Families: [ReferenceCorpusFeatureFamilies.Sensory],
                AnalyzerVersion: "llm-feature-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z")),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, result.Status);
        Assert.Equal(2, result.ObservationCount);
        Assert.Equal(2, await ReadProjectionCountAsync(connection, "auditory"));
        Assert.Equal(2, await ReadProjectionCountAsync(connection, "tactile"));
        Assert.Equal(2, await ReadArrayObservationCountAsync(connection));
    }

    [Fact]
    public async Task RunAsyncRetriesInvalidSchemaOutputBeforeFailingTheWorkItem()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var analyzer = new FlakyFeatureFamilyAnalyzer();
        var runner = new ReferenceCorpusFeatureAnalysisRunner(analyzer);

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusFeatureAnalysisRunRequest(
                RunId: "llm-retry-run-1",
                AnchorId: 101,
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                Families: [ReferenceCorpusFeatureFamilies.Syntax],
                AnalyzerVersion: "llm-feature-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z"),
                MaxValidationAttempts: 2),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, result.Status);
        Assert.Equal(3, analyzer.Calls.Count);
        Assert.Equal(2, result.ProcessedWorkItems);
        Assert.Equal(2, await ReadObservationCountAsync(connection, "llm-retry-run-1"));
        var run = await ReadRunAsync(connection, "llm-retry-run-1");
        Assert.Equal(21, run.TokensSpent);
        Assert.Equal("node-b|syntax", run.ResumeCursor);
        Assert.Contains(result.Diagnostics, item => item.Contains("retrying", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsyncFailsAfterRetryLimitAndKeepsCursorBeforeFailedWorkItem()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var runner = new ReferenceCorpusFeatureAnalysisRunner(new AlwaysInvalidFeatureFamilyAnalyzer());

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusFeatureAnalysisRunRequest(
                RunId: "llm-retry-fail-run-1",
                AnchorId: 101,
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                Families: [ReferenceCorpusFeatureFamilies.Syntax],
                AnalyzerVersion: "llm-feature-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z"),
                MaxValidationAttempts: 2),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Failed, result.Status);
        Assert.Equal(0, result.ProcessedWorkItems);
        Assert.Equal(0, await ReadObservationCountAsync(connection, "llm-retry-fail-run-1"));
        var run = await ReadRunAsync(connection, "llm-retry-fail-run-1");
        Assert.Equal(10, run.TokensSpent);
        Assert.Null(run.ResumeCursor);
        Assert.Contains(result.Diagnostics, item => item.Contains("retry limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsyncRoutesLowConfidenceObservationsToReviewState()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var runner = new ReferenceCorpusFeatureAnalysisRunner(new LowConfidenceFeatureFamilyAnalyzer());

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusFeatureAnalysisRunRequest(
                RunId: "llm-low-confidence-run-1",
                AnchorId: 101,
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                Families: [ReferenceCorpusFeatureFamilies.Syntax],
                AnalyzerVersion: "llm-feature-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z")),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, result.Status);
        Assert.Equal(2, result.ObservationCount);
        Assert.Equal(["low_confidence"], await ReadDistinctReviewStatesAsync(connection, "llm-low-confidence-run-1"));
    }

    [Fact]
    public async Task RunAsyncForPassageAnalyzesOnlyParagraphSourceSegmentsAndPassesContext()
    {
        await using var connection = await OpenPassageFixtureConnectionAsync(includeSourceSegmentNodeId: true);
        var analyzer = new RecordingFeatureFamilyAnalyzer(tokensPerCall: 3);
        var runner = new ReferenceCorpusFeatureAnalysisRunner(analyzer);

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusFeatureAnalysisRunRequest(
                RunId: "llm-passage-run-1",
                AnchorId: 101,
                NodeType: ReferenceCorpusNodeTypes.Passage,
                Families: [ReferenceCorpusFeatureFamilies.Narrative],
                AnalyzerVersion: "llm-feature-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z")),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, result.Status);
        Assert.Equal(3, result.ProcessedWorkItems);
        Assert.Equal(3, analyzer.Calls.Count);
        Assert.Equal(["node-para-prev", "node-para-target", "node-para-next"], analyzer.Calls.Select(call => call.NodeId).ToArray());
        Assert.DoesNotContain(analyzer.Calls, call => call.NodeId == "node-beat-1");
        Assert.DoesNotContain(analyzer.Calls, call => call.NodeId == "node-hook-1");

        var target = Assert.Single(analyzer.Calls, call => call.NodeId == "node-para-target");
        Assert.Equal("seg-para-target", target.Context.SourceSegmentId);
        Assert.Equal("paragraph", target.Context.SourceSegmentType);
        Assert.Equal("node-chapter-1", target.Context.Parent?.NodeId);
        Assert.Equal("node-chapter-1", target.Context.Chapter?.NodeId);
        Assert.Equal("node-scene-1", target.Context.ContainingScene?.NodeId);
        Assert.Equal("node-para-prev", target.Context.PreviousParagraph?.NodeId);
        Assert.Equal("node-para-next", target.Context.NextParagraph?.NodeId);
        Assert.Equal("paragraph", target.Context.PreviousParagraph?.SourceSegmentType);
        Assert.Equal("paragraph", target.Context.NextParagraph?.SourceSegmentType);
    }

    [Fact]
    public async Task RunAsyncForPassageSkipsLegacyStoresWithoutSourceSegmentNodeId()
    {
        await using var connection = await OpenPassageFixtureConnectionAsync(includeSourceSegmentNodeId: false);
        var analyzer = new RecordingFeatureFamilyAnalyzer(tokensPerCall: 3);
        var runner = new ReferenceCorpusFeatureAnalysisRunner(analyzer);

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusFeatureAnalysisRunRequest(
                RunId: "llm-passage-legacy-run-1",
                AnchorId: 101,
                NodeType: ReferenceCorpusNodeTypes.Passage,
                Families: [ReferenceCorpusFeatureFamilies.Narrative],
                AnalyzerVersion: "llm-feature-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z")),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, result.Status);
        Assert.Equal(0, result.ProcessedWorkItems);
        Assert.Empty(analyzer.Calls);
        Assert.Contains(result.Diagnostics, item => item.Contains("reference_source_segments.node_id", StringComparison.OrdinalIgnoreCase));
    }

    private static async ValueTask<SqliteConnection> OpenFixtureConnectionAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS reference_anchors (
                  anchor_id INTEGER PRIMARY KEY
                );
                INSERT OR IGNORE INTO reference_anchors(anchor_id) VALUES (101);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO reference_text_nodes
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

        return connection;
    }

    private static async ValueTask<SqliteConnection> OpenPassageFixtureConnectionAsync(bool includeSourceSegmentNodeId)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS reference_anchors (
                  anchor_id INTEGER PRIMARY KEY
                );
                INSERT OR IGNORE INTO reference_anchors(anchor_id) VALUES (101);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO reference_text_nodes
                  (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
                   chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
                VALUES
                  ('node-chapter-1', 101, NULL, 'chapter', 1, 0,
                   1, 0, 120, 120, 'hash-chapter-1', '第一章：雨夜', '2026-07-09T00:00:00Z'),
                  ('node-scene-1', 101, 'node-chapter-1', 'scene', 2, 1,
                   1, 10, 100, 90, 'hash-scene-1', '雨夜场景', '2026-07-09T00:00:00Z'),
                  ('node-hook-1', 101, 'node-scene-1', 'passage', 3, 2,
                   1, 10, 18, 8, 'hash-hook-1', '门外有人。', '2026-07-09T00:00:00Z'),
                  ('node-para-prev', 101, 'node-chapter-1', 'passage', 4, 1,
                   1, 20, 32, 12, 'hash-para-prev', '雨声压住了脚步。', '2026-07-09T00:00:00Z'),
                  ('node-beat-1', 101, 'node-scene-1', 'passage', 5, 2,
                   1, 33, 48, 15, 'hash-beat-1', '她攥紧钥匙。', '2026-07-09T00:00:00Z'),
                  ('node-para-target', 101, 'node-chapter-1', 'passage', 6, 1,
                   1, 40, 55, 15, 'hash-para-target', '她没有开口，只扣紧钥匙。', '2026-07-09T00:00:00Z'),
                  ('node-para-next', 101, 'node-chapter-1', 'passage', 7, 1,
                   1, 70, 84, 14, 'hash-para-next', '门锁轻轻响了一声。', '2026-07-09T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = includeSourceSegmentNodeId
                ? """
                CREATE TABLE reference_source_segments (
                  segment_id TEXT PRIMARY KEY,
                  anchor_id INTEGER NOT NULL,
                  chapter_index INTEGER NOT NULL,
                  chapter_title TEXT NOT NULL,
                  segment_type TEXT NOT NULL,
                  segment_index INTEGER NOT NULL,
                  parent_segment_id TEXT NOT NULL,
                  start_offset INTEGER NOT NULL,
                  end_offset INTEGER NOT NULL,
                  text TEXT NOT NULL,
                  text_hash TEXT NOT NULL,
                  node_id TEXT REFERENCES reference_text_nodes(node_id)
                );
                INSERT INTO reference_source_segments
                  (segment_id, anchor_id, chapter_index, chapter_title, segment_type, segment_index,
                   parent_segment_id, start_offset, end_offset, text, text_hash, node_id)
                VALUES
                  ('seg-chapter-1', 101, 1, '第一章', 'chapter', 1, '', 0, 120, '第一章：雨夜', 'hash-chapter-1', 'node-chapter-1'),
                  ('seg-scene-1', 101, 1, '第一章', 'scene', 1, 'seg-chapter-1', 10, 100, '雨夜场景', 'hash-scene-1', 'node-scene-1'),
                  ('seg-hook-1', 101, 1, '第一章', 'hook', 1, 'seg-scene-1', 10, 18, '门外有人。', 'hash-hook-1', 'node-hook-1'),
                  ('seg-para-prev', 101, 1, '第一章', 'paragraph', 1, 'seg-chapter-1', 20, 32, '雨声压住了脚步。', 'hash-para-prev', 'node-para-prev'),
                  ('seg-beat-1', 101, 1, '第一章', 'beat', 1, 'seg-scene-1', 33, 48, '她攥紧钥匙。', 'hash-beat-1', 'node-beat-1'),
                  ('seg-para-target', 101, 1, '第一章', 'paragraph', 2, 'seg-chapter-1', 40, 55, '她没有开口，只扣紧钥匙。', 'hash-para-target', 'node-para-target'),
                  ('seg-para-next', 101, 1, '第一章', 'paragraph', 3, 'seg-chapter-1', 70, 84, '门锁轻轻响了一声。', 'hash-para-next', 'node-para-next');
                """
                : """
                CREATE TABLE reference_source_segments (
                  segment_id TEXT PRIMARY KEY,
                  anchor_id INTEGER NOT NULL,
                  chapter_index INTEGER NOT NULL,
                  chapter_title TEXT NOT NULL,
                  segment_type TEXT NOT NULL,
                  segment_index INTEGER NOT NULL,
                  parent_segment_id TEXT NOT NULL,
                  start_offset INTEGER NOT NULL,
                  end_offset INTEGER NOT NULL,
                  text TEXT NOT NULL,
                  text_hash TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        return connection;
    }

    private static async ValueTask<int> ReadObservationCountAsync(SqliteConnection connection)
    {
        return await ReadObservationCountAsync(connection, "llm-sentence-run-1");
    }

    private static async ValueTask<int> ReadObservationCountAsync(SqliteConnection connection, string runId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_feature_observations WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask<PersistedRun> ReadRunAsync(SqliteConnection connection)
    {
        return await ReadRunAsync(connection, "llm-sentence-run-1");
    }

    private static async ValueTask<PersistedRun> ReadRunAsync(SqliteConnection connection, string runId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, tokens_spent, resume_cursor, observation_count
            FROM reference_analysis_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PersistedRun(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3));
    }

    private static async ValueTask<int> ReadProjectionCountAsync(SqliteConnection connection, string sense)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_obs_sensory
            WHERE sense = $sense;
            """;
        command.Parameters.AddWithValue("$sense", sense);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask<int> ReadArrayObservationCountAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_feature_observations
            WHERE feature_family = 'sensory'
              AND feature_key = 'senses'
              AND value_kind = 'array';
        """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask<string[]> ReadDistinctReviewStatesAsync(SqliteConnection connection, string runId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT review_state
            FROM reference_feature_observations
            WHERE run_id = $run_id
            ORDER BY review_state;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }

 private sealed class SequenceExecutionControl(params string[] actions) : IReferenceCorpusAnalysisExecutionControl
 {
 private int _index;

 public ValueTask<string> CheckpointAsync(string runId, string? resumeCursor, CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 var index = Math.Min(Interlocked.Increment(ref _index) - 1, actions.Length - 1);
 return ValueTask.FromResult(actions[index]);
 }
 }

 private sealed class RecordingFeatureFamilyAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
    {
        private readonly int _tokensPerCall;

        public RecordingFeatureFamilyAnalyzer(int tokensPerCall)
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
            var json = input.Family switch
            {
                ReferenceCorpusFeatureFamilies.Syntax => SyntaxJson(input.NodeText.Length),
                ReferenceCorpusFeatureFamilies.Emotion => EmotionJson(input.NodeText.Length),
                ReferenceCorpusFeatureFamilies.Sensory => SensoryJson(input.NodeText.Length),
                ReferenceCorpusFeatureFamilies.Narrative => NarrativeJson(input.NodeText.Length),
                _ => throw new InvalidOperationException("Unexpected family in fixture.")
            };
            return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(json, _tokensPerCall));
        }

        private static string SyntaxJson(int sourceLength)
        {
            return $$"""
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "syntax",
              "node_type": "sentence",
              "observations": [
                {
                  "feature_key": "sentence_pattern",
                  "label": "subject_predicate",
                  "complexity": "simple",
                  "confidence": 0.82,
                  "evidence_start": 0,
                  "evidence_end": {{Math.Min(4, sourceLength)}},
                  "explanation": "fake analyzer emits grounded syntax."
                }
              ]
            }
            """;
        }

        private static string EmotionJson(int sourceLength)
        {
            return $$"""
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "emotion",
              "node_type": "sentence",
              "observations": [
                {
                  "feature_key": "emotion_state",
                  "surface": "calm",
                  "subtext": "restrained",
                  "direction": "stable",
                  "mode": "suppressed",
                  "intensity": 5,
                  "confidence": 0.78,
                  "evidence_start": 0,
                  "evidence_end": {{Math.Min(6, sourceLength)}},
                  "explanation": "fake analyzer emits grounded emotion."
                }
              ]
            }
            """;
        }

        private static string SensoryJson(int sourceLength)
        {
            return $$"""
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "sensory",
              "node_type": "sentence",
              "observations": [
                {
                  "feature_key": "sensory_detail",
                  "sense": "auditory",
                  "intensity": 7,
                  "narrative_function": "raise_tension",
                  "confidence": 0.82,
                  "evidence_start": 0,
                  "evidence_end": {{Math.Min(5, sourceLength)}},
                  "explanation": "fake analyzer emits auditory pressure."
                },
                {
                  "feature_key": "sensory_detail",
                  "sense": "tactile",
                  "intensity": 6,
                  "narrative_function": "reveal_state",
                  "confidence": 0.80,
                  "evidence_start": 0,
                  "evidence_end": {{Math.Min(6, sourceLength)}},
                  "explanation": "fake analyzer emits tactile pressure."
                }
              ]
            }
            """;
        }

        private static string NarrativeJson(int sourceLength)
        {
            return $$"""
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "narrative",
              "node_type": "passage",
              "observations": [
                {
                  "feature_key": "narrative_function",
                  "function": "advance_plot",
                  "confidence": 0.82,
                  "evidence_start": 0,
                  "evidence_end": {{Math.Min(6, sourceLength)}},
                  "explanation": "fake analyzer emits grounded narrative function."
                }
              ]
            }
            """;
        }
    }

    private sealed class FlakyFeatureFamilyAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
    {
        public List<ReferenceCorpusFeatureFamilyAnalysisInput> Calls { get; } = [];

        public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusFeatureFamilyAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(input);
            if (Calls.Count == 1)
            {
                return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
                    """{"schema_version":"reference-corpus-feature-family-v1","family":"syntax","node_type":"sentence","observations":[{"feature_key":"sentence_pattern","label":"unsupported","complexity":"simple","confidence":0.8,"evidence_start":0,"evidence_end":4,"explanation":"bad enum"}]}""",
                    TokensSpent: 7));
            }

            return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
                """
                {
                  "schema_version": "reference-corpus-feature-family-v1",
                  "family": "syntax",
                  "node_type": "sentence",
                  "observations": [
                    {
                      "feature_key": "sentence_pattern",
                      "label": "subject_predicate",
                      "complexity": "simple",
                      "confidence": 0.82,
                      "evidence_start": 0,
                      "evidence_end": 4,
                      "explanation": "retry produced valid syntax."
                    }
                  ]
                }
                """,
                TokensSpent: 7));
        }
    }

    private sealed class AlwaysInvalidFeatureFamilyAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
    {
        public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusFeatureFamilyAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
                """{"schema_version":"reference-corpus-feature-family-v1","family":"syntax","node_type":"sentence","observations":[{"feature_key":"sentence_pattern","label":"unsupported","complexity":"simple","confidence":0.8,"evidence_start":0,"evidence_end":4,"explanation":"bad enum"}]}""",
                TokensSpent: 5));
        }
    }

    private sealed class LowConfidenceFeatureFamilyAnalyzer : IReferenceCorpusFeatureFamilyAnalyzer
    {
        public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusFeatureFamilyAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(
                """
                {
                  "schema_version": "reference-corpus-feature-family-v1",
                  "family": "syntax",
                  "node_type": "sentence",
                  "observations": [
                    {
                      "feature_key": "sentence_pattern",
                      "label": "subject_predicate",
                      "complexity": "simple",
                      "confidence": 0.42,
                      "evidence_start": 0,
                      "evidence_end": 4,
                      "explanation": "low confidence should require review without claiming a conflict."
                    }
                  ]
                }
                """,
                TokensSpent: 5));
        }
    }

    private sealed record PersistedRun(
        string Status,
        int TokensSpent,
        string? ResumeCursor,
        int ObservationCount);
}
