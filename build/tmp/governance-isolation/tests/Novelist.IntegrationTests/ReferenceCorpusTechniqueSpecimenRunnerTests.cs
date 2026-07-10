using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusTechniqueSpecimenRunnerTests
{
    [Fact]
    public async Task RunAsyncPersistsTechniqueSpecimenAndEvidenceJunctions()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var observationIds = await SeedFeatureObservationsAsync(connection);
        var analyzer = new RecordingTechniqueSpecimenAnalyzer(observationIds);
        var runner = new ReferenceCorpusTechniqueSpecimenRunner(analyzer);

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusTechniqueSpecimenRunRequest(
                RunId: "technique-run-1",
                AnchorId: 101,
                SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
                AnalyzerVersion: "technique-specimen-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                MinObservationConfidence: 0.70,
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z")),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, result.Status);
        Assert.Equal(1, result.ProcessedNodes);
        Assert.Equal(1, result.SpecimenCount);
        Assert.Equal(17, result.TokensSpent);
        await AssertNoForeignKeyViolationsAsync(connection);
        Assert.Single(analyzer.Calls);
        Assert.Equal("node-tech-1", analyzer.Calls[0].NodeId);
        Assert.Equal(observationIds.Order(StringComparer.Ordinal), analyzer.Calls[0].Observations.Select(item => item.ObservationId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(analyzer.Calls[0].Observations, item => item.FeatureFamily == ReferenceCorpusFeatureFamilies.Syntax);

        var specimen = await ReadSpecimenAsync(connection);
        Assert.Equal("node-tech-1", specimen.SourceNodeId);
        Assert.Equal("technique-run-1", specimen.AnalysisRunId);
        Assert.Equal("action_as_emotion", specimen.TechniqueFamily);
        Assert.DoesNotContain("林岚", specimen.TechniqueAbstract, StringComparison.Ordinal);
        Assert.DoesNotContain("捏了捏拳", specimen.TechniqueAbstract, StringComparison.Ordinal);
        Assert.DoesNotContain("林岚", specimen.TransferTemplate, StringComparison.Ordinal);
        Assert.Equal(["role", "external_action", "silence"], ReadSlotNames(specimen.TransferSlotsJson));
        Assert.Equal(observationIds.Order(StringComparer.Ordinal), (await ReadEvidenceIdsAsync(connection, specimen.SpecimenId)).Order(StringComparer.Ordinal));

        using var why = JsonDocument.Parse(specimen.WhyItWorksJson);
        var factors = why.RootElement.GetProperty("contributing_factors").EnumerateArray().ToArray();
        Assert.Equal(2, factors.Length);
        Assert.All(factors, factor => Assert.NotEmpty(factor.GetProperty("observation_ids").EnumerateArray()));

        await UpdateSpecimenReviewStateAsync(connection, specimen.SpecimenId, "confirmed");
 await Assert.ThrowsAsync<ReferenceCorpusTechniqueSpecimenRunner.ReferenceCorpusAnalysisRunPreconditionException>(async () =>
        await runner.RunAsync(
        connection,
        new ReferenceCorpusTechniqueSpecimenRunRequest(
        RunId: "technique-run-1",
        AnchorId: 101,
        SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
        AnalyzerVersion: "technique-specimen-v1",
        ModelProvider: "fake",
        ModelId: "fake-model",
        MinObservationConfidence: 0.70,
        TokenBudget: null,
        Resume: false,
        StartedAt: DateTimeOffset.Parse("2026-07-09T00:01:00Z")),
        CancellationToken.None));

        Assert.Single(analyzer.Calls);
        Assert.Equal("confirmed", await ReadSpecimenReviewStateAsync(connection, specimen.SpecimenId));
    }

    [Fact]
    public async Task RunAsyncDoesNotCallAnalyzerWhenTokenBudgetIsZero()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var observationIds = await SeedFeatureObservationsAsync(connection);
        var analyzer = new RecordingTechniqueSpecimenAnalyzer(observationIds);
        var runner = new ReferenceCorpusTechniqueSpecimenRunner(analyzer);

        var result = await runner.RunAsync(
        connection,
        new ReferenceCorpusTechniqueSpecimenRunRequest(
        RunId: "technique-zero-budget-run-1",
        AnchorId: 101,
        SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
        AnalyzerVersion: "technique-specimen-v1",
        ModelProvider: "fake",
        ModelId: "fake-model",
        MinObservationConfidence: 0.70,
        TokenBudget: 0,
        Resume: false,
        StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z")),
        CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, result.Status);
        Assert.Equal(0, result.TokensSpent);
        Assert.Null(result.ResumeCursor);
        Assert.Equal(0, result.ProcessedNodes);
        Assert.Empty(analyzer.Calls);
        Assert.Equal(0, await ReadSpecimenCountAsync(connection));
    }

    [Fact]
    public async Task RunAsyncRejectsStaleResumeCursorWithoutMutatingPersistedRun()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var observationIds = await SeedFeatureObservationsAsync(connection);
        await SeedTechniqueRunAsync(
        connection,
        runId: "technique-stale-cursor-run-1",
        scope: "technique_specimen",
        status: ReferenceCorpusAnalysisRunStatuses.BudgetExhausted,
        tokenBudget: 17,
        tokensSpent: 17,
        resumeCursor: "node-removed");
        var analyzer = new RecordingTechniqueSpecimenAnalyzer(observationIds);
        var runner = new ReferenceCorpusTechniqueSpecimenRunner(analyzer);

 await Assert.ThrowsAsync<ReferenceCorpusTechniqueSpecimenRunner.ReferenceCorpusAnalysisRunPreconditionException>(async () =>
        await runner.RunAsync(
        connection,
        new ReferenceCorpusTechniqueSpecimenRunRequest(
        RunId: "technique-stale-cursor-run-1",
        AnchorId: 101,
        SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
        AnalyzerVersion: "technique-specimen-v1",
        ModelProvider: "fake",
        ModelId: "fake-model",
        MinObservationConfidence: 0.70,
        TokenBudget: 34,
        Resume: true,
        StartedAt: DateTimeOffset.Parse("2026-07-09T00:01:00Z")),
        CancellationToken.None));

        Assert.Empty(analyzer.Calls);
        var state = await ReadRunStateAsync(connection, "technique-stale-cursor-run-1");
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, state.Status);
        Assert.Equal(17, state.TokenBudget);
        Assert.Equal(17, state.TokensSpent);
        Assert.Equal("node-removed", state.ResumeCursor);
    }

    [Fact]
    public async Task RunAsyncRejectsRunIdOwnedByAnotherAnalysisScope()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var observationIds = await SeedFeatureObservationsAsync(connection);
        var analyzer = new RecordingTechniqueSpecimenAnalyzer(observationIds);
        var runner = new ReferenceCorpusTechniqueSpecimenRunner(analyzer);

 await Assert.ThrowsAsync<ReferenceCorpusTechniqueSpecimenRunner.ReferenceCorpusAnalysisRunPreconditionException>(async () =>
        await runner.RunAsync(
        connection,
        new ReferenceCorpusTechniqueSpecimenRunRequest(
        RunId: "feature-run-1",
        AnchorId: 101,
        SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
        AnalyzerVersion: "technique-specimen-v1",
        ModelProvider: "fake",
        ModelId: "fake-model",
        MinObservationConfidence: 0.70,
        TokenBudget: 34,
        Resume: true,
        StartedAt: DateTimeOffset.Parse("2026-07-09T00:01:00Z")),
        CancellationToken.None));

        Assert.Empty(analyzer.Calls);
        var state = await ReadRunStateAsync(connection, "feature-run-1");
        Assert.Equal("sentence", state.Scope);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Completed, state.Status);
    }

    [Fact]
    public async Task RunAsyncRejectsWhyItWorksFactorsWithoutRealObservationEvidence()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var observationIds = await SeedFeatureObservationsAsync(connection);
        var runner = new ReferenceCorpusTechniqueSpecimenRunner(
            new MissingEvidenceTechniqueSpecimenAnalyzer(observationIds));

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusTechniqueSpecimenRunRequest(
                RunId: "technique-missing-evidence-run-1",
                AnchorId: 101,
                SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
                AnalyzerVersion: "technique-specimen-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                MinObservationConfidence: 0.70,
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z")),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Failed, result.Status);
        Assert.Equal(0, await ReadSpecimenCountAsync(connection));
        Assert.Contains(result.Diagnostics, item => item.Contains("unknown observation_id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsyncRejectsTechniqueAbstractAndTemplateThatLeakSourceTerms()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var observationIds = await SeedFeatureObservationsAsync(connection);
        var runner = new ReferenceCorpusTechniqueSpecimenRunner(
            new SourceLeakingTechniqueSpecimenAnalyzer(observationIds));

        var result = await runner.RunAsync(
            connection,
            new ReferenceCorpusTechniqueSpecimenRunRequest(
                RunId: "technique-source-leak-run-1",
                AnchorId: 101,
                SourceNodeType: ReferenceCorpusNodeTypes.Sentence,
                AnalyzerVersion: "technique-specimen-v1",
                ModelProvider: "fake",
                ModelId: "fake-model",
                MinObservationConfidence: 0.70,
                TokenBudget: null,
                Resume: false,
                StartedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z")),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Failed, result.Status);
        Assert.Equal(0, await ReadSpecimenCountAsync(connection));
        Assert.Contains(result.Diagnostics, item => item.Contains("source term", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Diagnostics, item => item.Contains("林岚", StringComparison.Ordinal));
    }

    private static async ValueTask<SqliteConnection> OpenFixtureConnectionAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at,
               corpus_visibility, source_trust, user_tags_json)
            VALUES
              (101, 42, '技法测试源', 'fixture', '', 'markdown', 'user_provided',
               'hash-anchor', 'test-v1', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z',
               'workspace', 'user_verified', '[]');

            INSERT INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-tech-1', 101, NULL, 'sentence', 1, 1,
               1, 0, 13, 13, 'hash-node-tech-1', '林岚捏了捏拳，没有说话。', '2026-07-09T00:00:00Z');

            INSERT INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ('feature-run-1', 101, 'feature-v1', 'reference-corpus-feature-family-v1', 'fake', 'fake-model',
               'sentence', 'completed', NULL, 22, 'node-tech-1|emotion', '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 2);
            """;
        await command.ExecuteNonQueryAsync();

        return connection;
    }

    private static async ValueTask<IReadOnlyList<string>> SeedFeatureObservationsAsync(SqliteConnection connection)
    {
        var createdAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        var emotion = await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            new ReferenceCorpusFeatureObservation(
                NodeId: "node-tech-1",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                RunId: "feature-run-1",
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
                EvidenceEnd: 13,
                Explanation: "动作和沉默共同显示压抑情绪。",
                ReviewState: "unverified",
                ValidityState: "active",
                SupersededByRunId: null,
                CreatedAt: createdAt),
            CancellationToken.None);
        var rhetoric = await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            new ReferenceCorpusFeatureObservation(
                NodeId: "node-tech-1",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                RunId: "feature-run-1",
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
                EvidenceEnd: 13,
                Explanation: "省略直接情绪词，保留读者补全空间。",
                ReviewState: "unverified",
                ValidityState: "active",
                SupersededByRunId: null,
                CreatedAt: createdAt),
            CancellationToken.None);
        await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            new ReferenceCorpusFeatureObservation(
                NodeId: "node-tech-1",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                RunId: "feature-run-1",
                AnchorId: 101,
                FeatureFamily: ReferenceCorpusFeatureFamilies.Syntax,
                FeatureKey: "sentence_pattern",
                ValueKind: "enum",
                ValueText: "subject_predicate",
                ValueNum: null,
                ValueBool: null,
                ValueJson: null,
                Intensity: null,
 Confidence: 0.90,
                EvidenceStart: 0,
                EvidenceEnd: 4,
 Explanation: "人工拒绝的 observation 不应触发技法合成。",
 ReviewState: "rejected",
                ValidityState: "active",
                SupersededByRunId: null,
                CreatedAt: createdAt),
            CancellationToken.None);

        return [emotion.ObservationId, rhetoric.ObservationId];
    }

    private static async ValueTask<PersistedSpecimen> ReadSpecimenAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT specimen_id, source_node_id, analysis_run_id, technique_family,
                   technique_abstract, transfer_template, transfer_slots_json, why_it_works_json
            FROM reference_technique_specimens;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PersistedSpecimen(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));
    }

    private static async ValueTask<int> ReadSpecimenCountAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_technique_specimens;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask SeedTechniqueRunAsync(
    SqliteConnection connection,
    string runId,
    string scope,
    string status,
    int? tokenBudget,
    int tokensSpent,
    string? resumeCursor)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
 INSERT INTO reference_analysis_runs
 (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
 scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
 VALUES
 ($run_id, 101, 'technique-specimen-v1', 'reference-corpus-technique-specimen-v1', 'fake', 'fake-model',
 $scope, $status, $token_budget, $tokens_spent, $resume_cursor, '2026-07-09T00:00:00Z', NULL, 0);
 """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$token_budget", tokenBudget is null ? DBNull.Value : tokenBudget.Value);
        command.Parameters.AddWithValue("$tokens_spent", tokensSpent);
        command.Parameters.AddWithValue("$resume_cursor", resumeCursor is null ? DBNull.Value : resumeCursor);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<PersistedRunState> ReadRunStateAsync(SqliteConnection connection, string runId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
 SELECT scope, status, token_budget, tokens_spent, resume_cursor
 FROM reference_analysis_runs
 WHERE run_id = $run_id;
 """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PersistedRunState(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async ValueTask<string[]> ReadEvidenceIdsAsync(SqliteConnection connection, string specimenId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observation_id
            FROM reference_specimen_evidence
            WHERE specimen_id = $specimen_id
            ORDER BY observation_id;
            """;
        command.Parameters.AddWithValue("$specimen_id", specimenId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }

    private static async ValueTask UpdateSpecimenReviewStateAsync(SqliteConnection connection, string specimenId, string reviewState)
    {
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

    private static async ValueTask<string> ReadSpecimenReviewStateAsync(SqliteConnection connection, string specimenId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT review_state
            FROM reference_technique_specimens
            WHERE specimen_id = $specimen_id;
            """;
        command.Parameters.AddWithValue("$specimen_id", specimenId);
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
    }

    private static async ValueTask AssertNoForeignKeyViolationsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private static string[] ReadSlotNames(string transferSlotsJson)
    {
        using var document = JsonDocument.Parse(transferSlotsJson);
        return document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("slot_name").GetString() ?? string.Empty)
            .ToArray();
    }

    private sealed class RecordingTechniqueSpecimenAnalyzer(IReadOnlyList<string> observationIds)
        : IReferenceCorpusTechniqueSpecimenAnalyzer
    {
        public List<ReferenceCorpusTechniqueSpecimenAnalysisInput> Calls { get; } = [];

        public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusTechniqueSpecimenAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(input);
            return ValueTask.FromResult(new ReferenceCorpusTechniqueSpecimenAnalysisOutput(
                ValidSpecimenJson(input.NodeId, observationIds),
                TokensSpent: 17));
        }
    }

    private sealed class MissingEvidenceTechniqueSpecimenAnalyzer(IReadOnlyList<string> observationIds)
        : IReferenceCorpusTechniqueSpecimenAnalyzer
    {
        public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusTechniqueSpecimenAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceCorpusTechniqueSpecimenAnalysisOutput(
                ValidSpecimenJson(input.NodeId, [observationIds[0], "obs_missing"]),
                TokensSpent: 11));
        }
    }

    private sealed class SourceLeakingTechniqueSpecimenAnalyzer(IReadOnlyList<string> observationIds)
        : IReferenceCorpusTechniqueSpecimenAnalyzer
    {
        public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
            ReferenceCorpusTechniqueSpecimenAnalysisInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = ValidSpecimenJson(input.NodeId, observationIds)
                .Replace("用细节动作承载压抑情绪，以沉默留白放大张力", "林岚捏了捏拳的动作承载压抑情绪", StringComparison.Ordinal)
                .Replace("[角色] [外化细节动作]，随后留出沉默。", "[角色] 捏了捏拳，随后留出沉默。", StringComparison.Ordinal);
            return ValueTask.FromResult(new ReferenceCorpusTechniqueSpecimenAnalysisOutput(json, TokensSpent: 11));
        }
    }

    private static string ValidSpecimenJson(string sourceNodeId, IReadOnlyList<string> observationIds)
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
            { "slot_name": "role", "purpose": "当前承压角色", "constraints": "必须处在情绪压抑状态" },
            { "slot_name": "external_action", "purpose": "可见的细节动作", "constraints": "动作必须能承载情绪压力" },
            { "slot_name": "silence", "purpose": "省略直接情绪陈述", "constraints": "不要直接写情绪词" }
          ],
          "effect_on_reader": "让读者从动作和空白中自行补全情绪，压迫感更稳",
          "applicability_conditions": ["角色需要压住反应", "场景允许短暂停顿"],
          "failure_modes": ["动作与情境没有因果时会显得装饰化"],
          "anti_patterns": ["直接解释角色情绪", "动作过密导致节奏失焦"],
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

    private sealed record PersistedSpecimen(
            string SpecimenId,
            string SourceNodeId,
            string AnalysisRunId,
            string TechniqueFamily,
            string TechniqueAbstract,
            string TransferTemplate,
    string TransferSlotsJson,
    string WhyItWorksJson);

    private sealed record PersistedRunState(
    string Scope,
    string Status,
    int? TokenBudget,
    int TokensSpent,
    string? ResumeCursor);
}
