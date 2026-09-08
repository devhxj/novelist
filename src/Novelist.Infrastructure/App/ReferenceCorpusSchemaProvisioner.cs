using Microsoft.Data.Sqlite;

namespace Novelist.Infrastructure.App;

internal static class ReferenceCorpusSchemaProvisioner
{
    public static async ValueTask EnsureCoreTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await RebuildStaleChapterProgressTableAsync(connection, cancellationToken);
        await RebuildLegacyMaterializationRunsTableAsync(connection, cancellationToken);
        await RelaxMaterializationBatchSizeConstraintAsync(connection, cancellationToken);
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

            CREATE TABLE IF NOT EXISTS reference_chapter_split_profiles (
              split_profile_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              source_hash TEXT NOT NULL,
              split_mode TEXT NOT NULL,
              sample_char_count INTEGER NOT NULL,
              sample_hash TEXT NOT NULL,
              pattern_kind TEXT NOT NULL,
              delimiter_template TEXT NOT NULL,
              pattern_json TEXT NOT NULL,
              model_provider TEXT,
              model_id TEXT,
              confidence REAL,
              status TEXT NOT NULL,
              chapter_count INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              confirmed_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_chapter_split_boundaries (
              split_profile_id TEXT NOT NULL,
              chapter_index INTEGER NOT NULL,
              title TEXT NOT NULL,
              heading_start INTEGER NOT NULL,
              content_start INTEGER NOT NULL,
              content_end INTEGER NOT NULL,
              text_hash TEXT NOT NULL,
              PRIMARY KEY(split_profile_id, chapter_index),
              FOREIGN KEY(split_profile_id) REFERENCES reference_chapter_split_profiles(split_profile_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_chapter_split_profiles_anchor
              ON reference_chapter_split_profiles(anchor_id, status, created_at DESC);

            CREATE TABLE IF NOT EXISTS reference_materialization_runs (
              run_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              split_profile_id TEXT NOT NULL,
              generation_id TEXT NOT NULL,
              policy_version TEXT NOT NULL,
              candidate_version TEXT NOT NULL,
              qualifier_version TEXT NOT NULL,
              model_provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              embedding_provider TEXT NOT NULL,
              embedding_model_id TEXT NOT NULL,
              embedding_dimensions INTEGER NOT NULL CHECK(embedding_dimensions > 0),
              status TEXT NOT NULL,
              chapter_batch_size INTEGER NOT NULL CHECK(chapter_batch_size IN (1, 5, 10)),
              total_chapters INTEGER NOT NULL DEFAULT 0 CHECK(total_chapters >= 0),
              processed_chapters INTEGER NOT NULL DEFAULT 0 CHECK(processed_chapters >= 0),
              total_chapter_batches INTEGER NOT NULL DEFAULT 0 CHECK(total_chapter_batches >= 0),
              completed_chapter_batches INTEGER NOT NULL DEFAULT 0 CHECK(completed_chapter_batches >= 0),
              current_batch_index INTEGER,
              current_batch_start_chapter INTEGER,
              current_batch_end_chapter INTEGER,
              candidate_count INTEGER NOT NULL DEFAULT 0 CHECK(candidate_count >= 0),
              accepted_count INTEGER NOT NULL DEFAULT 0 CHECK(accepted_count >= 0),
              rejected_count INTEGER NOT NULL DEFAULT 0 CHECK(rejected_count >= 0),
              review_count INTEGER NOT NULL DEFAULT 0 CHECK(review_count >= 0),
              vector_count INTEGER NOT NULL DEFAULT 0 CHECK(vector_count >= 0),
              tokens_spent INTEGER NOT NULL DEFAULT 0 CHECK(tokens_spent >= 0),
              last_error_code TEXT,
              last_error_message TEXT,
              started_at TEXT NOT NULL,
              completed_at TEXT,
              activated_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(split_profile_id) REFERENCES reference_chapter_split_profiles(split_profile_id) ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_materialization_runs_generation
              ON reference_materialization_runs(generation_id);

            CREATE INDEX IF NOT EXISTS idx_reference_materialization_runs_anchor_status
              ON reference_materialization_runs(anchor_id, status, started_at DESC);

            CREATE TABLE IF NOT EXISTS reference_anchor_materialization_state (
              anchor_id INTEGER PRIMARY KEY,
              active_generation_id TEXT,
              previous_generation_id TEXT,
              row_version INTEGER NOT NULL DEFAULT 0 CHECK(row_version >= 0),
              updated_at TEXT NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_materialization_chapter_progress (
              run_id TEXT NOT NULL,
              chapter_node_id TEXT NOT NULL,
              chapter_index INTEGER NOT NULL CHECK(chapter_index > 0),
              batch_index INTEGER NOT NULL CHECK(batch_index >= 0),
              status TEXT NOT NULL,
              current_stage TEXT NOT NULL,
              candidate_count INTEGER NOT NULL DEFAULT 0 CHECK(candidate_count >= 0),
              decided_count INTEGER NOT NULL DEFAULT 0 CHECK(decided_count >= 0),
              accepted_count INTEGER NOT NULL DEFAULT 0 CHECK(accepted_count >= 0),
              rejected_count INTEGER NOT NULL DEFAULT 0 CHECK(rejected_count >= 0),
              review_count INTEGER NOT NULL DEFAULT 0 CHECK(review_count >= 0),
              vector_count INTEGER NOT NULL DEFAULT 0 CHECK(vector_count >= 0),
              model_call_count INTEGER NOT NULL DEFAULT 0 CHECK(model_call_count >= 0),
              started_at TEXT,
              completed_at TEXT,
              last_error_code TEXT,
              last_error_message TEXT,
              row_version INTEGER NOT NULL DEFAULT 0 CHECK(row_version >= 0),
              PRIMARY KEY(run_id, chapter_node_id),
              UNIQUE(run_id, chapter_index),
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_materialization_chapter_progress_run_batch
              ON reference_materialization_chapter_progress(run_id, batch_index, chapter_index);

            CREATE TABLE IF NOT EXISTS reference_materialization_run_leases (
              run_id TEXT PRIMARY KEY,
              worker_id TEXT NOT NULL,
              lease_token TEXT NOT NULL,
              lease_expires_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_material_candidates (
              candidate_id TEXT PRIMARY KEY,
              candidate_key TEXT NOT NULL,
              run_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              candidate_type TEXT NOT NULL,
              text_hash TEXT NOT NULL,
              decision TEXT NOT NULL,
              decision_origin TEXT NOT NULL,
              quality_score REAL,
              confidence REAL,
              scores_json TEXT NOT NULL,
              tags_json TEXT NOT NULL,
              reason_codes_json TEXT NOT NULL,
              created_at TEXT NOT NULL,
              reviewed_at TEXT,
              row_version INTEGER NOT NULL DEFAULT 0 CHECK(row_version >= 0),
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_material_candidates_run_key
              ON reference_material_candidates(run_id, candidate_key);

            CREATE INDEX IF NOT EXISTS idx_reference_material_candidates_run_decision
              ON reference_material_candidates(run_id, decision, candidate_id);

            CREATE TABLE IF NOT EXISTS reference_material_candidate_nodes (
              candidate_id TEXT NOT NULL,
              node_id TEXT NOT NULL,
              ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
              evidence_start INTEGER NOT NULL CHECK(evidence_start >= 0),
              evidence_end INTEGER NOT NULL CHECK(evidence_end > evidence_start),
              text_hash TEXT NOT NULL,
              PRIMARY KEY(candidate_id, ordinal),
              FOREIGN KEY(candidate_id) REFERENCES reference_material_candidates(candidate_id) ON DELETE CASCADE,
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE RESTRICT
            );

            CREATE INDEX IF NOT EXISTS idx_reference_material_candidate_nodes_node
              ON reference_material_candidate_nodes(node_id, candidate_id);

            CREATE TABLE IF NOT EXISTS reference_materialization_candidate_embeddings (
              embedding_id TEXT PRIMARY KEY,
              generation_id TEXT NOT NULL,
              run_id TEXT NOT NULL,
              candidate_id TEXT NOT NULL,
              provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL CHECK(dimensions > 0),
              text_hash TEXT NOT NULL,
              embedding_hash TEXT NOT NULL,
              embedding_json TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE,
              FOREIGN KEY(candidate_id) REFERENCES reference_material_candidates(candidate_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_materialization_candidate_embeddings_model
              ON reference_materialization_candidate_embeddings(candidate_id, provider, model_id, dimensions);

            CREATE INDEX IF NOT EXISTS idx_reference_materialization_candidate_embeddings_generation
              ON reference_materialization_candidate_embeddings(generation_id, candidate_id);

            CREATE TABLE IF NOT EXISTS reference_materialization_vector_indexes (
              generation_id TEXT PRIMARY KEY,
              run_id TEXT NOT NULL,
              table_name TEXT NOT NULL,
              provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL CHECK(dimensions > 0),
              vector_count INTEGER NOT NULL CHECK(vector_count >= 0),
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_materialization_vector_indexes_run
              ON reference_materialization_vector_indexes(run_id);

            CREATE TABLE IF NOT EXISTS reference_materialization_materials (
              material_id TEXT PRIMARY KEY,
              generation_id TEXT NOT NULL,
              run_id TEXT NOT NULL,
              candidate_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              material_type TEXT NOT NULL,
              text TEXT NOT NULL,
              text_hash TEXT NOT NULL,
              quality_score REAL NOT NULL,
              confidence REAL NOT NULL,
              scores_json TEXT NOT NULL,
              tags_json TEXT NOT NULL,
              reason_codes_json TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE,
              FOREIGN KEY(candidate_id) REFERENCES reference_material_candidates(candidate_id) ON DELETE RESTRICT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_materialization_materials_generation_candidate
              ON reference_materialization_materials(generation_id, candidate_id);

            CREATE INDEX IF NOT EXISTS idx_reference_materialization_materials_active_lookup
              ON reference_materialization_materials(anchor_id, generation_id, material_type, material_id);

            CREATE TABLE IF NOT EXISTS reference_materialization_material_nodes (
              material_id TEXT NOT NULL,
              node_id TEXT NOT NULL,
              ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
              evidence_start INTEGER NOT NULL CHECK(evidence_start >= 0),
              evidence_end INTEGER NOT NULL CHECK(evidence_end > evidence_start),
              text_hash TEXT NOT NULL,
              PRIMARY KEY(material_id, ordinal),
              FOREIGN KEY(material_id) REFERENCES reference_materialization_materials(material_id) ON DELETE CASCADE,
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS reference_materialization_blueprint_preview_sessions (
              session_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              goal TEXT NOT NULL,
              status TEXT NOT NULL CHECK(status IN ('active', 'stale')),
              next_action TEXT NOT NULL CHECK(next_action IN ('none', 'rebuild')),
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_materialization_blueprint_preview_sources (
              session_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              generation_id TEXT NOT NULL,
              material_count INTEGER NOT NULL CHECK(material_count > 0),
              PRIMARY KEY(session_id, anchor_id),
              FOREIGN KEY(session_id) REFERENCES reference_materialization_blueprint_preview_sessions(session_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_materialization_blueprint_preview_candidates (
              session_id TEXT NOT NULL,
              blueprint_id TEXT NOT NULL,
              candidate_index INTEGER NOT NULL CHECK(candidate_index >= 0),
              strategy TEXT NOT NULL,
              PRIMARY KEY(session_id, blueprint_id),
              UNIQUE(session_id, candidate_index),
              FOREIGN KEY(session_id) REFERENCES reference_materialization_blueprint_preview_sessions(session_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_materialization_blueprint_preview_beats (
              session_id TEXT NOT NULL,
              blueprint_id TEXT NOT NULL,
              beat_id TEXT NOT NULL,
              beat_index INTEGER NOT NULL CHECK(beat_index >= 0),
              intent TEXT NOT NULL,
              narrative_function TEXT NOT NULL,
              PRIMARY KEY(session_id, blueprint_id, beat_id),
              UNIQUE(session_id, blueprint_id, beat_index),
              FOREIGN KEY(session_id, blueprint_id)
                REFERENCES reference_materialization_blueprint_preview_candidates(session_id, blueprint_id)
                ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_materialization_blueprint_preview_material_links (
              session_id TEXT NOT NULL,
              blueprint_id TEXT NOT NULL,
              beat_id TEXT NOT NULL,
              material_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              generation_id TEXT NOT NULL,
              material_type TEXT NOT NULL,
              text_preview TEXT NOT NULL,
              quality_score REAL NOT NULL,
              vector_score REAL NOT NULL,
              fit_explanation TEXT NOT NULL,
              material_rank INTEGER NOT NULL CHECK(material_rank >= 0),
              PRIMARY KEY(session_id, blueprint_id, beat_id, material_id),
              FOREIGN KEY(session_id, blueprint_id, beat_id)
                REFERENCES reference_materialization_blueprint_preview_beats(session_id, blueprint_id, beat_id)
                ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_materialization_blueprint_preview_sources_generation
              ON reference_materialization_blueprint_preview_sources(anchor_id, generation_id);

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

 CREATE TABLE IF NOT EXISTS reference_session_library_scope_state (
 session_id TEXT PRIMARY KEY,
 is_explicit INTEGER NOT NULL DEFAULT 1,
 updated_at TEXT NOT NULL
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

 CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_technique_specimens_generation
 ON reference_technique_specimens(analysis_run_id, source_node_id, technique_family);

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
 await EnsureAnalysisJobTablesAsync(connection, cancellationToken);
 await EnsureMaterializationV6ColumnsAsync(connection, cancellationToken);
 }

 // v6 批处理拆分给三张运行期表追加了列，CREATE TABLE IF NOT EXISTS 不会升级已存在的
 // 旧形状表，租期/锚点状态/向量索引写入会在运行期抛 no such column。这里逐列探测补齐
 // （同 analysis job 列的模式），旧行用默认值填充；全部为追加列，原数据不动。
 private static async ValueTask EnsureMaterializationV6ColumnsAsync(
 SqliteConnection connection,
 CancellationToken cancellationToken)
 {
 await EnsureColumnAsync(connection, "reference_anchor_materialization_state", "previous_generation_id", "TEXT", cancellationToken);
 await EnsureColumnAsync(connection, "reference_anchor_materialization_state", "row_version", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
 await EnsureColumnAsync(connection, "reference_anchor_materialization_state", "updated_at", "TEXT", cancellationToken);

 await EnsureColumnAsync(connection, "reference_materialization_run_leases", "worker_id", "TEXT NOT NULL DEFAULT ''", cancellationToken);
 await EnsureColumnAsync(connection, "reference_materialization_run_leases", "updated_at", "TEXT", cancellationToken);

 await EnsureColumnAsync(connection, "reference_materialization_vector_indexes", "created_at", "TEXT", cancellationToken);
 await EnsureColumnAsync(connection, "reference_materialization_vector_indexes", "updated_at", "TEXT", cancellationToken);
 }

 // v6 批处理拆分前的 runs 表无法追加升级：既缺 batch 列，又带着 INSERT 不再提供、且无默认值的
 // 旧 NOT NULL 列（extractor_schema_version/material_count 等）。copy-first：旧表改名备份、旧行按
 // 共有列回填新表（新增必填列取默认值），再以 v6 形状重建，并写 manifest。重建期间关闭外键并
 // 启用 legacy_alter_table，避免 SQLite 把子表外键改指向备份表。
 private static async ValueTask RebuildLegacyMaterializationRunsTableAsync(
 SqliteConnection connection,
 CancellationToken cancellationToken)
 {
 var legacyColumns = await ReadColumnNamesAsync(connection, "reference_materialization_runs", cancellationToken);
 if (legacyColumns.Count == 0 || legacyColumns.Contains("chapter_batch_size"))
 {
 return;
 }

 var suffix = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
 var backupTable = $"reference_materialization_runs_legacy_{suffix}";
 var foreignKeysEnabled = await ScalarPragmaAsync(connection, "PRAGMA foreign_keys;", cancellationToken);
 await using (var rename = connection.CreateCommand())
 {
 rename.CommandText = $"""
            PRAGMA foreign_keys=OFF;
            PRAGMA legacy_alter_table=ON;
            ALTER TABLE reference_materialization_runs RENAME TO {backupTable};
            PRAGMA legacy_alter_table=OFF;
            """;
 await rename.ExecuteNonQueryAsync(cancellationToken);
 }

 // 旧表的索引随改名挂在备份表上并占用原名，先卸载，稍后由主建表脚本在新表上重建。
 await using (var dropIndexes = connection.CreateCommand())
 {
 dropIndexes.CommandText = """
            DROP INDEX IF EXISTS ux_reference_materialization_runs_generation;
            DROP INDEX IF EXISTS idx_reference_materialization_runs_anchor_status;
            """;
 await dropIndexes.ExecuteNonQueryAsync(cancellationToken);
 }

 await using (var create = connection.CreateCommand())
 {
 create.CommandText = """
            CREATE TABLE reference_materialization_runs (
              run_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              split_profile_id TEXT NOT NULL,
              generation_id TEXT NOT NULL,
              policy_version TEXT NOT NULL,
              candidate_version TEXT NOT NULL,
              qualifier_version TEXT NOT NULL,
              model_provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              embedding_provider TEXT NOT NULL,
              embedding_model_id TEXT NOT NULL,
              embedding_dimensions INTEGER NOT NULL CHECK(embedding_dimensions > 0),
              status TEXT NOT NULL,
              chapter_batch_size INTEGER NOT NULL CHECK(chapter_batch_size IN (1, 5, 10)),
              total_chapters INTEGER NOT NULL DEFAULT 0 CHECK(total_chapters >= 0),
              processed_chapters INTEGER NOT NULL DEFAULT 0 CHECK(processed_chapters >= 0),
              total_chapter_batches INTEGER NOT NULL DEFAULT 0 CHECK(total_chapter_batches >= 0),
              completed_chapter_batches INTEGER NOT NULL DEFAULT 0 CHECK(completed_chapter_batches >= 0),
              current_batch_index INTEGER,
              current_batch_start_chapter INTEGER,
              current_batch_end_chapter INTEGER,
              candidate_count INTEGER NOT NULL DEFAULT 0 CHECK(candidate_count >= 0),
              accepted_count INTEGER NOT NULL DEFAULT 0 CHECK(accepted_count >= 0),
              rejected_count INTEGER NOT NULL DEFAULT 0 CHECK(rejected_count >= 0),
              review_count INTEGER NOT NULL DEFAULT 0 CHECK(review_count >= 0),
              vector_count INTEGER NOT NULL DEFAULT 0 CHECK(vector_count >= 0),
              tokens_spent INTEGER NOT NULL DEFAULT 0 CHECK(tokens_spent >= 0),
              last_error_code TEXT,
              last_error_message TEXT,
              started_at TEXT NOT NULL,
              completed_at TEXT,
              activated_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(split_profile_id) REFERENCES reference_chapter_split_profiles(split_profile_id) ON DELETE RESTRICT
            );
            """;
 await create.ExecuteNonQueryAsync(cancellationToken);
 }

 // 回填必须在外键恢复之前执行：FK 开启时 SQLite 在 prepare 阶段就解析新表的外键父表，
 // 而主建表脚本稍后才会创建 anchors/split_profiles，父表缺失会误报 no such table。
 await CopyLegacyMaterializationRunsAsync(connection, backupTable, legacyColumns, cancellationToken);

 // 重建期间外键保持关闭；结束前恢复连接原本的外键设置。
 await using (var restore = connection.CreateCommand())
 {
 restore.CommandText = $"PRAGMA foreign_keys={(foreignKeysEnabled != 0 ? "ON" : "OFF")};";
 await restore.ExecuteNonQueryAsync(cancellationToken);
 }

 await WriteRebuildManifestAsync(
 connection,
 backupTable,
 "runs-rebuild",
 "pre-v6 reference_materialization_runs shape cannot be additively upgraded (legacy NOT NULL columns without defaults block v6 inserts); table renamed copy-first, legacy rows carried over with v6 defaults, and the table recreated with the v6 shape.",
 cancellationToken);
 }

 // v7 挨章处理：runs 表的 chapter_batch_size CHECK 从 (5, 10) 放宽为 (1, 5, 10)。
 // CHECK 属于表定义，ADD COLUMN 无法修改——copy-first：旧表改名备份、原样回填全部行、
 // 按新约束重建，并写 manifest。行数据逐字保留，不引入默认值。
 private static async ValueTask RelaxMaterializationBatchSizeConstraintAsync(
 SqliteConnection connection,
 CancellationToken cancellationToken)
 {
 string? createSql = null;
 await using (var read = connection.CreateCommand())
 {
 read.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='reference_materialization_runs';";
 createSql = await read.ExecuteScalarAsync(cancellationToken) as string;
 }

 if (createSql is null ||
 !createSql.Contains("chapter_batch_size IN (5, 10)", StringComparison.Ordinal))
 {
 return;
 }

 var suffix = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
 var backupTable = $"reference_materialization_runs_legacy_{suffix}";
 var foreignKeysEnabled = await ScalarPragmaAsync(connection, "PRAGMA foreign_keys;", cancellationToken);
 await using (var rename = connection.CreateCommand())
 {
 rename.CommandText = $"""
            PRAGMA foreign_keys=OFF;
            PRAGMA legacy_alter_table=ON;
            ALTER TABLE reference_materialization_runs RENAME TO {backupTable};
            PRAGMA legacy_alter_table=OFF;
            """;
 await rename.ExecuteNonQueryAsync(cancellationToken);
 }

 await using (var dropIndexes = connection.CreateCommand())
 {
 dropIndexes.CommandText = """
            DROP INDEX IF EXISTS ux_reference_materialization_runs_generation;
            DROP INDEX IF EXISTS idx_reference_materialization_runs_anchor_status;
            """;
 await dropIndexes.ExecuteNonQueryAsync(cancellationToken);
 }

 await using (var create = connection.CreateCommand())
 {
 create.CommandText = """
            CREATE TABLE reference_materialization_runs (
              run_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              split_profile_id TEXT NOT NULL,
              generation_id TEXT NOT NULL,
              policy_version TEXT NOT NULL,
              candidate_version TEXT NOT NULL,
              qualifier_version TEXT NOT NULL,
              model_provider TEXT NOT NULL,
              model_id TEXT NOT NULL,
              embedding_provider TEXT NOT NULL,
              embedding_model_id TEXT NOT NULL,
              embedding_dimensions INTEGER NOT NULL CHECK(embedding_dimensions > 0),
              status TEXT NOT NULL,
              chapter_batch_size INTEGER NOT NULL CHECK(chapter_batch_size IN (1, 5, 10)),
              total_chapters INTEGER NOT NULL DEFAULT 0 CHECK(total_chapters >= 0),
              processed_chapters INTEGER NOT NULL DEFAULT 0 CHECK(processed_chapters >= 0),
              total_chapter_batches INTEGER NOT NULL DEFAULT 0 CHECK(total_chapter_batches >= 0),
              completed_chapter_batches INTEGER NOT NULL DEFAULT 0 CHECK(completed_chapter_batches >= 0),
              current_batch_index INTEGER,
              current_batch_start_chapter INTEGER,
              current_batch_end_chapter INTEGER,
              candidate_count INTEGER NOT NULL DEFAULT 0 CHECK(candidate_count >= 0),
              accepted_count INTEGER NOT NULL DEFAULT 0 CHECK(accepted_count >= 0),
              rejected_count INTEGER NOT NULL DEFAULT 0 CHECK(rejected_count >= 0),
              review_count INTEGER NOT NULL DEFAULT 0 CHECK(review_count >= 0),
              vector_count INTEGER NOT NULL DEFAULT 0 CHECK(vector_count >= 0),
              tokens_spent INTEGER NOT NULL DEFAULT 0 CHECK(tokens_spent >= 0),
              last_error_code TEXT,
              last_error_message TEXT,
              started_at TEXT NOT NULL,
              completed_at TEXT,
              activated_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(split_profile_id) REFERENCES reference_chapter_split_profiles(split_profile_id) ON DELETE RESTRICT
            );
            """;
 await create.ExecuteNonQueryAsync(cancellationToken);
 }

 // 回填在外键恢复前执行（同 v6 重建：FK 开启时 prepare 阶段解析父表会误报缺失）。
 await using (var copy = connection.CreateCommand())
 {
 copy.CommandText = $"""
            INSERT INTO reference_materialization_runs
            SELECT run_id, anchor_id, split_profile_id, generation_id, policy_version, candidate_version,
                   qualifier_version, model_provider, model_id, embedding_provider, embedding_model_id,
                   embedding_dimensions, status, chapter_batch_size, total_chapters, processed_chapters,
                   total_chapter_batches, completed_chapter_batches, current_batch_index,
                   current_batch_start_chapter, current_batch_end_chapter, candidate_count, accepted_count,
                   rejected_count, review_count, vector_count, tokens_spent, last_error_code,
                   last_error_message, started_at, completed_at, activated_at
            FROM {backupTable};
            """;
 await copy.ExecuteNonQueryAsync(cancellationToken);
 }

 await using (var restore = connection.CreateCommand())
 {
 restore.CommandText = $"PRAGMA foreign_keys={(foreignKeysEnabled != 0 ? "ON" : "OFF")};";
 await restore.ExecuteNonQueryAsync(cancellationToken);
 }

 await WriteRebuildManifestAsync(
 connection,
 backupTable,
 "runs-rebuild",
 "chapter_batch_size CHECK relaxed from (5, 10) to (1, 5, 10) for chapter-wise processing; table renamed copy-first, rows carried over unchanged, and the table recreated.",
 cancellationToken);
 }

 // v6 插入不再提供的旧 NOT NULL 列（extractor_schema_version 等）不回填，留在备份表里；
 // 新表回填行只带共有列 + v6 必填列默认值，保证子表（materials/candidates）外键仍然可解析。
 private static readonly (string Name, string Fallback)[] LegacyRunCopyColumns =
 [
 ("run_id", "''"),
 ("anchor_id", "0"),
 ("split_profile_id", "''"),
 ("generation_id", "''"),
 ("policy_version", "''"),
 ("model_provider", "''"),
 ("model_id", "''"),
 ("embedding_provider", "''"),
 ("embedding_model_id", "''"),
 ("embedding_dimensions", "1024"),
 ("status", "'completed'"),
 ("total_chapters", "0"),
 ("processed_chapters", "0"),
 ("vector_count", "0"),
 ("last_error_code", "NULL"),
 ("last_error_message", "NULL"),
 ("started_at", "''"),
 ("completed_at", "NULL"),
 ];

 private static async ValueTask CopyLegacyMaterializationRunsAsync(
 SqliteConnection connection,
 string backupTable,
 IReadOnlySet<string> legacyColumns,
 CancellationToken cancellationToken)
 {
 var targetColumns = new List<string>();
 var sourceColumns = new List<string>();
 foreach (var (name, fallback) in LegacyRunCopyColumns)
 {
 targetColumns.Add(name);
 sourceColumns.Add(legacyColumns.Contains(name) ? name : fallback);
 }

 targetColumns.AddRange(["candidate_version", "qualifier_version", "chapter_batch_size"]);
 sourceColumns.AddRange(["''", "''", "10"]);

 await using var command = connection.CreateCommand();
 command.CommandText = $"""
            INSERT INTO reference_materialization_runs ({string.Join(", ", targetColumns)})
            SELECT {string.Join(", ", sourceColumns)} FROM {backupTable};
            """;
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async ValueTask<HashSet<string>> ReadColumnNamesAsync(
 SqliteConnection connection,
 string tableName,
 CancellationToken cancellationToken)
 {
 var columns = new HashSet<string>(StringComparer.Ordinal);
 await using (var read = connection.CreateCommand())
 {
 read.CommandText = $"PRAGMA table_info({tableName});";
 await using var reader = await read.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 columns.Add(reader.GetString(1));
 }
 }

 return columns;
 }

 private static async ValueTask<long> ScalarPragmaAsync(
 SqliteConnection connection,
 string pragma,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = pragma;
 var result = await command.ExecuteScalarAsync(cancellationToken);
 return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
 }

 private static async ValueTask RebuildStaleChapterProgressTableAsync(
 SqliteConnection connection,
 CancellationToken cancellationToken)
 {
 var columns = new HashSet<string>(StringComparer.Ordinal);
 await using (var read = connection.CreateCommand())
 {
 read.CommandText = "PRAGMA table_info(reference_materialization_chapter_progress);";
 await using var reader = await read.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 columns.Add(reader.GetString(1));
 }
 }

 if (columns.Count == 0 || columns.Contains("chapter_node_id"))
 {
 return;
 }

 var suffix = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
 var backupTable = $"reference_materialization_chapter_progress_legacy_{suffix}";
 await using (var rename = connection.CreateCommand())
 {
 rename.CommandText = $"ALTER TABLE reference_materialization_chapter_progress RENAME TO {backupTable};";
 await rename.ExecuteNonQueryAsync(cancellationToken);
 }

 await using (var create = connection.CreateCommand())
 {
 create.CommandText = $"""
            CREATE TABLE reference_materialization_chapter_progress (
              run_id TEXT NOT NULL,
              chapter_node_id TEXT NOT NULL,
              chapter_index INTEGER NOT NULL CHECK(chapter_index > 0),
              batch_index INTEGER NOT NULL CHECK(batch_index >= 0),
              status TEXT NOT NULL,
              current_stage TEXT NOT NULL,
              candidate_count INTEGER NOT NULL DEFAULT 0 CHECK(candidate_count >= 0),
              decided_count INTEGER NOT NULL DEFAULT 0 CHECK(decided_count >= 0),
              accepted_count INTEGER NOT NULL DEFAULT 0 CHECK(accepted_count >= 0),
              rejected_count INTEGER NOT NULL DEFAULT 0 CHECK(rejected_count >= 0),
              review_count INTEGER NOT NULL DEFAULT 0 CHECK(review_count >= 0),
              vector_count INTEGER NOT NULL DEFAULT 0 CHECK(vector_count >= 0),
              model_call_count INTEGER NOT NULL DEFAULT 0 CHECK(model_call_count >= 0),
              started_at TEXT,
              completed_at TEXT,
              last_error_code TEXT,
              last_error_message TEXT,
              row_version INTEGER NOT NULL DEFAULT 0 CHECK(row_version >= 0),
              PRIMARY KEY(run_id, chapter_node_id),
              UNIQUE(run_id, chapter_index),
              FOREIGN KEY(run_id) REFERENCES reference_materialization_runs(run_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_reference_materialization_chapter_progress_run_batch
              ON reference_materialization_chapter_progress(run_id, batch_index, chapter_index);
            """;
 await create.ExecuteNonQueryAsync(cancellationToken);
 }

 await WriteRebuildManifestAsync(
 connection,
 backupTable,
 "chapter-progress-rebuild",
 "pre-v6 reference_materialization_chapter_progress shape cannot be additively upgraded (primary key changed); table renamed copy-first and recreated with the v6 shape.",
 cancellationToken);
 }

 private static async ValueTask WriteRebuildManifestAsync(
 SqliteConnection connection,
 string backupTable,
 string manifestKind,
 string reason,
 CancellationToken cancellationToken)
 {
 try
 {
 var databasePath = connection.DataSource;
 if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
 {
 return;
 }

 var directory = Path.GetDirectoryName(databasePath);
 if (string.IsNullOrWhiteSpace(directory))
 {
 return;
 }

 var manifest = new
 {
 Status = "completed",
 SourceDatabase = databasePath,
 BackupTable = backupTable,
 Reason = reason,
 RecordedAt = DateTimeOffset.UtcNow,
 Error = (string?)null,
 };
 var manifestPath = Path.Combine(
 directory,
 $"reference-schema-{manifestKind}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
 await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest), cancellationToken);
 }
 catch
 {
 // manifest 写入失败不阻塞启动；备份表本身已保留全部旧数据。
 }
 }

 private static async ValueTask EnsureAnalysisJobTablesAsync(
 SqliteConnection connection,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = """
 CREATE TABLE IF NOT EXISTS reference_analysis_input_snapshots (
 input_snapshot_id TEXT PRIMARY KEY,
 anchor_id INTEGER NOT NULL,
 analysis_stage TEXT NOT NULL,
 scope TEXT NOT NULL,
 node_set_hash TEXT NOT NULL,
 family_set_json TEXT NOT NULL,
 schema_version TEXT NOT NULL,
 analyzer_version TEXT NOT NULL,
 model_provider TEXT NOT NULL,
 model_id TEXT NOT NULL,
 total_nodes INTEGER NOT NULL CHECK(total_nodes > 0),
 total_work_items INTEGER NOT NULL CHECK(total_work_items > 0),
 created_at TEXT NOT NULL,
 FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
 );

 CREATE TABLE IF NOT EXISTS reference_analysis_work_items (
 input_snapshot_id TEXT NOT NULL,
 ordinal INTEGER NOT NULL,
 node_id TEXT NOT NULL,
 chapter_node_id TEXT,
 feature_family TEXT NOT NULL,
 node_text_hash TEXT NOT NULL,
 input_payload_json TEXT NOT NULL,
 input_payload_hash TEXT NOT NULL,
 work_state TEXT NOT NULL DEFAULT 'pending',
 execution_worker_id TEXT,
 execution_lease_token TEXT,
 execution_attempt_no INTEGER,
 invocation_no INTEGER NOT NULL DEFAULT 0,
 reserved_tokens INTEGER NOT NULL DEFAULT 0 CHECK(reserved_tokens >= 0),
 committed_run_id TEXT,
 committed_at TEXT,
 PRIMARY KEY(input_snapshot_id, ordinal),
 UNIQUE(input_snapshot_id, node_id, feature_family),
 FOREIGN KEY(input_snapshot_id) REFERENCES reference_analysis_input_snapshots(input_snapshot_id) ON DELETE CASCADE,
 FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
 FOREIGN KEY(chapter_node_id) REFERENCES reference_text_nodes(node_id) ON DELETE SET NULL
 );

 CREATE TABLE IF NOT EXISTS reference_analysis_jobs (
 job_id TEXT PRIMARY KEY,
 run_id TEXT NOT NULL UNIQUE,
 input_snapshot_id TEXT NOT NULL,
 novel_id INTEGER NOT NULL,
 anchor_id INTEGER NOT NULL,
 job_kind TEXT NOT NULL,
 input_json TEXT NOT NULL,
 input_hash TEXT NOT NULL,
 dependency_job_id TEXT,
 priority_class TEXT NOT NULL,
 priority_value INTEGER NOT NULL DEFAULT 0,
 status TEXT NOT NULL,
 total_nodes INTEGER NOT NULL CHECK(total_nodes > 0),
 total_work_items INTEGER NOT NULL CHECK(total_work_items > 0),
 processed_work_items INTEGER NOT NULL DEFAULT 0,
 succeeded_work_items INTEGER NOT NULL DEFAULT 0,
 skipped_work_items INTEGER NOT NULL DEFAULT 0,
 failed_work_items INTEGER NOT NULL DEFAULT 0,
 retrying_work_items INTEGER NOT NULL DEFAULT 0,
 token_budget INTEGER CHECK(token_budget IS NULL OR token_budget >= 0),
 tokens_spent INTEGER NOT NULL DEFAULT 0,
 tokens_reserved INTEGER NOT NULL DEFAULT 0 CHECK(tokens_reserved >= 0),
 resume_cursor TEXT,
 current_stage TEXT NOT NULL,
 current_chapter INTEGER,
 attempt_count INTEGER NOT NULL DEFAULT 0,
 failure_attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(failure_attempt_count >= 0),
 max_attempts INTEGER NOT NULL DEFAULT 3 CHECK(max_attempts > 0),
 next_attempt_at TEXT,
 lease_owner TEXT,
 lease_token TEXT,
 lease_acquired_at TEXT,
 lease_expires_at TEXT,
 heartbeat_at TEXT,
 pause_requested_at TEXT,
 cancel_requested_at TEXT,
 queued_at TEXT NOT NULL,
 started_at TEXT,
 completed_at TEXT,
 updated_at TEXT NOT NULL,
 last_error_code TEXT,
 last_error_message TEXT,
 row_version INTEGER NOT NULL DEFAULT 0 CHECK(row_version >= 0),
 FOREIGN KEY(input_snapshot_id) REFERENCES reference_analysis_input_snapshots(input_snapshot_id) ON DELETE RESTRICT,
 FOREIGN KEY(run_id) REFERENCES reference_analysis_runs(run_id) ON DELETE RESTRICT,
 FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
 FOREIGN KEY(dependency_job_id) REFERENCES reference_analysis_jobs(job_id) ON DELETE RESTRICT
 );

 CREATE TABLE IF NOT EXISTS reference_analysis_job_attempts (
 job_id TEXT NOT NULL,
 attempt_no INTEGER NOT NULL,
 worker_id TEXT NOT NULL,
 lease_token TEXT NOT NULL,
 started_at TEXT NOT NULL,
 completed_at TEXT,
 outcome TEXT,
 error_code TEXT,
 error_message TEXT,
 tokens_spent INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(job_id, attempt_no),
 FOREIGN KEY(job_id) REFERENCES reference_analysis_jobs(job_id) ON DELETE CASCADE
 );

 CREATE TABLE IF NOT EXISTS reference_analysis_work_item_completions (
 completion_key TEXT PRIMARY KEY,
 job_id TEXT NOT NULL,
 run_id TEXT NOT NULL,
 input_snapshot_id TEXT NOT NULL,
 ordinal INTEGER NOT NULL,
 invocation_no INTEGER NOT NULL,
 attempt_no INTEGER NOT NULL,
 reserved_tokens INTEGER NOT NULL CHECK(reserved_tokens > 0),
 output_kind TEXT NOT NULL,
 output_payload_json TEXT NOT NULL,
 output_payload_hash TEXT NOT NULL,
 tokens_spent INTEGER NOT NULL CHECK(tokens_spent >= 0),
 diagnostics_json TEXT NOT NULL,
 model_completed_at TEXT NOT NULL,
 finalized_at TEXT,
 UNIQUE(input_snapshot_id, ordinal, invocation_no),
 FOREIGN KEY(job_id) REFERENCES reference_analysis_jobs(job_id) ON DELETE CASCADE,
 FOREIGN KEY(input_snapshot_id, ordinal) REFERENCES reference_analysis_work_items(input_snapshot_id, ordinal) ON DELETE CASCADE
 );

 CREATE INDEX IF NOT EXISTS idx_reference_analysis_jobs_claim
 ON reference_analysis_jobs(status, next_attempt_at, priority_value DESC, queued_at, job_id);

 CREATE INDEX IF NOT EXISTS idx_reference_analysis_jobs_anchor
 ON reference_analysis_jobs(anchor_id, updated_at DESC, job_id);

 CREATE INDEX IF NOT EXISTS idx_reference_analysis_jobs_dependency
 ON reference_analysis_jobs(dependency_job_id, status);

 CREATE INDEX IF NOT EXISTS idx_reference_analysis_jobs_lease
 ON reference_analysis_jobs(status, lease_expires_at);

 CREATE INDEX IF NOT EXISTS idx_reference_analysis_work_items_state
 ON reference_analysis_work_items(input_snapshot_id, work_state, ordinal);

 CREATE INDEX IF NOT EXISTS idx_reference_analysis_completions_unfinalized
 ON reference_analysis_work_item_completions(input_snapshot_id, finalized_at, ordinal);
 """;
 await command.ExecuteNonQueryAsync(cancellationToken);
 await EnsureColumnAsync(connection, "reference_analysis_work_items", "execution_worker_id", "TEXT", cancellationToken);
 await EnsureColumnAsync(connection, "reference_analysis_work_items", "execution_lease_token", "TEXT", cancellationToken);
 await EnsureColumnAsync(connection, "reference_analysis_work_items", "execution_attempt_no", "INTEGER", cancellationToken);
 await EnsureColumnAsync(connection, "reference_analysis_work_items", "invocation_no", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
 await EnsureColumnAsync(connection, "reference_analysis_work_items", "reserved_tokens", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
 await EnsureColumnAsync(connection, "reference_analysis_work_items", "input_payload_json", "TEXT", cancellationToken);
 await EnsureColumnAsync(connection, "reference_analysis_work_items", "input_payload_hash", "TEXT", cancellationToken);
 await EnsureColumnAsync(connection, "reference_analysis_jobs", "tokens_reserved", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
 await EnsureColumnAsync(
 connection, "reference_analysis_jobs", "failure_attempt_count",
 "INTEGER NOT NULL DEFAULT 0", cancellationToken);
 }

 private static async ValueTask EnsureColumnAsync(
 SqliteConnection connection,
 string tableName,
 string columnName,
 string definition,
 CancellationToken cancellationToken)
 {
 await using var inspect = connection.CreateCommand();
 inspect.CommandText = $"PRAGMA table_info({tableName});";
 await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal)) return;
 }
 await reader.DisposeAsync();
 await using var alter = connection.CreateCommand();
 alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
 await alter.ExecuteNonQueryAsync(cancellationToken);
 }
}
