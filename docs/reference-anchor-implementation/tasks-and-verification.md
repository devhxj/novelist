# Reference Anchor Tasks and Verification

[Back to implementation index](../reference-anchor-implementation-plan.md).

## Implementation Task Breakdown

### Phase 0: Contract and Test Fixture Foundation

**Description:** Add contracts, enum constants, benchmark fixture format, and tests for payload serialization.

**Acceptance criteria:**

- [x] `ReferenceAnchorPayloads.cs` compiles.
- [x] `ReferenceAnchoredDraftPayloads.cs` compiles.
- [x] `IReferenceAnchorService.cs` compiles.
- [x] `IReferenceAnchoredDraftService.cs` compiles.
- [x] JSON property names match frontend snake_case.
- [x] Rewrite level constants, build states, blueprint states, beat types, and review statuses are documented in code or tests.
- [x] Blueprint contracts can represent logic, emotion, narration, character, reference-use, transition, and execution tracks.
- [x] Revision contracts can express field-level blueprint edits and approval invalidation reasons.
- [x] Review contracts expose separate logic, narration, execution, character-state, transition, material-fit, and novelistic-narration errors.

**Verification:**

- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceAnchorContractTests`

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs`
- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchorService.cs`
- `src/Novelist.Core/App/IReferenceAnchoredDraftService.cs`
- `tests/Novelist.Tests/`

### Phase 1: SQLite Store and Source Import

**Description:** Implement anchor creation, source file validation, immutable source segmentation, and build status persistence.

**Acceptance criteria:**

- [x] Create anchor validates novel id and source file.
- [x] TXT/MD source is split into chapter/paragraph/sentence segments.
- [x] Segment ids and hashes are stable across rebuilds.
- [x] Rebuild is idempotent for unchanged source.
- [x] Failed import records a failed status with a redacted error.

**Verification:**

- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchor`

**Files likely touched:**

- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs`
- `tests/Novelist.IntegrationTests/ReferenceAnchorServiceTests.cs`

### Phase 2: Material Extraction and Slots

**Description:** Build sentence and passage material banks with deterministic tags, conservative slots, and blueprint-usable narrative metadata.

**Acceptance criteria:**

- [x] Material rows point to valid source segments.
- [x] Function/material tags exist with confidence fields.
- [x] Emotion, POV, and technique material tags plus blueprint prose-duty/external-evidence duties exist for blueprint matching.
- [x] Slots are stored separately and tied to material ids.
- [x] Locked phrases survive L1 adaptation.
- [x] User corrections can be represented even if UI arrives later.

Current design note: narrative duty and external evidence are beat-level blueprint duties (`prose_duties` / `external_evidence`) matched against material function and technique tags during binding, not standalone `reference_materials` columns.

**Verification:**

- [x] extractor integration tests for Chinese punctuation/dialogue/paragraph cases
- [x] extractor integration tests for emotion, narrative-duty-adjacent function tags, and POV tag cases
- [x] integration test verifies material counts and provenance joins

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- internal extractor classes in `src/Novelist.Infrastructure/App/`
- `tests/Novelist.Tests/` extractor tests

### Phase 3: Hybrid Search and Blueprint Material Matching

**Description:** Add paginated material search with tag filters, optional embeddings, and score components usable by blueprint beat binding.

**Acceptance criteria:**

- [x] Search works without embedding configuration using lexical/tag ranking.
- [x] Search records score components.
- [x] If embedding config exists, vectors are provisioned in reference-specific vec tables.
- [x] Missing sqlite-vec returns a recoverable status.
- [x] Results are bounded and paginated.
- [x] Search can filter by narrative duty, emotion transition, POV, technique, and material type.
- [x] Beat-level material matching returns ranked candidates without selecting them automatically unless requested.
- [x] Ready reference vector indexes contribute transient `embedding` score components to material search and blueprint binding, while embedding/vector failures fall back to lexical/tag ranking.

**Verification:**

- [x] lexical ranking and pagination tests without embedding configuration
- [x] search score-component contract and integration tests
- [x] fake embedding client test
- [x] fake sqlite-vec provisioner test
- [x] complete search filter tests for narrative duty and emotion transition coverage
- [x] search embedding-score integration tests and binding score propagation tests
- [x] beat-to-material ranking tests

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- possibly shared sqlite-vec table-name helper
- `ReferenceAnchorServiceTests.cs`

### Phase 4: Chapter Narrative Blueprint Analysis and Review Gate

**Description:** Implement structured chapter blueprint generation with logic, emotion, narration, character, reference-use, transition, and execution tracks; deterministic review; explicit revision; approval invalidation; and material binding. This phase must land before any chapter drafting tool.

This is the implementation phase for the "write the detailed chapter scenario first, review it, then draft prose" workflow. It must be delivered as vertical slices that each leave the app in a testable state:

```text
Slice 4A: deterministic blueprint contract
  contract + SQLite persistence + bridge serialization + contract tests

Slice 4B: review gate
  deterministic hard gates + failed/pass review records + approval disabled before pass + field-level defects

Slice 4C: explicit approval gate
  passing review hash/version verification + approved execution contract + draft generation still disabled before material binding

Slice 4D: revision and invalidation
  field-level changes + review history + approval/material-link invalidation

Slice 4E: material binding
  beat-level ranked links + score components + stale-link rejection

Slice 4F: MAF/tool fixture
  full agent workflow through generate/review/approve/bind without writing chapter content
```

Recommended implementation slices:

1. Persist full blueprint records and beats with `context_hash`, `source_plan_hash`, `analysis_contract_hash`, `review_version`, and generator version.
2. Build a deterministic context pack from chapter plan, previous state, world entities, known facts, forbidden facts, and selected anchors.
3. [x] Extract a `ReferenceChapterBlueprintNormalizer` so hashes and reviewed field sets are stable and reusable in service, tests, and future UI editing.
4. Add a constrained blueprint generator that returns structured payloads only.
5. [x] Extract a `ReferenceChapterBlueprintReviewer` and add the current deterministic review rules for logic, emotion, narration, character, reference-use, transition, execution, causality, POV, forbidden facts, prose duties, anti-screenplay duties, and final hook dependency.
6. Add an explicit approval gate that freezes the latest passing `analysis_contract_hash` and `review_version`.
7. Add explicit revision and invalidation semantics for reviewed fields, approval rows, and material links.
8. Add material binding only after approval, with score components and stale-link handling.
9. Add fixture files for bad blueprints so fake emotion, hard transition, POV leak, missing prose duty, action/dialogue-only beat, and material mismatch never regress.

**Acceptance criteria:**

- [x] Blueprint generation targets `novel_id` and `chapter_number`.
- [x] Blueprint generation builds and hashes a normalized context pack before persistence.
- [x] Blueprint stores chapter function, causality chain, emotion trajectory, POV constraints, scene facts, forbidden facts, prose duties, and beat-level reference queries.
- [x] Blueprint stores complete logic, emotion, narration, character, reference-use, transition, and execution tracks.
- [x] Each beat stores transition-in/out, character goal/knowledge/misbelief/state delta, suppressed reaction, external evidence, narration strategy, rhythm strategy, paragraph intention, execution mode, anti-screenplay duty, source-backed detail target, slot plan, locked phrase policy, and no-reuse reason.
- [x] Blueprint generator cannot persist final prose paragraphs as a substitute for beat duties.
- [x] Normalizer computes the same `analysis_contract_hash` for semantically identical payloads with equivalent whitespace/array defaults.
- [x] Reviewer is deterministic and idempotent for unchanged `blueprint_id`, `context_hash`, `source_plan_hash`, `analysis_contract_hash`, and `review_version`.
- [x] Review fails blueprints with missing analysis tracks, missing execution track, missing causality, unsupported emotional shifts, missing external evidence, POV knowledge leaks, missing transition reasons, forbidden facts, material mismatch, action/dialogue-only beats, or screenplay drift risks.
- [x] Review result separates logic, causality, emotion, narration, execution, character-state, POV, continuity, transition, forbidden-fact, reference-binding, material-fit, screenplay-drift, novelistic-narration, and AI-prose findings.
- [x] Review defects include field path or beat id, severity, reason, and required fix in a form the UI can render without parsing prose.
- [x] Review stores `context_hash`, `source_plan_hash`, `analysis_contract_hash`, and `review_version` so approvals can be invalidated deterministically.
- [x] Approved status requires a passing review.
- [x] Approval records freeze `review_id`, `context_hash`, `source_plan_hash`, `analysis_contract_hash`, `review_version`, approver origin, and approval time.
- [x] A blueprint with `review_passed` but no explicit approval cannot bind materials or generate draft candidates.
- [x] A blueprint with approval but no current material links cannot generate draft candidates unless every requested beat has an approved `no_reuse_reason`.
- [x] Editing approved analysis tracks, execution contract, known/forbidden facts, and beat reference query invalidates approval and requires re-review.
- [x] Editing approved beat POV, character-state, emotion-mechanic, scene-fact, prose-duty, and material-query tag fields invalidates approval and records revision paths.
- [x] Editing any approved blueprint beat, analysis track, execution track, known/forbidden fact, or reference query invalidates approval and requires re-review.
- [x] Blueprint revision records field paths, previous/new value hashes, origin, invalidated review id, and reason.
- [x] Changing the source chapter plan hash marks existing blueprints stale.
- [x] Material binding links candidate reference materials to beats with max rewrite levels.
- [x] Material binding records and exposes score components: lexical, tag, function, emotion, POV, prose-duty, and user-verified boosts.
- [x] Material binding rejects semantic-only matches when function, POV, emotion, or prose-duty fit is absent.
- [x] Material binding stores the `analysis_contract_hash` it was created against and is stale when that hash changes.
- [x] Stale material links are not used for draft generation.

**Verification:**

- [x] unit tests for blueprint payload serialization
- [x] unit tests for context-pack hashing and stale detection
- [x] component tests for blueprint normalization and analysis-contract hashing
- [x] component tests for deterministic blueprint review rules, including anti-screenplay and execution-track defects
- [x] fixture tests for fake emotion, hard transition, POV leak, missing prose duty, action/dialogue-only beat, and material mismatch
- [x] unit tests for explicit approval hash/version matching
- [x] integration tests for approval invalidation after beat, analysis, execution, known-fact, and reference-query edits
- [x] integration test for generate/review/approve/stale lifecycle
- [x] integration test for beat material binding and provenance joins
- [x] integration test proving review-passed-without-approval cannot bind or draft
- [x] bridge test for `BindReferenceBlueprintMaterials` after approval and for failure before approval

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchoredDraftService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs`
- `tests/Novelist.Tests/ReferenceChapterBlueprint*Tests.cs`
- `tests/Novelist.IntegrationTests/ReferenceAnchoredDraftServiceTests.cs`

### Phase 5: Controlled Adaptation, Draft Generation, and Audit

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

### Phase 6: Bridge Integration

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

### Phase 7: Desktop and Agent Wiring

**Description:** Instantiate services in Photino desktop composition and pass them to bridge and agent registry.

**Acceptance criteria:**

- [x] Desktop service graph compiles.
- [x] Reference-anchor and anchored-draft services are created in `PhotinoWindowFactory.cs` or equivalent desktop composition, not ASP.NET Core DI.
- [x] Existing constructor tests still pass.
- [x] No existing tool disappears.
- [x] Reference material tools are absent when service is null and present when configured.
- [x] Reference draft tools are absent when draft service is null and present when configured.
- [x] Agent tools enforce blueprint workflow order and cannot call `SaveContent`.
- [x] `make dev` or documented dev workflow clearly requires built frontend assets or a Vite `--start-url`; missing `frontend/dist/index.html` reports a direct asset-build error.

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

### Phase 8: Frontend Workflow

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

### Phase 9: Feedback Loop and Hardening

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

### Phase 10: Product Hardening and Design Closure

**Description:** Convert the remaining loose recommendations and open design questions into a final hardening phase. Phases 0-9 are considered the core implementation; Phase 10 is complete only when runtime verification, UX decisions, and optional-expansion boundaries are explicit and tested.

**Acceptance criteria:**

- [ ] Full reference-anchor workflow is exercised in the real Photino desktop shell through the bridge: create/rebuild anchor, search material, generate/review/revise/approve blueprint, bind materials, generate candidates, inspect audit, and confirm no automatic chapter insertion.
- [ ] Stale blueprint UI behavior is decided and covered by tests or documented manual verification: preserve read-only for comparison, hide by default, or expose with clear regeneration affordance.
- [ ] Reference-anchor search scope is decided: dedicated panel only, global `SearchAll` integration, or a staged hybrid path.
- [ ] Optional LLM-assisted material tagging/adaptation has a feature-flag/design decision and does not weaken deterministic review, binding, rewrite-level, or audit gates.
- [ ] Full-chapter candidate assembly is either explicitly deferred or implemented only after every beat candidate can prove passing audit and provenance.
- [ ] Source preview policy is decided for unknown-license anchors, including whether exact source text is truncated by default.
- [ ] Generator reproducibility policy is decided: store generator version/context hash only, or persist prompt/schema metadata without creating prompt-churn in the database.
- [ ] Developer workflow expectation is finalized: `make dev` dependency on frontend build versus explicit Vite/build steps.
- [ ] `overview.md`, `schema-and-integration.md`, and `decisions.md` no longer describe completed Phase 0-9 items as incomplete.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj`
- [ ] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj`
- [ ] `cd frontend && npm run build`
- [ ] `cd frontend && npm run lint`
- [ ] Manual desktop smoke test of the full Phase 10 workflow in Photino.

**Files likely touched:**

- `frontend/src/components/reference-anchor/*`
- `frontend/src/views/WorkspaceView.tsx`
- `frontend/src/lib/novelist/*`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs`
- `tests/Novelist.IntegrationTests/*Reference*Tests.cs`
- `docs/reference-anchor-implementation/*.md`

### Phase 11: AI-Orchestrated Low-Intervention Workflow

**Description:** Build the default reference-anchored experience around AI automation instead of manual step-by-step operation. The hard gates from earlier phases stay mandatory, but routine sequencing is handled by an orchestrator so users only intervene for source trust, chapter intent, fact boundaries, blueprint/risk approval, and final prose insertion. The orchestrator should select relevant materials from the available corpus by story context rather than requiring the user to pre-select anchors for every run.

**Default workflow target:**

```text
confirm source + chapter target + known/forbidden facts
  -> auto-generate blueprint
  -> auto-run deterministic review
  -> if failed: AI proposes field-level revisions, user approves applying them
  -> if passed: user approves compact blueprint/risk summary
  -> auto-bind materials
  -> auto-generate beat candidates
  -> auto-run draft audit
  -> user selects/edits final candidate before insertion
```

**Acceptance criteria:**

- [ ] A single user command can start a reference-anchored candidate run for a chapter using chapter goal/plan, known facts, forbidden facts, and an optional corpus search policy; selected anchors are an advanced override, not a required default.
- [ ] The orchestration run persists stage status, errors, generated artifacts, and the current required user decision so it can be resumed after app restart.
- [ ] The orchestrator automatically performs safe stages: blueprint generation, deterministic blueprint review, material binding after approval, beat candidate generation, and draft audit.
- [ ] The orchestrator stops for required human decisions: source/license confirmation, known/forbidden fact boundary changes, blueprint approval, AI-proposed blueprint revision application, and final chapter insertion.
- [ ] A compact approval summary shows chapter function, POV, fact boundary changes, emotional trajectory, material-use plan, rewrite budget, and high-risk findings without forcing users to inspect every field.
- [ ] Failed blueprint reviews can trigger AI-suggested field-level fixes, but suggested fixes are stored as proposed revisions and require user or explicit agent approval before application.
- [ ] Low-risk passing blueprints can proceed from approval to material binding and candidate generation without additional manual clicks.
- [ ] High-risk conditions require an explicit stop: stale blueprint, new facts outside approved boundary, forbidden fact pressure, missing material provenance, L3/L4 rewrite risk, POV leak risk, or audit failure.
- [ ] Advanced mode still exposes manual generate/review/revise/approve/bind/draft/audit controls for debugging and strict editorial review.
- [ ] The orchestration flow never calls `SaveContent` or inserts chapter prose automatically; insertion remains a separate user-confirmed action.
- [ ] Agent tool descriptions and UI copy make clear which decisions AI may automate and which decisions require the author.
- [ ] Telemetry or local run history records why the workflow stopped, what AI proposed, what the user approved/rejected, and which deterministic gate produced each block.

**Verification:**

- [ ] Unit tests for orchestration state transitions and resume behavior.
- [ ] Integration tests for happy path: one command reaches audited candidates after user blueprint approval.
- [ ] Integration tests for failed-review path: AI proposes revisions, user approves, re-review passes, then binding/draft continues.
- [ ] Integration tests proving orchestration stops for stale blueprints, forbidden facts, unsupported facts, missing material links, and draft audit failure.
- [ ] Bridge tests for starting, resuming, cancelling, and inspecting orchestration runs.
- [ ] Agent tool tests proving the orchestrator cannot approve blueprint revisions, expand fact boundaries, or insert prose without explicit approval.
- [ ] Frontend runtime test or manual smoke test proving the default flow requires only necessary confirmations while advanced controls remain available.

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchoredDraftService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs`
- `src/Novelist.Core/Bridge/ReferenceAnchoredDraftBridgeHandlers.cs`
- `src/Novelist.Agent/NovelistMafReferenceTools.cs`
- `frontend/src/components/reference-anchor/*`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- `tests/Novelist.Tests/*Reference*Tests.cs`
- `tests/Novelist.IntegrationTests/*Reference*Tests.cs`

### Phase 12: Shared Reference Corpus and AI-Driven Material Selection

**Description:** Promote reference sources, extracted materials, style examples, scene examples, and technique libraries from per-novel assets into shared workspace-level corpus infrastructure. Novels should not bind libraries manually as a prerequisite. AI should retrieve and rank relevant materials from the shared corpus at blueprint/binding time using the current story context, while deterministic gates keep provenance, license, rewrite, fact, and POV safety intact.

**Target model:**

```text
workspace/global reference corpus
  -> AI retrieves candidates by chapter goal, beat duty, POV, emotion, scene facts, and style need
  -> binder records which corpus materials were selected for this blueprint contract
  -> draft candidates must trace to selected corpus material or approved no-reuse reason
  -> per-novel facts, audits, approvals, and feedback remain isolated
```

**Acceptance criteria:**

- [ ] Reference source libraries and extracted materials can exist without `novel_id` ownership.
- [ ] Existing per-novel anchors migrate or are readable through a compatibility layer without losing source hashes, segment ids, material ids, user-verified tags, feedback, or audit provenance.
- [ ] Corpus records include visibility, license status, source trust, and optional user-defined tags, but no required per-novel binding gate.
- [ ] Material search accepts story-context inputs and returns globally sourced candidates that are filtered by license/status policy and scored by beat function, emotion, POV, prose duty, technique, lexical similarity, embeddings when available, and feedback boosts.
- [ ] Blueprint generation no longer requires `anchor_ids`; it may accept an optional corpus search policy or explicit include/exclude anchors for advanced control.
- [ ] Material binding records exact selected corpus material ids against the current blueprint `analysis_contract_hash` so later draft candidates remain auditable.
- [ ] Per-novel known facts, forbidden facts, timeline state, character state, and POV boundaries still gate whether a globally sourced material may be used.
- [ ] AI can explain why it selected each material and which story need it satisfies, without requiring the user to manually choose libraries first.
- [ ] User feedback is split into global corpus feedback and per-novel usage feedback; accepting a material for one novel must not silently make it safe for another novel with different facts or POV.
- [ ] Agent tools search the shared corpus by injected novel context but do not expose arbitrary file reads or allow the model to bypass source/license filters.
- [ ] UI provides corpus management as a library feature and reference-anchored drafting as an automatic retrieval feature, not as a per-novel setup checklist.

**Verification:**

- [ ] Migration tests from per-novel `reference_anchors` to shared corpus records or compatibility reads.
- [ ] Service tests proving global corpus materials can be searched from different novels without duplicating source import.
- [ ] Tests proving per-novel forbidden facts and POV boundaries still reject globally sourced materials/candidates.
- [ ] Tests proving license/visibility policy filters corpus results before AI selection.
- [ ] Binding/audit tests proving selected global material provenance is stored with blueprint hash and cannot be reused after blueprint edits.
- [ ] Feedback tests proving global tag feedback and per-novel usage feedback have different scopes.
- [ ] Bridge and agent tests proving `anchor_ids` are optional and AI-driven corpus retrieval is the default path.
- [ ] Frontend smoke test for corpus management and default automatic material selection.

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs`
- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchorService.cs`
- `src/Novelist.Core/App/IReferenceAnchoredDraftService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs`
- `src/Novelist.Agent/NovelistMafReferenceTools.cs`
- `frontend/src/components/reference-anchor/*`
- `tests/Novelist.Tests/*Reference*Tests.cs`
- `tests/Novelist.IntegrationTests/*Reference*Tests.cs`
- `docs/reference-anchor-implementation/schema-and-integration.md`

## Required Test Matrix

Run after backend phases:

```text
dotnet test tests/Novelist.Tests/Novelist.Tests.csproj
dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj
```

Run after frontend phases:

```text
cd frontend
npm run build
npm run lint
```

Targeted new tests:

```text
tests/Novelist.Tests/ReferenceAnchorSegmentationTests.cs
tests/Novelist.Tests/ReferenceAnchorRewriteLevelTests.cs
tests/Novelist.Tests/ReferenceAnchorAuditTests.cs
tests/Novelist.Tests/ReferenceChapterBlueprintAnalysisContractTests.cs
tests/Novelist.Tests/ReferenceChapterBlueprintReviewTests.cs
tests/Novelist.Tests/ReferenceChapterBlueprintApprovalInvalidationTests.cs
tests/Novelist.Tests/ReferenceAnchoredDraftAuditTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchorServiceTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchorBridgeTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchoredDraftServiceTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchoredDraftBridgeTests.cs
```

## Critical Guardrails

- Do not put reference source text into `goink.md`, chapter files, skills, or preferences.
- Do not expose arbitrary file reads through agent tools.
- Do not let agent tools call `SaveContent` through this workflow.
- Do not generate chapter prose from a raw prompt or chapter plan when the reference-anchored workflow is selected.
- Do not generate draft candidates from unreviewed, failed, stale, or unapproved blueprints.
- Do not let `review_passed` alone unlock draft generation; explicit approval is required.
- Do not let draft generation use stale material links after blueprint edits.
- Do not let a blueprint hide final prose paragraphs in analysis fields to bypass candidate audit.
- Do not store blueprints as unstructured Markdown only; beat-level fields must remain machine-checkable.
- Do not allow a blueprint to pass with missing logic, emotion, narration, character, or reference-use tracks.
- Do not allow emotional state changes without trigger, suppressed/internal reaction, and external evidence.
- Do not allow scene transitions that only say location/time changed without causal, emotional, or informational pressure.
- Do not allow POV knowledge outside the current viewpoint boundary.
- Do not let a material match pass on semantic similarity alone; beat function, emotion, POV, prose duty, or explicit no-reuse policy must be considered.
- Do not let blueprint generation introduce facts outside the known fact set without marking them for review.
- Do not let blueprint approval survive a changed source chapter-plan hash.
- Do not let blueprint approval survive manual edits to analysis tracks or beats.
- Do not build source import into `ExtractStyle`.
- Do not store large source/material banks in JSON stores.
- Do not reuse `rag_chunks` or story-memory vector tables.
- Do not depend on embeddings being configured for basic source import/search.
- Do not add ASP.NET Core/Kestrel as the product API path for reference anchors; use the Photino bridge.
- Do not ship L2+ adaptation without rewrite-level tests.
- Do not ship blueprint-gated drafting without blueprint review tests and draft audit tests.
- Do not add frontend API methods without updating `BridgeCompatibilityAppMethods`.

## Phase 10 Design Decisions To Close

- Unknown-license source previews: truncate exact source text by default, show only derived metadata, or leave current full preview behavior.
- Source segment text policy: keep full original line text, normalized text, or both.
- Optional model assistance: keep deterministic-only extraction/adaptation, or add LLM-assisted tagging/adaptation behind an explicit feature flag.
- Search scope: keep reference-anchor results isolated, include them in global `SearchAll`, or expose a staged opt-in integration.
- Revision lineage: keep in-place draft blueprint revision, or create `parent_blueprint_id` lineage rows for every material contract edit.
- Failed review assistance: keep revision fully manual/agent-driven, or add field-level fix suggestions that remain separate from approval.
- Transition/no-reuse policy: require every generated paragraph to trace to material, or allow approved no-reuse transition beats without direct links.
- Candidate assembly: keep beat-level candidates only, or assemble a full-chapter candidate after all beats pass audit.
- Stale blueprint UX: preserve stale blueprints read-only for comparison, hide them by default, or show them with regeneration prompts.
- Development workflow: make `make dev` depend on frontend build, or keep explicit frontend build/Vite steps for faster backend-only loops.

## Phase 11 Automation Decisions To Close

- Default stop points: which risks always require human confirmation, and which low-risk stages can continue automatically.
- Approval granularity: whether approving a compact blueprint summary approves the entire current analysis contract or only selected beats.
- AI revision authority: whether AI may apply its own proposed revision in agent mode, or whether every proposed blueprint change requires user approval.
- Run persistence: whether orchestration runs live only in SQLite reference-anchor storage or also appear in chat/session history.
- Candidate insertion UX: whether final insertion is copy-only, diff-preview insertion, or a separate approved chapter-edit operation.
- Advanced mode boundary: which manual controls remain visible by default and which move behind an advanced/debug toggle.
- Failure recovery: whether users should resume from the failed stage, regenerate from scratch, or branch a new blueprint revision.

## Phase 12 Shared Corpus Decisions To Close

- Corpus scope: workspace-only for now, or also support user-level/importable library packs later.
- Source ownership migration: convert `reference_anchors.novel_id` to nullable/visibility metadata, or create new corpus tables and compatibility views.
- Material identity: keep current anchor-derived material ids, or introduce stable corpus material ids independent of importing novel.
- Search policy: define default license/status filters, include/exclude controls, and whether explicit source opt-out is needed per run.
- Feedback scope: which decisions should improve global corpus ranking, and which should remain per-novel because they depend on plot facts.
- UI information architecture: separate corpus library management from reference-anchored drafting controls.
- Agent authority: whether AI may import new corpus sources, or only search/use already imported sources.

## Recommended Next Coding Sessions

Start with Phase 10. Do not restart from Phase 0 unless contracts have regressed.

Latest verified scope: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge -v minimal` passed 29/29, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceAnchorContractTests -v minimal` passed 10/10, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter MafToolRegistryTests -v minimal` passed 11/11, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchorServiceTests -v minimal` passed 26/26, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchor -v minimal` passed 89/89, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceChapterBlueprintPayloadsUseStableSnakeCaseJsonNames -v minimal` passed 1/1, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'FullyQualifiedName~ReviseApprovedBlueprintInvalidatesApprovalAndMaterialLinks|FullyQualifiedName~BridgeReferenceAnchoredDraftHandlersGenerateReviewAndApproveBlueprint' -v minimal` passed 2/2, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceDraftTools -v minimal` passed 2/2, and `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter Reference -v minimal` passed 124/124 after adding full reference bridge service-routing coverage, reference bridge app-not-initialized/invalid-path semantics coverage, MAF tool exposure/schema constraints, stable reference bridge invalid-payload coverage, structured blueprint bridge payload verification, approval/material-link invalidation coverage, complete Phase 1 create/import/rebuild/failure-status coverage, Phase 2 material provenance/tag/slot coverage, blueprint tag-filter/prose-duty binding coverage, extractor Chinese punctuation/dialogue/paragraph plus emotion/POV/function-tag coverage, Phase 3 lexical ranking/pagination coverage, Phase 3 search score-component coverage, and Phase 3 narrative-duty/emotion-transition search filtering coverage. `cd frontend && npm run build` and `cd frontend && npm run lint` passed after adding optional reference material search scoring and filter fields to the frontend types.

Recommended next session:

1. Execute the full desktop workflow in Photino and record any runtime-only bridge/UI gaps under Phase 10.
2. Decide stale blueprint UI behavior and add coverage if stale rows remain visible.
3. Close the model-assisted tagging/adaptation decision before adding any optional LLM path.

Recommended following session:

1. Keep broadening deterministic Chinese narration, emotion, POV, and unsupported-fact fixtures before enabling optional model-assisted tagging or adaptation.
2. Design Phase 11 orchestration contracts before changing UI, so frontend, bridge, service, and agent tools share the same run-state model.
3. Design Phase 12 shared-corpus contracts before migrating storage, so global materials can be reused without weakening per-novel safety gates.

Do not broaden frontend workflow beyond the review-first path until source corpus, material binding, blueprint review, and draft audit are reliable. The system's quality depends on immutable provenance, hard blueprint gates, and candidate audit before any manual insertion.

After Phase 3, implement Phase 4 before any prose generation work. The blueprint review gate is the control layer that makes reference reuse robust; drafting before that gate would recreate the original failure mode under a different name.
