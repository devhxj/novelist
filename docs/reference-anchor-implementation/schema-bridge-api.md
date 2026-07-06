# Reference Anchor Bridge API Surface

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to schema and integration index](schema-and-integration.md).

## Bridge API Surface

Current reference-anchor bridge methods:

```text
CreateReferenceAnchor
GetReferenceAnchors
DeleteReferenceAnchor
PromoteReferenceAnchorToWorkspaceCorpus
UpdateReferenceAnchorMetadata
RebuildReferenceAnchor
GetReferenceAnchorBuildStatus
SearchReferenceMaterials
UpdateReferenceMaterialTags
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
StartReferenceOrchestrationRun
GetReferenceOrchestrationRuns
GetReferenceOrchestrationRun
GetReferenceOrchestrationRunEvents
ResumeReferenceOrchestrationRun
CancelReferenceOrchestrationRun
```

`CreateReferenceAnchorPayload` accepts optional `visibility`, `source_trust`, and `user_tags` fields for corpus metadata; omitted values default to `private`, `user_verified`, and an empty tag list. Creating with `visibility = "workspace"` stores the anchor as a workspace-corpus row with nullable source ownership; creating with `private` or `restricted` keeps the anchor owned by the requested novel. `PromoteReferenceAnchorToWorkspaceCorpus` accepts a positive initiating `novel_id`, an owned per-novel `anchor_id`, and optional `source_trust` / `user_tags`; it converts that existing anchor to nullable workspace-corpus ownership without rebuilding source segments or extracted materials. Omitted promotion metadata preserves existing source trust and tags, while provided values replace those fields. `UpdateReferenceAnchorMetadata` accepts `novel_id`, `anchor_id`, title, author, license status, visibility, source trust, and user tags; it updates only corpus/list metadata, never source path, source hash, extracted segment ids, material ids, build state, or feedback rows. Updating a per-novel anchor to `visibility = "workspace"` stores it as nullable workspace-corpus ownership without reimporting materials. Already shared workspace-corpus anchors must remain workspace-visible until archive/delete policy is implemented, and private/restricted rows remain hidden from other novels even when clients pass explicit ids. `ReferenceAnchorPayload` returns `visibility`, `source_trust`, `user_tags`, `owner_scope`, and optional `owner_novel_id` alongside the existing source, license, hash, build, and status fields. `owner_scope` is `novel` for per-novel anchors and `workspace_corpus` for shared corpus rows, including rows stored with `reference_anchors.novel_id IS NULL`. The legacy `novel_id` field remains in the payload for existing clients and still reports `0` for workspace-corpus rows.

`SearchReferenceMaterials` returns paged `ReferenceMaterialPayload` items. Search requests accept optional `narrative_duties`, `emotion_transitions`, and `prose_duties` story-context filters in addition to material type, function, emotion, POV, and technique filters. Search responses attach optional `score_components` to each returned material for ranking explainability; stored material rows do not persist those transient components. Current score components cover lexical/tag fit, narrative duty, emotion transition, prose duty, embedding similarity when available, confidence, current-novel accepted feedback, and length. When the active Embeddings configuration matches a ready reference vector index, search adds transient `embedding` scores from sqlite-vec results and falls back to lexical/tag ranking if query embedding or vector search is unavailable. Search now treats `reference_anchors.novel_id IS NULL OR reference_anchors.novel_id = 0` plus `corpus_visibility = 'workspace'` as accessible workspace-corpus records, so a novel can read shared anchors/materials without seeing private anchors from other novels or private/restricted workspace rows; explicit `anchor_ids` do not bypass this filter. Per-novel feedback still records the consuming novel id, and accepted-material feedback contributes only a current-novel `accepted_feedback` ranking component. For anchors whose `license_status` is `unknown`, search/library preview payloads truncate exact source text by default; the full imported material text remains in SQLite and is still used for provenance, adaptation, material binding, and audit.

Material binding may run a deterministic expanded-query fallback when the exact beat query returns no candidates. Selected fallback links persist a negative `low_confidence` score component and a `fit_explanation` mentioning the expanded-query weak match. `GenerateReferenceAnchoredDraft` and `AuditReferenceAnchoredDraft` read the current selected material links for the blueprint `analysis_contract_hash`; draft candidates backed by low-confidence weak-match links fail draft audit with provenance risk and required fixes instead of silently passing to insertion.

Reference material search remains separate from workspace-wide `SearchAll` in the current implementation. `SearchAll` continues to return workspace entities, chapter/content matches, and story-memory RAG hits; reference material results are exposed through `SearchReferenceMaterials` and the dedicated reference UI so license-sensitive previews, ranking explanations, and material filters stay under the reference-anchor policy. Any future global search integration should be a staged opt-in change with explicit result types and preview rules.

`ReferenceChapterBlueprintPayload` exposes `build_version`, `context_hash`, `source_plan_hash`, and `analysis_contract_hash` as the reproducibility surface for generated blueprints. Blueprint rows do not persist prompt text, prompt templates, or schema snapshots; `review_version` is stored on review and approval rows so gate decisions remain auditable without prompt-churn in the blueprint table.

The Phase 11 orchestration bridge surface currently covers run state plus the automatic safe stages through audited candidates. `StartReferenceOrchestrationRun` accepts chapter goal, known facts, forbidden facts, optional explicit `anchor_ids`, and a `corpus_search_policy`. When `source_confirmed` is false, the run persists as `waiting_for_user` at `source_confirmation` with a required decision; confirming that decision resumes the run. Once source/fact boundaries are confirmed, the service automatically generates a deterministic blueprint, runs deterministic blueprint review, records `blueprint_id` and `review_id`, then stops for either `approve_blueprint` or `apply_blueprint_revision`. Failed blueprint review decisions can now carry a persisted `proposed_blueprint_revision` containing field-level changes from an injectable proposal provider; approving that decision applies the proposal, re-runs deterministic review, and then stops again for blueprint approval if the revised blueprint passes. The default proposal provider is deterministic, and desktop composition wires an AI-backed provider that uses the selected chat model, treats model output as untrusted JSON, filters changes to supported fields referenced by current review defects, and falls back deterministically when no model is selected or output is invalid. All provider proposals are rebound to the current blueprint/review, saved as pending decisions, and not applied automatically. `ReferenceOrchestrationStateMachine` centralizes the pure resume transition rules: source/fact confirmation resumes at blueprint generation, approved proposed revisions resume at blueprint review, blueprint approval resumes at material binding, and high-risk resolution records a terminal failed run. If the pending blueprint becomes stale before approval or later safe stages, orchestration now stops as a high-risk `resolve_high_risk_stop` decision with `stale_blueprint` in the approval summary, requiring regeneration and re-review before continuation. Approving a passing blueprint resumes safe automation: material binding, beat candidate generation, and draft audit. During orchestration binding, `corpus_search_policy` include/exclude anchor filters, license-status filters, and workspace-corpus visibility filters are applied before reference materials are selected; explicit `anchor_ids` remain an advanced override rather than the default path. Material binding gaps now stop as a high-risk `resolve_high_risk_stop` decision at `material_binding` when any source-backed beat lacks a selected current material link, with missing beat ids in the approval summary and `high_risk_gate_blocked` in `last_stop_reason`; resolving that stop records the run as failed instead of free-drafting. Passing draft audit stops at `final_insertion` with candidate ids persisted for user review. `ResumeReferenceOrchestrationRun` deliberately rejects `approve_final_insertion`, so the orchestration bridge cannot mark final insertion complete or write chapter prose; final prose insertion must use a separate user-confirmed chapter edit/save path. Draft audit failure is now a persisted high-risk stop: the run remains `waiting_for_user` at `draft_audit` with a `resolve_high_risk_stop` decision, candidate ids, audit findings in the approval summary, and the failure text in `error_message`; resolving that stop records the run as failed rather than inserting prose. Draft audit high-risk stops cover forbidden facts, unsupported new facts, POV leaks, and L3/L4 rewrite risk. Infrastructure/configuration binding errors still record a failed run because the workflow cannot produce an auditable user decision. `GetReferenceOrchestrationRuns`, `GetReferenceOrchestrationRun`, `GetReferenceOrchestrationRunEvents`, `ResumeReferenceOrchestrationRun`, and `CancelReferenceOrchestrationRun` allow clients to inspect state, inspect event history, resume safe decisions, and cancel the persisted run. Run events are returned in `event_id` order and record run starts, required decisions, user resumes/approvals, stops, failures, and cancellations with stage, status, stop reason, decision type, and a compact summary. The frontend default-flow UI is mounted and the agent can inspect run-event history read-only; full shared-corpus management UI and AI retrieval-gap handling remain pending Phase 12 work.

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
- `UpdateReferenceMaterialTags` updates the stored material row for user-confirmed function, emotion, POV, scene, or technique tags and marks it `user_verified`
