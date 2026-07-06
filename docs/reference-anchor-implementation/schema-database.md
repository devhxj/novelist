# Reference Anchor Database Schema

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to schema and integration index](schema-and-integration.md).

## Database Schema

The current schema is created by `SqliteReferenceAnchorService.EnsureSchemaAsync` and `SqliteReferenceAnchoredDraftService.EnsureSchemaAsync` in the dedicated reference-anchor SQLite database.

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
```

Core columns:

```text
reference_anchors
- anchor_id INTEGER PRIMARY KEY
- novel_id INTEGER NOT NULL
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

`reference_anchors.novel_id = 0` is reserved as the workspace-corpus compatibility owner. Public create/rebuild/delete APIs still require a positive novel id. Read paths for listing anchors, build status, material search, material adaptation, reuse audit, tag correction, and per-novel feedback validation include the active novel's private anchors plus `novel_id = 0` anchors whose `corpus_visibility = 'workspace'`, while continuing to exclude private/restricted workspace rows and private anchors owned by other novels. Explicit `anchor_ids` do not bypass the visibility filter. Existing databases that already had `novel_id = 0` compatibility rows before these columns are added promote those legacy rows to `corpus_visibility = 'workspace'` once during migration; new per-novel anchors default to `private`. `reference_user_feedback.novel_id` remains the consuming novel id, so feedback about a shared material is still scoped to the novel that used it.

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
- risk_flags_json TEXT NOT NULL

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

`score_components_json` currently records material type, function, emotion, POV, prose-duty, lexical, embedding similarity, confidence, `user_verified`, current-novel accepted-feedback boosts, and negative `low_confidence` markers for expanded-query weak matches when applicable. Draft generation and persisted draft re-audit read the selected link for the current `analysis_contract_hash`; a low-confidence selected link turns into draft-audit provenance risk until the material is rebound, the blueprint query is revised, or the retrieval gap is explicitly resolved.

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
```

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
idx_reference_feedback_novel_target
idx_reference_feedback_material
```

Foreign key enforcement is enabled on reference-anchor SQLite connections:

```sql
PRAGMA foreign_keys = ON;
```

The current RAG service does not enable foreign keys because it has only two flat tables. Reference-anchor storage has real parent/child integrity and enables it.

`reference_orchestration_runs` is the Phase 11 run state and resume surface. It persists stage/status, source/fact decision details, optional explicit anchors, corpus search policy, artifact ids, stop reason, and error text so a run can be inspected or resumed after restart. The current implementation records generated `blueprint_id` and deterministic `review_id` after source confirmation, stores pending required decisions in `current_decision_json`, can persist a proposed field-level blueprint revision inside that decision, stops for blueprint approval or revision, then after blueprint approval can record generated `candidate_ids_json` and stop for final insertion. The final-insertion decision is an inspection boundary only: `ResumeReferenceOrchestrationRun` rejects `approve_final_insertion`, leaving the run parked with candidate ids until a separate user-confirmed chapter edit/save path handles prose insertion. Stale blueprint detection persists as a high-risk `resolve_high_risk_stop` decision with `stale_blueprint` in the approval summary, so a run can be inspected after the source plan invalidates a pending or approved blueprint. Material binding gaps persist as a high-risk `resolve_high_risk_stop` decision at `material_binding`, with `high_risk_gate_blocked`, missing beat ids in the approval summary, and error text retained for inspection; resolving that stop marks the run failed without free-drafting. Draft audit failure persists as a high-risk `resolve_high_risk_stop` decision at `draft_audit`, with candidate ids, stop reason, and error text retained for inspection; resolving that stop marks the run failed without inserting prose. Draft audit details remain available by re-auditing the persisted candidates; a later slice can add a first-class audit history column/table if the UI needs durable audit snapshots.

`reference_orchestration_run_events` is the append-only local run history for that state surface. It records `run_started`, `required_decision`, `decision_resumed`, `run_updated`, `run_failed`, `run_completed`, and `run_cancelled` events with the stage/status snapshot, stop reason, decision type, and compact summary. This gives the desktop bridge a durable answer for why a workflow stopped, what decision the AI proposed, what the user approved or cancelled, and which deterministic gate produced a block.
