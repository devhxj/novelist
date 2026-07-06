# Reference Anchor Verification and Guardrails

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

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

Run after Phase 13 app-wide Playwright coverage exists:

```text
cd frontend
npm run test:app
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
- Advanced mode boundary: resolved for the current frontend surface. The reference panel defaults to the orchestration-first flow; manual material search and manual blueprint generate/revise/review/approve/bind/draft controls are hidden until `高级模式` is opened, and the Playwright reference workflow asserts both the hidden default and the advanced manual path.

## Phase 11 Automation Decisions Closed

- Default stop points: source/license/fact confirmation, blueprint approval, AI-proposed blueprint revision approval, material-binding gaps, stale blueprints, draft-audit failures, and final insertion remain explicit stops; low-risk blueprint generation, deterministic review, binding after approval, candidate generation, and draft audit can run automatically.
- Approval granularity: approving the compact blueprint summary approves the current blueprint analysis contract, guarded by context/source-plan/analysis-contract hashes and invalidated by reviewed-field edits.
- AI revision authority: AI can propose failed-review field fixes through the proposal provider, but application always uses the persisted pending proposal and requires user approval.
- Run persistence: orchestration runs and events live in the reference-anchor SQLite store and are exposed through bridge/frontend/agent read APIs; chat/session mirroring is not part of the current implementation.
- Candidate insertion UX: final insertion is a separate user-confirmed chapter edit/save path. `ResumeReferenceOrchestrationRun` rejects `approve_final_insertion`.
- Failure recovery: high-risk stops preserve run state and findings for inspection; resolving a high-risk stop marks the run failed so users regenerate, revise, or start a new run instead of free-drafting through the block.

## Phase 12 Shared Corpus Decisions To Close

- Corpus scope: workspace-only for now, or also support user-level/importable library packs later.
- Source ownership migration: convert `reference_anchors.novel_id` to nullable/visibility metadata, or create new corpus tables and compatibility views.
- Material identity: keep current anchor-derived material ids, or introduce stable corpus material ids independent of importing novel.
- Retrieval gap policy: decide thresholds for automatic query expansion, weak-match warnings, approved no-reuse continuation, and mandatory user stop.
- Feedback scope: which decisions should improve global corpus ranking, and which should remain per-novel because they depend on plot facts.
- UI information architecture: separate corpus library management from reference-anchored drafting controls.
- Search policy: resolved for the current implementation. Default orchestration uses `story_context`, `max_results_per_beat = 3`, no selected-anchor restriction, and `license_statuses = ["user_provided"]`; `unknown` or narrower include/exclude anchor filters must be supplied explicitly through the advanced corpus search policy.
- Archive/delete policy: resolved for the current implementation. Per-novel anchors keep hard-delete behavior. Visible workspace-corpus rows are archived by setting `corpus_visibility = restricted`, which removes them from normal list/search/build-status reads while preserving source segments, materials, hashes, and provenance for audit/debug history.
- Agent authority: resolved for the current implementation. MAF reference tools may list/search/adapt/audit already imported materials and start orchestration with injected novel context, but they do not expose source import, file picking, metadata promotion/update/delete, arbitrary path/source parameters, or source/license filter bypass. New corpus imports remain bridge/UI/user-driven.

## Phase 13 App-Wide Playwright Boundary

- Suite boundary: `test:reference-anchor` remains the deep reference workflow and `test:app` owns broad product regression.
- Fixture strategy: the current app-wide workflow uses one deterministic mocked Novelist bridge fixture.
- Selector policy: prefer accessible roles/names; use scoped locators only where visible labels are intentionally repeated or dynamic.
- Visual baseline scope: screenshots cover stable bootstrap, shell, editor, search, chat, settings, metadata, reference entry, and compact viewport states.
- Real Photino boundary: app-wide coverage stays on Vite plus mocked bridge; real Photino remains limited to asset loading and representative production bridge routing.
- CI policy: `npm run verify` now runs frontend build, lint, deep reference-anchor workflow, and app-wide workflow without requiring the Photino desktop shell.

## Recommended Next Coding Sessions

Start with the remaining Phase 12 shared-corpus gaps. Do not restart from Phase 0 unless contracts have regressed.

Latest Phase 12 bulk source import bridge slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'CreateReferenceAnchorsPayloadUsesStableSnakeCaseJsonNames|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods|BridgeFrontendContractTests|CompatibilityAppMethodListHasExpectedCoverage' -v minimal -p:UseSharedCompilation=false` passed 14/14, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'MafToolRegistryTests' -v minimal -p:UseSharedCompilation=false` passed 16/16, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'CreateAnchorsImportsWorkspaceCorpusSourcesWithoutLosingMaterialIdentity|CreateAnchorsValidatesBatchSize' -v minimal -p:UseSharedCompilation=false` passed 2/2, `npm --prefix frontend run lint` passed, and `npm --prefix frontend run build` passed with only the existing Vite large-chunk warning. `CreateReferenceAnchors` is now a contract/bridge/service/frontend-adapter entrypoint for 1-50 sequential source imports; full library-pack import and bulk import UI remain pending.

Latest verified scope: `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'ReferenceOrchestrationRunPersistsResumeAndCancelState|BridgeReferenceOrchestrationRunUsesWorkspaceCorpusWhenAnchorIdsAreOmitted|ReferenceOrchestrationRunUsesWorkspaceCorpusAnchorsWithoutExplicitAnchorIds|BuildDraftAuditFailsWhenSelectedMaterialLinkIsLowConfidenceWeakMatch|BindBlueprintMaterialsMarksExpandedQueryFallbackAsLowConfidenceWeakMatch' -v minimal` passed 5/5 after source confirmation decisions gained an explicit `confirm_license_status` action and workspace-corpus happy-path fixtures were adjusted to avoid weak-match fallback. `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|MafToolRegistryTests' -v minimal` passed 30/30.

Latest frontend Phase 12 smoke: `npm --prefix frontend run test:reference-anchor` passed after the default orchestration panel started showing story-context workspace-corpus retrieval policy and the browser workflow asserted default automatic material selection without selected `anchor_ids`, empty include/exclude anchor filters, and no `SaveContent` call. Corpus management UI remains a separate open Phase 12 item.

Latest Phase 12 story-context search scope: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'SearchReferenceMaterialsPayloadUsesStableNarrativeFilterJsonNames|MafToolRegistryTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations' -v minimal` passed 17/17 and `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'SearchMaterialsFiltersAndScoresByProseDutyStoryContext|SearchMaterialsFiltersByNarrativeDutyEmotionTransitionPovTechniqueAndType|SearchMaterialsMatchesSubtextDutyForObjectBasedExternalEvidence|SearchMaterialsMatchesSubtextDutyForRestrainedObjectActionEvidence|BindBlueprintMaterialsPreservesLexicalScoreForUnknownLicenseTruncatedPreviews|WorkspaceCorpusMaterialLinksAreBoundToCurrentBlueprintAnalysisContract' -v minimal` passed 6/6 after adding `prose_duties` to material search contracts, scoring, MAF tools, and the frontend advanced search flow. `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run build`, and `npm --prefix frontend run lint` passed; Vite still reports only the existing large-chunk warning.

Latest Phase 12 corpus-metadata UI thin slice: `npm --prefix frontend run test:reference-anchor` passed after the create-reference form exposed `visibility`, `source_trust`, and semicolon-separated `user_tags`, the anchor list displayed those metadata fields, and the mocked browser workflow asserted the `CreateReferenceAnchor` bridge payload. This is not the full corpus library management UI; the dedicated library information architecture, global corpus schema, and migration work remain open.

Latest Phase 12 single-anchor promotion UI slice: `npm --prefix frontend run test:reference-anchor` passed after the anchor list exposed a per-novel promote action that calls `PromoteReferenceAnchorToWorkspaceCorpus`, preserves existing metadata by omitting optional promotion fields, updates the row display to `workspace_corpus`, and verifies owner-scope filtering/counts for all/current-novel/workspace-corpus rows. This remains a targeted action inside the reference panel, not the full corpus library information architecture.

Latest Phase 12 corpus-list filtering UI slice: `npm --prefix frontend run test:reference-anchor` passed after the anchor list gained local query filtering across title, author, path, tags, and metadata plus license, visibility, and source-trust filters. The mocked browser workflow asserts author/tag query matches, empty states, source-trust filtering, license filtering, and filter clearing. This remains an incremental list-management slice inside the reference panel, not the full corpus library information architecture.

Latest Phase 12 corpus-metadata edit slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'ReferenceAnchorContractTests|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal` passed 18/18, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'UpdateAnchorMetadataCanPromoteToWorkspaceCorpusWithoutChangingMaterialIdentity|UpdateAnchorMetadataCannotBypassOtherNovelPrivateOrWorkspaceRestrictedVisibility' -v minimal -p:UseSharedCompilation=false` passed 2/2, and `npm --prefix frontend run test:reference-anchor` passed after adding `UpdateReferenceAnchorMetadata`. The method edits only title, author, license status, visibility, source trust, and user tags; SQLite coverage proves it can move an owned anchor into workspace-corpus ownership without changing source hash, source segment ids, or material ids, and cannot bypass other-novel private or restricted workspace-corpus visibility. The frontend exposes this as row-level metadata editing in the current reference panel, still not a full corpus library information architecture.

Latest Phase 12 row-level material preview slice: `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run build`, and `npm --prefix frontend run lint` passed after the anchor list gained a row-level material preview backed by `SearchReferenceMaterials` with explicit `anchor_ids`. The mocked browser workflow asserts material id, text, and score-component display plus the anchor-scoped search payload. This is a focused material-browsing slice inside the current reference panel, not the full corpus library material browser, sorting, bulk editing, or import management UI.

Latest Phase 12 paginated material browsing slice: `npm --prefix frontend run test:reference-anchor` passed after the row-level material preview started preserving `SearchReferenceMaterials` pagination metadata, showing page/total counts, and loading next/previous pages with the same anchor-scoped filters. The mocked browser workflow asserts page 1 and page 2 material rows and bridge payloads with `anchor_ids: [101]`, `page`, and `size: 5`. This still stops short of full material-level library management, sorting, bulk editing, or archive/delete policy.

Latest Phase 12 single-material tag correction slice: `npm --prefix frontend run test:reference-anchor` passed after the row-level material browser gained a compact tag-correction form for function, emotion, scene, POV, and technique tags backed by `UpdateReferenceMaterialTags`. The mocked browser workflow asserts corrected row display and the bridge payload with `origin: "ui"` and `note: "corpus material browser correction"`. This is a single-row correction path, not bulk material editing, sorting, or archive/delete policy.

Latest Phase 12 material-browser filter/sort slice: `npm --prefix frontend run test:reference-anchor` passed after the row-level material browser gained local current-page filtering by material id/text/type/tags/source segment and current-page sorting by highest score or material id. The mocked browser workflow asserts matching, empty-filter state, score ordering, id ordering, and unchanged anchor-scoped page payloads. This remains local page-level browsing, not full material-library search, bulk editing, or import management.

Latest Phase 12 selected-row bulk corpus actions slice: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'DeleteReferenceAnchorsPayloadUsesStableSnakeCaseJsonNames|ReferenceAnchorHandlersRouteEveryMethodToServiceOperations|CompatibilityRegistryIncludesReferenceAnchorMethods' -v minimal -p:UseSharedCompilation=false`, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'DeleteAnchorsBulkDeletesPrivateAnchorsAndArchivesWorkspaceCorpusRows|DeleteAnchorsBulkRollsBackWhenAnyAnchorCannotBeProcessed' -v minimal -p:UseSharedCompilation=false`, and `npm --prefix frontend run test:reference-anchor` passed after the corpus library list moved selected-row bulk archive to transactional `DeleteReferenceAnchors`. Coverage asserts stable bridge JSON, bridge routing, compatibility registration, mixed private-delete/workspace-archive behavior, rollback when any selected row cannot be processed, selection counts, clearing processed selections, bulk promotion payloads, bulk archive payloads, and archived rows disappearing from the visible library. This remains a selected-row management slice, not bulk import, automatic migration, or bulk material editing.

Latest Phase 12 corpus-library IA naming slice: `npm --prefix frontend run test:reference-anchor` passed after the reference-anchor page introduced explicit `语料库管理` and `参考写作检索` sections, with import/list headings under the library side and default orchestration/manual debugging under the writing-retrieval side. The mocked browser workflow asserts those headings so future UI work does not collapse corpus management back into a per-novel setup checklist. Full corpus library IA, bulk import/migration, and material-browser management remain pending.

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

1. Continue Phase 12 shared-corpus modeling with storage/migration design before replacing the current `novel_id = 0` compatibility layer.
2. Keep the Phase 11 orchestration gates intact while broadening Phase 12 corpus retrieval, migration, and UI coverage.

Recommended following session:

1. Keep broadening deterministic Chinese narration, emotion, POV, and unsupported-fact fixtures; any optional model-assisted tagging or adaptation should be designed later as an explicit opt-in path that feeds the deterministic gates.
2. Design Phase 12 shared-corpus contracts before migrating storage, so global materials can be reused without weakening per-novel safety gates.
3. Keep Phase 13 app-wide Playwright coverage green while implementing remaining Phase 12 work.

Do not broaden frontend workflow beyond the review-first path until source corpus, material binding, blueprint review, and draft audit are reliable. The system's quality depends on immutable provenance, hard blueprint gates, and candidate audit before any manual insertion.

After Phase 3, implement Phase 4 before any prose generation work. The blueprint review gate is the control layer that makes reference reuse robust; drafting before that gate would recreate the original failure mode under a different name.
