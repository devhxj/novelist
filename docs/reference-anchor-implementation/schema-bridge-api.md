# Reference Anchor Bridge API Surface

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to schema and integration index](schema-and-integration.md).

## Bridge API Surface

Current reference-anchor bridge methods:

```text
CreateReferenceAnchor
GetReferenceAnchors
DeleteReferenceAnchor
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
ResumeReferenceOrchestrationRun
CancelReferenceOrchestrationRun
```

`SearchReferenceMaterials` returns paged `ReferenceMaterialPayload` items. Search requests accept optional `narrative_duties` and `emotion_transitions` filters in addition to material type, function, emotion, POV, and technique filters. Search responses attach optional `score_components` to each returned material for ranking explainability; stored material rows do not persist those transient components. When the active Embeddings configuration matches a ready reference vector index, search adds transient `embedding` scores from sqlite-vec results and falls back to lexical/tag ranking if query embedding or vector search is unavailable. For anchors whose `license_status` is `unknown`, search/library preview payloads truncate exact source text by default; the full imported material text remains in SQLite and is still used for provenance, adaptation, material binding, and audit.

Reference material search remains separate from workspace-wide `SearchAll` in the current implementation. `SearchAll` continues to return workspace entities, chapter/content matches, and story-memory RAG hits; reference material results are exposed through `SearchReferenceMaterials` and the dedicated reference UI so license-sensitive previews, ranking explanations, and material filters stay under the reference-anchor policy. Any future global search integration should be a staged opt-in change with explicit result types and preview rules.

`ReferenceChapterBlueprintPayload` exposes `build_version`, `context_hash`, `source_plan_hash`, and `analysis_contract_hash` as the reproducibility surface for generated blueprints. Blueprint rows do not persist prompt text, prompt templates, or schema snapshots; `review_version` is stored on review and approval rows so gate decisions remain auditable without prompt-churn in the blueprint table.

The Phase 11 orchestration bridge surface currently covers run state plus the automatic safe stages through audited candidates. `StartReferenceOrchestrationRun` accepts chapter goal, known facts, forbidden facts, optional explicit `anchor_ids`, and a `corpus_search_policy`. When `source_confirmed` is false, the run persists as `waiting_for_user` at `source_confirmation` with a required decision; confirming that decision resumes the run. Once source/fact boundaries are confirmed, the service automatically generates a deterministic blueprint, runs deterministic blueprint review, records `blueprint_id` and `review_id`, then stops for either `approve_blueprint` or `apply_blueprint_revision`. Failed blueprint review decisions can now carry a persisted `proposed_blueprint_revision` containing field-level changes; approving that decision applies the proposal, re-runs deterministic review, and then stops again for blueprint approval if the revised blueprint passes. Approving a passing blueprint resumes safe automation: material binding, beat candidate generation, and draft audit. Passing draft audit stops at `final_insertion` with candidate ids persisted for user review; binding or audit failure records a failed run instead of inserting prose. `GetReferenceOrchestrationRuns`, `GetReferenceOrchestrationRun`, `ResumeReferenceOrchestrationRun`, and `CancelReferenceOrchestrationRun` allow clients to inspect, resume, and cancel the persisted run. Agent controls, frontend default-flow UI, broad high-risk stop coverage, and shared corpus retrieval remain pending Phase 11/12 work.

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
