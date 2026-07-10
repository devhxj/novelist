using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceCorpusGovernanceService
{
 public async ValueTask<int> RefreshReviewQueueAsync(RefreshReferenceCorpusReviewQueuePayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.ConfidenceThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(input.ConfidenceThreshold));
 return await LockedAsync(async connection =>
 {
 var inserted = await ExecuteAsync(connection,
 "INSERT INTO reference_review_queue(queue_id,item_type,item_id,anchor_id,node_id,reason,review_state,confidence,feature_family,created_at) SELECT 'review-' || lower(hex(randomblob(16))),'observation',observation_id,anchor_id,node_id,'low_confidence',review_state,confidence,feature_family,$time FROM reference_feature_observations WHERE validity_state='active' AND review_state IN ('unverified','low_confidence') AND confidence < $threshold ON CONFLICT(item_type,item_id,reason) DO NOTHING;",
 cancellationToken, ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$threshold", input.ConfidenceThreshold));
 inserted += await ExecuteAsync(connection,
 "INSERT INTO reference_review_queue(queue_id,item_type,item_id,anchor_id,node_id,reason,review_state,confidence,feature_family,created_at) SELECT 'review-' || lower(hex(randomblob(16))),'observation',latest.observation_id,latest.anchor_id,latest.node_id,'cross_run_conflict','conflicted',latest.confidence,latest.feature_family,$time FROM reference_feature_observations AS latest JOIN reference_feature_observations AS prior ON prior.node_id=latest.node_id AND prior.feature_family=latest.feature_family AND prior.feature_key=latest.feature_key AND prior.run_id<>latest.run_id WHERE latest.validity_state='active' AND prior.validity_state='active' AND COALESCE(latest.value_text,latest.value_json,CAST(latest.value_num AS TEXT),CAST(latest.value_bool AS TEXT),'')<>COALESCE(prior.value_text,prior.value_json,CAST(prior.value_num AS TEXT),CAST(prior.value_bool AS TEXT),'') ON CONFLICT(item_type,item_id,reason) DO NOTHING;",
 cancellationToken, ("$time", DateTimeOffset.UtcNow.ToString("O")));
 return inserted;
 }, cancellationToken);
 }

 public async ValueTask<PageResultPayload<ReferenceCorpusReviewQueueItemPayload>> ListReviewQueueAsync(ListReferenceCorpusReviewQueuePayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 var page = int.TryParse(input.PageRequest.Cursor, out var parsedPage) && parsedPage > 0 ? parsedPage : 1;
 var size = Math.Clamp(input.PageRequest.PageSize <= 0 ? 20 : input.PageRequest.PageSize, 1, 100);
 return await LockedAsync(async connection =>
 {
 var total = await CountPendingAsync(connection, cancellationToken);
 var items = new List<ReferenceCorpusReviewQueueItemPayload>();
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT queue.queue_id,queue.item_type,queue.item_id,queue.anchor_id,queue.node_id,
 queue.reason,queue.review_state,queue.confidence,queue.feature_family,queue.created_at,
 anchor.title,COALESCE(observation.evidence_start,0),COALESCE(observation.evidence_end,node.char_len),
 CASE
 WHEN observation.evidence_start IS NULL OR observation.evidence_end IS NULL THEN substr(node.text,1,160)
 ELSE substr(node.text,MAX(1,observation.evidence_start-39),MIN(160,length(node.text)-MAX(0,observation.evidence_start-40)))
 END
 FROM reference_review_queue AS queue
 LEFT JOIN reference_anchors AS anchor ON anchor.anchor_id=queue.anchor_id
 LEFT JOIN reference_text_nodes AS node ON node.node_id=queue.node_id
 LEFT JOIN reference_feature_observations AS observation
 ON queue.item_type='observation' AND observation.observation_id=queue.item_id
 WHERE queue.resolved_at IS NULL
 ORDER BY queue.created_at,queue.queue_id LIMIT $size OFFSET $offset;
 """;
 command.Parameters.AddWithValue("$size", size); command.Parameters.AddWithValue("$offset", (page - 1) * size);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken)) items.Add(new(
 reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
 reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetDouble(7),
 reader.IsDBNull(8) ? null : reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9)),
 reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt32(11),
 reader.IsDBNull(12) ? null : reader.GetInt32(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
 var hasMore = page * size < total;
 return new PageResultPayload<ReferenceCorpusReviewQueueItemPayload>(items, total, page, size, Math.Max(1, (int)Math.Ceiling(total / (double)size)), hasMore ? (page + 1).ToString() : null, hasMore, total);
 }, cancellationToken);
 }

 public async ValueTask<int> ReviewItemsAsync(ReviewReferenceCorpusItemsPayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.QueueIds.Count == 0) return 0;
 if (input.ReviewState is not ("confirmed" or "rejected")) throw new ArgumentOutOfRangeException(nameof(input.ReviewState));
 return await LockedAsync(async connection =>
 {
 var changed = 0;
 foreach (var queueId in input.QueueIds.Distinct())
 {
 await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
 await using var updateItem = connection.CreateCommand(); updateItem.Transaction = (SqliteTransaction)transaction;
 updateItem.CommandText = "UPDATE reference_feature_observations SET review_state=$state WHERE observation_id=(SELECT item_id FROM reference_review_queue WHERE queue_id=$queue AND item_type='observation');";
 updateItem.Parameters.AddWithValue("$state", input.ReviewState); updateItem.Parameters.AddWithValue("$queue", queueId);
 changed += await updateItem.ExecuteNonQueryAsync(cancellationToken);
 await using var resolve = connection.CreateCommand(); resolve.Transaction = (SqliteTransaction)transaction;
 resolve.CommandText = "UPDATE reference_review_queue SET review_state=$state,resolved_at=$time WHERE queue_id=$queue AND resolved_at IS NULL;";
 resolve.Parameters.AddWithValue("$state", input.ReviewState); resolve.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O")); resolve.Parameters.AddWithValue("$queue", queueId);
 await resolve.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
 }
 return changed;
 }, cancellationToken);
 }

 public async ValueTask<ReferenceCorpusReconcileResultPayload> ReconcileRunAsync(ReconcileReferenceCorpusRunPayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.AnchorId <= 0) throw new ArgumentOutOfRangeException(nameof(input.AnchorId));
 Required(input.NewRunId, nameof(input.NewRunId));
 return await LockedAsync(async connection =>
 {
 var observations = await ExecuteAsync(connection, "UPDATE reference_feature_observations SET validity_state='superseded',superseded_by_run_id=$run WHERE anchor_id=$anchor AND run_id<>$run AND validity_state='active';", cancellationToken, ("$run", input.NewRunId), ("$anchor", input.AnchorId));
 var specimens = await ExecuteAsync(connection, "UPDATE reference_technique_specimens SET validity_state='superseded',superseded_by_run_id=$run WHERE source_anchor_id=$anchor AND validity_state='active' AND NOT EXISTS(SELECT 1 FROM reference_specimen_evidence AS evidence JOIN reference_feature_observations AS observation ON observation.observation_id=evidence.observation_id WHERE evidence.specimen_id=reference_technique_specimens.specimen_id AND observation.validity_state='active');", cancellationToken, ("$run", input.NewRunId), ("$anchor", input.AnchorId));
var stale = await ExecuteAsync(connection, "UPDATE reference_aggregates SET validity_state='stale' WHERE aggregate_id IN(SELECT aggregate_id FROM reference_aggregate_provenance WHERE anchor_id=$anchor AND COALESCE(run_id,'')<>$run);", cancellationToken, ("$anchor", input.AnchorId), ("$run", input.NewRunId));
 foreach (var table in new[] { "reference_corpus_style_aggregates", "reference_corpus_scene_aggregates", "reference_corpus_world_aggregates", "reference_corpus_dialogue_aggregates" })
 await ExecuteAsync(connection, $"UPDATE {table} SET validity_state='stale' WHERE aggregate_id IN(SELECT aggregate_id FROM reference_aggregate_provenance WHERE anchor_id=$anchor AND COALESCE(run_id,'')<>$run);", cancellationToken, ("$anchor", input.AnchorId), ("$run", input.NewRunId));
 var conflicts = await RefreshReviewQueueCoreAsync(connection, cancellationToken);
 return new ReferenceCorpusReconcileResultPayload(observations, specimens, conflicts, stale);
 }, cancellationToken);
 }

 private static async ValueTask<int> RefreshReviewQueueCoreAsync(SqliteConnection connection, CancellationToken cancellationToken) => await ExecuteAsync(connection,
 "INSERT INTO reference_review_queue(queue_id,item_type,item_id,anchor_id,node_id,reason,review_state,confidence,feature_family,created_at) SELECT 'review-' || lower(hex(randomblob(16))),'observation',latest.observation_id,latest.anchor_id,latest.node_id,'cross_run_conflict','conflicted',latest.confidence,latest.feature_family,$time FROM reference_feature_observations AS latest JOIN reference_feature_observations AS prior ON prior.node_id=latest.node_id AND prior.feature_family=latest.feature_family AND prior.feature_key=latest.feature_key AND prior.run_id<>latest.run_id WHERE latest.validity_state='active' AND COALESCE(latest.value_text,latest.value_json,CAST(latest.value_num AS TEXT),CAST(latest.value_bool AS TEXT),'')<>COALESCE(prior.value_text,prior.value_json,CAST(prior.value_num AS TEXT),CAST(prior.value_bool AS TEXT),'') ON CONFLICT(item_type,item_id,reason) DO NOTHING;",
 cancellationToken, ("$time", DateTimeOffset.UtcNow.ToString("O")));
 private static async ValueTask<int> CountPendingAsync(SqliteConnection connection, CancellationToken cancellationToken) { await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM reference_review_queue WHERE resolved_at IS NULL;"; return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)); }
}
