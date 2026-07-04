# Reference Anchor Schema and Integration Plan

[Back to implementation index](../reference-anchor-implementation-plan.md).

## Database Schema Plan

Create schema in `SqliteReferenceAnchorService.EnsureSchemaAsync`.

Tables:

```text
reference_anchors
reference_anchor_build_state
reference_source_segments
reference_materials
reference_material_slots
reference_material_scores
reference_reuse_candidates
reference_reuse_audits
reference_user_feedback
reference_chapter_blueprints
reference_chapter_blueprint_beats
reference_chapter_blueprint_reviews
reference_chapter_blueprint_revisions
reference_blueprint_material_links
reference_draft_paragraph_candidates
reference_draft_audits
```

Minimum columns:

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
- review_version TEXT NOT NULL
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
- reviewed_at TEXT NOT NULL

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
- status TEXT NOT NULL
- created_at TEXT NOT NULL

`score_components_json` currently records material type, function, emotion, POV, prose-duty, lexical, confidence, `user_verified`, and accepted-feedback boosts when applicable.

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

reference_draft_audits
- audit_id TEXT PRIMARY KEY
- candidate_id TEXT NOT NULL
- blueprint_id INTEGER NOT NULL
- beat_id TEXT NOT NULL
- status TEXT NOT NULL
- rewrite_level TEXT NOT NULL
- provenance_errors_json TEXT NOT NULL
- blueprint_errors_json TEXT NOT NULL
- unsupported_fact_errors_json TEXT NOT NULL
- pov_errors_json TEXT NOT NULL
- ai_prose_risks_json TEXT NOT NULL
- required_fixes_json TEXT NOT NULL
- audited_at TEXT NOT NULL

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

Add indexes:

```text
idx_reference_anchors_novel
idx_reference_segments_anchor_type
idx_reference_materials_anchor_type
idx_reference_materials_tags
idx_reference_candidates_material
idx_reference_blueprints_novel_chapter
idx_reference_blueprint_beats_blueprint
idx_reference_blueprint_reviews_blueprint
idx_reference_blueprint_revisions_blueprint
idx_reference_blueprint_links_beat
idx_reference_draft_candidates_blueprint
idx_reference_feedback_novel_target
idx_reference_feedback_material
```

Enable foreign key enforcement on every SQLite connection:

```sql
PRAGMA foreign_keys = ON;
```

The current RAG service does not enable foreign keys because it has only two flat tables. Reference-anchor storage has real parent/child integrity and should enable it.

## Bridge API Plan

Add:

```text
CreateReferenceAnchor
GetReferenceAnchors
DeleteReferenceAnchor
RebuildReferenceAnchor
GetReferenceAnchorBuildStatus
SearchReferenceMaterials
AdaptReferenceMaterial
AuditReferenceReuse
GenerateReferenceChapterBlueprint
GetReferenceChapterBlueprints
GetReferenceChapterBlueprint
ReviewReferenceChapterBlueprint
ReviseReferenceChapterBlueprint
ApproveReferenceChapterBlueprint
BindReferenceBlueprintMaterials
GenerateReferenceAnchoredDraft
AuditReferenceAnchoredDraft
```

Implementation files:

```text
src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs
src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs
src/Novelist.Core/App/IReferenceAnchorService.cs
src/Novelist.Core/App/IReferenceAnchoredDraftService.cs
src/Novelist.Core/Bridge/ReferenceAnchorBridgeHandlers.cs
src/Novelist.Core/Bridge/ReferenceAnchoredDraftBridgeHandlers.cs
src/Novelist.Core/Bridge/BridgeCompatibilityAppMethods.cs
```

Handler pattern should mirror `WorkspaceUtilityBridgeHandlers`:

- parse args array
- deserialize object payloads through `BridgeJson.SerializerOptions`
- validate primitive ids at boundary
- throw `BridgeValidationException` for shape issues
- let service-level `ArgumentException` map to validation errors
- return structured payloads

## Desktop Composition Plan

Update `src/Novelist.App/Desktop/PhotinoWindowFactory.cs`.

Construct after embedding/RAG dependencies:

```csharp
var referenceAnchorService = new SqliteReferenceAnchorService(
    appOptions,
    novelService,
    embeddingService,
    embeddingClient,
    sqliteVecProvider,
    sqliteVecProvider);

var referenceAnchoredDraftService = new SqliteReferenceAnchoredDraftService(
    appOptions,
    referenceAnchorService,
    chapterContentService,
    planningService,
    worldService,
    llmService);
```

Then:

```csharp
var chatToolExecutor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
    storyMemoryService,
    chapterContentService,
    approvalCoordinator,
    eventSink,
    subagentRunner,
    preferenceService,
    worldService,
    planningService,
    webFetchService,
    webSearchService,
    referenceAnchorService,
    referenceAnchoredDraftService));
```

And:

```csharp
.RegisterReferenceAnchorHandlers(referenceAnchorService)
.RegisterReferenceAnchoredDraftHandlers(referenceAnchoredDraftService)
```

The exact constructor should be added in a backward-compatible way:

- keep existing constructor signatures working for tests
- add optional `IReferenceAnchorService? referenceAnchors = null` parameter near the end
- add optional `IReferenceAnchoredDraftService? referenceDrafts = null` after reference anchors

## Agent Tool Plan

`NovelistMafToolRegistry` carries optional reference dependencies:

```csharp
private readonly IReferenceAnchorService? _referenceAnchors;
private readonly IReferenceAnchoredDraftService? _referenceDrafts;
```

Reference material tools live in `NovelistMafReferenceTools.cs`:

```text
get_reference_anchors
search_reference_materials
adapt_reference_material
audit_reference_reuse
```

Blueprint-gated drafting tools are also grouped in `NovelistMafReferenceTools.cs` under the draft tool adapter:

```text
generate_reference_chapter_blueprint
review_reference_chapter_blueprint
revise_reference_chapter_blueprint
approve_reference_chapter_blueprint
bind_reference_blueprint_materials
generate_reference_anchored_draft
audit_reference_anchored_draft
```

Current status:

- tool registration is optional: reference tools appear only when reference services are configured;
- `novel_id` is injected from `NovelistMafToolContext`, not exposed to the model schema;
- reference tools do not expose session/turn/tool internals;
- reference draft tools return blueprints, material links, candidates, and audits only;
- no reference tool is allowed to call `SaveContent` or mutate chapter prose.

Tool limits:

- `search_reference_materials`: max page size 20
- `adapt_reference_material`: requires `material_id`, `slot_values`, `max_rewrite_level`
- `audit_reference_reuse`: pure check only
- `generate_reference_chapter_blueprint`: requires `chapter_number`, optional user chapter goal, known facts, forbidden facts, and active anchor ids; it must return logic, emotion, narration, character, reference, transition, and execution tracks
- `review_reference_chapter_blueprint`: pure check only; must not revise the blueprint silently
- `revise_reference_chapter_blueprint`: requires explicit field-level changes, records a revision, and invalidates approval/material links when reviewed fields change
- `approve_reference_chapter_blueprint`: allowed only after a passing review
- `bind_reference_blueprint_materials`: allowed only after explicit approval; must bind material to beat duties, not only semantic similarity
- `generate_reference_anchored_draft`: requires an approved and material-bound `blueprint_id`; returns candidates only
- `audit_reference_anchored_draft`: pure check only
- no `SaveContent`
- no direct file path reads

Tool schemas must not expose `novel_id`, `session_id`, `turn_id`, or `tool_id`.

Agent workflow order must be enforced in tool descriptions and service validation:

```text
search/reference context
  -> generate_reference_chapter_blueprint
  -> review_reference_chapter_blueprint
  -> revise_reference_chapter_blueprint when review fails
  -> review_reference_chapter_blueprint again after revision
  -> approve_reference_chapter_blueprint
  -> bind_reference_blueprint_materials
  -> generate_reference_anchored_draft
  -> audit_reference_anchored_draft
```

Agent hardening still required:

- tool-description regression tests proving models are told to create/review a blueprint before drafting;
- schema regression tests proving internal fields remain hidden.

## Frontend Plan

Update API/type layer:

```text
frontend/src/lib/novelist/types.ts
frontend/src/lib/novelist/api.ts
frontend/src/hooks/useApp.ts
```

Add namespace:

```ts
export namespace reference {
  export interface Anchor { ... }
  export interface BuildStatus { ... }
  export interface Material { ... }
  export interface SearchMaterialsInput { ... }
  export interface ReuseCandidate { ... }
  export interface ReuseAudit { ... }
  export interface ChapterBlueprint { ... }
  export interface ChapterBlueprintAnalysisTrack { ... }
  export interface ChapterBlueprintExecutionTrack { ... }
  export interface ChapterBlueprintBeat { ... }
  export interface ChapterBlueprintReview { ... }
  export interface AnchoredDraft { ... }
  export interface AnchoredDraftAudit { ... }
}
```

Add view components:

```text
frontend/src/components/reference-anchor/ReferenceAnchorView.tsx
frontend/src/components/reference-anchor/ReferenceAnchorImportDialog.tsx
frontend/src/components/reference-anchor/ReferenceMaterialSearch.tsx
frontend/src/components/reference-anchor/ReferenceCandidatePreview.tsx
frontend/src/components/reference-anchor/ReferenceChapterBlueprintView.tsx
frontend/src/components/reference-anchor/ReferenceBlueprintBeatEditor.tsx
frontend/src/components/reference-anchor/ReferenceBlueprintReviewPanel.tsx
frontend/src/components/reference-anchor/ReferenceAnchoredDraftPreview.tsx
```

Update:

```text
frontend/src/components/shell/ActivityBar.tsx
frontend/src/views/WorkspaceView.tsx
```

UI first version:

- list anchors for active novel
- create anchor from local `.txt`/`.md` path
- rebuild anchor
- show build state and counts
- search material bank with filters
- preview source/adapted/diff/audit
- generate a chapter blueprint for a selected `chapter_number`
- show the five blueprint analysis tracks: logic, emotion, narration, character, and reference use
- show the execution track separately: paragraph intention, execution mode, anti-screenplay duty, source-backed detail target, and candidate rejection rule
- show blueprint beats, causality chain, transition plan, emotion trajectory, character state deltas, POV constraints, forbidden facts, prose duties, and execution duties
- run blueprint review and block draft generation until it passes
- expose review defects as actionable fields rather than a single free-form critique
- invalidate approval after user edits a beat or analysis track
- bind reference materials to blueprint beats
- generate draft candidates only from approved blueprints
- show draft text alongside source material, blueprint beat, rewrite level, and audit result
- copy candidate; no automatic insertion in phase 1

Current frontend status:

- `frontend/src/components/reference-anchor/ReferenceAnchorView.tsx` exists as the first full main-panel implementation.
- `ActivityBar.tsx` has a `reference` activity entry.
- `WorkspaceView.tsx` renders `ReferenceAnchorView` for the active novel.
- The first panel supports anchor create/rebuild/list, material search, blueprint generate/list/detail/review/approve, material binding, and draft candidate preview.
- The first panel does not yet provide field-level beat editing, a dedicated side-panel list/filter, separated child components, material score-component explanations, or copy-to-clipboard controls.

If direct file picker is needed, add it as a separate runtime capability later. Do not block backend implementation on an open-file native dialog.

### Desktop Debugging Plan

The reference-anchor feature should not change the desktop launch contract. If Photino reports that `frontend/dist/index.html` cannot be found, fix the frontend asset workflow:

- run `npm --prefix frontend run build` before `make dev` when loading local built assets;
- or start Vite and launch the app with the existing `--start-url=` development path;
- improve the launch error message if needed so it points to the missing frontend build;
- do not add ASP.NET Core/Kestrel merely to mask a missing local asset;
- keep bridge business calls independent from whether assets are loaded from `file://`, packaged dist, or Vite dev server.
