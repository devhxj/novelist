# Reference Anchor Tasks: Phases 5-9

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

## Phase 5: Controlled Adaptation, Draft Generation, and Audit

**Description:** Implement L1/L2 material adaptation, paragraph candidate generation from approved blueprints, and deterministic draft audit.

Draft generation must be understood as candidate assembly from approved blueprint beats and audited material reuse. It is not a general chapter writer.

Recommended implementation slices:

1. [x] Promote slot detection into first-class storage and make L1 adaptation depend on stored slot definitions.
2. [x] Extract rewrite-level classification into a separately testable component.
3. [x] Add a draft preflight component that loads blueprint, approval, review, and material links together and rejects stale hashes before any generation.
4. Implement beat-level candidate generation for one approved and material-bound beat at a time.
5. Keep the candidate generator separate from the auditor. The generator may be model-assisted, but the auditor decides whether the candidate is usable.
6. Audit every candidate against material provenance, rewrite level, known facts, forbidden facts, POV, emotion evidence, and prose duties.
7. Add deterministic action-only screenplay drift detection after dialogue-only drift, with conservative false-positive controls.
8. Enforce explicit emotion evidence targets when a beat declares `required external evidence:` or `required emotion evidence:`.
9. Only after beat-level audit is stable, add optional full-chapter candidate assembly from passing beat candidates.

**Acceptance criteria:**

- [x] `AdaptMaterialAsync` still supports standalone preview/audit.
- [x] Draft generation rejects missing, failed, stale, or unapproved blueprints.
- [x] Draft generation rejects approved blueprints whose latest review hash/version no longer matches.
- [x] Draft generation rejects beats without fresh material links unless the beat has an explicit approved `no_reuse_reason`.
- [x] Draft generation rejects material links created against a different `analysis_contract_hash`.
- [x] Draft generation rejects candidates when the preflight cannot prove `blueprint -> approval -> material_link -> candidate` provenance.
- [x] Draft generation consumes only reviewed blueprint beat facts, duties, execution track, material links, slot plans, and allowed rewrite levels.
- [x] Draft generation rejects candidates that violate the beat's paragraph intention, execution mode, or candidate rejection rule.
- [x] Draft generation never calls a model with unbounded "write this chapter" instructions; prompts must be beat-scoped and grounded in approved fields.
- [x] L1 changes only declared slots.
- [x] L1 slot replacement preserves locked phrases and source order where applicable.
- [x] L2 reports non-slot edits.
- [x] L3 is classified and blocked unless requested.
- [x] L4 cannot pass.
- [x] Missing provenance fails audit.
- [x] Unsupported new facts fail audit.
- [x] Unsupported high-risk identity reveals fail audit unless the reveal is already present in approved known, scene, viewpoint, or slot facts.
- [x] Forbidden facts fail audit even when the prose is otherwise fluent.
- [x] Draft candidates preserve beat POV, narrative distance, scene facts, forbidden facts, and prose duties.
- [x] Draft candidates fail when non-POV character hidden emotion or intention is named directly instead of shown through observable evidence.
- [x] Draft candidates fail when declared novelistic prose duties have no detectable evidence in the text.
- [x] Draft candidates are audited for screenplay drift: dialogue/action-only paragraphs fail unless the beat explicitly allows a short exchange.
- [x] Action-only candidates fail when the approved beat requires interiority, external evidence, sensory pressure, transition work, subtext, or delayed reaction.
- [x] Draft candidates are audited for novelistic execution: paragraph intention, execution mode, anti-screenplay duty, sensory/subtext targets, and source-backed detail target.
- [x] Draft candidates fail when a declared Chinese emotion change has no trigger, suppressed reaction, or external-evidence mechanic present in the text.
- [x] Draft candidates are audited against the beat's emotion trigger, suppressed reaction, external evidence, and after-state.
- [x] Draft candidates expose source material id, beat id, rewrite level, changed slots, non-slot edits, and audit result in the bridge payload.
- [x] Draft service returns candidates only and never mutates chapter content.

**Verification:**

- [x] component/integration tests for slot extraction and slot-only replacement, covering brace styles, repeated slots, malformed placeholders, stable ids, offsets, undeclared-slot rejection, non-slot phrase preservation, and source order
- [x] component tests for rewrite-level classifier covering L0, slot-only L1, light L2, loose L3, and unrelated L4
- [x] integration tests for reuse audit rewrite-level gates covering L3 max-level blocking/allowance and unconditional L4 failure
- [x] component tests for draft preflight status, review-hash, target-beat, no-reuse, and material-link validation
- [x] integration tests for draft preflight material-link hash validation
- [x] component tests for missing material provenance and unsupported fact detection covering key object/evidence terms, identity reveals, relationship reveals, and approved-fact allowance
- [x] unit tests for blueprint-to-draft audit rules
- [x] component tests for dialogue-only drift, action-only drift, missing prose duty evidence, missing emotion evidence, POV leakage, and missing required prose target
- [x] integration test for persisted draft audit forbidden-fact rejection
- [x] integration test for `AdaptMaterialAsync`
- [x] integration test for `GenerateDraftFromBlueprintAsync`
- [x] integration test proving `GenerateDraftFromBlueprintAsync` returns candidates without mutating chapter content
- [x] integration test proving missing, failed, unapproved, and stale blueprint generation is blocked
- [x] integration test proving approved blueprint draft generation is blocked when the latest review hash no longer matches
- [x] bridge test proving `GenerateReferenceAnchoredDraft` returns a stable validation error before approval

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- `SqliteReferenceAnchoredDraftService.cs`
- internal adaptation/audit classes
- `tests/Novelist.Tests/ReferenceAnchor*Tests.cs`
- `tests/Novelist.Tests/ReferenceAnchoredDraft*Tests.cs`

## Phase 6: Bridge Integration

**Description:** Expose reference-anchor and blueprint-gated drafting operations through the Photino bridge.

**Acceptance criteria:**

- [x] All reference-anchor bridge methods route to service operations.
- [x] All reference-anchored draft bridge methods route to service operations.
- [x] Blueprint payloads preserve analysis tracks, transition plan, execution track, and review defect arrays without stringifying them into one markdown field.
- [x] `ReviseReferenceChapterBlueprint` invalidates approval and material links when reviewed fields change.
- [x] Invalid payloads return stable `VALIDATION_ERROR`.
- [x] app-not-initialized and invalid path errors use existing bridge semantics.
- [x] Draft generation through bridge fails for unapproved blueprints.
- [x] Frontend/backend method registry test passes.
- [x] Bridge handlers do not add HTTP endpoints or depend on ASP.NET Core host services.

**Verification:**

- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge`
- [x] integration test dispatches representative reference-anchor requests
- [x] integration test dispatches representative reference-anchored draft requests

**Files likely touched:**

- `ReferenceAnchorBridgeHandlers.cs`
- `ReferenceAnchoredDraftBridgeHandlers.cs`
- `BridgeCompatibilityAppMethods.cs`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- bridge tests

## Phase 7: Desktop and Agent Wiring

**Description:** Instantiate services in Photino desktop composition and pass them to bridge and agent registry.

**Acceptance criteria:**

- [x] Desktop service graph compiles.
- [x] Reference-anchor and anchored-draft services are created in `PhotinoWindowFactory.cs` or equivalent desktop composition, not ASP.NET Core DI.
- [x] Existing constructor tests still pass.
- [x] No existing tool disappears.
- [x] Reference material tools are absent when service is null and present when configured.
- [x] Reference draft tools are absent when draft service is null and present when configured.
- [x] Agent tools enforce blueprint workflow order and cannot call `SaveContent`.
- [x] Documented dev workflow clearly requires built frontend assets or a Vite `--start-url`; missing `frontend/dist/index.html` reports a direct asset-build error.

**Verification:**

- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter MafToolRegistry`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceDraftTools`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter Photino`

**Files likely touched:**

- `PhotinoWindowFactory.cs`
- `NovelistMafToolRegistry.cs`
- `NovelistMafReferenceTools.cs`
- `MafToolRegistryTests.cs`
- `MafStructuredToolIntegrationTests.cs`

## Phase 8: Frontend Workflow

**Description:** Add UI for anchors, search, blueprint generation/review, material binding, draft candidate preview, and audit.

**Acceptance criteria:**

- [x] ActivityBar has a reference-anchor entry.
- [x] WorkspaceView renders ReferenceAnchorView for the active novel.
- [x] User can create/rebuild anchor and search materials.
- [x] User can choose a reference source file through the native Photino bridge while raw path entry remains available.
- [x] Candidate preview shows source, adapted text, rewrite level, changed slots, audit warnings.
- [x] User can generate and inspect a chapter blueprint for a chapter number.
- [x] Blueprint view shows logic, emotion, narration, character, and reference tracks separately.
- [x] Blueprint view shows execution track separately from analysis tracks.
- [x] Blueprint detail has a compact field editor for facts, POV, emotion evidence, paragraph intention, prose duties, and reference query, using `ReviseReferenceChapterBlueprint`.
- [x] Beat editor exposes transition, POV knowledge boundary, character state delta, emotion trigger/evidence, narration strategy, paragraph intention, execution mode, anti-screenplay duty, prose duties, reference query tags, rewrite policy, and no-reuse reason for the current beat.
- [x] Beat editor supports structured `slot_plan` edits through typed slot-name/value rows and `beat:{beat_id}:slot_plan` revisions.
- [x] Review panel shows logic, causality, emotion, narration, execution, character-state, POV, continuity, transition, forbidden-fact, reference-binding, material-fit, screenplay-drift, novelistic-narration, and AI-prose defects.
- [x] Editing exposed approved blueprint fields clearly invalidates approval and disables draft generation until re-review/re-approval.
- [x] Draft generation button is disabled until blueprint review passes and approval exists.
- [x] Draft preview shows source material, blueprint beat, rewrite level, changed slots, and audit warnings.
- [x] No automatic chapter insertion.

**Verification:**

- [x] `cd frontend && npm run build`
- [x] `cd frontend && npm run lint`

**Files likely touched:**

- `frontend/src/components/reference-anchor/*`
- `ActivityBar.tsx`
- `WorkspaceView.tsx`
- `api.ts`
- `types.ts`

## Phase 9: Feedback Loop and Hardening

**Description:** Store user decisions and use them to improve ranking, blueprint review, and regression tests.

**Acceptance criteria:**

- [x] User feedback rows persist accept/reject/edit decisions.
- [x] User-verified tags can override extractor tags.
- [x] User-edited blueprint beats can be re-reviewed and approved.
- [x] Regression fixtures include previously bad blueprints and candidates.
- [x] Rebuild preserves user corrections where source segment hash is unchanged.
- [x] Ranking can boost materials previously accepted for similar blueprint beats.

**Verification:**

- [x] integration tests for feedback persistence
- [x] ranking test for accepted-feedback boost
- [x] integration tests for user-verified material tag overrides
- [x] integration test for preserving user-verified material tag overrides across rebuild when material text hash is unchanged
- [x] integration test for re-reviewing and re-approving a user-edited blueprint beat
- [x] blueprint regression fixture tests
