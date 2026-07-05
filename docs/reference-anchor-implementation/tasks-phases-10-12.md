# Reference Anchor Tasks: Phases 10-12

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

## Phase 10: Product Hardening and Design Closure

**Description:** Convert the remaining loose recommendations and open design questions into a final hardening phase. Phases 0-9 are considered the core implementation; Phase 10 is complete only when runtime verification, mocked frontend workflow coverage, UX decisions, and optional-expansion boundaries are explicit and tested.

**Acceptance criteria:**

- [x] Full reference-anchor frontend workflow is covered by Playwright screenshot/DOM tests with a mocked Novelist bridge: create/rebuild anchor, search material, generate/review/revise/approve blueprint, bind materials, generate candidates, inspect audit, stale/disabled states, final-insertion stop, and no automatic chapter insertion.
- [ ] Real Photino desktop verification is reduced to a minimal runtime smoke: the app loads the reference panel, the bridge can call representative reference methods through the production composition, and no runtime path auto-inserts chapter prose.
- [x] Stale blueprint UI behavior is decided and covered by build/lint verification: preserve stale blueprints read-only for comparison, disable review/approval/revision/material binding/candidate generation, and show a regeneration prompt.
- [x] Reference-anchor search scope is decided: keep reference material results in the dedicated reference panel/API for the current implementation; any global `SearchAll` integration must be a later staged opt-in change with its own result taxonomy and preview policy.
- [x] Optional LLM-assisted material tagging/adaptation is decided for the current implementation: keep extraction, tagging, slot adaptation, rewrite-level classification, and audit deterministic-only; any future model-assisted path must use an explicit opt-in interface or feature flag and cannot weaken deterministic review, binding, rewrite-level, or audit gates.
- [x] Full-chapter candidate assembly is explicitly deferred: anchored draft APIs return beat-scoped paragraph candidates only, without `chapter_text`, `assembled_text`, or `full_chapter` fields, and existing generation still does not mutate chapter content.
- [x] Source preview policy is decided for unknown-license anchors: material search/library previews return truncated exact text by default, while stored materials, provenance hashes, adaptation, binding, and audit still use the complete imported text.
- [x] Generator reproducibility policy is decided: blueprint records expose `build_version` plus `context_hash`, `source_plan_hash`, and `analysis_contract_hash`; review/approval records carry `review_version`; prompt/schema snapshots are not persisted on blueprint rows to avoid prompt-churn.
- [x] Developer workflow expectation is finalized: keep explicit frontend build/Vite steps for faster backend-only loops; `make dev` does not build frontend assets, `npm --prefix frontend run build` prepares `frontend/dist`, and Vite debugging uses `--start-url=http://localhost:5173/`.
- [x] `overview.md`, `schema-and-integration.md`, and `decisions.md` no longer describe completed Phase 0-9 items as incomplete.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj`
- [ ] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj`
- [ ] `cd frontend && npm run build`
- [ ] `cd frontend && npm run lint`
- [x] `cd frontend && npm run test:reference-anchor`
- [ ] Minimal real Photino smoke covering app load, representative bridge invocation, and no automatic chapter insertion.

Targeted Phase 10 checks completed:

- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter SearchAllLeavesReferenceMaterialsInDedicatedSearch -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter GeneratedBlueprintExposesStableGeneratorVersionWithoutPromptSnapshots -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceChapterBlueprintPayloadsUseStableSnakeCaseJsonNames -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter AnchoredDraftPayloadSerializesBeatCandidatesWithoutFullChapterAssembly -v minimal`
- [x] Frontend stale-blueprint UI check: stale blueprints are retained read-only for comparison, with review/approval/revision/binding/candidate actions disabled and a regeneration prompt shown.
- [x] `cd frontend && npm run build`
- [x] `cd frontend && npm run lint`
- [x] `cd frontend && npm run test:reference-anchor` covers the reference-anchor panel in a real browser with a mocked Photino `window.external` bridge, including create/rebuild/search, blueprint revision/review/approval, material binding, candidate generation/audit, default orchestration through final-insertion stop, stale blueprint disabled controls, screenshots in `output/playwright/`, and a bridge-call assertion that `SaveContent` is never invoked.
- [x] Documentation closure check: `overview.md`, `schema-and-integration.md`, and `decisions.md` describe Phase 0-9 bridge, desktop composition, agent tools, frontend entry, import semantics, extracted blueprint components, and current SQLite tables as implemented/current rather than as pending work.
- [x] Documentation check: `Makefile`, `README.md`, `docs/build-setup.md`, and `schema-and-integration.md` agree that `make dev` uses prebuilt `frontend/dist` or an explicit Vite `--start-url`.
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter MaterialTaggingAndAdaptationRemainDeterministicWithoutModelAssistedConfiguration -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter PhotinoReferenceWorkflowSmokeTests -v minimal` verifies the production desktop bridge composition can run the reference-anchor workflow through `PhotinoWebMessageBridge` without saving chapter content.
- [x] Phase 10 verification boundary decided: Playwright mock-bridge tests own full frontend workflow coverage, .NET integration tests own bridge/service behavior, and real Photino smoke is only a minimal runtime check.

**Files likely touched:**

- `frontend/src/components/reference-anchor/*`
- `frontend/src/views/WorkspaceView.tsx`
- `frontend/src/lib/novelist/*`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs`
- `tests/Novelist.IntegrationTests/*Reference*Tests.cs`
- `docs/reference-anchor-implementation/*.md`

## Phase 11: AI-Orchestrated Low-Intervention Workflow

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
- [x] The orchestration flow never calls `SaveContent` or inserts chapter prose automatically; insertion remains a separate user-confirmed action.
- [ ] Agent tool descriptions and UI copy make clear which decisions AI may automate and which decisions require the author.
- [x] Telemetry or local run history records why the workflow stopped, what AI proposed, what the user approved/rejected, and which deterministic gate produced each block.

**Verification:**

- [ ] Unit tests for orchestration state transitions and resume behavior.
- [ ] Integration tests for happy path: one command reaches audited candidates after user blueprint approval.
- [ ] Integration tests for failed-review path: AI proposes revisions, user approves, re-review passes, then binding/draft continues.
- [ ] Integration tests proving orchestration stops for stale blueprints, forbidden facts, unsupported facts, missing material links, and draft audit failure.
- [ ] Bridge tests for starting, resuming, cancelling, and inspecting orchestration runs.
- [ ] Agent tool tests proving the orchestrator cannot approve blueprint revisions, expand fact boundaries, or insert prose without explicit approval.
- [ ] Frontend runtime test or manual smoke test proving the default flow requires only necessary confirmations while advanced controls remain available.

Targeted Phase 11 thin-slice checks completed:

- [x] Contract, bridge, SQLite state persistence shell, and frontend adapter types now exist for starting, listing, inspecting, resuming, and cancelling orchestration runs.
- [x] After source/fact confirmation, orchestration automatically generates a deterministic blueprint, runs deterministic blueprint review, persists `blueprint_id`/`review_id`, and stops for either blueprint approval or blueprint revision.
- [x] After user blueprint approval, orchestration automatically binds materials, generates beat candidates, runs draft audit, persists candidate ids, and stops for final insertion confirmation without calling `SaveContent`.
- [x] Failed blueprint review can persist deterministic proposed field-level revisions in the required decision; approving that decision applies the revision, re-runs review, and stops for blueprint approval when the revision passes.
- [x] Agent orchestration tools can start, inspect, list, inspect run-event history, and cancel runs while deliberately withholding resume/approval/revision/final-insertion tools; start arguments cannot pre-confirm source/fact decisions, pass `anchor_ids`, decision payloads, or prose text.
- [x] Draft audit failure now persists as a high-risk `resolve_high_risk_stop` decision with candidate ids and audit findings; resolving that stop marks the run failed instead of inserting prose.
- [x] Material binding gaps now persist as a high-risk `resolve_high_risk_stop` decision with missing beat ids; resolving that stop marks the run failed instead of free-drafting.
- [x] Stale blueprints now persist as a high-risk `resolve_high_risk_stop` decision when source-plan changes invalidate a pending approval or safe-stage continuation.
- [x] Orchestration material binding now applies `corpus_search_policy` include/exclude anchor filters and license-status filters before selecting materials, so the default flow can retrieve by policy without a required manual anchor list.
- [x] Local orchestration event history now records run starts, required decisions, user resumes/approvals, stop reasons, deterministic gate stages, failures, and cancellations; `GetReferenceOrchestrationRunEvents` exposes it through the desktop bridge and frontend adapter.
- [x] The reference-anchor page now mounts a default orchestration panel that starts runs without requiring selected anchors, shows run history, required decisions, approval summaries, candidate ids, and event history, and allows safe resume/cancel actions while leaving final insertion manual.
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Reference -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRun -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunStopsForHighRiskDecisionWhenMaterialBindingHasMissingLinks -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunStopsForHighRiskDecisionWhenBlueprintBecomesStaleBeforeApproval -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter MafToolRegistryTests -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'ReferenceOrchestrationRunPersistsResumeAndCancelState|ReferenceAnchoredDraftServiceTests' -v minimal`
- [x] `npm --prefix frontend run build`
- [x] `npm --prefix frontend run lint`

These thin slices do not complete the Phase 11 automatic workflow or Phase 12 shared-corpus model. Proposed revision generation is currently deterministic and narrow; broader high-risk stop coverage, agent approval escalation, full Photino runtime workflow coverage, and shared-corpus storage/migration remain pending.

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

## Phase 12: Shared Reference Corpus and AI-Driven Material Selection

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
- [ ] Retrieval gaps are explicit states, not silent free-drafting fallbacks: automatic query expansion may run first, weak matches must be marked low confidence, optional no-reuse beats require approved reasons, and source-required beats must stop for user action.
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
- [ ] Retrieval-gap tests covering automatic query expansion, weak-match continuation, approved no-reuse continuation, and source-required stop states.
- [ ] Binding/audit tests proving selected global material provenance is stored with blueprint hash and cannot be reused after blueprint edits.
- [ ] Feedback tests proving global tag feedback and per-novel usage feedback have different scopes.
- [ ] Bridge and agent tests proving `anchor_ids` are optional and AI-driven corpus retrieval is the default path.
- [ ] Frontend smoke test for corpus management and default automatic material selection.

Targeted Phase 12 thin-slice checks completed:

- [x] Orchestration binding can use `corpus_search_policy` include/exclude anchor filters and license-status filters when `anchor_ids` are omitted, while keeping the existing per-novel reference-anchor storage model.
- [x] Reference material read paths now include `reference_anchors.novel_id = 0` workspace-corpus compatibility anchors for listing, search, adaptation, audit, tag correction, and per-novel feedback validation, while still excluding private anchors from other novels.
- [x] A real SQLite orchestration run can omit explicit `anchor_ids`, resolve `novel_id = 0` workspace-corpus anchors through the default corpus policy, bind selected material links, generate audited candidates, and stop at final insertion without writing chapter prose.
- [x] Real SQLite orchestration coverage proves workspace-corpus anchors are filtered by `corpus_search_policy.license_statuses` before binding selected materials.

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
