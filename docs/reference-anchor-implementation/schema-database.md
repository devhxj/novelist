# Reference Anchor Database Schema

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to schema and integration index](schema-and-integration.md).

## Database Schema

The current schema is created by `SqliteReferenceAnchorService.EnsureSchemaAsync`, `SqliteReferenceAnchoredDraftService.EnsureSchemaAsync`, and `SqliteReferenceStyleProfileService.EnsureSchemaAsync` in the dedicated reference-anchor SQLite database.

Current core tables:

```text
reference_anchors
reference_anchor_build_state
reference_source_segments
reference_materials
reference_material_slots
reference_reuse_candidates
reference_reuse_audits
reference_user_feedback
reference_chapter_blueprints
reference_chapter_blueprint_beats
reference_chapter_blueprint_reviews
reference_chapter_blueprint_approvals
reference_chapter_blueprint_revisions
reference_blueprint_material_links
reference_draft_paragraph_candidates
reference_orchestration_runs
reference_orchestration_run_events
reference_style_profiles
reference_style_profile_sources
reference_style_profile_evidence
reference_style_analysis_runs
reference_material_style_tags
```

Core columns:

```text
reference_anchors
- anchor_id INTEGER PRIMARY KEY
- novel_id INTEGER
- title TEXT NOT NULL
- author TEXT NOT NULL
- source_path TEXT NOT NULL
- source_kind TEXT NOT NULL
- license_status TEXT NOT NULL
- source_file_hash TEXT NOT NULL
- build_version TEXT NOT NULL
- status TEXT NOT NULL
- created_at TEXT NOT NULL
- updated_at TEXT NOT NULL
- corpus_visibility TEXT NOT NULL DEFAULT 'private'
- source_trust TEXT NOT NULL DEFAULT 'user_verified'
- user_tags_json TEXT NOT NULL DEFAULT '[]'

`reference_anchors.novel_id IS NULL` is the storage-level workspace-corpus owner for sources and extracted materials that are not owned by a single novel. `reference_anchors.novel_id = 0` remains a compatibility owner for older workspace-corpus rows and bridge output. Public create/rebuild/delete APIs still require a positive novel id as the initiating context, but `CreateReferenceAnchor` stores `visibility = 'workspace'` imports with nullable ownership so other novels can retrieve the same extracted materials immediately; `private` and `restricted` imports remain owned by the initiating novel. `PromoteReferenceAnchorToWorkspaceCorpus` is the explicit compatibility migration path for an existing per-novel anchor: it changes the owned `reference_anchors` row to `novel_id = NULL` and `corpus_visibility = 'workspace'`, optionally replacing source trust and user tags, without touching `reference_source_segments`, `reference_materials`, source hashes, material ids, build state, audits, or feedback rows. Read paths for listing anchors, build status, material search, material adaptation, reuse audit, tag correction, and per-novel feedback validation include the active novel's private anchors plus `novel_id IS NULL OR novel_id = 0` anchors whose `corpus_visibility = 'workspace'`, while continuing to exclude private/restricted workspace rows and private anchors owned by other novels. Explicit `anchor_ids` do not bypass the visibility filter. Existing databases that already had `novel_id = 0` compatibility rows before these columns are added promote those legacy rows to `corpus_visibility = 'workspace'` once during migration, and the `reference_anchors` table is rebuilt if needed so `novel_id` can become nullable without losing source hashes, source segment ids, material ids, user tags, feedback, or audit provenance. Schema ensure also promotes legacy rows that already carry `corpus_visibility = 'workspace'` but still have a positive per-novel owner to nullable workspace-corpus ownership; `private` and `restricted` rows are left owned by their original novel. New per-novel anchors default to `private`. `reference_user_feedback.novel_id` remains the consuming novel id, so feedback about a shared material is still scoped to the novel that used it.

reference_source_segments
- segment_id TEXT PRIMARY KEY
- anchor_id INTEGER NOT NULL
- chapter_index INTEGER NOT NULL
- chapter_title TEXT NOT NULL
- segment_type TEXT NOT NULL
- segment_index INTEGER NOT NULL
- parent_segment_id TEXT NOT NULL
- start_offset INTEGER NOT NULL
- end_offset INTEGER NOT NULL
- text TEXT NOT NULL
- text_hash TEXT NOT NULL

`segment_type` currently includes core `chapter`, `paragraph`, and `sentence` rows plus Phase 14 deterministic advanced rows: `scene`, `beat`, `dialogue_exchange`, `action_afterbeat`, `image_motif`, `hook`, `payoff`, and `transition`. Core ids remain deterministic under the existing chapter/paragraph/sentence scheme. Advanced rows are appended after core segmentation and keep explicit parent links: `scene -> chapter`, `beat -> scene`, and dialogue/action-afterbeat/image/hook/payoff/transition evidence rows -> `beat`. Large evidence children store bounded deterministic source windows so 10MB imports stay searchable without duplicating every long paragraph for each child type.

reference_materials
- material_id TEXT PRIMARY KEY
- anchor_id INTEGER NOT NULL
- source_segment_id TEXT NOT NULL
- material_type TEXT NOT NULL
- function_tag TEXT NOT NULL
- emotion_tag TEXT NOT NULL
- scene_tag TEXT NOT NULL
- pov_tag TEXT NOT NULL
- technique_tag TEXT NOT NULL
- function_confidence REAL NOT NULL
- emotion_confidence REAL NOT NULL
- pov_confidence REAL NOT NULL
- text TEXT NOT NULL
- source_hash TEXT NOT NULL
- extractor_version TEXT NOT NULL
- user_verified INTEGER NOT NULL
- created_at TEXT NOT NULL

`material_type` keeps the existing sentence/passages retrieval surface and now also supports `scene`, `beat`, `dialogue_exchange`, `action_afterbeat`, `image_motif`, `hook`, `payoff`, and `transition`. Each material's `source_hash` is the hash of its source segment text, so rebuilds can preserve user tag corrections and archive markers by stable id or unique material-type/hash match.

reference_chapter_blueprints
- blueprint_id INTEGER PRIMARY KEY
- novel_id INTEGER NOT NULL
- chapter_number INTEGER NOT NULL
- primary_anchor_id INTEGER NOT NULL
- title TEXT NOT NULL
- status TEXT NOT NULL
- source_plan_scope TEXT NOT NULL
- source_plan_hash TEXT NOT NULL
- context_hash TEXT NOT NULL
- analysis_contract_hash TEXT NOT NULL
- blueprint_version INTEGER NOT NULL
- parent_blueprint_id INTEGER NOT NULL
- chapter_function TEXT NOT NULL
- logic_analysis_json TEXT NOT NULL
- emotion_analysis_json TEXT NOT NULL
- narration_analysis_json TEXT NOT NULL
- character_analysis_json TEXT NOT NULL
- reference_analysis_json TEXT NOT NULL
- transition_plan_json TEXT NOT NULL
- previous_state TEXT NOT NULL
- final_state TEXT NOT NULL
- final_hook TEXT NOT NULL
- global_pov TEXT NOT NULL
- global_narrative_distance TEXT NOT NULL
- known_facts_json TEXT NOT NULL
- forbidden_facts_json TEXT NOT NULL
- risk_flags_json TEXT NOT NULL
- execution_contract_json TEXT NOT NULL
- build_version TEXT NOT NULL
- approved_review_id TEXT NOT NULL
- created_at TEXT NOT NULL
- updated_at TEXT NOT NULL
- approved_at TEXT NOT NULL

reference_chapter_blueprint_beats
- beat_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- beat_index INTEGER NOT NULL
- scene_index INTEGER NOT NULL
- beat_type TEXT NOT NULL
- narrative_function TEXT NOT NULL
- logic_premise TEXT NOT NULL
- conflict_pressure TEXT NOT NULL
- causality_in TEXT NOT NULL
- causality_out TEXT NOT NULL
- transition_in TEXT NOT NULL
- transition_out TEXT NOT NULL
- pov_character TEXT NOT NULL
- narrative_distance TEXT NOT NULL
- viewpoint_allowed_knowledge_json TEXT NOT NULL
- viewpoint_forbidden_knowledge_json TEXT NOT NULL
- character_states_before_json TEXT NOT NULL
- character_states_after_json TEXT NOT NULL
- character_goals_json TEXT NOT NULL
- character_misbeliefs_json TEXT NOT NULL
- relationship_pressure_json TEXT NOT NULL
- emotion_trigger TEXT NOT NULL
- emotion_before TEXT NOT NULL
- emotion_after TEXT NOT NULL
- suppressed_reaction TEXT NOT NULL
- external_evidence TEXT NOT NULL
- narration_strategy TEXT NOT NULL
- rhythm_strategy TEXT NOT NULL
- paragraph_intention TEXT NOT NULL
- execution_mode TEXT NOT NULL
- anti_screenplay_duty TEXT NOT NULL
- sensory_anchor_target TEXT NOT NULL
- subtext_plan TEXT NOT NULL
- source_backed_detail_target TEXT NOT NULL
- candidate_rejection_rule TEXT NOT NULL
- scene_facts_json TEXT NOT NULL
- forbidden_facts_json TEXT NOT NULL
- reference_query_json TEXT NOT NULL
- required_material_types_json TEXT NOT NULL
- max_rewrite_level TEXT NOT NULL
- slot_plan_json TEXT NOT NULL
- locked_phrase_policy TEXT NOT NULL
- no_reuse_reason TEXT NOT NULL
- prose_duties_json TEXT NOT NULL
- style_contract_json TEXT NOT NULL
- risk_flags_json TEXT NOT NULL

`style_contract_json` is the Phase 14 beat-level style contract. It stores target style profile ids, style dimensions, imitation intensity, minimum style-fit score, allowed closeness, required evidence types, and forbidden style risks. It is included in `analysis_contract_hash`; revising it makes previous approvals and material links stale. Deterministic blueprint review fails contracts with missing/invalid style profile ids, no style duties, contradictory intensity/fit thresholds, or required evidence labels/material granularities incompatible with the beat's effective material search. Material binding passes the style profile ids/dimensions/intensity into `SearchReferenceMaterials`; selected links below the contract's minimum style fit are persisted as `low_confidence` weak matches with a negative `style_fit_gap` score component. Draft audit consumes the same contract: non-diagnostic contracts verify the selected link's `style_fit` against `min_style_fit` and, when active referenced profile rows exist, compare candidate-observable deterministic features against `reference_style_profiles.feature_vector_json` for supported numeric dimensions.

reference_chapter_blueprint_reviews
- review_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- context_hash TEXT NOT NULL
- source_plan_hash TEXT NOT NULL
- analysis_contract_hash TEXT NOT NULL
- review_version TEXT NOT NULL
- status TEXT NOT NULL
- score REAL NOT NULL
- logic_errors_json TEXT NOT NULL
- causality_errors_json TEXT NOT NULL
- emotion_errors_json TEXT NOT NULL
- narration_errors_json TEXT NOT NULL
- execution_errors_json TEXT NOT NULL
- character_state_errors_json TEXT NOT NULL
- pov_errors_json TEXT NOT NULL
- continuity_errors_json TEXT NOT NULL
- transition_errors_json TEXT NOT NULL
- forbidden_fact_errors_json TEXT NOT NULL
- reference_binding_errors_json TEXT NOT NULL
- material_fit_errors_json TEXT NOT NULL
- screenplay_drift_risks_json TEXT NOT NULL
- ai_prose_risks_json TEXT NOT NULL
- novelistic_narration_errors_json TEXT NOT NULL
- required_fixes_json TEXT NOT NULL
- defects_json TEXT NOT NULL
- reviewed_at TEXT NOT NULL

reference_chapter_blueprint_approvals
- approval_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- review_id TEXT NOT NULL
- context_hash TEXT NOT NULL
- source_plan_hash TEXT NOT NULL
- analysis_contract_hash TEXT NOT NULL
- review_version INTEGER NOT NULL
- approver_origin TEXT NOT NULL
- approved_at TEXT NOT NULL

reference_chapter_blueprint_revisions
- revision_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- parent_blueprint_id INTEGER NOT NULL
- changed_field_path TEXT NOT NULL
- previous_value_hash TEXT NOT NULL
- new_value_hash TEXT NOT NULL
- origin TEXT NOT NULL
- revision_reason TEXT NOT NULL
- invalidated_review_id TEXT NOT NULL
- created_at TEXT NOT NULL

reference_blueprint_material_links
- link_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- analysis_contract_hash TEXT NOT NULL
- beat_id TEXT NOT NULL
- material_id TEXT NOT NULL
- intended_use TEXT NOT NULL
- max_rewrite_level TEXT NOT NULL
- selected INTEGER NOT NULL
- score REAL NOT NULL
- score_components_json TEXT NOT NULL
- fit_explanation TEXT NOT NULL
- status TEXT NOT NULL
- created_at TEXT NOT NULL

`score_components_json` on persisted material links currently records material type, function, emotion, POV, prose-duty, lexical, embedding similarity, optional style fit, negative style-fit gaps, confidence, `user_verified`, current-novel accepted-feedback boosts, and negative `low_confidence` markers for expanded-query or low style-fit weak matches when applicable. `SearchReferenceMaterials` also returns transient score components for lexical/tag fit, story-context narrative duty, emotion transition, prose duty, optional style fit from `reference_material_style_tags`, optional same-source `source_risk_penalty` for moderate/strong style requests, embedding similarity when available, confidence, current-novel accepted feedback, and length. Draft generation and persisted draft re-audit read the selected link for the current `analysis_contract_hash`; a low-confidence selected link turns into draft-audit provenance risk until the material is rebound, the blueprint query is revised, or the retrieval gap is explicitly resolved. When the selected material row is still visible and active, draft audit also reads its source text from `reference_materials` and applies deterministic source-leak checks to non-L0/L1 candidates without storing extra source text in draft rows. If the beat style contract uses strong imitation, draft audit uses stricter source-leak thresholds against that selected material text. When `reference_style_profiles` exists, draft generation and re-audit also load active same-novel profile `feature_vector_json` rows referenced by non-diagnostic beat style contracts, then fail candidates whose supported observable feature distances exceed the configured imitation-intensity tolerance.

reference_draft_paragraph_candidates
- candidate_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- beat_id TEXT NOT NULL
- material_id TEXT NOT NULL
- rewrite_level TEXT NOT NULL
- text TEXT NOT NULL
- changed_slots_json TEXT NOT NULL
- non_slot_edits_json TEXT NOT NULL
- audit_status TEXT NOT NULL
- created_at TEXT NOT NULL

reference_orchestration_runs
- run_id TEXT PRIMARY KEY
- novel_id INTEGER NOT NULL
- chapter_number INTEGER NOT NULL
- status TEXT NOT NULL
- stage TEXT NOT NULL
- chapter_goal TEXT NOT NULL
- known_facts_json TEXT NOT NULL
- forbidden_facts_json TEXT NOT NULL
- anchor_ids_json TEXT NOT NULL
- corpus_search_policy_json TEXT NOT NULL
- blueprint_id INTEGER NOT NULL
- review_id TEXT NOT NULL
- candidate_ids_json TEXT NOT NULL
- current_decision_json TEXT NOT NULL
- last_stop_reason TEXT NOT NULL
- error_message TEXT NOT NULL
- created_at TEXT NOT NULL
- updated_at TEXT NOT NULL

reference_orchestration_run_events
- event_id INTEGER PRIMARY KEY AUTOINCREMENT
- run_id TEXT NOT NULL
- novel_id INTEGER NOT NULL
- event_type TEXT NOT NULL
- stage TEXT NOT NULL
- status TEXT NOT NULL
- stop_reason TEXT NOT NULL
- decision_type TEXT NOT NULL
- summary TEXT NOT NULL
- created_at TEXT NOT NULL

reference_user_feedback
- feedback_id TEXT PRIMARY KEY
- novel_id INTEGER NOT NULL
- target_type TEXT NOT NULL
- target_id TEXT NOT NULL
- decision TEXT NOT NULL
- material_id TEXT NOT NULL
- candidate_id TEXT NOT NULL
- blueprint_id INTEGER NOT NULL
- beat_id TEXT NOT NULL
- feedback_tags_json TEXT NOT NULL
- note TEXT NOT NULL
- edited_text_hash TEXT NOT NULL
- origin TEXT NOT NULL
- created_at TEXT NOT NULL

reference_style_profiles
- profile_id INTEGER PRIMARY KEY
- novel_id INTEGER NOT NULL
- title TEXT NOT NULL
- description TEXT NOT NULL
- status TEXT NOT NULL
- analyzer_version TEXT NOT NULL
- feature_schema_version TEXT NOT NULL
- analyzer_source TEXT NOT NULL
- anchor_ids_json TEXT NOT NULL
- source_hashes_json TEXT NOT NULL
- allowed_license_statuses_json TEXT NOT NULL
- allowed_source_trust_levels_json TEXT NOT NULL
- feature_vector_json TEXT NOT NULL
- aggregate_confidence REAL NOT NULL
- created_at TEXT NOT NULL
- updated_at TEXT NOT NULL
- archived_at TEXT

reference_style_profile_sources
- profile_id INTEGER NOT NULL
- anchor_id INTEGER NOT NULL
- source_file_hash TEXT NOT NULL
- license_status TEXT NOT NULL
- source_trust TEXT NOT NULL
- corpus_visibility TEXT NOT NULL
- material_count INTEGER NOT NULL
- segment_count INTEGER NOT NULL

reference_style_profile_evidence
- evidence_id TEXT PRIMARY KEY
- profile_id INTEGER NOT NULL
- anchor_id INTEGER NOT NULL
- source_segment_id TEXT NOT NULL
- material_id TEXT
- feature_key TEXT NOT NULL
- label TEXT NOT NULL
- start_offset INTEGER NOT NULL
- end_offset INTEGER NOT NULL
- text_hash TEXT NOT NULL
- confidence REAL NOT NULL
- analyzer_source TEXT NOT NULL
- created_at TEXT NOT NULL

reference_style_analysis_runs
- run_id TEXT PRIMARY KEY
- profile_id INTEGER NOT NULL
- analyzer_version TEXT NOT NULL
- feature_schema_version TEXT NOT NULL
- analyzer_source TEXT NOT NULL
- input_anchor_ids_json TEXT NOT NULL
- input_source_hashes_json TEXT NOT NULL
- status TEXT NOT NULL
- diagnostics_json TEXT NOT NULL
- created_at TEXT NOT NULL

reference_material_style_tags
- profile_id INTEGER NOT NULL
- material_id TEXT NOT NULL
- tag_key TEXT NOT NULL
- tag_value TEXT NOT NULL
- confidence REAL NOT NULL
- evidence_id TEXT NOT NULL
- analyzer_source TEXT NOT NULL
- analyzer_version TEXT NOT NULL
- created_at TEXT NOT NULL
```

`reference_style_profile_evidence` intentionally does not store source text. It stores source/material provenance ids, offsets, hashes, feature key, label, confidence, and analyzer source. The large imported source text remains only in `reference_source_segments` and `reference_materials`.

`reference_material_style_tags` is the search-side bridge from deterministic or future model-assisted style analysis to retrieval. `SearchReferenceMaterials.style_profile_ids` reads only active profiles owned by the current novel, then joins these tags back to currently visible active materials. The join never expands material visibility and cross-novel or archived profile ids fail the request.

Current indexes:

```text
idx_reference_anchors_novel
idx_reference_anchors_corpus_visibility
idx_reference_segments_anchor_type
idx_reference_materials_anchor_type
idx_reference_materials_tags
idx_reference_material_slots_material
idx_reference_candidates_material
idx_reference_blueprints_novel_chapter
idx_reference_blueprint_beats_blueprint
idx_reference_blueprint_reviews_blueprint
idx_reference_blueprint_approvals_blueprint
idx_reference_blueprint_revisions_blueprint
idx_reference_blueprint_links_beat
idx_reference_draft_candidates_blueprint
idx_reference_orchestration_runs_novel_chapter
idx_reference_orchestration_run_events_run
idx_reference_style_profiles_novel
idx_reference_style_profile_sources_anchor
idx_reference_style_evidence_profile_feature
idx_reference_material_style_tags_material
idx_reference_feedback_novel_target
idx_reference_feedback_material
```

Foreign key enforcement is enabled on reference-anchor SQLite connections:

```sql
PRAGMA foreign_keys = ON;
```

The current RAG service does not enable foreign keys because it has only two flat tables. Reference-anchor storage has real parent/child integrity and enables it.

The Phase 14 style-profile tables use foreign keys back to `reference_anchors`, `reference_source_segments`, and `reference_materials` with restrictive provenance behavior. Soft-archiving materials keeps evidence readable because material rows remain in place. Hard-deleting a referenced source/material is blocked by SQLite foreign keys instead of silently orphaning profile provenance.

`reference_orchestration_runs` is the Phase 11 run state and resume surface. It persists stage/status, source/fact decision details, optional explicit anchors, corpus search policy, artifact ids, stop reason, and error text so a run can be inspected or resumed after restart. The current implementation records generated `blueprint_id` and deterministic `review_id` after source confirmation, stores pending required decisions in `current_decision_json`, can persist a proposed field-level blueprint revision inside that decision, stops for blueprint approval or revision, then after blueprint approval can record generated `candidate_ids_json` and stop for final insertion. Blueprint approval decisions summarize beat style contracts in `approval_summary.material_use_plan` under `style contracts:` so the stored decision remains reviewable without adding a new payload field. The final-insertion decision is an inspection boundary only: `ResumeReferenceOrchestrationRun` rejects `approve_final_insertion`, leaving the run parked with candidate ids until a separate user-confirmed chapter edit/save path handles prose insertion. Stale blueprint detection persists as a high-risk `resolve_high_risk_stop` decision with `stale_blueprint` in the approval summary, so a run can be inspected after the source plan invalidates a pending or approved blueprint. Material binding gaps persist as a high-risk `resolve_high_risk_stop` decision at `material_binding`, with `high_risk_gate_blocked`, missing beat ids in the approval summary, and error text retained for inspection; resolving that stop marks the run failed without free-drafting. Draft audit failure persists as a high-risk `resolve_high_risk_stop` decision at `draft_audit`, with candidate ids, stop reason, and error text retained for inspection; resolving that stop marks the run failed without inserting prose. Draft audit details remain available by re-auditing the persisted candidates; a later slice can add a first-class audit history column/table if the UI needs durable audit snapshots.

`reference_orchestration_run_events` is the append-only local run history for that state surface. It records `run_started`, `required_decision`, `decision_resumed`, `run_updated`, `run_failed`, `run_completed`, and `run_cancelled` events with the stage/status snapshot, stop reason, decision type, and compact summary. This gives the desktop bridge a durable answer for why a workflow stopped, what decision the AI proposed, what the user approved or cancelled, and which deterministic gate produced a block.
