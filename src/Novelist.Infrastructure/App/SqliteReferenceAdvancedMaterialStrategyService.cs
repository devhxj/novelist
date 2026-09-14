using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

// 书级策略聚合（确定性）：按 (family, feature_key, layer) 归并观测/机理行，
// 取众数（观测）或最高置信（机理）作为主导结论，证据为来源行证据并集。可重复运行（按 source_ref 幂等）。
public sealed class SqliteReferenceAdvancedMaterialStrategyService : IReferenceAdvancedMaterialStrategyService
{
    private const int MaxEvidenceRefs = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReferenceCorpusDatabasePathResolver _pathResolver;

    public SqliteReferenceAdvancedMaterialStrategyService(IReferenceCorpusDatabasePathResolver pathResolver)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    public async ValueTask<ReferenceAdvancedMaterialStrategyResult> AggregateStrategyAsync(
        long anchorId,
        CancellationToken cancellationToken)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sources = await ReadSourceRowsAsync(connection, anchorId, cancellationToken);

        var groups = sources
            .GroupBy(row => (row.Family, row.FeatureKey, row.Layer))
            .ToArray();

        var evidenceRefCount = 0;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var group in groups)
        {
            var rows = group.ToArray();
            var dominant = SelectDominant(rows);
            var evidence = MergeEvidence(rows, out var addedEvidence);
            evidenceRefCount += addedEvidence;

            var sourceRef = BuildSourceRef(group.Key.Family, group.Key.FeatureKey, group.Key.Layer);
            await UpsertAsync(
                connection,
                transaction,
                anchorId,
                group.Key.Family,
                group.Key.FeatureKey,
                sourceRef,
                dominant.ValueText,
                BuildStrategyValueJson(group.Key.Layer, rows, dominant),
                evidence,
                ClampConfidence(rows.Average(row => row.Confidence)),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReferenceAdvancedMaterialStrategyResult(groups.Length, evidenceRefCount);
    }

    private static Dominant SelectDominant(SourceRow[] rows)
    {
        if (string.Equals(rows[0].Layer, ReferenceAdvancedMaterialLayers.Observation, StringComparison.Ordinal))
        {
            var topObservation = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ValueText))
                .GroupBy(row => row.ValueText!, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new Dominant(group.Key, group.Count()))
                .FirstOrDefault();
            return topObservation.ValueText is null ? new Dominant(string.Empty, 0) : topObservation;
        }

        var top = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ValueText))
            .OrderByDescending(row => row.Confidence)
            .ThenBy(row => row.MaterialId, StringComparer.Ordinal)
            .FirstOrDefault();
        return new Dominant(top?.ValueText ?? string.Empty, rows.Length);
    }

    private static string BuildStrategyValueJson(string layer, SourceRow[] rows, Dominant dominant)
    {
        var payload = new JsonObject
        {
            ["source_layer"] = layer,
            ["total"] = rows.Length,
            ["dominant"] = dominant.ValueText,
            ["dominant_count"] = dominant.Count
        };

        if (string.Equals(layer, ReferenceAdvancedMaterialLayers.Observation, StringComparison.Ordinal))
        {
            var distribution = new JsonObject();
            foreach (var item in rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ValueText))
                .GroupBy(row => row.ValueText!, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                distribution[item.Key] = item.Count();
            }

            payload["distribution"] = distribution;
        }

        return payload.ToJsonString();
    }

    private static string MergeEvidence(SourceRow[] rows, out int addedEvidence)
    {
        var merged = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var reference in ParseEvidence(row.EvidenceRefsJson))
            {
                var key = $"{reference.NodeId}|{reference.StartOffset}|{reference.EndOffset}";
                if (!seen.Add(key))
                {
                    continue;
                }

                merged.Add(new JsonObject
                {
                    ["node_id"] = reference.NodeId,
                    ["material_id"] = reference.MaterialId,
                    ["start_offset"] = reference.StartOffset,
                    ["end_offset"] = reference.EndOffset
                });

                if (merged.Count >= MaxEvidenceRefs)
                {
                    addedEvidence = merged.Count;
                    return merged.ToJsonString();
                }
            }
        }

        addedEvidence = merged.Count;
        return merged.ToJsonString();
    }

    private static IReadOnlyList<EvidenceRef> ParseEvidence(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<EvidenceRef>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async ValueTask<SourceRow[]> ReadSourceRowsAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var rows = new List<SourceRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id, layer, family, feature_key, value_text, confidence, evidence_refs_json
            FROM reference_advanced_materials
            WHERE anchor_id = $anchor_id
              AND validity_state = 'active'
              AND layer IN ('observation', 'specimen');
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SourceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDouble(5),
                reader.GetString(6)));
        }

        return rows.ToArray();
    }

    private static async ValueTask UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        string family,
        string featureKey,
        string sourceRef,
        string? valueText,
        string valueJson,
        string evidenceRefsJson,
        double confidence,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var materialId = $"advm-{Hash($"{anchorId}|{ReferenceAdvancedMaterialLayers.Strategy}|{family}|{featureKey}|{sourceRef}")}";
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
               NULL, NULL, NULL, NULL, $evidence_refs_json,
               $confidence, $review_state, $validity_state, NULL, NULL,
               $extractor_version, $created_at, $updated_at)
            ON CONFLICT(anchor_id, layer, family, feature_key, source_ref) DO UPDATE SET
              value_text = excluded.value_text,
              value_json = excluded.value_json,
              evidence_refs_json = excluded.evidence_refs_json,
              confidence = excluded.confidence,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$material_id", materialId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$layer", ReferenceAdvancedMaterialLayers.Strategy);
        command.Parameters.AddWithValue("$family", family);
        command.Parameters.AddWithValue("$feature_key", featureKey);
        command.Parameters.AddWithValue("$source_ref", sourceRef);
        command.Parameters.AddWithValue("$value_text", (object?)valueText ?? DBNull.Value);
        command.Parameters.AddWithValue("$value_json", valueJson);
        command.Parameters.AddWithValue("$evidence_refs_json", evidenceRefsJson);
        command.Parameters.AddWithValue("$confidence", confidence);
        command.Parameters.AddWithValue("$review_state", ReferenceAdvancedMaterialReviewStates.Unverified);
        command.Parameters.AddWithValue("$validity_state", ReferenceAdvancedMaterialValidityStates.Active);
        command.Parameters.AddWithValue("$extractor_version", "advanced-material-strategy-v1");
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildSourceRef(params string?[] parts) =>
        Hash(string.Join('\u001e', parts.Select(part => part ?? string.Empty)));

    private static string Hash(string raw)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var character in raw)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16");
    }

    private static double ClampConfidence(double confidence) =>
        Math.Round(Math.Clamp(confidence, 0, 0.95), 4);

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

    private sealed record SourceRow(
        string MaterialId,
        string Layer,
        string Family,
        string FeatureKey,
        string? ValueText,
        double Confidence,
        string EvidenceRefsJson);

    private sealed record EvidenceRef(
        [property: System.Text.Json.Serialization.JsonPropertyName("node_id")] string NodeId,
        [property: System.Text.Json.Serialization.JsonPropertyName("material_id")] string? MaterialId,
        [property: System.Text.Json.Serialization.JsonPropertyName("start_offset")] int StartOffset,
        [property: System.Text.Json.Serialization.JsonPropertyName("end_offset")] int EndOffset);

    private readonly record struct Dominant(string? ValueText, int Count);
}
