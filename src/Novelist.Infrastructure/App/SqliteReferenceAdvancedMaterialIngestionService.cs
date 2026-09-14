using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

// 抽取落库：草稿按冻结词表 + 证据边界校验，通过才写壳；拒绝项只计数不落库。
public sealed class SqliteReferenceAdvancedMaterialIngestionService : IReferenceAdvancedMaterialIngestionService
{
    internal const string ObservationExtractorVersion = "advanced-material-observation-v1";
    internal const string SpecimenExtractorVersion = "advanced-material-specimen-v1";

    private readonly IReferenceCorpusDatabasePathResolver _pathResolver;

    public SqliteReferenceAdvancedMaterialIngestionService(IReferenceCorpusDatabasePathResolver pathResolver)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    public async ValueTask<AdvancedMaterialIngestionResult> IngestObservationsAsync(
        long anchorId,
        string runId,
        IReadOnlyList<AdvancedMaterialObservationDraft> drafts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ValidateAnchorAndRun(anchorId, runId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var nodeLengths = await ReadNodeLengthsAsync(connection, anchorId, cancellationToken);
        var nodeTexts = await ReadNodeTextsAsync(connection, anchorId, cancellationToken);

        var accepted = 0;
        var rejected = 0;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var draft in drafts)
        {
            if (!IsValidObservation(draft, nodeLengths, nodeTexts))
            {
                rejected++;
                continue;
            }

            var evidence = draft.Evidence.Select(item => (item.NodeId, item.StartOffset, item.EndOffset)).ToArray();
            var sourceRef = BuildSourceRef(draft.Family, draft.FeatureKey, draft.Value, DescribeEvidence(evidence));
            await UpsertAsync(
                connection,
                transaction,
                new ShellRow(
                    MaterialId: BuildMaterialId(anchorId, ReferenceAdvancedMaterialLayers.Observation, draft.Family, draft.FeatureKey, sourceRef),
                    AnchorId: anchorId,
                    Layer: ReferenceAdvancedMaterialLayers.Observation,
                    Family: draft.Family,
                    FeatureKey: draft.FeatureKey,
                    SourceRef: sourceRef,
                    ValueText: draft.Value,
                    ValueJson: BuildObservationValueJson(draft.Explanation),
                    RationaleJson: null,
                    BoundaryJson: null,
                    TransferTemplate: null,
                    TransferSlotsJson: null,
                    EvidenceRefsJson: BuildEvidenceRefsJson(evidence),
                    Confidence: ClampConfidence(draft.Confidence),
                    ReviewState: ReferenceAdvancedMaterialReviewStates.Unverified,
                    ValidityState: ReferenceAdvancedMaterialValidityStates.Active,
                    AnalysisRunId: runId,
                    ExtractorVersion: ObservationExtractorVersion),
                cancellationToken);
            accepted++;
        }

        await transaction.CommitAsync(cancellationToken);
        return new AdvancedMaterialIngestionResult(accepted, rejected);
    }

    public async ValueTask<AdvancedMaterialIngestionResult> IngestSpecimensAsync(
        long anchorId,
        string runId,
        IReadOnlyList<AdvancedMaterialSpecimenDraft> drafts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ValidateAnchorAndRun(anchorId, runId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var nodeLengths = await ReadNodeLengthsAsync(connection, anchorId, cancellationToken);
        var nodeTexts = await ReadNodeTextsAsync(connection, anchorId, cancellationToken);

        var accepted = 0;
        var rejected = 0;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var draft in drafts)
        {
            if (!IsValidSpecimen(draft, nodeLengths, nodeTexts))
            {
                rejected++;
                continue;
            }

            var evidence = draft.Evidence.Select(item => (item.NodeId, item.StartOffset, item.EndOffset)).ToArray();
            var sourceRef = BuildSourceRef(draft.Family, draft.FeatureKey, draft.Abstract, draft.TransferTemplate);
            await UpsertAsync(
                connection,
                transaction,
                new ShellRow(
                    MaterialId: BuildMaterialId(anchorId, ReferenceAdvancedMaterialLayers.Specimen, draft.Family, draft.FeatureKey, sourceRef),
                    AnchorId: anchorId,
                    Layer: ReferenceAdvancedMaterialLayers.Specimen,
                    Family: draft.Family,
                    FeatureKey: draft.FeatureKey,
                    SourceRef: sourceRef,
                    ValueText: draft.Abstract,
                    ValueJson: new JsonObject { ["trigger_context"] = draft.TriggerContext }.ToJsonString(),
                    RationaleJson: new JsonObject
                    {
                        ["why_it_works"] = new JsonArray(draft.WhyItWorks.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
                        ["effect_on_reader"] = draft.EffectOnReader
                    }.ToJsonString(),
                    BoundaryJson: new JsonObject
                    {
                        ["world_context_dependencies"] = new JsonArray(draft.WorldContextDependencies.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
                        ["failure_modes"] = new JsonArray(draft.FailureModes.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
                        ["anti_patterns"] = new JsonArray(draft.AntiPatterns.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray())
                    }.ToJsonString(),
                    TransferTemplate: draft.TransferTemplate,
                    TransferSlotsJson: new JsonObject(
                        draft.TransferSlots.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))).ToJsonString(),
                    EvidenceRefsJson: BuildEvidenceRefsJson(evidence),
                    Confidence: ClampConfidence(draft.Confidence),
                    ReviewState: ReferenceAdvancedMaterialReviewStates.Unverified,
                    ValidityState: ReferenceAdvancedMaterialValidityStates.Active,
                    AnalysisRunId: runId,
                    ExtractorVersion: SpecimenExtractorVersion),
                cancellationToken);
            accepted++;
        }

        await transaction.CommitAsync(cancellationToken);
        return new AdvancedMaterialIngestionResult(accepted, rejected);
    }

    private static bool IsValidObservation(
        AdvancedMaterialObservationDraft draft,
        Dictionary<string, int> nodeLengths,
        Dictionary<string, string> nodeTexts)
    {
        return draft is not null &&
            ReferenceAdvancedMaterialFeatureVocabulary.IsSupportedValue(draft.Family, draft.FeatureKey, draft.Value) &&
            IsConfidenceInRange(draft.Confidence) &&
            HasValidEvidence(draft.Evidence, nodeLengths) &&
            PassesLeakCheck(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["value"] = draft.Value,
                    ["explanation"] = draft.Explanation
                },
                draft.Evidence,
                nodeTexts);
    }

    private static bool IsValidSpecimen(
        AdvancedMaterialSpecimenDraft draft,
        Dictionary<string, int> nodeLengths,
        Dictionary<string, string> nodeTexts)
    {
        return draft is not null &&
            ReferenceAdvancedMaterialFeatureVocabulary.IsSupportedFeatureKey(draft.Family, draft.FeatureKey) &&
            IsConfidenceInRange(draft.Confidence) &&
            !string.IsNullOrWhiteSpace(draft.Abstract) &&
            !string.IsNullOrWhiteSpace(draft.TransferTemplate) &&
            draft.WhyItWorks.Count > 0 &&
            draft.WorldContextDependencies.Count > 0 &&
            draft.FailureModes.Count > 0 &&
            draft.AntiPatterns.Count > 0 &&
            HasValidEvidence(draft.Evidence, nodeLengths) &&
            PassesLeakCheck(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["abstract"] = draft.Abstract,
                    ["transfer_template"] = draft.TransferTemplate,
                    ["trigger_context"] = draft.TriggerContext,
                    ["effect_on_reader"] = draft.EffectOnReader,
                    ["why_it_works"] = string.Join(' ', draft.WhyItWorks),
                    ["boundary"] = string.Join(
                        ' ',
                        draft.WorldContextDependencies.Concat(draft.FailureModes).Concat(draft.AntiPatterns)),
                    ["transfer_slots"] = string.Join(' ', draft.TransferSlots.Values)
                },
                draft.Evidence,
                nodeTexts);
    }

    // 防直搬：机理/策略字段不得与证据原文共享超阈连续片段（evidence 本身是引用，不校验）。
    private static bool PassesLeakCheck(
        IReadOnlyDictionary<string, string?> fields,
        IReadOnlyList<AdvancedMaterialEvidenceDraft> evidence,
        Dictionary<string, string> nodeTexts)
    {
        if (evidence is null || evidence.Count == 0 || nodeTexts.Count == 0)
        {
            return true;
        }

        var sources = evidence
            .Select(item => nodeTexts.TryGetValue(item.NodeId, out var text) ? text : null)
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToArray();
        return AdvancedMaterialLeakValidator.Validate(fields, sources).Count == 0;
    }

    private static bool HasValidEvidence(
        IReadOnlyList<AdvancedMaterialEvidenceDraft> evidence,
        Dictionary<string, int> nodeLengths)
    {
        if (evidence is null || evidence.Count == 0)
        {
            return false;
        }

        foreach (var item in evidence)
        {
            if (item.StartOffset < 0 ||
                item.EndOffset <= item.StartOffset ||
                !nodeLengths.TryGetValue(item.NodeId, out var nodeLength) ||
                item.EndOffset > nodeLength)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsConfidenceInRange(double confidence) =>
        !double.IsNaN(confidence) && !double.IsInfinity(confidence) && confidence >= 0 && confidence <= 0.95;

    private static string? BuildObservationValueJson(string? explanation) =>
        string.IsNullOrWhiteSpace(explanation) ? null : new JsonObject { ["explanation"] = explanation }.ToJsonString();

    private static string DescribeEvidence(IReadOnlyList<(string NodeId, int Start, int End)> evidence) =>
        string.Join(',', evidence.Select(item => $"{item.NodeId}:{item.Start}-{item.End}"));

    private static string BuildEvidenceRefsJson(IReadOnlyList<(string NodeId, int Start, int End)> evidence)
    {
        var array = new JsonArray();
        foreach (var (nodeId, start, end) in evidence)
        {
            array.Add(new JsonObject
            {
                ["node_id"] = nodeId,
                ["material_id"] = null,
                ["start_offset"] = start,
                ["end_offset"] = end
            });
        }

        return array.ToJsonString();
    }

    private static string BuildSourceRef(params string?[] parts) =>
        Hash(string.Join('\u001e', parts.Select(part => part ?? string.Empty)));

    private static string BuildMaterialId(long anchorId, string layer, string family, string featureKey, string sourceRef) =>
        $"advm-{Hash($"{anchorId}|{layer}|{family}|{featureKey}|{sourceRef}")}";

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

    private static void ValidateAnchorAndRun(long anchorId, string runId)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId));
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("run_id is required.", nameof(runId));
        }
    }

    private static async ValueTask<Dictionary<string, int>> ReadNodeLengthsAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT node_id, char_len FROM reference_text_nodes WHERE anchor_id = $anchor_id;";
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lengths[reader.GetString(0)] = reader.GetInt32(1);
        }

        return lengths;
    }

    private static async ValueTask<Dictionary<string, string>> ReadNodeTextsAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT node_id, text FROM reference_text_nodes WHERE anchor_id = $anchor_id;";
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            texts[reader.GetString(0)] = reader.GetString(1);
        }

        return texts;
    }

    private static async ValueTask UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ShellRow row,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
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
               $confidence, $review_state, $validity_state, NULL, $analysis_run_id,
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
        command.Parameters.AddWithValue("$analysis_run_id", row.AnalysisRunId);
        command.Parameters.AddWithValue("$extractor_version", row.ExtractorVersion);
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        string? AnalysisRunId,
        string ExtractorVersion);
}
