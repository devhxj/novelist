# Reference Anchor Bridge API Surface

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to schema and integration index](schema-and-integration.md).

## Bridge API Surface

Current reference-anchor bridge methods:

```text
CreateReferenceAnchor
CreateReferenceAnchors
GetReferenceAnchors
DeleteReferenceAnchor
DeleteReferenceAnchors
DeleteReferenceMaterials
RestoreReferenceMaterials
PromoteReferenceAnchorToWorkspaceCorpus
PromoteReferenceAnchorsToWorkspaceCorpus
UpdateReferenceAnchorMetadata
RebuildReferenceAnchor
GetReferenceAnchorBuildStatus
SearchReferenceMaterials
UpdateReferenceMaterialTags
UpdateReferenceMaterialsTags
AdaptReferenceMaterial
AuditReferenceReuse
RecordReferenceUserFeedback
GetReferenceUserFeedback
GenerateReferenceChapterBlueprint
GetReferenceChapterBlueprints
GetReferenceChapterBlueprint
ReviewReferenceChapterBlueprint
ReviseReferenceChapterBlueprint
ApproveReferenceChapterBlueprint
BindReferenceBlueprintMaterials
GenerateReferenceAnchoredDraft
AuditReferenceAnchoredDraft
BuildReferenceStyleProfile
GetReferenceStyleProfiles
GetReferenceStyleProfile
StartReferenceOrchestrationRun
GetReferenceOrchestrationRuns
GetReferenceOrchestrationRun
GetReferenceOrchestrationRunEvents
ResumeReferenceOrchestrationRun
CancelReferenceOrchestrationRun
```

`CreateReferenceAnchorPayload` accepts optional `visibility`, `source_trust`, and `user_tags` fields for corpus metadata; omitted values default to `private`, `user_verified`, and an empty tag list. Creating with `visibility = "workspace"` stores the anchor as a workspace-corpus row with nullable source ownership; creating with `private` or `restricted` keeps the anchor owned by the requested novel. `CreateReferenceAnchorsPayload` wraps an `anchors` array for bridge/API bulk source import. The current service implementation validates a non-empty batch of at most 50 items, imports them sequentially through the same single-anchor path, preserves response order, and returns the created `ReferenceAnchorPayload` rows. The frontend can expand a simple JSON library-pack manifest into this payload, but the bridge remains a controlled bulk entrypoint rather than an all-or-nothing transactional pack importer. `PromoteReferenceAnchorToWorkspaceCorpus` accepts a positive initiating `novel_id`, an owned per-novel `anchor_id`, and optional `source_trust` / `user_tags`; it converts that existing anchor to nullable workspace-corpus ownership without rebuilding source segments or extracted materials. Omitted promotion metadata preserves existing source trust and tags, while provided values replace those fields. `UpdateReferenceAnchorMetadata` accepts `novel_id`, `anchor_id`, title, author, license status, visibility, source trust, and user tags; it updates only corpus/list metadata, never source path, source hash, extracted segment ids, material ids, build state, or feedback rows. Updating a per-novel anchor to `visibility = "workspace"` stores it as nullable workspace-corpus ownership without reimporting materials. Workspace-corpus archive/delete policy hides shared rows by moving them to restricted visibility while preserving source/material provenance, and private/restricted rows remain hidden from other novels even when clients pass explicit ids. `ReferenceAnchorPayload` returns `visibility`, `source_trust`, `user_tags`, `owner_scope`, and optional `owner_novel_id` alongside the existing source, license, hash, build, and status fields. `owner_scope` is `novel` for per-novel anchors and `workspace_corpus` for shared corpus rows, including rows stored with `reference_anchors.novel_id IS NULL`. The legacy `novel_id` field remains in the payload for existing clients and still reports `0` for workspace-corpus rows.

`SearchReferenceMaterials` returns paged `ReferenceMaterialPayload` items. Search requests accept optional `narrative_duties`, `emotion_transitions`, and `prose_duties` story-context filters in addition to material type, function, emotion, POV, and technique filters. Material type filters can target the original `sentence` / `passage` materials or Phase 14 multi-scale materials: `scene`, `beat`, `dialogue_exchange`, `action_afterbeat`, `image_motif`, `hook`, `payoff`, and `transition`. They also accept optional `style_profile_ids`, `style_dimensions`, and `imitation_intensity` (`diagnostic_only`, `loose`, `moderate`, or `strong`) for style-aware ranking from deterministic profile evidence; inaccessible, archived, or cross-novel profile ids fail the request instead of silently dropping the style constraint. They also accept optional `archive_filter` values of `active`, `archived`, or `all`; omitted values default to `active` so normal retrieval continues to hide archived rows. Search responses attach optional `score_components` to each returned material for ranking explainability; stored material rows do not persist those transient components. Current score components cover lexical/tag fit, narrative duty, emotion transition, prose duty, style fit, source-risk penalty for same-source strong/moderate style requests, embedding similarity when available, confidence, current-novel accepted feedback, and length. When the active Embeddings configuration matches a ready reference vector index, search adds transient `embedding` scores from sqlite-vec results and falls back to lexical/tag ranking if query embedding or vector search is unavailable. Search now treats `reference_anchors.novel_id IS NULL OR reference_anchors.novel_id = 0` plus `corpus_visibility = 'workspace'` as accessible workspace-corpus records, so a novel can read shared anchors/materials without seeing private anchors from other novels or private/restricted workspace rows; explicit `anchor_ids` do not bypass this filter. Archived material rows are hidden from normal search, adaptation, audit, tag-correction, and vector-scoring paths while retaining source segments and material provenance rows. `UpdateReferenceMaterialTags` corrects one material's function/emotion/scene/POV/technique tags; `UpdateReferenceMaterialsTags` applies provided tag overrides transactionally to a selected material-id list and rolls back when any selected material is inaccessible. `DeleteReferenceMaterials` soft-archives selected accessible material ids transactionally by setting `reference_materials.archived_at`; it rolls back if any selected material is inaccessible, preserves the archive marker across anchor rebuilds, and never deletes source segments, material rows, historical links, candidates, or audit provenance. `RestoreReferenceMaterials` clears `archived_at` for selected accessible archived material ids transactionally and rolls back if any selected material is inaccessible; restored rows become visible to default search/adaptation/audit/tag-correction paths again. Per-novel feedback still records the consuming novel id, and accepted-material feedback contributes only a current-novel `accepted_feedback` ranking component. For anchors whose `license_status` is `unknown`, search/library preview payloads truncate exact source text by default; the full imported material text remains in SQLite and is still used for provenance, adaptation, material binding, and audit unless that material row has been archived.

`ReferenceChapterBlueprintBeatPayload.style_contract` is the first Phase 14 beat-level style contract surface. It can carry `style_profile_ids`, `style_dimensions`, `imitation_intensity`, `min_style_fit`, `allowed_closeness`, `required_evidence_types`, and `forbidden_style_risks`. The contract is part of `analysis_contract_hash`; revising it invalidates approval/material links like other reviewed beat fields. Deterministic blueprint review treats invalid style contracts as hard defects: style-aware beats must name positive style profile ids, carry at least one style duty or required evidence type, keep strong imitation above the minimum style-fit floor, and ensure required evidence labels or material granularities are compatible with the beat's effective material search.

Material binding may run a deterministic expanded-query fallback when the exact beat query returns no candidates. Selected fallback links persist a negative `low_confidence` score component and a `fit_explanation` mentioning the expanded-query weak match. When a beat has a style contract, material binding passes style profile ids, dimensions, and imitation intensity into `SearchReferenceMaterials`; selected links below `min_style_fit` also persist `low_confidence` plus `style_fit_gap` and explain the low style-fit weak match. `GenerateReferenceAnchoredDraft` and `AuditReferenceAnchoredDraft` read the current selected material links for the blueprint `analysis_contract_hash`; draft candidates backed by low-confidence weak-match links fail draft audit with provenance risk and required fixes instead of silently passing to insertion. For non-L0/L1 candidates, draft audit compares the candidate against the selected material source text with deterministic source-leak thresholds; beats using strong style contracts apply stricter overlap/coverage/span thresholds and can fail even when the selected link has high `style_fit`. Draft audit also reads persisted active style profile feature vectors when a beat style contract references them, verifies selected-link `style_fit` against `min_style_fit`, and fails candidates whose observable deterministic style features are too far from supported profile numeric dimensions such as `dialogue_ratio`, `interiority_ratio`, `sensory_ratio`, `transition_ratio`, hook markers, punctuation rhythm, and average sentence length.

Reference material search remains separate from workspace-wide `SearchAll` in the current implementation. `SearchAll` continues to return workspace entities, chapter/content matches, and story-memory RAG hits; reference material results are exposed through `SearchReferenceMaterials` and the dedicated reference UI so license-sensitive previews, ranking explanations, and material filters stay under the reference-anchor policy. Any future global search integration should be a staged opt-in change with explicit result types and preview rules.

`ReferenceChapterBlueprintPayload` exposes `build_version`, `context_hash`, `source_plan_hash`, and `analysis_contract_hash` as the reproducibility surface for generated blueprints. Blueprint rows do not persist prompt text, prompt templates, or schema snapshots; `review_version` is stored on review and approval rows so gate decisions remain auditable without prompt-churn in the blueprint table.

The Phase 11 orchestration bridge surface currently covers run state plus the automatic safe stages through audited candidates. `StartReferenceOrchestrationRun` accepts chapter goal, known facts, forbidden facts, optional explicit `anchor_ids`, and a `corpus_search_policy`. When the policy is omitted or its license list is empty, service normalization uses `story_context`, `max_results_per_beat = 3`, empty include/exclude anchor filters, and `license_statuses = ["user_provided"]`; clients must explicitly include `unknown` if a run should use unknown-license material. When `source_confirmed` is false, the run persists as `waiting_for_user` at `source_confirmation` with a required decision; confirming that decision resumes the run. Once source/fact boundaries are confirmed, the service automatically generates a deterministic blueprint, runs deterministic blueprint review, records `blueprint_id` and `review_id`, then stops for either `approve_blueprint` or `apply_blueprint_revision`. Failed blueprint review decisions can now carry a persisted `proposed_blueprint_revision` containing field-level changes from an injectable proposal provider; approving that decision applies the proposal, re-runs deterministic review, and then stops again for blueprint approval if the revised blueprint passes. The default proposal provider is deterministic, and desktop composition wires an AI-backed provider that uses the selected chat model, treats model output as untrusted JSON, filters changes to supported fields referenced by current review defects, and falls back deterministically when no model is selected or output is invalid. All provider proposals are rebound to the current blueprint/review, saved as pending decisions, and not applied automatically.

`ReferenceOrchestrationStateMachine` centralizes the pure resume transition rules: source/fact confirmation resumes at blueprint generation, approved proposed revisions resume at blueprint review, blueprint approval resumes at material binding, and high-risk resolution records a terminal failed run. If the pending blueprint becomes stale before approval or later safe stages, orchestration now stops as a high-risk `resolve_high_risk_stop` decision with `stale_blueprint` in the approval summary, requiring regeneration and re-review before continuation. Approving a passing blueprint resumes safe automation: material binding, beat candidate generation, and draft audit. Blueprint approval summaries compact style contracts inside `approval_summary.material_use_plan` with a `style contracts:` section listing beat index, profile ids, imitation intensity, minimum fit, closeness, dimensions, required evidence, and forbidden style risks. During orchestration binding, `corpus_search_policy` include/exclude anchor filters, license-status filters, and workspace-corpus visibility filters are applied before reference materials are selected; explicit `anchor_ids` remain an advanced override rather than the default path.

Material binding gaps now stop as a high-risk `resolve_high_risk_stop` decision at `material_binding` when any source-backed beat lacks a selected current material link, with missing beat ids in the approval summary and `high_risk_gate_blocked` in `last_stop_reason`; resolving that stop records the run as failed instead of free-drafting. Passing draft audit stops at `final_insertion` with candidate ids persisted for user review. `ResumeReferenceOrchestrationRun` deliberately rejects `approve_final_insertion`, so the orchestration bridge cannot mark final insertion complete or write chapter prose; final prose insertion must use a separate user-confirmed chapter edit/save path. Draft audit failure is now a persisted high-risk stop: the run remains `waiting_for_user` at `draft_audit` with a `resolve_high_risk_stop` decision, candidate ids, audit findings in the approval summary, and the failure text in `error_message`; resolving that stop records the run as failed rather than inserting prose. Draft audit high-risk stops cover forbidden facts, unsupported new facts, POV leaks, and L3/L4 rewrite risk. Infrastructure/configuration binding errors still record a failed run because the workflow cannot produce an auditable user decision.

`GetReferenceOrchestrationRuns`, `GetReferenceOrchestrationRun`, `GetReferenceOrchestrationRunEvents`, `ResumeReferenceOrchestrationRun`, and `CancelReferenceOrchestrationRun` allow clients to inspect state, inspect event history, resume safe decisions, and cancel the persisted run. Run events are returned in `event_id` order and record run starts, required decisions, user resumes/approvals, stops, failures, and cancellations with stage, status, stop reason, decision type, and a compact summary. Phase 12 completes the shared-corpus management and default AI retrieval boundary: the frontend separates `语料库管理` from `参考写作检索`, supports source and manifest import, metadata management, promotion/archive/restore, material browsing and correction, and default story-context retrieval without selected anchors. Deeper library product IA and hard-delete administration remain future product work; app-wide stress/usability coverage closed in Phase 13 and should stay in the regression matrix rather than revisiting Phase 12.

Phase 14 adds the first style-profile bridge surface:

- `BuildReferenceStyleProfile` accepts `novel_id`, title, description, selected `anchor_ids`, allowed license statuses, and allowed source-trust levels. It builds a deterministic baseline profile from accessible active materials only. Empty policy lists use conservative defaults: `user_provided`, `licensed`, `public_domain` for license status and `user_verified`, `imported` for source trust; `unknown` and `unverified` require explicit opt-in.
- `GetReferenceStyleProfiles` lists profile summaries for a novel and can include archived rows when requested.
- `GetReferenceStyleProfile` returns a profile detail with structured feature vectors and evidence spans. Evidence spans expose provenance ids, source offsets, hashes, confidence, and analyzer source, but not source text.

The style-profile bridge is inspect/build only at this boundary. It does not approve style contracts, generate style-guided prose, call `SaveContent`, or expose arbitrary source-file reads.

Implementation files:

```text
src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs
src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs
src/Novelist.Contracts/App/ReferenceStylePayloads.cs
src/Novelist.Core/App/IReferenceAnchorService.cs
src/Novelist.Core/App/IReferenceAnchoredDraftService.cs
src/Novelist.Core/App/IReferenceStyleProfileService.cs
src/Novelist.Core/Bridge/ReferenceAnchorBridgeHandlers.cs
src/Novelist.Core/Bridge/ReferenceAnchoredDraftBridgeHandlers.cs
src/Novelist.Core/Bridge/ReferenceStyleProfileBridgeHandlers.cs
src/Novelist.Core/Bridge/BridgeCompatibilityAppMethods.cs
```

Handler pattern should mirror `WorkspaceUtilityBridgeHandlers`:

- parse args array
- deserialize object payloads through `BridgeJson.SerializerOptions`
- validate primitive ids at boundary
- throw `BridgeValidationException` for shape issues
- let service-level `ArgumentException` map to validation errors
- return structured payloads
- `UpdateReferenceMaterialTags` updates the stored material row for user-confirmed function, emotion, POV, scene, or technique tags and marks it `user_verified`
