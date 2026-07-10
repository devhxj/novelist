using Microsoft.Data.Sqlite;

namespace Novelist.Infrastructure.App;

internal static class ReferenceCorpusSchemaProvisioner
{
    public static async ValueTask EnsureCoreTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS reference_anchors (
              anchor_id INTEGER PRIMARY KEY,
              novel_id INTEGER,
              title TEXT NOT NULL,
              author TEXT NOT NULL,
              source_path TEXT NOT NULL,
              source_kind TEXT NOT NULL,
              license_status TEXT NOT NULL,
              source_file_hash TEXT NOT NULL,
              build_version TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              corpus_visibility TEXT NOT NULL DEFAULT 'private',
              source_trust TEXT NOT NULL DEFAULT 'user_verified',
              user_tags_json TEXT NOT NULL DEFAULT '[]'
            );

            CREATE TABLE IF NOT EXISTS reference_text_nodes (
              node_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              parent_node_id TEXT,
              node_type TEXT NOT NULL,
              sequence_index INTEGER NOT NULL,
              depth INTEGER NOT NULL,
              chapter_index INTEGER,
              start_offset INTEGER NOT NULL,
              end_offset INTEGER NOT NULL,
              char_len INTEGER NOT NULL,
              text_hash TEXT NOT NULL,
              text TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(parent_node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_analysis_runs (
              run_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              analyzer_version TEXT NOT NULL,
              schema_version TEXT NOT NULL,
              model_provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              scope TEXT NOT NULL,
              status TEXT NOT NULL,
              token_budget INTEGER,
              tokens_spent INTEGER NOT NULL DEFAULT 0,
              resume_cursor TEXT,
              started_at TEXT NOT NULL,
              completed_at TEXT,
              observation_count INTEGER NOT NULL DEFAULT 0,
              diagnostics_json TEXT NOT NULL DEFAULT '[]',
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_feature_observations (
              observation_id TEXT PRIMARY KEY,
              node_id TEXT NOT NULL,
              node_type TEXT NOT NULL,
              run_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              feature_family TEXT NOT NULL,
              feature_key TEXT NOT NULL,
              value_kind TEXT NOT NULL,
              value_text TEXT,
              value_num REAL,
              value_bool INTEGER,
              value_json TEXT,
              intensity REAL,
              confidence REAL NOT NULL,
              evidence_start INTEGER,
              evidence_end INTEGER,
              explanation TEXT,
              review_state TEXT NOT NULL DEFAULT 'unverified',
              validity_state TEXT NOT NULL DEFAULT 'active',
              superseded_by_run_id TEXT,
              created_at TEXT NOT NULL,
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(run_id) REFERENCES reference_analysis_runs(run_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_corpus_libraries (
              library_id TEXT PRIMARY KEY,
              scope TEXT NOT NULL,
              novel_id INTEGER,
              name TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_library_members (
              library_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              enabled INTEGER NOT NULL DEFAULT 1,
              source_quality TEXT,
              disabled_reason TEXT,
              dedup_group_id TEXT,
              PRIMARY KEY(library_id, anchor_id),
              FOREIGN KEY(library_id) REFERENCES reference_corpus_libraries(library_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_session_library_binding (
              session_id TEXT NOT NULL,
              library_id TEXT NOT NULL,
              PRIMARY KEY(session_id, library_id),
              FOREIGN KEY(library_id) REFERENCES reference_corpus_libraries(library_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_source_license (
              anchor_id INTEGER PRIMARY KEY,
              license_state TEXT NOT NULL,
              authorization_evidence TEXT,
              reuse_policy TEXT NOT NULL,
              max_verbatim_ratio REAL,
              cleared_for_insertion INTEGER NOT NULL DEFAULT 0,
              reviewed_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_text_node_embeddings (
              embedding_id TEXT PRIMARY KEY,
              node_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              text_hash TEXT NOT NULL,
              embedding_json TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_text_node_embeddings_generation
              ON reference_text_node_embeddings(node_id, provider_key, model_id, dimensions);

            CREATE INDEX IF NOT EXISTS idx_reference_text_node_embeddings_lookup
              ON reference_text_node_embeddings(provider_key, model_id, dimensions, anchor_id);

            CREATE TABLE IF NOT EXISTS reference_current_chapter_embedding_cache (
              cache_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              chapter_number INTEGER NOT NULL,
              draft_text_hash TEXT NOT NULL,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              embedding_json TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_current_chapter_embedding_cache_generation
              ON reference_current_chapter_embedding_cache(
                novel_id,
                chapter_number,
                draft_text_hash,
                provider_key,
                model_id,
                dimensions);

            CREATE TABLE IF NOT EXISTS reference_obs_sensory (
              observation_id TEXT NOT NULL,
              node_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              sense TEXT NOT NULL,
              intensity REAL NOT NULL,
              PRIMARY KEY(observation_id, sense),
              FOREIGN KEY(observation_id) REFERENCES reference_feature_observations(observation_id) ON DELETE CASCADE,
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_technique_specimens (
              specimen_id TEXT PRIMARY KEY,
              source_node_id TEXT NOT NULL,
              source_anchor_id INTEGER NOT NULL,
              analysis_run_id TEXT NOT NULL,
              technique_family TEXT NOT NULL,
              technique_abstract TEXT NOT NULL,
              trigger_context TEXT NOT NULL,
              transfer_template TEXT NOT NULL,
              transfer_slots_json TEXT NOT NULL,
              effect_on_reader TEXT NOT NULL,
              applicability_conditions TEXT NOT NULL,
              failure_modes TEXT NOT NULL,
              anti_patterns TEXT NOT NULL,
              world_context_dependencies TEXT,
              why_it_works_json TEXT NOT NULL,
              confidence REAL NOT NULL,
              review_state TEXT NOT NULL DEFAULT 'unverified',
              validity_state TEXT NOT NULL DEFAULT 'active',
              superseded_by_run_id TEXT,
              mastery_notes TEXT,
              created_at TEXT NOT NULL,
              FOREIGN KEY(source_node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(source_anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(analysis_run_id) REFERENCES reference_analysis_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_technique_vectors (
              vector_id TEXT PRIMARY KEY,
              specimen_id TEXT NOT NULL,
              source_node_id TEXT NOT NULL,
              source_anchor_id INTEGER NOT NULL,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              technique_hash TEXT NOT NULL,
              embedding_json TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(specimen_id) REFERENCES reference_technique_specimens(specimen_id) ON DELETE CASCADE,
              FOREIGN KEY(source_node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(source_anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_technique_vectors_generation
              ON reference_technique_vectors(specimen_id, provider_key, model_id, dimensions);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_vectors_node
              ON reference_technique_vectors(source_node_id, provider_key, model_id, dimensions);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_vectors_anchor
              ON reference_technique_vectors(source_anchor_id, provider_key, model_id, dimensions);

            CREATE TABLE IF NOT EXISTS reference_technique_vector_rows (
              index_scope_key TEXT NOT NULL,
              row_id INTEGER NOT NULL,
              vector_id TEXT NOT NULL,
              specimen_id TEXT NOT NULL,
              source_node_id TEXT NOT NULL,
              source_anchor_id INTEGER NOT NULL,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              technique_hash TEXT NOT NULL,
              table_name TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              PRIMARY KEY(index_scope_key, row_id),
              FOREIGN KEY(vector_id) REFERENCES reference_technique_vectors(vector_id) ON DELETE CASCADE,
              FOREIGN KEY(specimen_id) REFERENCES reference_technique_specimens(specimen_id) ON DELETE CASCADE,
              FOREIGN KEY(source_node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(source_anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_technique_vector_rows_vector
              ON reference_technique_vector_rows(index_scope_key, vector_id);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_vector_rows_scope_node
              ON reference_technique_vector_rows(index_scope_key, source_node_id);

            CREATE TABLE IF NOT EXISTS reference_technique_vector_index_state (
              index_scope_key TEXT PRIMARY KEY,
              table_name TEXT NOT NULL,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              source_hash TEXT NOT NULL,
              source_count INTEGER NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_specimen_evidence (
              specimen_id TEXT NOT NULL,
              observation_id TEXT NOT NULL,
              PRIMARY KEY(specimen_id, observation_id),
              FOREIGN KEY(specimen_id) REFERENCES reference_technique_specimens(specimen_id) ON DELETE CASCADE,
              FOREIGN KEY(observation_id) REFERENCES reference_feature_observations(observation_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_template_examples (
              template_id TEXT NOT NULL,
              node_id TEXT NOT NULL,
              PRIMARY KEY(template_id, node_id),
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_blueprint_beat_pieces (
              beat_id TEXT NOT NULL,
              node_id TEXT NOT NULL,
              observation_id TEXT,
              role_in_beat TEXT,
              sequence_index INTEGER NOT NULL,
              PRIMARY KEY(beat_id, node_id),
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(observation_id) REFERENCES reference_feature_observations(observation_id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS reference_corpus_blueprints (
              blueprint_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              chapter_number INTEGER NOT NULL,
              query_context_hash TEXT NOT NULL,
              assembly_strategy TEXT NOT NULL,
              coverage_score REAL NOT NULL,
              gap_reasons_json TEXT NOT NULL,
              gap_positions_json TEXT NOT NULL,
              query_context_json TEXT NOT NULL,
              source_distribution_json TEXT NOT NULL,
              feedback_reason TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_corpus_blueprint_beats (
              blueprint_id TEXT NOT NULL,
              beat_id TEXT NOT NULL,
              beat_index INTEGER NOT NULL,
              role_in_beat TEXT NOT NULL,
              narrative_function TEXT NOT NULL,
              PRIMARY KEY(blueprint_id, beat_id),
              FOREIGN KEY(blueprint_id) REFERENCES reference_corpus_blueprints(blueprint_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_user_feedback (
              feedback_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              target_type TEXT NOT NULL,
              target_id TEXT NOT NULL,
              decision TEXT NOT NULL,
              material_id TEXT NOT NULL,
              candidate_id TEXT NOT NULL,
              blueprint_id INTEGER NOT NULL,
              beat_id TEXT NOT NULL,
              feedback_tags_json TEXT NOT NULL,
              note TEXT NOT NULL,
              edited_text_hash TEXT NOT NULL,
              origin TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_aggregate_provenance (
              aggregate_id TEXT NOT NULL,
              aggregate_kind TEXT NOT NULL,
              library_id TEXT,
              anchor_id INTEGER NOT NULL,
              run_id TEXT NOT NULL,
              PRIMARY KEY(aggregate_id, anchor_id, run_id),
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(run_id) REFERENCES reference_analysis_runs(run_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_text_nodes_parent
              ON reference_text_nodes(parent_node_id, sequence_index);

            CREATE INDEX IF NOT EXISTS idx_reference_text_nodes_anchor_type
              ON reference_text_nodes(anchor_id, node_type);

            CREATE INDEX IF NOT EXISTS idx_reference_text_nodes_chapter
              ON reference_text_nodes(anchor_id, chapter_index, sequence_index);

            CREATE INDEX IF NOT EXISTS idx_reference_observations_family
              ON reference_feature_observations(anchor_id, feature_family, feature_key, value_text);

            CREATE INDEX IF NOT EXISTS idx_reference_observations_num
              ON reference_feature_observations(anchor_id, feature_family, feature_key, value_num);

            CREATE INDEX IF NOT EXISTS idx_reference_observations_node
              ON reference_feature_observations(node_id, run_id, validity_state);

            CREATE INDEX IF NOT EXISTS idx_reference_observations_list
              ON reference_feature_observations(anchor_id, validity_state, created_at, observation_id);

            CREATE INDEX IF NOT EXISTS idx_reference_observations_node_family_list
              ON reference_feature_observations(anchor_id, node_id, validity_state, feature_family, created_at, observation_id);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_obs_generation_key
              ON reference_feature_observations(
                run_id,
                node_id,
                feature_family,
                feature_key,
                IFNULL(evidence_start, -1),
                IFNULL(evidence_end, -1));

            CREATE INDEX IF NOT EXISTS idx_reference_library_members_anchor
              ON reference_library_members(anchor_id, enabled);

            CREATE INDEX IF NOT EXISTS idx_reference_obs_sensory_query
              ON reference_obs_sensory(anchor_id, sense, intensity);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_specimens_source
              ON reference_technique_specimens(source_anchor_id, source_node_id, validity_state);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_specimens_list
              ON reference_technique_specimens(source_anchor_id, validity_state, created_at, specimen_id);

            CREATE INDEX IF NOT EXISTS idx_reference_specimen_evidence_observation
              ON reference_specimen_evidence(observation_id, specimen_id);

            CREATE INDEX IF NOT EXISTS idx_reference_blueprint_beat_pieces_beat
              ON reference_blueprint_beat_pieces(beat_id, sequence_index);

            CREATE INDEX IF NOT EXISTS idx_reference_corpus_blueprints_chapter
              ON reference_corpus_blueprints(novel_id, chapter_number, updated_at DESC, blueprint_id);

            CREATE INDEX IF NOT EXISTS idx_reference_corpus_blueprints_query
              ON reference_corpus_blueprints(query_context_hash, assembly_strategy);

            CREATE INDEX IF NOT EXISTS idx_reference_corpus_blueprint_beats_blueprint
              ON reference_corpus_blueprint_beats(blueprint_id, beat_index);

            CREATE INDEX IF NOT EXISTS idx_reference_feedback_novel_target
              ON reference_user_feedback(novel_id, target_type, target_id, created_at);

            CREATE INDEX IF NOT EXISTS idx_reference_aggregate_provenance_anchor_run
              ON reference_aggregate_provenance(anchor_id, run_id, aggregate_kind);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
