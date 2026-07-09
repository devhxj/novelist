using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed class ReferenceCorpusTechniqueSpecimenRunner
{
    private readonly IReferenceCorpusTechniqueSpecimenAnalyzer _analyzer;

    public ReferenceCorpusTechniqueSpecimenRunner(IReferenceCorpusTechniqueSpecimenAnalyzer analyzer)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    public async ValueTask<ReferenceCorpusTechniqueSpecimenRunResult> RunAsync(
        SqliteConnection connection,
        ReferenceCorpusTechniqueSpecimenRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<string>();
        var processed = 0;
        var tokensSpent = 0;
        await UpsertRunAsync(
            connection,
            request,
            ReferenceCorpusAnalysisRunStatuses.Running,
            tokensSpent,
            specimenCount: 0,
            completedAt: null,
            cancellationToken);

        var nodes = await ReadNodesWithEvidenceAsync(connection, request, cancellationToken);
        foreach (var node in nodes)
        {
            var output = await _analyzer.AnalyzeAsync(
                new ReferenceCorpusTechniqueSpecimenAnalysisInput(
                    request.RunId,
                    request.AnchorId,
                    node.NodeId,
                    node.NodeType,
                    node.Text,
                    node.Observations),
                cancellationToken);
            tokensSpent += Math.Max(0, output.TokensSpent);

            var validation = ReferenceCorpusTechniqueSpecimenOutputValidator.Validate(
                output.ModelOutputJson,
                node.NodeId,
                node.Text,
                node.Observations);
            diagnostics.AddRange(validation.Diagnostics);
            if (validation.Status != ReferenceCorpusTechniqueSpecimenValidationStatuses.Passed ||
                validation.Candidate is null)
            {
                await UpsertRunAsync(
                    connection,
                    request,
                    ReferenceCorpusAnalysisRunStatuses.Failed,
                    tokensSpent,
                    specimenCount: await CountSpecimensAsync(connection, request.RunId, cancellationToken),
                    completedAt: request.StartedAt,
                    cancellationToken);
                return BuildResult(
                    request,
                    ReferenceCorpusAnalysisRunStatuses.Failed,
                    tokensSpent,
                    await CountSpecimensAsync(connection, request.RunId, cancellationToken),
                    processed,
                    diagnostics);
            }

            await PersistSpecimenAsync(connection, request, validation.Candidate, cancellationToken);
            processed++;
        }

        await UpsertRunAsync(
            connection,
            request,
            ReferenceCorpusAnalysisRunStatuses.Completed,
            tokensSpent,
            specimenCount: await CountSpecimensAsync(connection, request.RunId, cancellationToken),
            completedAt: request.StartedAt,
            cancellationToken);
        return BuildResult(
            request,
            ReferenceCorpusAnalysisRunStatuses.Completed,
            tokensSpent,
            await CountSpecimensAsync(connection, request.RunId, cancellationToken),
            processed,
            diagnostics);
    }

    private static void ValidateRequest(ReferenceCorpusTechniqueSpecimenRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new ArgumentException("Run id is required.", nameof(request));
        }

        if (request.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.AnchorId, "Anchor id must be positive.");
        }

        if (request.SourceNodeType is not ReferenceCorpusNodeTypes.Sentence and not ReferenceCorpusNodeTypes.Passage)
        {
            throw new ArgumentException("Technique specimen source node_type must be sentence or passage.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.AnalyzerVersion))
        {
            throw new ArgumentException("Analyzer version is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ModelProvider) || string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("Model provider and id are required.", nameof(request));
        }

        if (request.MinObservationConfidence is < 0 or > 0.95)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MinObservationConfidence, "Observation confidence threshold must be between 0 and 0.95.");
        }
    }

    private static async ValueTask<IReadOnlyList<TechniqueSpecimenNode>> ReadNodesWithEvidenceAsync(
        SqliteConnection connection,
        ReferenceCorpusTechniqueSpecimenRunRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.node_id,
                   n.node_type,
                   n.text,
                   o.observation_id,
                   o.feature_family,
                   o.feature_key,
                   o.value_kind,
                   o.value_text,
                   o.value_num,
                   o.value_bool,
                   o.value_json,
                   o.intensity,
                   o.confidence,
                   o.evidence_start,
                   o.evidence_end,
                   o.explanation
            FROM reference_text_nodes n
            INNER JOIN reference_feature_observations o ON o.node_id = n.node_id
            WHERE n.anchor_id = $anchor_id
              AND n.node_type = $node_type
              AND o.anchor_id = n.anchor_id
              AND o.node_type = n.node_type
              AND o.validity_state = 'active'
              AND o.confidence >= $min_confidence
            ORDER BY n.chapter_index, n.start_offset, n.sequence_index, n.node_id,
                     o.feature_family, o.feature_key, o.observation_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", request.AnchorId);
        command.Parameters.AddWithValue("$node_type", request.SourceNodeType);
        command.Parameters.AddWithValue("$min_confidence", request.MinObservationConfidence);

        var builders = new Dictionary<string, TechniqueSpecimenNodeBuilder>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.GetString(0);
            if (!builders.TryGetValue(nodeId, out var builder))
            {
                builder = new TechniqueSpecimenNodeBuilder(
                    nodeId,
                    reader.GetString(1),
                    reader.GetString(2));
                builders[nodeId] = builder;
            }

            builder.Observations.Add(new ReferenceCorpusTechniqueObservationEvidence(
                ObservationId: reader.GetString(3),
                FeatureFamily: reader.GetString(4),
                FeatureKey: reader.GetString(5),
                ValueKind: reader.GetString(6),
                ValueText: reader.IsDBNull(7) ? null : reader.GetString(7),
                ValueNum: reader.IsDBNull(8) ? null : reader.GetDouble(8),
                ValueBool: reader.IsDBNull(9) ? null : reader.GetInt32(9) != 0,
                ValueJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                Intensity: reader.IsDBNull(11) ? null : reader.GetDouble(11),
                Confidence: reader.GetDouble(12),
                EvidenceStart: reader.IsDBNull(13) ? null : reader.GetInt32(13),
                EvidenceEnd: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                Explanation: reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return builders.Values
            .Where(node => node.Observations.Count > 0)
            .Select(node => new TechniqueSpecimenNode(node.NodeId, node.NodeType, node.Text, node.Observations))
            .ToArray();
    }

    private static async ValueTask PersistSpecimenAsync(
        SqliteConnection connection,
        ReferenceCorpusTechniqueSpecimenRunRequest request,
        ReferenceCorpusTechniqueSpecimenCandidate candidate,
        CancellationToken cancellationToken)
    {
        var specimenId = BuildSpecimenId(request.RunId, candidate.SourceNodeId, candidate.TechniqueFamily);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_technique_specimens
                  (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
                   technique_abstract, trigger_context, transfer_template, transfer_slots_json,
                   effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
                   world_context_dependencies, why_it_works_json, confidence, review_state,
                   validity_state, superseded_by_run_id, mastery_notes, created_at)
                VALUES
                  ($specimen_id, $source_node_id, $source_anchor_id, $analysis_run_id, $technique_family,
                   $technique_abstract, $trigger_context, $transfer_template, $transfer_slots_json,
                   $effect_on_reader, $applicability_conditions, $failure_modes, $anti_patterns,
                   $world_context_dependencies, $why_it_works_json, $confidence, 'unverified',
                   'active', NULL, $mastery_notes, $created_at)
                ON CONFLICT(specimen_id) DO UPDATE SET
                  technique_family = excluded.technique_family,
                  technique_abstract = excluded.technique_abstract,
                  trigger_context = excluded.trigger_context,
                  transfer_template = excluded.transfer_template,
                  transfer_slots_json = excluded.transfer_slots_json,
                  effect_on_reader = excluded.effect_on_reader,
                  applicability_conditions = excluded.applicability_conditions,
                  failure_modes = excluded.failure_modes,
                  anti_patterns = excluded.anti_patterns,
                  world_context_dependencies = excluded.world_context_dependencies,
                  why_it_works_json = excluded.why_it_works_json,
                  confidence = excluded.confidence,
                  validity_state = excluded.validity_state,
                  superseded_by_run_id = excluded.superseded_by_run_id,
                  mastery_notes = excluded.mastery_notes;
                """;
            command.Parameters.AddWithValue("$specimen_id", specimenId);
            command.Parameters.AddWithValue("$source_node_id", candidate.SourceNodeId);
            command.Parameters.AddWithValue("$source_anchor_id", request.AnchorId);
            command.Parameters.AddWithValue("$analysis_run_id", request.RunId);
            command.Parameters.AddWithValue("$technique_family", candidate.TechniqueFamily);
            command.Parameters.AddWithValue("$technique_abstract", candidate.TechniqueAbstract);
            command.Parameters.AddWithValue("$trigger_context", candidate.TriggerContext);
            command.Parameters.AddWithValue("$transfer_template", candidate.TransferTemplate);
            command.Parameters.AddWithValue("$transfer_slots_json", candidate.TransferSlotsJson);
            command.Parameters.AddWithValue("$effect_on_reader", candidate.EffectOnReader);
            command.Parameters.AddWithValue("$applicability_conditions", candidate.ApplicabilityConditionsJson);
            command.Parameters.AddWithValue("$failure_modes", candidate.FailureModesJson);
            command.Parameters.AddWithValue("$anti_patterns", candidate.AntiPatternsJson);
            command.Parameters.AddWithValue("$world_context_dependencies", candidate.WorldContextDependenciesJson is null ? DBNull.Value : candidate.WorldContextDependenciesJson);
            command.Parameters.AddWithValue("$why_it_works_json", candidate.WhyItWorksJson);
            command.Parameters.AddWithValue("$confidence", candidate.Confidence);
            command.Parameters.AddWithValue("$mastery_notes", candidate.MasteryNotes is null ? DBNull.Value : candidate.MasteryNotes);
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(request.StartedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_specimen_evidence WHERE specimen_id = $specimen_id;";
            delete.Parameters.AddWithValue("$specimen_id", specimenId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var observationId in candidate.EvidenceObservationIds)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reference_specimen_evidence
                  (specimen_id, observation_id)
                VALUES
                  ($specimen_id, $observation_id);
                """;
            insert.Parameters.AddWithValue("$specimen_id", specimenId);
            insert.Parameters.AddWithValue("$observation_id", observationId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask UpsertRunAsync(
        SqliteConnection connection,
        ReferenceCorpusTechniqueSpecimenRunRequest request,
        string status,
        int tokensSpent,
        int specimenCount,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ($run_id, $anchor_id, $analyzer_version, $schema_version, $model_provider, $model_id,
               'technique_specimen', $status, NULL, $tokens_spent, NULL, $started_at, $completed_at, $observation_count)
            ON CONFLICT(run_id) DO UPDATE SET
              analyzer_version = excluded.analyzer_version,
              schema_version = excluded.schema_version,
              model_provider = excluded.model_provider,
              model_id = excluded.model_id,
              scope = excluded.scope,
              status = excluded.status,
              tokens_spent = excluded.tokens_spent,
              completed_at = excluded.completed_at,
              observation_count = excluded.observation_count;
            """;
        command.Parameters.AddWithValue("$run_id", request.RunId);
        command.Parameters.AddWithValue("$anchor_id", request.AnchorId);
        command.Parameters.AddWithValue("$analyzer_version", request.AnalyzerVersion);
        command.Parameters.AddWithValue("$schema_version", ReferenceCorpusTechniqueSpecimenSchemaVersions.V1);
        command.Parameters.AddWithValue("$model_provider", request.ModelProvider);
        command.Parameters.AddWithValue("$model_id", request.ModelId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$tokens_spent", tokensSpent);
        command.Parameters.AddWithValue("$started_at", FormatTimestamp(request.StartedAt));
        command.Parameters.AddWithValue("$completed_at", completedAt is null ? DBNull.Value : FormatTimestamp(completedAt.Value));
        command.Parameters.AddWithValue("$observation_count", specimenCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<int> CountSpecimensAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_technique_specimens
            WHERE analysis_run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static ReferenceCorpusTechniqueSpecimenRunResult BuildResult(
        ReferenceCorpusTechniqueSpecimenRunRequest request,
        string status,
        int tokensSpent,
        int specimenCount,
        int processed,
        IReadOnlyList<string> diagnostics)
    {
        return new ReferenceCorpusTechniqueSpecimenRunResult(
            request.RunId,
            status,
            tokensSpent,
            specimenCount,
            processed,
            SanitizeDiagnostics(diagnostics));
    }

    private static IReadOnlyList<string> SanitizeDiagnostics(IReadOnlyList<string> diagnostics)
    {
        const int maxDiagnostics = 50;
        const int maxDiagnosticLength = 1_200;
        if (diagnostics.Count == 0)
        {
            return [];
        }

        var result = new List<string>(Math.Min(diagnostics.Count, maxDiagnostics + 1));
        foreach (var diagnostic in diagnostics.Take(maxDiagnostics))
        {
            var safe = ReferencePayloadSanitizer.RedactSensitiveIdentifier(diagnostic);
            result.Add(safe.Length <= maxDiagnosticLength ? safe : safe[..maxDiagnosticLength].TrimEnd() + "...");
        }

        if (diagnostics.Count > maxDiagnostics)
        {
            result.Add("diagnostics_truncated");
        }

        return result;
    }

    private static string BuildSpecimenId(string runId, string sourceNodeId, string techniqueFamily)
    {
        var key = string.Join('\u001F', runId, sourceNodeId, techniqueFamily);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return "spec_" + hash;
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private sealed record TechniqueSpecimenNode(
        string NodeId,
        string NodeType,
        string Text,
        IReadOnlyList<ReferenceCorpusTechniqueObservationEvidence> Observations);

    private sealed record TechniqueSpecimenNodeBuilder(
        string NodeId,
        string NodeType,
        string Text)
    {
        public List<ReferenceCorpusTechniqueObservationEvidence> Observations { get; } = [];
    }
}
