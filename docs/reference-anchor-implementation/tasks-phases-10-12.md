# Reference Anchor Tasks: Phases 10-12

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

## Phase 10: Product Hardening and Design Closure

**Description:** Convert the remaining loose recommendations and open design questions into a final hardening phase. Phases 0-9 are considered the core implementation; Phase 10 is complete only when runtime verification, mocked frontend workflow coverage, UX decisions, and optional-expansion boundaries are explicit and tested.

**Acceptance criteria:**

- [x] Full reference-anchor frontend workflow is covered by Playwright screenshot/DOM tests with a mocked Novelist bridge: create/rebuild anchor, search material, generate/review/revise/approve blueprint, bind materials, generate candidates, inspect audit, stale/disabled states, final-insertion stop, and no automatic chapter insertion.
- [x] Real Photino desktop verification is reduced to a minimal runtime smoke: the app loads the reference panel, the bridge can call representative reference methods through the production composition, and no runtime path auto-inserts chapter prose.
- [x] Stale blueprint UI behavior is decided and covered by build/lint verification: preserve stale blueprints read-only for comparison, disable review/approval/revision/material binding/candidate generation, and show a regeneration prompt.
- [x] Reference-anchor search scope is decided: keep reference material results in the dedicated reference panel/API for the current implementation; any global `SearchAll` integration must be a later staged opt-in change with its own result taxonomy and preview policy.
- [x] Optional LLM-assisted material tagging/adaptation is decided for the current implementation: keep extraction, tagging, slot adaptation, rewrite-level classification, and audit deterministic-only; any future model-assisted path must use an explicit opt-in interface or feature flag and cannot weaken deterministic review, binding, rewrite-level, or audit gates.
- [x] Full-chapter candidate assembly is explicitly deferred: anchored draft APIs return beat-scoped paragraph candidates only, without `chapter_text`, `assembled_text`, or `full_chapter` fields, and existing generation still does not mutate chapter content.
- [x] Source preview policy is decided for unknown-license anchors: material search/library previews return truncated exact text by default, while stored materials, provenance hashes, adaptation, binding, and audit still use the complete imported text.
- [x] Generator reproducibility policy is decided: blueprint records expose `build_version` plus `context_hash`, `source_plan_hash`, and `analysis_contract_hash`; review/approval records carry `review_version`; prompt/schema snapshots are not persisted on blueprint rows to avoid prompt-churn.
- [x] Developer workflow expectation is finalized: keep explicit frontend build/Vite steps for faster backend-only loops; `make dev` does not build frontend assets, `npm --prefix frontend run build` prepares `frontend/dist`, and Vite debugging uses `--start-url=http://localhost:5173/`.
- [x] `overview.md`, `schema-and-integration.md`, and `decisions.md` no longer describe completed Phase 0-9 items as incomplete.

**Verification:**

- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj`
- [x] `cd frontend && npm run build`
- [x] `cd frontend && npm run lint`
- [x] `cd frontend && npm run test:reference-anchor`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter PhotinoReferenceWorkflowSmokeTests -v minimal`

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
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter PhotinoReferenceWorkflowSmokeTests -v minimal` verifies the production desktop bridge composition can run the reference-anchor workflow through `PhotinoWebMessageBridge` without saving chapter content; app-load/reference-panel coverage stays with the real-browser mock-bridge suite per the Phase 10 verification boundary.
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

- [x] A single user command can start a reference-anchored candidate run for a chapter using chapter goal/plan, known facts, forbidden facts, and an optional corpus search policy; selected anchors are an advanced override, not a required default.
- [x] The orchestration run persists stage status, errors, generated artifacts, and the current required user decision so it can be resumed after app restart.
- [x] The orchestrator automatically performs safe stages: blueprint generation, deterministic blueprint review, material binding after approval, beat candidate generation, and draft audit.
- [x] The orchestrator stops for required human decisions: source/license confirmation, known/forbidden fact boundary changes, blueprint approval, AI-proposed blueprint revision application, and final chapter insertion.
- [x] A compact approval summary shows chapter function, POV, fact boundary changes, emotional trajectory, material-use plan, rewrite budget, and high-risk findings without forcing users to inspect every field.
- [x] Failed blueprint reviews can trigger AI-suggested field-level fixes, but suggested fixes are stored as proposed revisions and require user or explicit agent approval before application.
- [x] Low-risk passing blueprints can proceed from approval to material binding and candidate generation without additional manual clicks.
- [x] High-risk conditions require an explicit stop: stale blueprint, new facts outside approved boundary, forbidden fact pressure, missing material provenance, L3/L4 rewrite risk, POV leak risk, or audit failure.
- [x] Advanced mode still exposes manual generate/review/revise/approve/bind/draft/audit controls for debugging and strict editorial review.
- [x] The orchestration flow never calls `SaveContent` or inserts chapter prose automatically; insertion remains a separate user-confirmed action.
- [x] Agent tool descriptions and UI copy make clear which decisions AI may automate and which decisions require the author.
- [x] Telemetry or local run history records why the workflow stopped, what AI proposed, what the user approved/rejected, and which deterministic gate produced each block.

**Verification:**

- [x] Unit tests for orchestration state transitions and resume behavior.
- [x] Integration tests for happy path: one command reaches audited candidates after user blueprint approval.
- [x] Integration tests for failed-review path: AI proposes revisions, user approves, re-review passes, then binding/draft continues.
- [x] Integration tests proving orchestration stops for stale blueprints, forbidden facts, unsupported facts, missing material links, and draft audit failure.
- [x] Bridge tests for starting, resuming, cancelling, and inspecting orchestration runs.
- [x] Agent tool tests proving the orchestrator cannot approve blueprint revisions, expand fact boundaries, or insert prose without explicit approval.
- [x] Frontend runtime test or manual smoke test proving the default flow requires only necessary confirmations while advanced controls remain available.

Targeted Phase 11 thin-slice checks completed:

- [x] Contract, bridge, SQLite state persistence shell, and frontend adapter types now exist for starting, listing, inspecting, resuming, and cancelling orchestration runs.
- [x] After source/fact confirmation, orchestration automatically generates a deterministic blueprint, runs deterministic blueprint review, persists `blueprint_id`/`review_id`, and stops for either blueprint approval or blueprint revision.
- [x] After user blueprint approval, orchestration automatically binds materials, generates beat candidates, runs draft audit, persists candidate ids, and stops for final insertion confirmation without calling `SaveContent`.
- [x] Source confirmation decisions now explicitly require source trust, license-status, known-fact, and forbidden-fact confirmation before safe automation can start.
- [x] `ResumeReferenceOrchestrationRun` rejects `approve_final_insertion`, keeping the run parked at the final-insertion stop with candidate ids intact; final prose insertion must use a separate user-confirmed chapter edit/save path.
- [x] Failed blueprint review can persist deterministic proposed field-level revisions in the required decision; approving that decision applies the revision, re-runs review, and stops for blueprint approval when the revision passes.
- [x] Blueprint revision proposal generation now goes through an injectable `IReferenceBlueprintRevisionProposalProvider`; the default provider remains deterministic, and injected AI/agent-style proposals are rebound to the current blueprint/review, persisted as pending `proposed_blueprint_revision` data, applied only after user approval, re-reviewed, then allowed to continue through blueprint approval into binding/candidate generation/final-insertion stop.
- [x] The desktop composition now wires an AI-backed `AiReferenceBlueprintRevisionProposalProvider` that uses the currently selected chat model to propose failed-review field fixes, validates model output as untrusted JSON, filters proposed changes to current error defects and supported blueprint fields, rebinds the proposal to the current blueprint/review, and falls back to the deterministic provider when no model is selected or model output is invalid.
- [x] Applying a blueprint revision now uses the persisted pending proposal only: a client may submit the same proposal payload or an empty payload as approval, but cannot replace the pending proposal changes even when `blueprint_id` and `review_id` match.
- [x] Agent orchestration tools can start, inspect, list, inspect run-event history, and cancel runs while deliberately withholding resume/approval/revision/final-insertion tools; start arguments cannot pre-confirm source/fact decisions, pass `anchor_ids`, decision payloads, or prose text.
- [x] Draft audit failure now persists as a high-risk `resolve_high_risk_stop` decision with candidate ids and audit findings; resolving that stop marks the run failed instead of inserting prose.
- [x] Material binding gaps now persist as a high-risk `resolve_high_risk_stop` decision with missing beat ids; resolving that stop marks the run failed instead of free-drafting.
- [x] Stale blueprints now persist as a high-risk `resolve_high_risk_stop` decision when source-plan changes invalidate a pending approval or safe-stage continuation.
- [x] Draft audit high-risk stops now cover unsupported new facts, forbidden-fact/POV leaks, and L3/L4 rewrite risk in addition to generic audit failure.
- [x] Orchestration material binding now applies `corpus_search_policy` include/exclude anchor filters and license-status filters before selecting materials, so the default flow can retrieve by policy without a required manual anchor list.
- [x] Local orchestration event history now records run starts, required decisions, user resumes/approvals, stop reasons, deterministic gate stages, failures, and cancellations; `GetReferenceOrchestrationRunEvents` exposes it through the desktop bridge and frontend adapter.
- [x] Orchestration resume/safe-stage transitions are centralized in `ReferenceOrchestrationStateMachine` and covered by focused unit tests for approved decisions, high-risk resolution, and automatic-stage eligibility.
- [x] The reference-anchor page now mounts a default orchestration panel that starts runs without requiring selected anchors, shows run history, required decisions, approval summaries, candidate ids, and event history, and allows safe resume/cancel actions while leaving final insertion manual.
- [x] Agent orchestration tool descriptions and the default orchestration panel now distinguish AI-automated stages from author-only decisions; the reference-anchor Playwright workflow asserts this copy, the source/fact and blueprint confirmations, and the final-insertion stop that exposes no resume confirmation button.
- [x] The reference-anchor page now hides manual material-search and blueprint generate/revise/review/approve/bind/draft controls by default behind `高级模式`; `npm --prefix frontend run test:reference-anchor` asserts the default hidden state, opens advanced mode, and completes the manual workflow plus stale-read-only checks.
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Reference -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRun -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunStopsForHighRiskDecisionWhenMaterialBindingHasMissingLinks -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunStopsForHighRiskDecisionWhenBlueprintBecomesStaleBeforeApproval -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunRejectsFinalInsertionResumeAndKeepsManualInsertionBoundary -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceOrchestrationRunStopsForHighRiskDecisionWhenDraftCandidate" -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceOrchestrationStateMachineTests -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceOrchestration|ReferenceAnchorContractTests' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunPersistsInjectedBlueprintRevisionProposalUntilUserApprovesIt -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter AiReferenceBlueprintRevisionProposalProviderTests -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunPersistsAiBlueprintRevisionProposalUntilUserApprovesIt -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunRejectsClientModifiedBlueprintRevisionProposal -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'ReferenceOrchestrationRunRejectsClientModifiedBlueprintRevisionProposal|ReferenceOrchestrationRunRejectsMismatchedBlueprintRevisionProposal|ReferenceOrchestrationRunPersistsInjectedBlueprintRevisionProposalUntilUserApprovesIt|ReferenceOrchestrationRunAppliesProposedBlueprintRevisionThenContinuesAfterApproval' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'ReferenceOrchestrationRunRejectsClientModifiedBlueprintRevisionProposal|ReferenceOrchestrationRunRejectsMismatchedBlueprintRevisionProposal|ReferenceOrchestrationRunPersistsInjectedBlueprintRevisionProposalUntilUserApprovesIt|ReferenceOrchestrationRunPersistsAiBlueprintRevisionProposalUntilUserApprovesIt|ReferenceOrchestrationRunAppliesProposedBlueprintRevisionThenContinuesAfterApproval|AiReferenceBlueprintRevisionProposalProviderTests|PhotinoReferenceWorkflowSmokeTests' -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter MafToolRegistryTests -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'Reference|MafToolRegistryTests' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'ReferenceOrchestrationRunPersistsResumeAndCancelState|ReferenceAnchoredDraftServiceTests' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunPersistsResumeAndCancelState -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|MafToolRegistryTests' -v minimal`
- [x] `npm --prefix frontend run test:reference-anchor`
- [x] `npm --prefix frontend run build`
- [x] `npm --prefix frontend run lint`

Phase 11 is complete at the current implementation boundary: the proposal-provider path supports a production AI-backed provider using the selected chat model while preserving explicit approval and deterministic fallback, source/license/fact confirmation is explicit, high-risk recovery stops fail closed, and final insertion remains a separate user-confirmed edit/save path. Phase 12 shared-corpus storage, migration, global-vs-usage feedback, AI explanation, and corpus-management UI remain pending.

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchoredDraftService.cs`
- `src/Novelist.Core/App/IReferenceBlueprintRevisionProposalProvider.cs`
- `src/Novelist.Core/App/ReferenceOrchestrationStateMachine.cs`
- `src/Novelist.Infrastructure/App/DeterministicReferenceBlueprintRevisionProposalProvider.cs`
- `src/Novelist.Infrastructure/App/AiReferenceBlueprintRevisionProposalProvider.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs`
- `src/Novelist.App/Desktop/DesktopBridgeComposition.cs`
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
- [x] Corpus records include visibility, license status, source trust, and optional user-defined tags, but no required per-novel binding gate.
- [ ] Material search accepts story-context inputs and returns globally sourced candidates that are filtered by license/status policy and scored by beat function, emotion, POV, prose duty, technique, lexical similarity, embeddings when available, and feedback boosts.
- [ ] Retrieval gaps are explicit states, not silent free-drafting fallbacks: automatic query expansion may run first, weak matches must be marked low confidence and carry elevated draft-audit risk, optional no-reuse beats require approved reasons, and source-required beats must stop for user action.
- [ ] Blueprint generation no longer requires `anchor_ids`; it may accept an optional corpus search policy or explicit include/exclude anchors for advanced control.
- [ ] Material binding records exact selected corpus material ids against the current blueprint `analysis_contract_hash` so later draft candidates remain auditable.
- [ ] Per-novel known facts, forbidden facts, timeline state, character state, and POV boundaries still gate whether a globally sourced material may be used.
- [ ] AI can explain why it selected each material and which story need it satisfies, without requiring the user to manually choose libraries first.
- [ ] User feedback is split into global corpus feedback and per-novel usage feedback; accepting a material for one novel must not silently make it safe for another novel with different facts or POV.
- [x] Agent tools search the shared corpus by injected novel context but do not expose arbitrary file reads or allow the model to bypass source/license filters.
- [ ] UI provides corpus management as a library feature and reference-anchored drafting as an automatic retrieval feature, not as a per-novel setup checklist. Current coverage names and separates `语料库管理` from `参考写作检索`, adds selected-row bulk promote/archive actions, and exposes an independent material-library search/filter/sort/cross-page correction surface that does not require selected anchors; full library management IA, bulk import, automatic migration, and archive/delete policy for materials remain pending.

**Verification:**

- [ ] Migration tests from per-novel `reference_anchors` to shared corpus records or compatibility reads. Current coverage includes legacy schema rebuild to nullable workspace-corpus ownership plus explicit single-anchor and transactional selected-anchor promotion APIs; full automatic per-novel-to-shared corpus migration remains pending.
- [x] Service tests proving workspace-corpus compatibility materials can be searched from different novels without duplicating source import.
- [x] Tests proving per-novel forbidden facts and POV boundaries still reject globally sourced materials/candidates.
- [x] Tests proving license/visibility policy filters corpus results before AI selection.
- [x] Retrieval-gap tests covering automatic query expansion, weak-match audit risk, approved no-reuse continuation, and source-required stop states.
- [x] Binding/audit tests proving selected global material provenance is stored with blueprint hash and cannot be reused after blueprint edits.
- [x] Feedback tests proving global tag feedback and per-novel usage feedback have different scopes.
- [x] Bridge and agent tests proving `anchor_ids` are optional and policy-driven corpus retrieval is the default orchestration path.
- [ ] Frontend smoke test for corpus management and default automatic material selection. Current coverage asserts default automatic material selection, separated corpus-library/reference-writing section headings, create/list/edit corpus metadata, owner-scope filtering/counts, local list query/license/source-trust filters, selected-row bulk promote/archive actions, paginated row-level material browsing with local page filtering/sorting, independent material-library search without selected anchor ids, material-library current-page filtering/sorting, material-library cross-page selected bulk tag correction, single-material and current-page selected bulk material tag correction, material-library bulk tag correction, single-anchor promotion, and workspace-corpus archive actions; full corpus library management UI smoke remains pending.

Targeted Phase 12 thin-slice checks completed:

- [x] Orchestration binding can use `corpus_search_policy` include/exclude anchor filters and license-status filters when `anchor_ids` are omitted, while keeping the existing per-novel reference-anchor storage model.
- [x] Reference material read paths now include `reference_anchors.novel_id = 0` workspace-corpus compatibility anchors for listing, search, adaptation, audit, tag correction, and per-novel feedback validation, while still excluding private anchors from other novels.
- [x] Workspace-corpus compatibility anchors now carry `corpus_visibility`, `source_trust`, and `user_tags_json`; read paths include only `novel_id = 0 AND corpus_visibility = 'workspace'`, explicit `anchor_ids` cannot bypass private/restricted workspace visibility, and legacy `novel_id = 0` rows are promoted to workspace-visible once during schema migration.
- [x] SQLite storage now allows `reference_anchors.novel_id IS NULL` for workspace-corpus ownership while retaining `novel_id = 0` compatibility reads and bridge output; migration rebuilds legacy `reference_anchors` tables when needed so nullable ownership can be used without losing source hashes, segment ids, material ids, feedback, or audit provenance.
- [x] `ReferenceAnchorPayload` now exposes `owner_scope` and optional `owner_novel_id` so clients can distinguish per-novel anchors from workspace-corpus rows without relying on the legacy `novel_id = 0` sentinel.
- [x] Creating an anchor with `visibility = "workspace"` now stores it directly as a nullable workspace-corpus record; another novel can search the same material ids immediately without a manual reparenting helper.
- [x] `PromoteReferenceAnchorToWorkspaceCorpus` now provides an explicit bridge/service migration path from an owned per-novel anchor to nullable workspace-corpus ownership while preserving source hashes, source segment ids, material ids, build state, existing metadata when omitted, and per-novel feedback scope.
- [x] Nullable workspace-corpus anchors can be searched from multiple novels without duplicating `reference_source_segments` or `reference_materials`, and private/restricted nullable corpus rows remain hidden even when clients pass explicit `anchor_ids`.
- [x] A single workspace-corpus import can be searched from multiple novels without duplicating `reference_source_segments` or `reference_materials`; both novels see the same `material_id`, `source_segment_id`, and `source_hash` through the compatibility layer.
- [x] A real SQLite orchestration run can omit explicit `anchor_ids`, resolve `novel_id = 0` workspace-corpus anchors through the default corpus policy, bind selected material links, generate audited candidates, and stop at final insertion without writing chapter prose.
- [x] Desktop bridge JSON dispatch can omit the `anchor_ids` property entirely on `StartReferenceOrchestrationRun`; the persisted run normalizes to an empty anchor list, uses `corpus_search_policy` to select workspace-corpus material after blueprint approval, and stops at final insertion with selected material provenance.
- [x] A real MAF executor can start the default orchestration tool without exposing or passing `anchor_ids`; after user source/fact confirmation the persisted run uses workspace-corpus material through `corpus_search_policy` and stops at final insertion.
- [x] Real SQLite orchestration coverage proves workspace-corpus anchors are filtered by `corpus_search_policy.license_statuses` before binding selected materials.
- [x] Workspace-corpus usage feedback remains per-novel: accepted material feedback boosts binding only for the novel that recorded it, while other novels can still retrieve the shared material without inheriting that usage approval.
- [x] Material search ranking now applies current-novel accepted material feedback as an `accepted_feedback` score component without leaking that boost to other novels that search the same workspace corpus material.
- [x] The MAF `search_reference_materials` tool injects the active novel context, accepts story-context filters such as `narrative_duties` and `emotion_transitions`, describes license/visibility filtered corpus search, and returns material `score_components` for agent-side selection explanation.
- [x] Workspace-corpus material links persist the current blueprint `analysis_contract_hash`; blueprint revisions mark old links stale and draft generation rejects stale/hash-mismatched links until materials are rebound.
- [x] Material binding now runs deterministic query-expansion fallback when the exact beat query returns no candidates, marks selected fallback links with a negative `low_confidence` score component, and explains the expanded-query weak match in `fit_explanation`.
- [x] Draft generation and persisted draft re-audit now read selected material-link score components; candidates backed by `low_confidence` weak-match links fail draft audit with provenance risk and required fixes, which also routes orchestration through the existing high-risk draft-audit stop.
- [x] Approved no-reuse continuation is limited to beats without `source_backed_detail_target`; source-backed beats still require material-fit review, selected current material links, and non-`no-reuse` provenance before draft generation, even if `no_reuse_reason` is present.
- [x] The default orchestration UI now states that material retrieval uses story context against accessible workspace corpus materials, shows the active run's corpus search policy, and keeps selected-anchor restriction as an advanced override.
- [x] The reference-anchor Playwright workflow now asserts default automatic material selection UI state and bridge payload behavior: `anchor_ids` is `null`, include/exclude anchor filters are empty, `corpus_search_policy.mode` is `story_context`, and no `SaveContent` call occurs.
- [x] `SearchReferenceMaterials` now accepts `prose_duties` as a story-context input alongside `narrative_duties` and `emotion_transitions`; service, bridge, MAF, and frontend paths pass it through, search can filter and score by prose-duty fit, and returned material `score_components` expose `prose_duty`.
- [x] The reference-anchor create/list UI now exposes corpus metadata fields for `visibility`, `source_trust`, and `user_tags`; the Playwright workflow asserts the `CreateReferenceAnchor` payload and list-row display for this metadata without treating it as the complete corpus library management UI.
- [x] The reference-anchor list UI now exposes a single-anchor promote action for per-novel anchors; the Playwright workflow asserts `PromoteReferenceAnchorToWorkspaceCorpus` payload behavior and the promoted row's workspace-corpus owner display without treating it as the complete corpus library management UI.
- [x] The reference-anchor list UI now exposes owner-scope segmented filtering and counts for all, current-novel, and workspace-corpus anchors; the Playwright workflow asserts the promoted workspace-corpus row appears under the workspace filter and disappears under the current-novel filter.
- [x] The reference-anchor list UI now supports local corpus-list query filtering across title, author, path, tags, and metadata plus license, visibility, and source-trust filters; the Playwright workflow asserts matching and empty states without treating this as the complete corpus library management UI.
- [x] `UpdateReferenceAnchorMetadata` now provides a bridge/service/frontend path for editing title, author, license status, visibility, source trust, and user tags without changing source path, source hash, segment ids, material ids, build state, or feedback rows; the Playwright workflow asserts the update payload and row/filter display, while SQLite tests assert material identity preservation and private/restricted visibility boundaries.
- [x] The reference-anchor list UI now supports paginated row-level material browsing for a specific anchor/corpus row using `SearchReferenceMaterials` with explicit `anchor_ids`; the Playwright workflow asserts page summary, material id/text/score display, next-page navigation, and anchor-scoped page payloads without treating this as the complete material browser/library UI.
- [x] The row-level material browser now supports local current-page filtering by material id, text, type, tags, and source segment plus current-page sorting by score or material id; the Playwright workflow asserts matching, empty-filter state, stable score ordering, and keeps the anchor-scoped `SearchReferenceMaterials` payload unchanged. This is still not full material-library search, bulk editing, or import management.
- [x] The row-level material browser now supports single-material tag correction for function, emotion, scene, POV, and technique tags through `UpdateReferenceMaterialTags`; the Playwright workflow asserts the corrected row display and bridge payload without treating it as bulk material editing or full library management.
- [x] The row-level material browser now supports current-page selected bulk tag correction for function, emotion, scene, POV, and technique tags through transactional `UpdateReferenceMaterialsTags`; the Playwright workflow asserts selection counts, corrected row display, and bridge payload while SQLite coverage proves rollback when any selected material is inaccessible. This remains a current-page correction slice, not full material-library bulk editing or import management.
- [x] The corpus management panel now exposes an independent `材料库` surface that searches accessible corpus materials with `anchor_ids: []`, shows score components, supports current-page filtering/sorting, preserves selected materials across material-library page navigation, and uses `UpdateReferenceMaterialsTags` for selected bulk tag correction. The Playwright workflow asserts the library search payload, filtered/empty/sorted current-page states, cross-page selection counts, corrected row display, and library-specific bulk-correction note. This is still not full material-library IA, material archive/delete policy, bulk import, or automatic migration.
- [x] The corpus library list now supports selected-row bulk promotion for per-novel anchors and selected-row bulk archive for workspace-corpus rows; promotion uses the transactional `PromoteReferenceAnchorsToWorkspaceCorpus` bridge API and archive uses the transactional `DeleteReferenceAnchors` bridge API. The Playwright workflow asserts selection counts, processed-selection clearing, bulk promotion payloads, bulk archive payloads, and archived rows disappearing from the visible library. This remains a selected-row management slice, not bulk import, automatic migration, or bulk material editing.
- [x] The reference-anchor page now separates current UI surfaces into `语料库管理` (`导入语料来源` and `库条目`) and `参考写作检索`; the Playwright workflow asserts these headings without treating the page as the complete corpus library IA.
- [x] Reference MAF tools now make the current agent authority boundary explicit: agents can list/search/adapt/audit already imported reference materials and start orchestration with injected novel context, but no reference tool exposes source import, file picking, metadata promotion/update/delete, arbitrary path/source parameters, or source/license filter bypass.
- [x] `DeleteReferenceAnchor` now keeps existing per-novel anchor deletion behavior while treating workspace-corpus deletion as archival: visible workspace corpus rows are marked `restricted`, disappear from normal list/search/build-status reads, and retain source segments/material provenance. The corpus list UI exposes this as a row-level archive action.
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'UpdateAnchorMetadataCanPromoteToWorkspaceCorpusWithoutChangingMaterialIdentity|UpdateAnchorMetadataCannotBypassOtherNovelPrivateOrWorkspaceRestrictedVisibility' -v minimal -p:UseSharedCompilation=false`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'WorkspaceCorpus|ReferenceOrchestrationRunUsesWorkspaceCorpus|ReferenceOrchestrationRunFiltersWorkspaceCorpus|ReferenceOrchestrationRunUsesCorpusSearchPolicy' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'BridgeReferenceOrchestrationRunUsesWorkspaceCorpusWhenAnchorIdsAreOmitted|ReferenceOrchestrationRunUsesWorkspaceCorpusAnchorsWithoutExplicitAnchorIds' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationAgentToolDefaultsToWorkspaceCorpusWithoutAnchorIds -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter WorkspaceCorpusMaterialsCanBeSearchedFromDifferentNovelsWithoutDuplicatingImport -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'WorkspaceCorpusMaterialsCanBeSearchedFromDifferentNovelsWithoutDuplicatingImport|NullableWorkspaceCorpusMaterialsCanBeSearchedFromDifferentNovelsWithoutDuplicatingImport|NullableWorkspaceCorpusVisibilityCannotBeBypassedWithExplicitAnchorIds|LegacyReferenceAnchorSchemaAllowsMigratingWorkspaceCorpusRowsToNullableOwnership|LegacyWorkspaceCorpusRowsMigrateToWorkspaceVisibleWithoutLosingMaterialIdentity|WorkspaceCorpusVisibilityFiltersAnchorsBeforeSearchAdaptAuditTagAndFeedback|AdaptAndAuditCanUseWorkspaceCorpusMaterialsWithoutReadingOtherNovelPrivateMaterials' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchorServiceTests -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter CreateWorkspaceVisibleAnchorStoresAsSharedCorpusWithoutManualReparenting -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|MafToolRegistryTests' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'PromotePerNovelAnchorToWorkspaceCorpusPreservesMaterialIdentityAndFeedbackScope|PromoteAnchorRequiresCurrentNovelOwnership|PromoteAnchorPreservesExistingCorpusMetadataWhenOptionalFieldsAreOmitted' -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'PromoteReferenceAnchorToWorkspaceCorpusPayloadUsesStableSnakeCaseJsonNames|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceOrchestrationAgentToolStartsRunWithoutApprovingHumanDecisions|ReferenceAnchorContractTests|MafToolRegistryTests' -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'CreateToolsIncludesReferenceToolsOnlyWhenServicesAreConfigured|ReferenceMaterialToolInjectsNovelContext' -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAgentToolsCannotImportCorpusSourcesOrReadArbitraryFiles|CreateToolsIncludesReferenceToolsOnlyWhenServicesAreConfigured|ReferenceMaterialToolInjectsNovelContext|ReferenceOrchestrationAgentToolStartsRunWithoutApprovingHumanDecisions|MafToolRegistryTests' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'DeleteWorkspaceCorpusAnchorArchivesWithoutDeletingMaterialProvenance|DeleteAnchorRemovesAnchorAndStatus' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter SearchMaterialsBoostsAcceptedMaterialFeedbackOnlyForCurrentNovel -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter WorkspaceCorpusFeedbackBoostsOnlyTheNovelThatRecordedUsage -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter WorkspaceCorpusMaterialLinksAreBoundToCurrentBlueprintAnalysisContract -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter BindBlueprintMaterialsMarksExpandedQueryFallbackAsLowConfidenceWeakMatch -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'BuildDraftAuditFailsWhenSelectedMaterialLinkIsLowConfidenceWeakMatch|BindBlueprintMaterialsMarksExpandedQueryFallbackAsLowConfidenceWeakMatch' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'SourceBackedNoReuseBeatStillRequiresSelectedMaterialBeforeDraftGeneration|ApprovedNoReuseBeatGeneratesDraftWithoutSelectedMaterialLink|RequiredMaterialBeatIdsStillRequiresSourceBackedNoReuseBeats|EnsureCandidateProvenanceRejectsNoReuseForSourceBackedBeat|ReviewDoesNotSkipMaterialFitForSourceBackedBeatWithNoReuseReason|BuildDraftAuditFailsWhenSourceBackedBeatUsesNoReuseProvenance' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'BuildDraftAuditFailsWhenSelectedMaterialLinkIsLowConfidenceWeakMatch|BindBlueprintMaterialsMarksExpandedQueryFallbackAsLowConfidenceWeakMatch|ApprovedNoReuseBeatGeneratesDraftWithoutSelectedMaterialLink|SourceBackedNoReuseBeatStillRequiresSelectedMaterialBeforeDraftGeneration|ReferenceOrchestrationRunStopsForHighRiskDecisionWhenMaterialBindingHasMissingLinks|ReviewDoesNotSkipMaterialFitForSourceBackedBeatWithNoReuseReason|RequiredMaterialBeatIdsStillRequiresSourceBackedNoReuseBeats' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter LegacyWorkspaceCorpusRowsMigrateToWorkspaceVisibleWithoutLosingMaterialIdentity -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter WorkspaceCorpusVisibilityFiltersAnchorsBeforeSearchAdaptAuditTagAndFeedback -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceMaterialToolCannotBypassWorkspaceCorpusVisibilityWithExplicitAnchorIds -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceAnchorContractTests -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'SearchReferenceMaterialsPayloadUsesStableNarrativeFilterJsonNames|MafToolRegistryTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations' -v minimal`
- [x] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceMaterialsTagUpdatePayloadUsesStableSnakeCaseJsonNames|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal -p:UseSharedCompilation=false`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'SearchMaterialsFiltersAndScoresByProseDutyStoryContext|SearchMaterialsFiltersByNarrativeDutyEmotionTransitionPovTechniqueAndType|SearchMaterialsMatchesSubtextDutyForObjectBasedExternalEvidence|SearchMaterialsMatchesSubtextDutyForRestrainedObjectActionEvidence|BindBlueprintMaterialsPreservesLexicalScoreForUnknownLicenseTruncatedPreviews|WorkspaceCorpusMaterialLinksAreBoundToCurrentBlueprintAnalysisContract' -v minimal`
- [x] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'UpdateMaterialsTagsBulkMarksSelectedMaterialsAsUserVerified|UpdateMaterialsTagsBulkRollsBackWhenAnyMaterialIsNotAccessible' -v minimal -p:UseSharedCompilation=false`
- [x] `npm --prefix frontend run test:reference-anchor`
- [x] `npm --prefix frontend run build`
- [x] `npm --prefix frontend run lint`

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
