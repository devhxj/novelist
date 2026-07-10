using Microsoft.Data.Sqlite;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceCorpusGovernanceService
{
 private static async ValueTask EnsureGovernanceTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = string.Join(Environment.NewLine,
 "CREATE TABLE IF NOT EXISTS reference_aggregates (aggregate_id TEXT PRIMARY KEY, aggregate_type TEXT NOT NULL, name TEXT NOT NULL, summary TEXT NOT NULL, sample_count INTEGER NOT NULL, validity_state TEXT NOT NULL DEFAULT 'active', updated_at TEXT NOT NULL);",
 "CREATE TABLE IF NOT EXISTS reference_corpus_style_aggregates (aggregate_id TEXT PRIMARY KEY, summary TEXT NOT NULL, sample_count INTEGER NOT NULL, validity_state TEXT NOT NULL, updated_at TEXT NOT NULL);",
 "CREATE TABLE IF NOT EXISTS reference_corpus_scene_aggregates (aggregate_id TEXT PRIMARY KEY, summary TEXT NOT NULL, sample_count INTEGER NOT NULL, validity_state TEXT NOT NULL, updated_at TEXT NOT NULL);",
 "CREATE TABLE IF NOT EXISTS reference_corpus_world_aggregates (aggregate_id TEXT PRIMARY KEY, summary TEXT NOT NULL, sample_count INTEGER NOT NULL, validity_state TEXT NOT NULL, updated_at TEXT NOT NULL);",
 "CREATE TABLE IF NOT EXISTS reference_corpus_dialogue_aggregates (aggregate_id TEXT PRIMARY KEY, summary TEXT NOT NULL, sample_count INTEGER NOT NULL, validity_state TEXT NOT NULL, updated_at TEXT NOT NULL);",
 "CREATE TABLE IF NOT EXISTS reference_aggregate_provenance (aggregate_id TEXT NOT NULL, aggregate_kind TEXT NOT NULL, library_id TEXT, anchor_id INTEGER NOT NULL, run_id TEXT NOT NULL, PRIMARY KEY(aggregate_id, anchor_id, run_id));",
 "CREATE INDEX IF NOT EXISTS idx_reference_aggregate_provenance_anchor ON reference_aggregate_provenance(anchor_id, run_id);",
 "CREATE TABLE IF NOT EXISTS reference_review_queue (queue_id TEXT PRIMARY KEY, item_type TEXT NOT NULL, item_id TEXT NOT NULL, anchor_id INTEGER NOT NULL, node_id TEXT NOT NULL, reason TEXT NOT NULL, review_state TEXT NOT NULL DEFAULT 'unverified', confidence REAL NOT NULL, feature_family TEXT, created_at TEXT NOT NULL, resolved_at TEXT, UNIQUE(item_type, item_id, reason));",
 "CREATE INDEX IF NOT EXISTS idx_reference_review_queue_state ON reference_review_queue(review_state, created_at, queue_id);",
 "CREATE TABLE IF NOT EXISTS reference_insertion_audits (audit_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, novel_id INTEGER NOT NULL, chapter_number INTEGER NOT NULL, candidate_id TEXT NOT NULL, assembled_text_hash TEXT NOT NULL, source_anchor_ids_json TEXT NOT NULL, max_similarity REAL NOT NULL, gate_passed INTEGER NOT NULL, diagnostics_json TEXT NOT NULL, created_at TEXT NOT NULL);");
 await command.ExecuteNonQueryAsync(cancellationToken);
 }
}
