using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

// 生产侧明细表 → 统一外壳表的投影。旧行保留原 feature_key，family 按固定映射归并，
// 并以 extractor_version 标记来源；证据无法解析的行整条作废并计入 InvalidEvidenceCount。
public sealed class SqliteReferenceAdvancedMaterialProjectionService : IReferenceAdvancedMaterialProjectionService
{
    internal const string ProjectionExtractorVersion = "legacy-projection-v1";

    // 旧 feature family → 新五类。style 由风格画像提供，不在此投影。
    private static readonly IReadOnlyDictionary<string, string> FamilyMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["syntax"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["rhythm"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["sensory"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["emotion"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["rhetoric"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["narrative"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["pov"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["action"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["character"] = ReferenceAdvancedMaterialFamilies.Craft,
        ["commercial"] = ReferenceAdvancedMaterialFamilies.Structure,
        ["scene"] = ReferenceAdvancedMaterialFamilies.Structure,
        ["trope"] = ReferenceAdvancedMaterialFamilies.Structure
    };

    private readonly IReferenceCorpusDatabasePathResolver _pathResolver;

    public SqliteReferenceAdvancedMaterialProjectionService(IReferenceCorpusDatabasePathResolver pathResolver)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    public async ValueTask<ReferenceAdvancedMaterialProjectionResult> ProjectAnchorAsync(
        long anchorId,
        CancellationToken cancellationToken)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var nodeLengths = await ReadNodeLengthsAsync(connection, transaction, anchorId, cancellationToken);

        var (observations, invalidObservations) = await ReadObservationsAsync(
            connection, transaction, anchorId, nodeLengths, cancellationToken);
        var (specimens, invalidSpecimens) = await ReadSpecimensAsync(
            connection, transaction, anchorId, nodeLengths, cancellationToken);

        foreach (var row in observations)
        {
            await UpsertAsync(connection, transaction, row, cancellationToken);
        }

        foreach (var row in specimens)
        {
            await UpsertAsync(connection, transaction, row, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReferenceAdvancedMaterialProjectionResult(
            observations.Count,
            specimens.Count,
            invalidObservations + invalidSpecimens);
    }

    private static async ValueTask<Dictionary<string, int>> ReadNodeLengthsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT node_id, char_len FROM reference_text_nodes WHERE anchor_id = $anchor_id;";
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lengths[reader.GetString(0)] = reader.GetInt32(1);
        }

        return lengths;
    }

    private static async ValueTask<(List<ShellRow> Rows, int InvalidEvidence)> ReadObservationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        Dictionary<string, int> nodeLengths,
        CancellationToken cancellationToken)
    {
        var invalidEvidence = 0;
        var rows = new List<ShellRow>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT observation_id, node_id, feature_family, feature_key, value_text, value_json,
                   confidence, evidence_start, evidence_end, explanation, review_state,
                   superseded_by_run_id, run_id, created_at
            FROM reference_feature_observations
            WHERE anchor_id = $anchor_id AND validity_state = 'active';
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var observationId = reader.GetString(0);
            var nodeId = reader.GetString(1);
            var legacyFamily = reader.GetString(2);
            if (!FamilyMap.TryGetValue(legacyFamily, out var family))
            {
                continue;
            }

            var featureKey = reader.GetString(3);
            var valueText = reader.IsDBNull(4) ? null : reader.GetString(4);
            var valueJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            var confidence = reader.GetDouble(6);
            var start = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
            var end = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8);
            var explanation = reader.IsDBNull(9) ? null : reader.GetString(9);
            var reviewState = reader.GetString(10);
            var supersededBy = reader.IsDBNull(11) ? null : reader.GetString(11);
            var runId = reader.GetString(12);
            var createdAt = reader.GetString(13);

            if (!IsValidEvidence(nodeId, start, end, nodeLengths))
            {
                invalidEvidence++;
                continue;
            }

            rows.Add(new ShellRow(
                MaterialId: BuildMaterialId(anchorId, ReferenceAdvancedMaterialLayers.Observation, family, featureKey, observationId),
                AnchorId: anchorId,
                Layer: ReferenceAdvancedMaterialLayers.Observation,
                Family: family,
                FeatureKey: featureKey,
                SourceRef: observationId,
                ValueText: valueText,
                ValueJson: BuildObservationValueJson(valueJson, explanation),
                RationaleJson: null,
                BoundaryJson: null,
                TransferTemplate: null,
                TransferSlotsJson: null,
                EvidenceRefsJson: BuildEvidenceRefsJson(nodeId, start!.Value, end!.Value),
                Confidence: ClampConfidence(confidence),
                ReviewState: MapReviewState(reviewState),
                ValidityState: ReferenceAdvancedMaterialValidityStates.Active,
                SupersededByRunId: supersededBy,
                AnalysisRunId: runId,
                CreatedAt: createdAt));
        }

        return (rows, invalidEvidence);
    }

    private static async ValueTask<(List<ShellRow> Rows, int InvalidEvidence)> ReadSpecimensAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        Dictionary<string, int> nodeLengths,
        CancellationToken cancellationToken)
    {
        var invalidEvidence = 0;
        var rows = new List<ShellRow>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT specimen_id, source_node_id, technique_family, technique_abstract, trigger_context,
                   transfer_template, transfer_slots_json, effect_on_reader, applicability_conditions,
                   failure_modes, anti_patterns, world_context_dependencies, why_it_works_json,
                   confidence, review_state, superseded_by_run_id, analysis_run_id, mastery_notes, created_at
            FROM reference_technique_specimens
            WHERE source_anchor_id = $anchor_id AND validity_state = 'active';
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var specimenId = reader.GetString(0);
            var nodeId = reader.GetString(1);
            var techniqueFamily = reader.GetString(2);
            var techniqueAbstract = reader.GetString(3);
            var triggerContext = reader.GetString(4);
            var transferTemplate = reader.GetString(5);
            var transferSlotsJson = reader.GetString(6);
            var effectOnReader = reader.GetString(7);
            var applicabilityConditions = reader.GetString(8);
            var failureModes = reader.GetString(9);
            var antiPatterns = reader.GetString(10);
            var worldContextDependencies = reader.IsDBNull(11) ? null : reader.GetString(11);
            var whyItWorksJson = reader.GetString(12);
            var confidence = reader.GetDouble(13);
            var reviewState = reader.GetString(14);
            var supersededBy = reader.IsDBNull(15) ? null : reader.GetString(15);
            var runId = reader.GetString(16);
            var masteryNotes = reader.IsDBNull(17) ? null : reader.GetString(17);
            var createdAt = reader.GetString(18);

            if (!nodeLengths.TryGetValue(nodeId, out var nodeLength) || nodeLength <= 0)
            {
                invalidEvidence++;
                continue;
            }

            rows.Add(new ShellRow(
                MaterialId: BuildMaterialId(anchorId, ReferenceAdvancedMaterialLayers.Specimen, ReferenceAdvancedMaterialFamilies.Technique, techniqueFamily, specimenId),
                AnchorId: anchorId,
                Layer: ReferenceAdvancedMaterialLayers.Specimen,
                Family: ReferenceAdvancedMaterialFamilies.Technique,
                FeatureKey: techniqueFamily,
                SourceRef: specimenId,
                ValueText: techniqueAbstract,
                ValueJson: BuildSpecimenValueJson(triggerContext, masteryNotes),
                RationaleJson: BuildSpecimenRationale(whyItWorksJson, effectOnReader),
                BoundaryJson: BuildSpecimenBoundary(worldContextDependencies, failureModes, antiPatterns, applicabilityConditions),
                TransferTemplate: transferTemplate,
                TransferSlotsJson: transferSlotsJson,
                EvidenceRefsJson: BuildEvidenceRefsJson(nodeId, 0, nodeLength),
                Confidence: ClampConfidence(confidence),
                ReviewState: MapReviewState(reviewState),
                ValidityState: ReferenceAdvancedMaterialValidityStates.Active,
                SupersededByRunId: supersededBy,
                AnalysisRunId: runId,
                CreatedAt: createdAt));
        }

        return (rows, invalidEvidence);
    }

    private static async ValueTask UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ShellRow row,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_advanced_materials
              (material_id, anchor_id, layer, family, feature_key, source_ref, value_text, value_json,
               rationale_json, boundary_json, transfer_template, transfer_slots_json, evidence_refs_json,
               confidence, review_state, validity_state, superseded_by_run_id, analysis_run_id,
               extractor_version, created_at, updated_at)
            VALUES
              ($material_id, $anchor_id, $layer, $family, $feature_key, $source_ref, $value_text, $value_json,
               $rationale_json, $boundary_json, $transfer_template, $transfer_slots_json, $evidence_refs_json,
               $confidence, $review_state, $validity_state, $superseded_by_run_id, $analysis_run_id,
               $extractor_version, $created_at, $updated_at)
            ON CONFLICT(anchor_id, layer, family, feature_key, source_ref) DO UPDATE SET
              value_text = excluded.value_text,
              value_json = excluded.value_json,
              rationale_json = excluded.rationale_json,
              boundary_json = excluded.boundary_json,
              transfer_template = excluded.transfer_template,
              transfer_slots_json = excluded.transfer_slots_json,
              evidence_refs_json = excluded.evidence_refs_json,
              confidence = excluded.confidence,
              review_state = excluded.review_state,
              validity_state = excluded.validity_state,
              superseded_by_run_id = excluded.superseded_by_run_id,
              analysis_run_id = excluded.analysis_run_id,
              extractor_version = excluded.extractor_version,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$material_id", row.MaterialId);
        command.Parameters.AddWithValue("$anchor_id", row.AnchorId);
        command.Parameters.AddWithValue("$layer", row.Layer);
        command.Parameters.AddWithValue("$family", row.Family);
        command.Parameters.AddWithValue("$feature_key", row.FeatureKey);
        command.Parameters.AddWithValue("$source_ref", row.SourceRef);
        AddNullable(command, "$value_text", row.ValueText);
        AddNullable(command, "$value_json", row.ValueJson);
        AddNullable(command, "$rationale_json", row.RationaleJson);
        AddNullable(command, "$boundary_json", row.BoundaryJson);
        AddNullable(command, "$transfer_template", row.TransferTemplate);
        AddNullable(command, "$transfer_slots_json", row.TransferSlotsJson);
        command.Parameters.AddWithValue("$evidence_refs_json", row.EvidenceRefsJson);
        command.Parameters.AddWithValue("$confidence", row.Confidence);
        command.Parameters.AddWithValue("$review_state", row.ReviewState);
        command.Parameters.AddWithValue("$validity_state", row.ValidityState);
        AddNullable(command, "$superseded_by_run_id", row.SupersededByRunId);
        AddNullable(command, "$analysis_run_id", row.AnalysisRunId);
        command.Parameters.AddWithValue("$extractor_version", ProjectionExtractorVersion);
        command.Parameters.AddWithValue("$created_at", row.CreatedAt);
        command.Parameters.AddWithValue("$updated_at", row.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsValidEvidence(
        string nodeId,
        int? start,
        int? end,
        Dictionary<string, int> nodeLengths)
    {
        return start is >= 0 &&
            end is not null &&
            end > start &&
            nodeLengths.TryGetValue(nodeId, out var nodeLength) &&
            end <= nodeLength;
    }

    private static string BuildEvidenceRefsJson(string nodeId, int start, int end)
    {
        var evidence = new JsonArray
        {
            new JsonObject
            {
                ["node_id"] = nodeId,
                ["material_id"] = null,
                ["start_offset"] = start,
                ["end_offset"] = end
            }
        };
        return evidence.ToJsonString();
    }

    private static string? BuildObservationValueJson(string? valueJson, string? explanation)
    {
        JsonObject payload;
        if (!string.IsNullOrWhiteSpace(valueJson))
        {
            try
            {
                payload = JsonNode.Parse(valueJson) as JsonObject ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException)
            {
                payload = new JsonObject();
            }
        }
        else
        {
            payload = new JsonObject();
        }

        if (!string.IsNullOrWhiteSpace(explanation))
        {
            payload["explanation"] ??= explanation;
        }

        return payload.Count == 0 ? null : payload.ToJsonString();
    }

    private static string BuildSpecimenValueJson(string triggerContext, string? masteryNotes)
    {
        var payload = new JsonObject
        {
            ["trigger_context"] = triggerContext
        };
        if (!string.IsNullOrWhiteSpace(masteryNotes))
        {
            payload["mastery_notes"] = masteryNotes;
        }

        return payload.ToJsonString();
    }

    private static string BuildSpecimenRationale(string whyItWorksJson, string effectOnReader)
    {
        var payload = new JsonObject
        {
            ["why_it_works"] = ParseOrText(whyItWorksJson),
            ["effect_on_reader"] = effectOnReader
        };
        return payload.ToJsonString();
    }

    private static string BuildSpecimenBoundary(
        string? worldContextDependencies,
        string failureModes,
        string antiPatterns,
        string applicabilityConditions)
    {
        var payload = new JsonObject
        {
            ["world_context_dependencies"] = ParseOrText(worldContextDependencies),
            ["failure_modes"] = ParseOrText(failureModes),
            ["anti_patterns"] = ParseOrText(antiPatterns),
            ["applicability_conditions"] = ParseOrText(applicabilityConditions)
        };
        return payload.ToJsonString();
    }

    private static JsonNode? ParseOrText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(value);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    private static string MapReviewState(string reviewState) => reviewState switch
    {
        ReferenceAdvancedMaterialReviewStates.Confirmed => ReferenceAdvancedMaterialReviewStates.Confirmed,
        ReferenceAdvancedMaterialReviewStates.Rejected => ReferenceAdvancedMaterialReviewStates.Rejected,
        _ => ReferenceAdvancedMaterialReviewStates.Unverified
    };

    private static double ClampConfidence(double confidence) =>
        Math.Round(Math.Clamp(confidence, 0, 0.95), 4);

    private static string BuildMaterialId(long anchorId, string layer, string family, string featureKey, string sourceRef)
    {
        var raw = $"{anchorId}|{layer}|{family}|{featureKey}|{sourceRef}";
        ulong hash = 14695981039346656037UL;
        foreach (var character in raw)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return $"advm-{hash:x16}";
    }

    private static void AddNullable(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var databasePath = await _pathResolver.ResolveAsync(cancellationToken);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        return connection;
    }

    private sealed record ShellRow(
        string MaterialId,
        long AnchorId,
        string Layer,
        string Family,
        string FeatureKey,
        string SourceRef,
        string? ValueText,
        string? ValueJson,
        string? RationaleJson,
        string? BoundaryJson,
        string? TransferTemplate,
        string? TransferSlotsJson,
        string EvidenceRefsJson,
        double Confidence,
        string ReviewState,
        string ValidityState,
        string? SupersededByRunId,
        string? AnalysisRunId,
        string CreatedAt);
}
