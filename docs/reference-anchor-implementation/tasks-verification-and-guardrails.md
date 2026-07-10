# Reference Anchor Verification and Guardrails

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

> **Status boundary (2026-07-10):** “Default orchestration” evidence in this history refers to the older reference main panel unless a checkpoint explicitly names `ChapterReferencePanel`. It must not be used alone as proof that the current editor default path is unified or restart-safe; corpus-driven writing M9 supplies the automated evidence, while real-user validation remains open.

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

Run after Phase 10 frontend workflow hardening once the Playwright harness exists:

```text
cd frontend
npm run test:reference-anchor
```

Run after the baseline Phase 13 app-wide Playwright coverage exists:

```text
cd frontend
npm run test:app
```

Run for the closed Phase 13 quality gate and as regression before future release-quality changes:

```text
cd frontend
npm run test:app:full
npm run test:app:stress
npm run test:app:usability
```

Run after Phase 14 style anchoring commands exist:

```text
cd frontend
npm run test:reference-style
npm run test:reference-style:stress
```

Run after Phase 15 product-merge workflows exist:

```text
cd frontend
npm run test:phase15
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

- Do not put reference source text into `novelist.md`, chapter files, skills, or preferences.
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
- Do not ship high-fidelity imitation without source-leak, n-gram overlap, rewrite-level, fact, POV, and style-quality audit tests.
- Do not let LLM-assisted style analysis write ungrounded labels; every advanced label must cite source segment ids or be downgraded/rejected.
- Do not let style profile generation duplicate large source text outside the source segment/material tables.
- Do not port `goink-master` by adding new Go/Wails/Python runtime code; port behavior into current .NET/Photino/React modules.
- Do not expose arbitrary local file reads through EPUB/TXT/MD import or drag/drop.
- Do not let import failures leave orphaned novel rows, chapter files, Git repos, RAG/index state, or reference build state.
- Do not let incomplete import runs left by process death remain unreconciled after startup.
- Do not silently import low-confidence decoded text; visible failure is preferable to mojibake.
- Do not allow oversized imports or EPUB expansion to bypass configured limits.
- Do not delete successfully imported user data only because the post-import Git commit failed.
- Do not let style sample extraction or narrative pattern extraction write chapter content, approve reference blueprints, approve final insertion, or bypass audit.
- Do not expose Git reset, checkout, revert, restore, or other mutating history operations in the Phase 15 Git visualization UI.
- Do not let automatic update checks block startup, depend on live network in tests, or open external URLs without explicit user action.
- Do not let copyable diagnostics include API keys, bearer tokens, local secrets, or long raw source text.

## Phase 10 Design Decisions Boundary

- Unknown-license source previews: resolved for the current implementation. Search/library preview payloads truncate exact text by default; complete source/material text remains in SQLite for provenance, adaptation, binding, and audit.
- Source segment text policy: keep the current imported source segment/material text behavior; revisit only if the Phase 12 shared-corpus storage model changes the import schema.
- Optional model assistance: resolved for the current implementation. Extraction, material tagging, slot adaptation, rewrite-level classification, and reuse audit stay deterministic-only. Future LLM-assisted tagging/adaptation requires an explicit opt-in interface or feature flag and must feed deterministic review/binding/audit instead of bypassing those gates.
- Search scope: resolved for the current implementation. Keep reference-anchor results isolated in `SearchReferenceMaterials` and the dedicated reference panel; do not merge them into global `SearchAll` until a staged opt-in design defines result taxonomy, ranking, and preview policy.
- Revision lineage: current behavior keeps in-place blueprint revision rows, invalidates review/material links after contract changes, and leaves fuller lineage expansion to a future migration.
- Failed review assistance: current behavior can persist field-level proposed revisions from an injectable proposal provider while keeping them separate from approval; proposals are rebound to the current blueprint/review before persistence. The default provider is deterministic, and the desktop composition wires an AI-backed provider that uses the selected chat model, validates model JSON as untrusted, filters changes to current review defects/supported fields, and falls back deterministically when no model is selected or output is invalid. Agent/user authorization stays explicit: model proposals are never applied without the user approval path.
- Pending revision authorization: applying an orchestration blueprint revision uses the persisted pending proposal as the source of truth. A client may approve with an empty payload or echo the same proposal, but may not replace proposal changes even if `blueprint_id` and `review_id` match.
- Transition/no-reuse policy: approved no-reuse transition beats are allowed only when the blueprint carries an explicit no-reuse reason; source-backed beats still require selected material links.
- Candidate assembly: resolved for current implementation as beat-level candidates only; full-chapter candidate assembly remains deferred.
- Stale blueprint UX: resolved for the current implementation. Preserve stale blueprints read-only for comparison, keep them visible, disable review/approval/revision/material binding/candidate generation, and show a regeneration prompt.
- Development workflow: resolved as explicit frontend build/Vite steps for faster backend-only loops.
- Phase 10 runtime verification: resolved as layered coverage. Playwright mock-bridge tests own the full frontend workflow and screenshot/DOM state matrix. .NET integration tests own bridge/service behavior and production composition. Real Photino verification is a minimal runtime smoke, not a manual full-workflow requirement.
- Advanced mode boundary: this records the historical Phase 11 frontend surface. The current default chapter panel uses the persistent blueprint session and exposes the compact target → blueprint → draft → insert path; manual material search and manual blueprint generate/revise/review/approve/bind/draft controls remain expert-only, and the browser workflows assert both information layers.

## Phase 11 Automation Decisions Closed

- Default stop points: source/license/fact confirmation, blueprint approval, AI-proposed blueprint revision approval, material-binding gaps, stale blueprints, draft-audit failures, and final insertion remain explicit stops; low-risk blueprint generation, deterministic review, binding after approval, candidate generation, and draft audit can run automatically.
- Approval granularity: approving the compact blueprint summary approves the current blueprint analysis contract, guarded by context/source-plan/analysis-contract hashes and invalidated by reviewed-field edits.
- AI revision authority: AI can propose failed-review field fixes through the proposal provider, but application always uses the persisted pending proposal and requires user approval.
- Run persistence: orchestration runs and events live in the reference-anchor SQLite store and are exposed through bridge/frontend/agent read APIs; chat/session mirroring is not part of the current implementation.
- Candidate insertion UX: final insertion is a separate user-confirmed chapter edit/save path. `ResumeReferenceOrchestrationRun` rejects `approve_final_insertion`.
- Failure recovery: high-risk stops preserve run state and findings for inspection; resolving a high-risk stop marks the run failed so users regenerate, revise, or start a new run instead of free-drafting through the block.

## Phase 12 Shared Corpus Decisions Closed or Deferred

- Corpus scope: resolved for Phase 12 as workspace-level shared corpus. User-level library packs remain future product work.
- Source ownership migration: resolved for Phase 12 with nullable workspace-corpus ownership plus compatibility handling for legacy `novel_id = 0` rows and positive-owner workspace rows.
- Material identity: resolved for Phase 12 by preserving anchor-derived source segment and material ids across promotion, migration, archive, restore, and metadata edits.
- Retrieval gap policy: resolved for Phase 12 at the safe-gate boundary: weak/low-confidence matches and missing required source-backed links stop instead of free-drafting; broader UX tuning belongs to Phase 13 or later product work.
- Feedback scope: resolved for Phase 12 as per-consuming-novel feedback for shared material usage; global ranking feedback remains future product work.
- UI information architecture: resolved for Phase 12 by separating `语料库管理` from `参考写作检索`; deeper library product IA remains future product work.
- Search policy: resolved for the current implementation. Default orchestration uses `story_context`, `max_results_per_beat = 3`, no selected-anchor restriction, and `license_statuses = ["user_provided"]`; `unknown` or narrower include/exclude anchor filters must be supplied explicitly through the advanced corpus search policy.
- Archive/delete policy: resolved for the current implementation. Per-novel anchors keep hard-delete behavior. Visible workspace-corpus rows are archived by setting `corpus_visibility = restricted`, which removes them from normal list/search/build-status reads while preserving source segments, materials, hashes, and provenance for audit/debug history.
- Agent authority: resolved for the current implementation. MAF reference tools may list/search/adapt/audit already imported materials and start orchestration with injected novel context, but they do not expose source import, file picking, metadata promotion/update/delete, arbitrary path/source parameters, or source/license filter bypass. New corpus imports remain bridge/UI/user-driven.

## Phase 13 Full-Product QA Boundary

- Status: complete as of 2026-07-07. The existing `test:app` smoke remains baseline coverage, not the full completion boundary.
- Suite boundary: `test:reference-anchor` remains the deep reference workflow, `test:app` owns baseline broad regression, and the expanded Phase 13 boundary is covered by full-surface, stress, and usability commands.
- Fixture strategy: tests must support deterministic success, slow, timeout, malformed, and failed bridge responses, with bridge-call recording per test.
- Automatic material generation: the tested default path must pass novel/reference text into the workflow, then verify deterministic source segmentation, material extraction, source hashes, provenance, story-context search, blueprint beat binding, and draft audit without manual text splitting or per-novel corpus binding.
- Large-input scope: include at least one 10MB Chinese novel/reference fixture generated by the test harness or stored as compact source data, and verify import/segmentation progress, material browsing, search, binding, and no white screen.
- Usability scope: produce `output/playwright/phase13/usability-report.md` with severity, reproduction steps, screenshots, user impact, and proposed fixes for workflow friction and product-quality issues.
- Selector policy: prefer accessible roles/names; use scoped locators only where visible labels are intentionally repeated or dynamic.
- Visual baseline scope: screenshots cover startup, shell, editor, chat, search, reference orchestration, corpus library, material library, settings, metadata, error states, 10MB stress state, and compact viewport states.
- Real Photino boundary: keep Vite/mocked-bridge tests as the broad product surface, but verify production `dist`, WebView cache freshness, Monaco visibility, representative bridge routing, and stale chunk absence through targeted desktop tests/smoke.
- CI policy: `npm run verify` remains a baseline command; Phase 13 closure and future regressions additionally require `test:app:full`, `test:app:stress`, and `test:app:usability`.

## Phase 14 Advanced Style Anchoring Boundary

- Status: open as of 2026-07-07.
- Scope: multi-scale segmentation, advanced material/style taxonomy, style profiles, optional source-grounded LLM analysis, style-aware retrieval, beat-level style contracts, style-guided candidates, source-leak/style-quality audit, UI/agent boundaries, and Phase 14 regression tests.
- Safety policy: basic reference import/search must keep working without model configuration. LLM-assisted style analysis is opt-in, validated, source-grounded, and unable to bypass deterministic gates.
- Imitation policy: high-fidelity style anchoring must be feature- and evidence-based, not direct copying. Strong imitation mode increases audit strictness.
- Regression policy: Phase 14 must keep the full Phase 13 matrix green and add `test:reference-style` plus `test:reference-style:stress`.

## Phase 15 Goink-Master Feature Merge Boundary

- Status: proposed as of 2026-07-07.
- Scope: EPUB/TXT/MD import with encoding detection and cleanup, style sample library and validated imitation skill extraction, narrative pattern extraction, read-only Git history visualization, configurable Git author identity, sidebar/window persistence, update checks, multi-range chapter selection, visible copyable errors, relative-time refresh, and associated bug fixes.
- Merge policy: `goink-master` is a read-only behavior reference. New code belongs in current Novelist contracts, core services, infrastructure, Photino desktop integration, owned TypeScript adapter, and React components.
- Safety policy: import/style/pattern/Git/update features must not add arbitrary file reads, agent-side imports, implicit chapter mutation, direct `SaveContent`, or unsafe external URL opening.
- Regression policy: Phase 15 must keep the full Phase 13 matrix green, preserve Phase 14 style/profile guardrails, and add `test:phase15` plus backend parser/import/Git/style/pattern/update fault-injection coverage.

## Recommended Next Coding Sessions

Start with Phase 14 contracts, storage, deterministic style baseline, and source-leak audit foundations. Keep the closed Phase 13 Playwright QA gate as the regression boundary; do not restart from Phase 0-13 unless contracts or implemented behavior have regressed.

Latest Phase 13 closure scope: the full matrix passed on 2026-07-07, including frontend build/lint, `test:reference-anchor`, `test:app`, `test:app:full`, `test:app:stress`, `test:app:usability`, `Novelist.Tests`, and `Novelist.IntegrationTests`. Artifacts are under `output/playwright/phase13/`, including smoke/full/reference/stress screenshots and traces plus `usability-report.md`.

Latest Phase 12 batch source import UI slice: `npm --prefix frontend run test:reference-anchor` passed after the `导入语料来源` panel added a basic batch source-path textarea backed by `CreateReferenceAnchors`. The mocked browser workflow asserts two imported paths, ordered derived titles, per-path source-kind inference, shared workspace visibility/source trust/user tags, and no automatic `SaveContent` call. Earlier bridge/service scope remains green: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'CreateReferenceAnchorsPayloadUsesStableSnakeCaseJsonNames|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods|BridgeFrontendContractTests|CompatibilityAppMethodListHasExpectedCoverage' -v minimal -p:UseSharedCompilation=false` passed 14/14, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'MafToolRegistryTests' -v minimal -p:UseSharedCompilation=false` passed 16/16, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'CreateAnchorsImportsWorkspaceCorpusSourcesWithoutLosingMaterialIdentity|CreateAnchorsValidatesBatchSize' -v minimal -p:UseSharedCompilation=false` passed 2/2, `npm --prefix frontend run lint` passed, and `npm --prefix frontend run build` passed with only the existing Vite large-chunk warning. At that slice, full library-pack import and final corpus-library IA were still later-slice scope; later Phase 12 slices closed the current implementation boundary, while deeper product IA is deferred.

Latest verified scope: `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'ReferenceOrchestrationRunPersistsResumeAndCancelState|BridgeReferenceOrchestrationRunUsesWorkspaceCorpusWhenAnchorIdsAreOmitted|ReferenceOrchestrationRunUsesWorkspaceCorpusAnchorsWithoutExplicitAnchorIds|BuildDraftAuditFailsWhenSelectedMaterialLinkIsLowConfidenceWeakMatch|BindBlueprintMaterialsMarksExpandedQueryFallbackAsLowConfidenceWeakMatch' -v minimal` passed 5/5 after source confirmation decisions gained an explicit `confirm_license_status` action and workspace-corpus happy-path fixtures were adjusted to avoid weak-match fallback. `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|MafToolRegistryTests' -v minimal` passed 30/30.

Latest frontend Phase 12 smoke: `npm --prefix frontend run test:reference-anchor` passed after the default orchestration panel started showing story-context workspace-corpus retrieval policy and the browser workflow asserted default automatic material selection without selected `anchor_ids`, empty include/exclude anchor filters, and no `SaveContent` call. At that slice, corpus management UI was still a separate Phase 12 item; later Phase 12 slices completed the current-boundary corpus management surface.

Latest Phase 12 story-context search scope: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'SearchReferenceMaterialsPayloadUsesStableNarrativeFilterJsonNames|MafToolRegistryTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations' -v minimal` passed 17/17 and `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'SearchMaterialsFiltersAndScoresByProseDutyStoryContext|SearchMaterialsFiltersByNarrativeDutyEmotionTransitionPovTechniqueAndType|SearchMaterialsMatchesSubtextDutyForObjectBasedExternalEvidence|SearchMaterialsMatchesSubtextDutyForRestrainedObjectActionEvidence|BindBlueprintMaterialsPreservesLexicalScoreForUnknownLicenseTruncatedPreviews|WorkspaceCorpusMaterialLinksAreBoundToCurrentBlueprintAnalysisContract' -v minimal` passed 6/6 after adding `prose_duties` to material search contracts, scoring, MAF tools, and the frontend advanced search flow. `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run build`, and `npm --prefix frontend run lint` passed; Vite still reports only the existing large-chunk warning.

Latest Phase 12 corpus-metadata UI thin slice: `npm --prefix frontend run test:reference-anchor` passed after the create-reference form exposed `visibility`, `source_trust`, and semicolon-separated `user_tags`, the anchor list displayed those metadata fields, and the mocked browser workflow asserted the `CreateReferenceAnchor` bridge payload. At that slice, dedicated library information architecture, global corpus schema, and migration work were still later-slice scope; later Phase 12 slices completed the current-boundary corpus management and storage model.

Latest Phase 12 single-anchor promotion UI slice: `npm --prefix frontend run test:reference-anchor` passed after the anchor list exposed a per-novel promote action that calls `PromoteReferenceAnchorToWorkspaceCorpus`, preserves existing metadata by omitting optional promotion fields, updates the row display to `workspace_corpus`, and verifies owner-scope filtering/counts for all/current-novel/workspace-corpus rows. This remains a targeted action inside the reference panel, not the full corpus library information architecture.

Latest Phase 12 corpus-list filtering UI slice: `npm --prefix frontend run test:reference-anchor` passed after the anchor list gained local query filtering across title, author, path, tags, and metadata plus license, visibility, and source-trust filters. The mocked browser workflow asserts author/tag query matches, empty states, source-trust filtering, license filtering, and filter clearing. This remains an incremental list-management slice inside the reference panel, not the full corpus library information architecture.

Latest Phase 12 corpus-metadata edit slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal` passed 18/18, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'UpdateAnchorMetadataCanPromoteToWorkspaceCorpusWithoutChangingMaterialIdentity|UpdateAnchorMetadataCannotBypassOtherNovelPrivateOrWorkspaceRestrictedVisibility' -v minimal -p:UseSharedCompilation=false` passed 2/2, and `npm --prefix frontend run test:reference-anchor` passed after adding `UpdateReferenceAnchorMetadata`. The method edits only title, author, license status, visibility, source trust, and user tags; SQLite coverage proves it can move an owned anchor into workspace-corpus ownership without changing source hash, source segment ids, or material ids, and cannot bypass other-novel private or restricted workspace-corpus visibility. The frontend exposes this as row-level metadata editing in the current reference panel, still not a full corpus library information architecture.

Latest Phase 12 row-level material preview slice: `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run build`, and `npm --prefix frontend run lint` passed after the anchor list gained a row-level material preview backed by `SearchReferenceMaterials` with explicit `anchor_ids`. The mocked browser workflow asserts material id, text, and score-component display plus the anchor-scoped search payload. This is a focused material-browsing slice inside the current reference panel, not the full corpus library material browser, sorting, bulk editing, or import management UI.

Latest Phase 12 paginated material browsing slice: `npm --prefix frontend run test:reference-anchor` passed after the row-level material preview started preserving `SearchReferenceMaterials` pagination metadata, showing page/total counts, and loading next/previous pages with the same anchor-scoped filters. The mocked browser workflow asserts page 1 and page 2 material rows and bridge payloads with `anchor_ids: [101]`, `page`, and `size: 5`. This still stops short of full material-level library management, sorting, bulk editing, or archive/delete policy.

Latest Phase 12 single-material tag correction slice: `npm --prefix frontend run test:reference-anchor` passed after the row-level material browser gained a compact tag-correction form for function, emotion, scene, POV, and technique tags backed by `UpdateReferenceMaterialTags`. The mocked browser workflow asserts corrected row display and the bridge payload with `origin: "ui"` and `note: "corpus material browser correction"`. This is a single-row correction path, not bulk material editing, sorting, or archive/delete policy.

Latest Phase 12 material-browser filter/sort slice: `npm --prefix frontend run test:reference-anchor` passed after the row-level material browser gained local current-page filtering by material id/text/type/tags/source segment and current-page sorting by highest score or material id. The mocked browser workflow asserts matching, empty-filter state, score ordering, id ordering, and unchanged anchor-scoped page payloads. This remains local page-level browsing, not full material-library search, bulk editing, or import management.

Latest Phase 12 selected-row bulk corpus actions slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'DeleteReferenceAnchorsPayloadUsesStableSnakeCaseJsonNames|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal -p:UseSharedCompilation=false`, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'DeleteAnchorsBulkDeletesPrivateAnchorsAndArchivesWorkspaceCorpusRows|DeleteAnchorsBulkRollsBackWhenAnyAnchorCannotBeProcessed' -v minimal -p:UseSharedCompilation=false`, and `npm --prefix frontend run test:reference-anchor` passed after the corpus library list moved selected-row bulk archive to transactional `DeleteReferenceAnchors`. Coverage asserts stable bridge JSON, bridge routing, compatibility registration, mixed private-delete/workspace-archive behavior, rollback when any selected row cannot be processed, selection counts, clearing processed selections, bulk promotion payloads, bulk archive payloads, and archived rows disappearing from the visible library. This remains a selected-row management slice, not bulk import, automatic migration, or bulk material editing.

Latest Phase 12 corpus-library IA naming slice: `npm --prefix frontend run test:reference-anchor` passed after the reference-anchor page introduced explicit `语料库管理` and `参考写作检索` sections, with import/list headings under the library side and default orchestration/manual debugging under the writing-retrieval side. The mocked browser workflow asserts those headings so future UI work does not collapse corpus management back into a per-novel setup checklist. At that slice, full corpus library IA, bulk import/migration, and material-browser management were still later-slice scope; later Phase 12 slices completed the current implementation boundary, while deeper library product IA remains future work.

Latest Phase 12 current-page bulk material correction slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceMaterialsTagUpdatePayloadUsesStableSnakeCaseJsonNames|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal -p:UseSharedCompilation=false`, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'UpdateMaterialsTagsBulkMarksSelectedMaterialsAsUserVerified|UpdateMaterialsTagsBulkRollsBackWhenAnyMaterialIsNotAccessible' -v minimal -p:UseSharedCompilation=false`, and `npm --prefix frontend run test:reference-anchor` passed after adding transactional `UpdateReferenceMaterialsTags` and current-page selected material correction controls to the row-level material browser. Coverage asserts stable bridge JSON, bridge routing, compatibility registration, selected material ids, UI origin/note, corrected row display, selection clearing, and rollback when any selected material is inaccessible. This is current-page bulk tag correction, not full material-library management, cross-page selection, archive/delete policy, or import management.

Latest Phase 12 material-library cross-page selection slice: `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run lint`, and `npm --prefix frontend run build` passed after the corpus management panel's dedicated `材料库` surface began preserving selected materials across material-library page navigation for bulk tag correction. The mocked browser workflow asserts `SearchReferenceMaterials` is called with `anchor_ids: []` for accessible corpus-wide search, verifies score-component display, filtered/empty/sorted page states, selects one material on page 1 and another on page 2, and asserts `UpdateReferenceMaterialsTags` with `note: "corpus material library bulk correction"` updates both selected material ids. This is an independent material-library management surface, but it still does not cover bulk import, automatic migration, material archive/delete policy, or the final full corpus library IA.

Latest Phase 12 material-library soft archive slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods|BridgeFrontendContractTests|CompatibilityAppMethodListHasExpectedCoverage' -v minimal -p:UseSharedCompilation=false`, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'DeleteMaterialsArchivesSelectedMaterialsWithoutDeletingProvenance|DeleteMaterialsRollsBackWhenAnyMaterialIsNotAccessible|UpdateMaterialsTagsBulkRollsBackWhenAnyMaterialIsNotAccessible|DeleteWorkspaceCorpusAnchorArchivesWithoutDeletingMaterialProvenance' -v minimal -p:UseSharedCompilation=false`, `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run lint`, and `npm --prefix frontend run build` passed after adding `DeleteReferenceMaterials`. Material archive is a soft archive via `reference_materials.archived_at`: normal search/adapt/audit/tag-correction/vector-scoring paths hide archived materials, inaccessible selections roll back transactionally, and source segments/material rows/historical provenance remain intact. The material-library UI can archive cross-page selected materials and removes archived rows from the current result page. This is a selected-material archive policy slice, not full restore/delete administration, bulk import, automatic migration, or final corpus-library IA.

Latest Phase 12 material-library restore slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods|BridgeFrontendContractTests|CompatibilityAppMethodListHasExpectedCoverage' -v minimal -p:UseSharedCompilation=false`, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'RestoreMaterialsMakesArchivedMaterialsSearchableAgain|RestoreMaterialsRollsBackWhenAnyMaterialIsNotAccessible|DeleteMaterialsArchivesSelectedMaterialsWithoutDeletingProvenance|DeleteMaterialsRollsBackWhenAnyMaterialIsNotAccessible' -v minimal -p:UseSharedCompilation=false`, `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run lint`, and `npm --prefix frontend run build` passed after adding `RestoreReferenceMaterials` and `SearchReferenceMaterials.archive_filter`. Default material retrieval still uses active-only rows; the material library can switch to archived rows, disables tag correction while browsing archived rows, restores selected archived material ids transactionally, and removes restored rows from the archived result page. This is a restore administration slice, not bulk import, automatic migration, or final corpus-library IA.

Latest Phase 12 agent authority slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAgentToolsCannotImportCorpusSourcesOrReadArbitraryFiles|CreateToolsIncludesReferenceToolsOnlyWhenServicesAreConfigured|ReferenceMaterialToolInjectsNovelContext|ReferenceOrchestrationAgentToolStartsRunWithoutApprovingHumanDecisions|MafToolRegistryTests' -v minimal` passed 16/16 after reference MAF tool descriptions and schema coverage were tightened around the already-imported-corpus boundary. Agents can search/use filtered corpus materials with injected novel context, but no reference tool exposes source import, file picking, path/source parameters, metadata promotion/update/delete, or author-only approvals.

Latest Phase 12 archive/delete slice: `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'DeleteWorkspaceCorpusAnchorArchivesWithoutDeletingMaterialProvenance|DeleteAnchorRemovesAnchorAndStatus' -v minimal` passed 2/2 and `npm --prefix frontend run test:reference-anchor` passed after `DeleteReferenceAnchor` kept hard-delete semantics for per-novel anchors but archived visible workspace-corpus anchors to `restricted`. Archived corpus rows disappear from normal list/search/build-status reads while retaining source segments and material provenance; the current corpus list exposes this as a row-level archive action, not a complete bulk archive/delete manager.

Latest Phase 12 nullable corpus storage and owner-contract slice: `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchorServiceTests -v minimal` passed 51/51 after `reference_anchors.novel_id` became nullable in SQLite storage, workspace-corpus read predicates accepted both `novel_id IS NULL` and legacy `novel_id = 0`, and legacy `reference_anchors` schemas are rebuilt when needed so nullable ownership can be used without losing source hashes, source segment ids, material ids, feedback, or audit provenance. `ReferenceAnchorPayload` now adds `owner_scope` plus optional `owner_novel_id`, while retaining `novel_id: 0` for workspace-corpus compatibility. Creating with `visibility = "workspace"` now stores the source as a nullable workspace-corpus row immediately, so another novel can retrieve the same material ids without manual DB reparenting.

Latest Phase 12 explicit promotion slice: `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'PromotePerNovelAnchorToWorkspaceCorpusPreservesMaterialIdentityAndFeedbackScope|PromoteAnchorRequiresCurrentNovelOwnership|PromoteAnchorPreservesExistingCorpusMetadataWhenOptionalFieldsAreOmitted' -v minimal` passed 3/3 and `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'PromoteReferenceAnchorToWorkspaceCorpusPayloadUsesStableSnakeCaseJsonNames|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal` passed 3/3 after adding `PromoteReferenceAnchorToWorkspaceCorpus`. The method requires the initiating novel to own the per-novel anchor, converts that row to nullable workspace-corpus ownership, preserves material/segment/source identities and per-novel feedback scope, and preserves existing source trust/tags when optional promotion metadata is omitted. This is an explicit single-anchor migration path, not the full bulk corpus library management UI or automatic migration.

Latest Phase 12 legacy workspace-owner migration slice: `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter LegacyPerNovelWorkspaceRowsAutoMigrateToNullableWorkspaceOwnership -v minimal -p:UseSharedCompilation=false` and `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'LegacyWorkspaceCorpusRowsMigrateToWorkspaceVisibleWithoutLosingMaterialIdentity|LegacyReferenceAnchorSchemaAllowsMigratingWorkspaceCorpusRowsToNullableOwnership|PromotePerNovelAnchorToWorkspaceCorpusPreservesMaterialIdentityAndFeedbackScope|PromoteAnchorsToWorkspaceCorpusPromotesOwnedRowsAtomically|WorkspaceCorpusVisibilityFiltersAnchorsBeforeSearchAdaptAuditTagAndFeedback' -v minimal -p:UseSharedCompilation=false` passed after schema ensure began auto-promoting legacy positive-owner rows that already carry `corpus_visibility = 'workspace'` to nullable workspace-corpus ownership. The migration preserves material ids and source segment ids, leaves `private`/`restricted` rows owned by the original novel, and remains a narrow legacy cleanup rather than full automatic per-novel-to-shared corpus migration.

Latest Phase 12 default corpus search policy slice: `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run lint`, and `npm --prefix frontend run build` passed after the default orchestration UI was aligned with service and Agent defaults. Default runs now submit `corpus_search_policy.license_statuses = ["user_provided"]`, keep include/exclude anchor filters empty unless advanced restrictions are enabled, and require explicit policy opt-in for unknown-license material. Vite still reports only the existing large chunk warning.

Latest verified scope: `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRun -v minimal` passed 21/21 after adding the selected-model AI-backed blueprint revision proposal provider and its orchestration path. The focused AI/provider and proposal-authorization filter `ReferenceOrchestrationRunRejectsClientModifiedBlueprintRevisionProposal|ReferenceOrchestrationRunRejectsMismatchedBlueprintRevisionProposal|ReferenceOrchestrationRunPersistsInjectedBlueprintRevisionProposalUntilUserApprovesIt|ReferenceOrchestrationRunPersistsAiBlueprintRevisionProposalUntilUserApprovesIt|ReferenceOrchestrationRunAppliesProposedBlueprintRevisionThenContinuesAfterApproval|AiReferenceBlueprintRevisionProposalProviderTests|PhotinoReferenceWorkflowSmokeTests` passed 9/9.

Latest verified scope: `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRun -v minimal` passed 20/20 after tightening blueprint revision approval so clients cannot replace the pending orchestration proposal. The injected proposal test now covers user approval, re-review, blueprint approval, material binding, candidate generation, audit, and the final-insertion stop. The focused proposal-authorization filter `ReferenceOrchestrationRunRejectsClientModifiedBlueprintRevisionProposal|ReferenceOrchestrationRunRejectsMismatchedBlueprintRevisionProposal|ReferenceOrchestrationRunPersistsInjectedBlueprintRevisionProposalUntilUserApprovesIt|ReferenceOrchestrationRunAppliesProposedBlueprintRevisionThenContinuesAfterApproval` passed 4/4. `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceOrchestration|ReferenceAnchorContractTests|MafToolRegistryTests' -v minimal` passed 42/42. `npm --prefix frontend run test:reference-anchor` passed after asserting manual controls are hidden by default, opening `高级模式`, completing the manual reference-anchor workflow, and rechecking stale-blueprint read-only controls. `npm --prefix frontend run lint` and `npm --prefix frontend run build` also passed; Vite reported only the existing large-chunk warning. Earlier backend/latest orchestration scope: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceOrchestrationStateMachineTests -v minimal` passed 12/12, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceOrchestration|ReferenceAnchorContractTests' -v minimal` passed 29/29, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRunPersistsInjectedBlueprintRevisionProposalUntilUserApprovesIt -v minimal` passed 1/1, and `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceOrchestrationRun -v minimal` passed 19/19 after extracting orchestration resume/safe-stage transitions into `ReferenceOrchestrationStateMachine` and routing blueprint revision suggestions through `IReferenceBlueprintRevisionProposalProvider`. Earlier verified scope: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge -v minimal` passed 29/29, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceAnchorContractTests -v minimal` passed 10/10, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter MafToolRegistryTests -v minimal` passed 15/15, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchorServiceTests -v minimal` passed 26/26, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchor -v minimal` passed 89/89, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceChapterBlueprintPayloadsUseStableSnakeCaseJsonNames -v minimal` passed 1/1, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'FullyQualifiedName~ReviseApprovedBlueprintInvalidatesApprovalAndMaterialLinks|FullyQualifiedName~BridgeReferenceAnchoredDraftHandlersGenerateReviewAndApproveBlueprint' -v minimal` passed 2/2, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceDraftTools -v minimal` passed 2/2, and `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter Reference -v minimal` passed 124/124 after adding full reference bridge service-routing coverage, reference bridge app-not-initialized/invalid-path semantics coverage, MAF tool exposure/schema constraints, stable reference bridge invalid-payload coverage, structured blueprint bridge payload verification, approval/material-link invalidation coverage, complete Phase 1 create/import/rebuild/failure-status coverage, Phase 2 material provenance/tag/slot coverage, blueprint tag-filter/prose-duty binding coverage, extractor Chinese punctuation/dialogue/paragraph plus emotion/POV/function-tag coverage, Phase 3 lexical ranking/pagination coverage, Phase 3 search score-component coverage, and Phase 3 narrative-duty/emotion-transition search filtering coverage.

Recommended next session:

1. Implement Phase 14 Task 1 and Task 2: style feature contracts/storage plus multi-scale source segmentation.
2. Add deterministic style baseline extraction before wiring any LLM-assisted analyzer.
3. Keep the Phase 11/12 orchestration and corpus gates intact while extending style behavior.

Recommended following session:

1. Add optional LLM-assisted style analysis only after deterministic style features and validation tests exist.
2. Build source-leak/style-quality audit before shipping high-fidelity candidate generation.
3. Keep the existing app-wide baseline and expanded Phase 13 matrix green during Phase 14 changes.

Do not broaden frontend workflow beyond the review-first path until source corpus, material binding, blueprint review, and draft audit are reliable. The system's quality depends on immutable provenance, hard blueprint gates, and candidate audit before any manual insertion.

After Phase 3, implement Phase 4 before any prose generation work. The blueprint review gate is the control layer that makes reference reuse robust; drafting before that gate would recreate the original failure mode under a different name.
