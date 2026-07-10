using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceCorpusObservationWriter
{
    public static async ValueTask<ReferenceCorpusObservationIdentity> UpsertAsync(
        SqliteConnection connection,
        ReferenceCorpusFeatureObservation observation,
        CancellationToken cancellationToken)
    {
        return await UpsertAsync(connection, transaction: null, observation, cancellationToken);
    }

    public static async ValueTask<ReferenceCorpusObservationIdentity> UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ReferenceCorpusFeatureObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();

        var identity = ReferenceCorpusObservationIdentity.Create(
            observation.RunId,
            observation.NodeId,
            observation.FeatureFamily,
            observation.FeatureKey,
            observation.EvidenceStart,
            observation.EvidenceEnd);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ($observation_id, $node_id, $node_type, $run_id, $anchor_id, $feature_family, $feature_key,
               $value_kind, $value_text, $value_num, $value_bool, $value_json, $intensity, $confidence,
               $evidence_start, $evidence_end, $explanation, $review_state, $validity_state,
               $superseded_by_run_id, $created_at)
            ON CONFLICT DO UPDATE SET
              node_type = excluded.node_type,
              anchor_id = excluded.anchor_id,
              value_kind = excluded.value_kind,
              value_text = excluded.value_text,
              value_num = excluded.value_num,
              value_bool = excluded.value_bool,
              value_json = excluded.value_json,
              intensity = excluded.intensity,
              confidence = excluded.confidence,
 explanation = excluded.explanation, validity_state = excluded.validity_state,
              superseded_by_run_id = excluded.superseded_by_run_id;
""";
        command.Parameters.AddWithValue("$observation_id", identity.ObservationId);
        command.Parameters.AddWithValue("$node_id", observation.NodeId);
        command.Parameters.AddWithValue("$node_type", observation.NodeType);
        command.Parameters.AddWithValue("$run_id", observation.RunId);
        command.Parameters.AddWithValue("$anchor_id", observation.AnchorId);
        command.Parameters.AddWithValue("$feature_family", observation.FeatureFamily);
        command.Parameters.AddWithValue("$feature_key", observation.FeatureKey);
        command.Parameters.AddWithValue("$value_kind", observation.ValueKind);
        command.Parameters.AddWithValue("$value_text", DbValue(observation.ValueText));
        command.Parameters.AddWithValue("$value_num", DbValue(observation.ValueNum));
        command.Parameters.AddWithValue("$value_bool", observation.ValueBool is null ? DBNull.Value : observation.ValueBool.Value ? 1 : 0);
        command.Parameters.AddWithValue("$value_json", DbValue(observation.ValueJson));
        command.Parameters.AddWithValue("$intensity", DbValue(observation.Intensity));
        command.Parameters.AddWithValue("$confidence", observation.Confidence);
        command.Parameters.AddWithValue("$evidence_start", DbValue(observation.EvidenceStart));
        command.Parameters.AddWithValue("$evidence_end", DbValue(observation.EvidenceEnd));
        command.Parameters.AddWithValue("$explanation", DbValue(observation.Explanation));
        command.Parameters.AddWithValue("$review_state", observation.ReviewState);
        command.Parameters.AddWithValue("$validity_state", observation.ValidityState);
        command.Parameters.AddWithValue("$superseded_by_run_id", DbValue(observation.SupersededByRunId));
        command.Parameters.AddWithValue("$created_at", observation.CreatedAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return identity;
    }

    private static object DbValue(string? value)
    {
        return value is null ? DBNull.Value : value;
    }

    private static object DbValue(double? value)
    {
        return value is null ? DBNull.Value : value.Value;
    }

    private static object DbValue(int? value)
    {
        return value is null ? DBNull.Value : value.Value;
    }
}

internal sealed record ReferenceCorpusFeatureObservation(
    string NodeId,
    string NodeType,
    string RunId,
    long AnchorId,
    string FeatureFamily,
    string FeatureKey,
    string ValueKind,
    string? ValueText,
    double? ValueNum,
    bool? ValueBool,
    string? ValueJson,
    double? Intensity,
    double Confidence,
    int? EvidenceStart,
    int? EvidenceEnd,
    string? Explanation,
    string ReviewState,
    string ValidityState,
    string? SupersededByRunId,
    DateTimeOffset CreatedAt);
