using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed record ReferenceCorpusFeatureObservationPersistenceRequest(
 string RunId,
 long AnchorId,
 string NodeId,
 string NodeType,
 DateTimeOffset CreatedAt,
 IReadOnlyList<ReferenceCorpusFeatureObservationCandidate> Observations);

internal sealed class ReferenceCorpusFeatureObservationPersistence
{
 private const double LowConfidenceThreshold = 0.70;

 public async ValueTask PersistAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusFeatureObservationPersistenceRequest request,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(connection);
 ArgumentNullException.ThrowIfNull(transaction);
 ArgumentNullException.ThrowIfNull(request);
 foreach (var candidate in request.Observations)
 {
 var identity = await ReferenceCorpusObservationWriter.UpsertAsync(
 connection,
 transaction,
 new(
 request.NodeId,
 request.NodeType,
 request.RunId,
 request.AnchorId,
 candidate.FeatureFamily,
 candidate.FeatureKey,
 candidate.ValueKind,
 candidate.ValueText,
 candidate.ValueNum,
 candidate.ValueBool,
 candidate.ValueJson,
 candidate.Intensity,
 candidate.Confidence,
 candidate.EvidenceStart,
 candidate.EvidenceEnd,
 candidate.Explanation,
 candidate.Confidence < LowConfidenceThreshold
 ? ReferenceCorpusFeatureObservationReviewStates.LowConfidence
 : ReferenceCorpusFeatureObservationReviewStates.Unverified,
 "active",
 null,
 request.CreatedAt),
 cancellationToken);
 await SyncSensoryProjectionAsync(connection, transaction, identity.ObservationId, request, candidate, cancellationToken);
 }
 }

 private static async ValueTask SyncSensoryProjectionAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string observationId,
 ReferenceCorpusFeatureObservationPersistenceRequest request,
 ReferenceCorpusFeatureObservationCandidate candidate,
 CancellationToken cancellationToken)
 {
 if (!string.Equals(candidate.FeatureFamily, ReferenceCorpusFeatureFamilies.Sensory, StringComparison.Ordinal)) return;
 await using (var delete = connection.CreateCommand())
 {
 delete.Transaction = transaction;
 delete.CommandText = "DELETE FROM reference_obs_sensory WHERE observation_id=$observation_id;";
 delete.Parameters.AddWithValue("$observation_id", observationId);
 await delete.ExecuteNonQueryAsync(cancellationToken);
 }
 if (string.IsNullOrWhiteSpace(candidate.ValueJson)) return;
 using var document = JsonDocument.Parse(candidate.ValueJson);
 if (document.RootElement.ValueKind != JsonValueKind.Array) return;
 foreach (var item in document.RootElement.EnumerateArray())
 {
 if (!item.TryGetProperty("sense", out var sense) || sense.ValueKind != JsonValueKind.String ||
 !item.TryGetProperty("intensity", out var intensity) || !intensity.TryGetDouble(out var value)) continue;
 await using var insert = connection.CreateCommand();
 insert.Transaction = transaction;
 insert.CommandText = "INSERT INTO reference_obs_sensory(observation_id,node_id,anchor_id,sense,intensity) VALUES($observation_id,$node_id,$anchor_id,$sense,$intensity);";
 insert.Parameters.AddWithValue("$observation_id", observationId);
 insert.Parameters.AddWithValue("$node_id", request.NodeId);
 insert.Parameters.AddWithValue("$anchor_id", request.AnchorId);
 insert.Parameters.AddWithValue("$sense", sense.GetString()!);
 insert.Parameters.AddWithValue("$intensity", value);
 await insert.ExecuteNonQueryAsync(cancellationToken);
 }
 }
}
