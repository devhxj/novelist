# Reference Anchor Tasks: Phase 16

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

## Phase 16: Corpus Library and Chapter Reference-Use Separation

**Status:** Proposed. This phase corrects the product information architecture of the current reference-anchor surface. It separates the shared corpus-library processing workflow from current-chapter reference use, while preserving the existing strict reference-anchor implementation: auditable source provenance, deterministic material extraction, style profiles, reviewed blueprints, material binding, draft audit, source-leak checks, and explicit user approval before any chapter content is inserted or saved.

## Problem Statement

The current `ReferenceAnchorView` mixes two different product domains in one page:

- **Shared corpus-library processing:** importing reference novels/materials, building anchor/material indexes, browsing processed materials, correcting tags, archiving/restoring materials, and building style profiles.
- **Current-chapter reference use:** using the shared corpus to support a specific chapter through chapter goals, fact boundaries, orchestration runs, blueprint review, material binding, candidate generation, and draft audit.

This makes the product feel unusable because a user who wants to process a shared material library is confronted with current-chapter controls, and a user writing a chapter must leave the chapter editor and operate a separate reference-debug surface. The issue is not missing backend capability; it is an incorrect UI boundary and workflow placement.

Phase 16 must make the common path simple without weakening the implementation. The system must become easier to use by hiding irrelevant controls in the wrong context, not by removing audit gates, provenance, or approval boundaries.

## Product Model

### Shared Corpus Library

The corpus library is a workspace-level asset. It is not owned by a single currently open chapter. It can include per-novel private sources and workspace-corpus sources, but its processing model is independent from chapter drafting.

The corpus library answers:

- What reference sources have been imported?
- Did parsing, segmentation, material extraction, indexing, and style-profile building succeed?
- What processed materials are available?
- Which material tags, metadata, visibility, trust, and archive states need review?
- Which style profiles are available for later chapter use?

The corpus library must not ask for chapter number, chapter goal, known facts, forbidden facts, blueprint approval, material binding, candidate generation, or insertion approval.

### Current-Chapter Reference Use

Current-chapter reference use is embedded in the chapter writing surface. It consumes the shared corpus library but does not manage it.

The chapter reference panel answers:

- Which chapter is currently open?
- What is the chapter goal or requested writing action?
- Which corpus materials are relevant to this chapter?
- What candidate text can be generated under the current fact, POV, style, and source-leak constraints?
- What audit findings must the author review before using the candidate?
- What explicit insertion action should apply candidate text to the editor?

The chapter-use surface may start orchestration runs and resume user decisions, but it must remain chapter-scoped and must not mutate corpus-library metadata such as source trust, tags, visibility, archive state, or style-profile definitions.

### Default Automation Model

Phase 16 must make the AI-assisted path the default path, with human review reserved for exceptions and final user-owned decisions.

The corpus library should automatically advance an explicitly imported source through the safe processing pipeline: parse, segment, extract materials, infer tags/confidence, index/search-rank, update build status, and summarize diagnostics. A user should not need to hand-split source text, manually create material rows, manually select anchors, or manually open advanced debugging before the corpus becomes searchable.

The default corpus outcome must be one of three visible states: searchable material output, focused human review required, or processing failure with actionable diagnostics. High-confidence extracted materials should flow into searchable corpus results without blocking on manual tag review.

Automatic corpus processing must be idempotent and resumable. Re-importing the same source, retrying a failed stage, rebuilding an anchor, or restarting the app during processing must not duplicate material rows, lose source provenance, clear user-verified corrections, or leave the source in an unrecoverable intermediate state.

Manual intervention should be focused into review queues:

- low-confidence, unknown, or conflicting material tags;
- failed or partially recovered source processing;
- visibility/license/source-trust promotion decisions;
- explicit archive/restore and destructive-adjacent administration;
- chapter-use approvals, high-risk recoveries, and final candidate insertion.

The chapter reference panel should also prefer automation: when opened from a valid chapter, it should derive the chapter context, suggest relevant accessible corpus materials, use the shared corpus by default, and make the common orchestration path one primary action. Manual source restriction, manual blueprint controls, and beat-to-material binding remain available only as advanced controls.

If no accessible corpus material exists, the chapter panel must show a corpus-import call to action and a recoverable empty state instead of a dead-end orchestration surface. After import/processing succeeds, returning to the chapter panel should use the newly searchable corpus without requiring advanced setup.

## Non-Negotiable Guardrails

- Do not simplify reference anchoring into plain RAG or a style prompt.
- Do not automatically convert an imported user novel into shared corpus material. Corpus import remains an explicit corpus-library action.
- Importing, rebuilding, tagging, archiving, or promoting corpus sources must not call `StartReferenceOrchestrationRun`, `GenerateReferenceChapterBlueprint`, `BindReferenceBlueprintMaterials`, `GenerateReferenceAnchoredDraft`, or `SaveContent`.
- Current-chapter reference use must begin from an explicit chapter action: open chapter reference panel, search relevant materials, start orchestration, generate/review a blueprint, or bind selected materials.
- Do not automatically call `SaveContent` from corpus processing, material search, orchestration, candidate generation, or audit.
- Do not insert candidate prose into an editor without an explicit user action in the chapter surface.
- Do not let corpus-library processing depend on the current chapter tab, active blueprint, or current editor content.
- Do not let current-chapter reference use edit corpus metadata, archive/restore materials, or rebuild source indexes.
- Chapter-reference UI must not call `UpdateReferenceMaterialTags`, `UpdateReferenceMaterialsTags`, `UpdateReferenceAnchorMetadata`, `DeleteReferenceMaterials`, `RestoreReferenceMaterials`, `DeleteReferenceAnchor`, or `RebuildReferenceAnchor`.
- Corpus import, retry, rebuild, and processing resume must preserve source hashes, material ids where possible, source segment ids, archive state, user-verified tag corrections, and audit provenance; they must not create duplicate searchable materials for the same source/build state.
- Keep source text, candidate text, prompts, local source paths, and sensitive diagnostics out of agent-exposed tools unless an existing read boundary explicitly allows a bounded preview.
- Preserve owner scope, visibility, license status, source trust, material ids, source hashes, and audit provenance when moving UI components.
- Preserve advanced blueprint/material-binding/debug workflows, but move them behind an explicit advanced mode in the chapter-use context.
- Preserve the existing final-insertion stop: orchestration resume must not auto-approve final insertion.
- Agent tools must not gain corpus import, arbitrary file-read, corpus delete/rebuild, or final-insertion approval capabilities as part of this UI correction.
- Reference materials must remain in dedicated corpus/reference search. Do not mix them into ordinary workspace full-text search results.
- Existing `goink-master` remains a read-only behavior reference only; do not reintroduce Go/Wails/Python paths.

## Target Information Architecture

### Activity Bar

Rename or repurpose the current `参考锚定` activity into a corpus-library activity. Recommended label: `素材库`.

The activity opens a full workspace panel dedicated to corpus processing and inspection. It should not render chapter-use orchestration by default.

### Corpus Library Page

The corpus page should be organized as task-oriented tabs:

1. **素材来源**
   - Import one or more local reference files.
   - Import a library-pack manifest.
   - Automatically start safe corpus processing for explicitly imported sources and refresh source build state until the source is searchable, requires review, or fails with diagnostics.
   - Make import/retry/rebuild idempotent and resumable across app restart, preserving provenance and user corrections while preventing duplicate searchable materials.
   - Display source rows with title, author, owner scope, license status, visibility, source trust, tags, build state, material counts, and last processed time.
   - Support source metadata edit, explicit promotion to workspace corpus, rebuild, delete/archive, and retry failed processing.

2. **处理后语料**
   - Browse processed materials across accessible corpus sources.
   - Filter by source, owner scope, archive state, material type, function tag, emotion tag, scene tag, POV tag, technique tag, narrative duty, prose duty, source trust, license, and keyword.
   - Show material preview, material id, source segment id, source hash, score components when available, tag confidence/user-verified state, and archive state.
   - Open a read-only material detail drawer for every material row. The detail must show provenance, tags/confidence, verification state, score components, extractor/build metadata, bounded source/material preview, detected slots/placeholders, source segment metadata, archive state, and processing notes where available.
   - Keep list and detail previews bounded. The UI must never expose full source files, local source paths, prompts, candidate text, or full chapter text through material detail.
   - Support current-page and cross-page selected material tag correction.
   - Support archive/restore of materials without deleting provenance.

3. **标签校正**
   - A focused review queue for unverified, low-confidence, unknown, or conflicting tags.
   - High-confidence extracted tags should not block normal corpus search or chapter use; they remain inspectable in material detail and can still be corrected later.
   - Batch correction with transactional rollback on inaccessible material ids.
   - Review notes and correction origin should be captured where existing payloads support it.

4. **风格画像**
   - Build, inspect, compare, archive, and restore reference style profiles from selected sources or style samples.
   - Show feature coverage, evidence counts, analyzer source, aggregate confidence, and active/archive status.
   - Make clear that style profiles become optional inputs for chapter use; they do not bypass blueprint approval or source-leak audit.

5. **处理记录**
   - Show source build/rebuild status, stage history, material/segment/slot/vector counts, failures, warnings, recovered processing state, and copyable diagnostics.
   - Provide a per-source processing detail view that lets users inspect which stages succeeded, which stages produced partial output, and which material/detail records are affected by a warning or failure.
   - Link or filter from a warning/failure to affected source, material, segment, or slot ids when those ids are available.
   - Copyable diagnostics must be redacted before display/copy; they must not include absolute local paths, usernames, environment variables, tokens, API keys, prompts, full source text, candidate text, or full chapter text.
   - Include retry and rebuild actions where safe.

6. **高级**
   - Optional location for low-level corpus debug operations that are not needed for normal library management.
   - This tab must not contain current-chapter candidate insertion.

### Chapter Editor Reference Panel

The chapter editor should gain a chapter-scoped reference panel. It should be visually integrated with the existing `ContentPanel`/chapter editor area rather than mounted as an activity-level page.

Recommended placement:

- Add a collapsible right-side drawer inside `ContentPanel`, opened from the existing file-header toolbar.
- The drawer appears only when the active tab is a chapter content file.
- The drawer can be opened from a compact toolbar button in the chapter header, for example `参考素材`.
- The drawer is independent from the global `ChatPanel`; chat remains conversational assistance, while this drawer is the auditable reference-use workflow.
- Do not mount the drawer in `ChatPanel`. `ChatPanel` does not own active chapter state, editor selection, view mode, dirty state, or editor insertion APIs.

The drawer should provide:

1. **Chapter Context**
   - Current chapter number derived from the active file path when possible.
   - Current chapter title from the active tab.
   - Optional chapter goal input; the panel should work with an empty goal when the user has not provided one.
   - Optional known facts and forbidden facts, with room for automatic suggestions or user correction where supported.
   - Optional style profile selection and imitation intensity.
   - Optional advanced source restriction to selected corpus sources, hidden by default.

2. **Relevant Materials**
   - Automatically suggest relevant materials from the current chapter context when the panel opens on a valid chapter.
   - Provide one-click/manual search as a supplement, not a prerequisite for the default path.
   - Material results from accessible corpus library.
   - Read-only material preview with provenance, tags, and fit explanation.
   - No corpus tag editing in this panel; provide a navigation link back to the corpus library row if needed.

3. **Candidate Workflow**
   - Start or resume an orchestration run for the current chapter through one primary default action.
   - Show current stage, required user decision, approval summary, candidate ids, and readable audit findings.
   - Keep manual blueprint search/review/approve/bind/generate controls behind advanced mode.
   - Default mode must not require users to manually select anchors, manually search materials, manually generate a blueprint, manually bind beat/material links, or open advanced mode before the first audited candidate attempt.

4. **Explicit Insertion**
   - Candidate text must be previewed before insertion.
   - User must choose an insertion action: insert at cursor, append to chapter, replace current selection, or copy candidate.
   - Insertion updates the editor buffer only. Normal editor autosave/explicit save remains responsible for `SaveContent`.
   - Every insertion action must be undoable through the editor undo stack.

## Existing Implementation Mapping

The current implementation already has most backend capabilities. Phase 16 should primarily move and rename frontend boundaries, then add small bridge helpers only where necessary.

Current frontend reference-anchor surface ownership:

| Current capability | Existing location | Primary calls/components | Phase 16 destination |
|---|---|---|---|
| Single source import | `frontend/src/components/reference-anchor/ReferenceAnchorView.tsx` `reference-import-panel` | `pickReferenceSourceFile()`, `createAnchor()`, `PickReferenceSourceFile`, `CreateReferenceAnchor` | Corpus library, `素材来源` |
| Batch source import | `ReferenceAnchorView.tsx` | `createAnchors()`, `CreateReferenceAnchors` | Corpus library, `素材来源` |
| Library-pack import | `ReferenceAnchorView.tsx` | `parseLibraryPackManifest()`, `importLibraryPack()`; supports JSON arrays or `{ sources: [...] }`, currently capped at 50 sources | Corpus library, `素材来源` |
| Source/anchor list | `ReferenceAnchorView.tsx` | `loadAnchors()`, `saveAnchorMetadata()`, `rebuildAnchor()`, `deleteOrArchiveAnchor()` | Corpus library, `素材来源` |
| Promote to workspace corpus | `ReferenceAnchorView.tsx` | `promoteAnchorToWorkspaceCorpus()`, `promoteSelectedAnchorsToWorkspaceCorpus()` | Corpus library, `素材来源`; keep visibility explicit |
| Single-anchor material preview | `ReferenceAnchorView.tsx` | `loadAnchorMaterialPreview()`, `SearchReferenceMaterials` with `anchor_ids: [anchor_id]` | Corpus library, source detail or processed-material tab |
| Corpus material browse/search | `ReferenceAnchorView.tsx` `reference-material-library` | `searchMaterialLibrary()`, `SearchReferenceMaterials` with `anchor_ids: []` | Corpus library, `处理后语料` |
| Material tag correction | `ReferenceAnchorView.tsx` | `saveMaterialTags()`, `saveBulkMaterialTags()`, `saveBulkLibraryMaterialTags()` | Corpus library, `标签校正` |
| Material archive/restore | `ReferenceAnchorView.tsx` | `archiveSelectedLibraryMaterials()`, `restoreSelectedLibraryMaterials()` | Corpus library, `归档` or `处理后语料` archive filter |
| Style profile library | `frontend/src/components/reference-anchor/StyleProfileLibraryPanel.tsx` | `buildProfile()`, `refreshBuildStatus()`, `cancelBuild()`, `inspectProfile()`, `archiveProfile()`, `restoreProfile()`, `compareProfiles()` | Corpus library, `风格画像` |
| Default chapter orchestration | `frontend/src/components/reference-anchor/OrchestrationPanel.tsx` | chapter number, chapter goal, fact boundaries, style strategy, run/events | Chapter editor reference drawer |
| Manual chapter material search | `ReferenceAnchorView.tsx` | `searchMaterials()` with selected anchors, narrative duties, emotion transitions | Chapter editor reference drawer advanced mode |
| Blueprint review/binding/drafting | `ReferenceAnchorView.tsx`, `frontend/src/components/reference-anchor/BlueprintDetail.tsx`, `blueprintRevision.ts` | generate/review/revise/approve blueprint, bind materials, generate draft | Chapter editor reference drawer advanced mode |
| Adapt/audit/user-feedback debug | `ReferenceAnchorBridgeHandlers.cs` exposes related methods | `AdaptReferenceMaterial`, `AuditReferenceReuse`, `RecordReferenceUserFeedback`, `GetReferenceUserFeedback` | Advanced/debug only; not corpus-library default path |

Current chapter editor structure:

- `frontend/src/views/WorkspaceView.tsx` renders `ActivityBar`, `SidePanel`, main content, and `ChatPanel`.
- `WorkspaceView.handleSelectChapter` opens chapter files through `contentRef.current?.openFile(ch.file_path, ...)`.
- `ContentPanel` owns the active `EditorTab`, active file path/title, editor instance, dirty state, save behavior, and `onActiveFileChange`.
- `frontend/src/components/content/types.ts` already includes helpers for content paths and chapter-number derivation; reuse those before adding new state.
- `ContentEditor` owns the Monaco editor mount and already exposes development-only test controls under `window.__novelistEditor`.
- `ChatPanel` is always rendered for most workspace panels and should remain the conversational/approval surface, not the structured reference-use workflow.

Existing frontend files likely affected:

- `frontend/src/components/reference-anchor/ReferenceAnchorView.tsx`
- `frontend/src/components/reference-anchor/OrchestrationPanel.tsx`
- `frontend/src/components/reference-anchor/BlueprintDetail.tsx`
- `frontend/src/components/reference-anchor/StyleProfileLibraryPanel.tsx`
- `frontend/src/components/reference-anchor/referenceAnchorStyles.ts`
- `frontend/src/views/WorkspaceView.tsx`
- `frontend/src/components/content/ContentPanel.tsx`
- `frontend/src/components/content/ContentEditor.tsx`
- `frontend/src/components/content/TabBar.tsx`
- `frontend/src/components/shell/ActivityBar.tsx`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- `frontend/scripts/app-mock-workflow/**/*`

Existing bridge and backend capabilities:

- Corpus-library bridge handlers live in `src/Novelist.Core/Bridge/ReferenceAnchorBridgeHandlers.cs` and already route source import, source listing, source metadata update, workspace-corpus promotion, material search, material tag update, material archive, and material restore.
- Style-profile bridge handlers live in `src/Novelist.Core/Bridge/ReferenceStyleProfileBridgeHandlers.cs` and already route profile build, list, inspect, status, cancel, archive, restore, and compare operations.
- Current-chapter reference-use bridge handlers live in `src/Novelist.Core/Bridge/ReferenceAnchoredDraftBridgeHandlers.cs` and already route blueprint generation/review/revision/approval, material binding, candidate generation, orchestration run start/list/detail/events/resume/cancel, and draft audit retrieval.
- `src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs` already contains corpus payloads for `CreateReferenceAnchor`, `CreateReferenceAnchors`, `PromoteReferenceAnchorToWorkspaceCorpus`, `PromoteReferenceAnchorsToWorkspaceCorpus`, `DeleteReferenceAnchors`, `DeleteReferenceMaterials`, `RestoreReferenceMaterials`, `UpdateReferenceAnchorMetadata`, `ReferenceAnchorPayload`, `ReferenceAnchorBuildStatusPayload`, `ReferenceMaterialPayload`, and `SearchReferenceMaterialsPayload`.
- `SearchReferenceMaterialsPayload` already supports the corpus-library browsing primitives needed by Phase 16: pagination, anchor filters, tag filters, archive filters, style-profile-related scoring inputs, and empty `anchor_ids` for all accessible current-novel/workspace corpus materials.
- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs` already contains the chapter-use payloads for blueprint, review, material binding, draft candidates, orchestration runs, decisions, and audits.
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs` already merges current-novel private anchors with visible workspace-corpus anchors for anchor listing/search, and existing tests cover workspace-corpus visibility without leaking other novels' private anchors.
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs` already supports chapter-number filtering for blueprints and orchestration runs.
- `GenerateDraftFromBlueprintAsync` returns draft candidates and must not mutate chapter content.
- The final insertion resume path is intentionally rejected by the orchestration service and must remain rejected after the UI move.

Required contract decisions and gaps to close during implementation:

- Processed-material detail: storage has source segments/materials/slots, but the bridge does not currently expose `GetReferenceSourceSegments`, `GetReferenceMaterialSlots`, or `GetReferenceMaterialDetail`. Phase 16 must provide a read-only material detail path for every material row. Prefer a single `GetReferenceMaterialDetail` summary contract that includes bounded material/source previews, source segment metadata, detected slots/placeholders, tag confidence/user verification, score components, extractor/build metadata, archive state, and processing notes. It must not return full source text, local source paths, prompts, candidate text, or full chapter content.
- Processing idempotency and resume: if existing anchor/build status data cannot distinguish new import, duplicate import, retry, rebuild, partial failure, recovered output, and app-restart recovery states, extend the read-only status/diagnostic payloads before relying on frontend-only interpretation. Backend service rules, not frontend filtering, must prevent duplicate searchable materials and preserve provenance/user corrections across retry/rebuild.
- Processing diagnostics: if existing build-status payloads cannot explain per-stage parse/segment/extract/index/profile failures clearly, add a read-only processing detail helper for source-level diagnostics. Users must be able to inspect processing failures without using developer tools. Diagnostic payloads and copied diagnostics must be redacted and should identify affected source/material/segment/slot ids instead of exposing local paths or raw source text.
- Chapter material-link recovery: `BindReferenceBlueprintMaterials` returns links, but there is no standalone `GetReferenceBlueprintMaterialLinks`. Add it only if the chapter drawer must restore selected links after reload without rebinding.
- Manual material selection: `BindReferenceBlueprintMaterialsPayload` currently has `select_top_candidate`. If the drawer needs explicit beat-to-material selection, add `SelectReferenceBlueprintMaterialLinks` or extend the binding payload; do not encode chapter selection by changing corpus tags.
- Chapter-use overview: if the drawer needs one efficient bootstrap call, add read-only `GetReferenceChapterUsage(novel_id, chapter_number)` that aggregates blueprints, runs, selected links, candidate/audit summaries, and search-policy summaries without returning `source_text`, `candidate_text`, prompts, local paths, or full chapter text.
- Do not add `insert_reference_anchored_draft` or any bridge method that writes candidate prose into chapter files. Candidate use is a frontend editor-buffer action plus normal save behavior.

Recommended new frontend components:

- `frontend/src/components/corpus/CorpusLibraryView.tsx`
- `frontend/src/components/corpus/CorpusSourceTab.tsx`
- `frontend/src/components/corpus/CorpusMaterialTab.tsx`
- `frontend/src/components/corpus/CorpusMaterialDetailDrawer.tsx`
- `frontend/src/components/corpus/CorpusTagReviewTab.tsx`
- `frontend/src/components/corpus/CorpusProcessingRunsTab.tsx`
- `frontend/src/components/corpus/CorpusProcessingRunDetail.tsx`
- `frontend/src/components/reference-use/ChapterReferencePanel.tsx`
- `frontend/src/components/reference-use/ChapterReferenceToolbar.tsx`
- `frontend/src/components/reference-use/ChapterMaterialResults.tsx`
- `frontend/src/components/reference-use/ChapterCandidateReview.tsx`

Recommended chapter-use component boundaries:

- `ChapterReferencePanel`: drawer shell mounted by `ContentPanel`; props include `novelId`, active chapter context, open state, and editor insertion callbacks.
- `ChapterReferenceToolbar`: compact `参考素材` trigger in the file header.
- `useReferenceChapterUsage`: hook extracted from the current `ReferenceAnchorView` orchestration/blueprint/candidate logic; it must not include corpus CRUD, material tag correction, archive/restore, or source import.
- `ChapterReferenceSetup`: chapter goal, known facts, forbidden facts, style profile, and start action.
- `ReferenceRunStatus`: orchestration run list, active run, search policy, event history, and cancel action.
- `ReferenceDecisionCard`: required user decision, recovery actions, approval summary, and explicit resume action. It must preserve the current rule that `approve_final_insertion` is not resumable as an automatic continuation.
- `ChapterCandidateReview`: candidate ids/text, readable audit findings, and explicit copy/insert actions.
- `BlueprintAdvancedPanel`: manual blueprint/review/approve/bind/generate controls moved behind advanced mode.

Backend/bridge changes should be conservative. Prefer reusing existing methods before adding new contracts:

- `PickReferenceSourceFile`
- `CreateReferenceAnchor`
- `CreateReferenceAnchors`
- `GetReferenceAnchors`
- `UpdateReferenceAnchorMetadata`
- `PromoteReferenceAnchorToWorkspaceCorpus`
- `RebuildReferenceAnchor`
- `GetReferenceAnchorBuildStatus`
- `SearchReferenceMaterials`
- `UpdateReferenceMaterialTags`
- `UpdateReferenceMaterialsTags`
- `DeleteReferenceMaterials`
- `RestoreReferenceMaterials`
- `BuildReferenceStyleProfile`
- `GetReferenceStyleProfiles`
- `GetReferenceStyleProfile`
- `ArchiveReferenceStyleProfile`
- `RestoreReferenceStyleProfile`
- `CompareReferenceStyleProfiles`
- `StartReferenceOrchestrationRun`
- `GetReferenceOrchestrationRuns`
- `GetReferenceOrchestrationRun`
- `GetReferenceOrchestrationRunEvents`
- `ResumeReferenceOrchestrationRun`
- `CancelReferenceOrchestrationRun`
- `GenerateReferenceChapterBlueprint`
- `ReviewReferenceChapterBlueprint`
- `ReviseReferenceChapterBlueprint`
- `ApproveReferenceChapterBlueprint`
- `BindReferenceBlueprintMaterials`
- `GenerateReferenceAnchoredDraft`
- `GetReferenceAnchoredDraftAudits`

Potential new bridge helpers should be added only where existing payloads cannot support the required UX:

- `GetReferenceCorpusOverview`: aggregate source/material/build counts for the corpus page header.
- `GetReferenceSourceMaterials`: thin wrapper over `SearchReferenceMaterials` for a single source if the UI would otherwise duplicate complex filter payload construction.
- `GetReferenceMaterialDetail`: required read-only material detail unless `ReferenceMaterialPayload` is safely extended to carry equivalent bounded detail fields without bloating browse/search results.
- `GetReferenceSourceProcessingDetail`: read-only source processing stage history and diagnostics if `GetReferenceAnchorBuildStatus` cannot explain parse/segment/extract/index failures and affected output counts.
- `GetReferenceSourceSegments`: read-only source segmentation view only if processing diagnostics require below-material inspection; keep previews bounded and path-free.
- `GetReferenceBlueprintMaterialLinks`: read-only recovery of existing blueprint-material links after reload.
- `SelectReferenceBlueprintMaterialLinks`: explicit manual beat-to-material selection if advanced chapter use needs user-directed binding.
- `GetReferenceChapterUsage`: optional one-call chapter drawer bootstrap for blueprints, orchestration runs, selected links, and audit summaries.
- `GetChapterReferenceContext`: optional helper to derive chapter number/title/path/outline hash from the active chapter if frontend derivation from `chapters/{number}.md` is insufficient.
- `InsertCandidateIntoEditor` should **not** be a bridge method. Insertion is a frontend editor-buffer operation followed by normal editor save behavior.
- Do not add `insert_reference_anchored_draft` or any equivalent backend/agent method that writes generated prose into a chapter file.

## Parallel Implementation Strategy

Phase 16 can be implemented in parallel after Task 1 locks the shared terminology and guardrails.

Safe parallel tracks:

- **Corpus track:** extract `CorpusLibraryView` and tabs from `ReferenceAnchorView`. Owns `frontend/src/components/corpus/**`, corpus-only extraction from `ReferenceAnchorView.tsx`, style profile placement, and `reference-library-workflow` coverage.
- **Chapter-use track:** add `ChapterReferencePanel` inside `ContentPanel` and move orchestration/blueprint/candidate logic into `frontend/src/components/reference-use/**`. Owns editor insertion callbacks, chapter derivation, and `reference-chapter-usage-workflow` coverage.
- **Contract/test track:** add only necessary read-only helpers, bridge routing tests, serialization negative tests, frontend API/type declarations, and integration tests. This track must coordinate method names before frontend tracks depend on them.
- **Documentation/help track:** update user help, release notes, and developer docs after the first two tracks settle naming.

Sequential constraints:

- Do not merge chapter insertion until the no-implicit-`SaveContent` browser assertions pass.
- Do not remove the old mixed `ReferenceAnchorView` exports until both corpus and chapter-use replacement workflows pass.
- Do not add new bridge methods before their payload fields, routing tests, frontend API declarations, and sensitive-field negative assertions are agreed.
- Do not expose advanced blueprint/debug controls in the corpus-library page even temporarily during migration.

## Implementation Tasks

### Task 1: Document and Lock the UI Boundary

**Description:** Establish the new product boundary in documentation and tests before moving UI code.

**Acceptance criteria:**

- [ ] Documentation states that corpus-library processing and chapter reference use are separate domains.
- [ ] Activity labels and help copy use `素材库` for corpus management and `参考素材` for chapter use.
- [ ] Guardrails explicitly forbid corpus processing from starting chapter candidate generation or calling `SaveContent`.
- [ ] `frontend/package.json` owns the Phase 16 browser gate names `test:corpus-library` and `test:chapter-reference`; the verification matrix must not reference scripts that are only aspirational.

**Verification:**

- [ ] Documentation links resolve.
- [ ] `rg` confirms the old mixed mental model is not presented as the primary flow in README/help docs.
- [ ] `npm --prefix frontend run test:corpus-library` and `npm --prefix frontend run test:chapter-reference` resolve to real package scripts before they become required matrix commands.

**Dependencies:** None.

**Estimated scope:** S.

### Task 2: Extract Corpus Library Components from `ReferenceAnchorView`

**Description:** Move source import, corpus-source list, material-library search, material tag correction, archive/restore, and style-profile management into dedicated corpus components.

**Acceptance criteria:**

- [ ] The corpus page renders without chapter number, chapter goal, blueprint, candidate, or orchestration controls.
- [ ] Existing source import, batch import, library-pack import, metadata edit, rebuild, promotion, material search, tag correction, archive/restore, and style-profile operations still work.
- [ ] Explicit source import automatically advances through parse, segment, material extraction, tagging, indexing, and build-status updates; successful imports become searchable without hand-created material rows or advanced debug steps.
- [ ] Duplicate import, retry, rebuild, and app restart during processing are idempotent and resumable: they do not duplicate searchable materials, lose source provenance, clear user-verified tag corrections, or strand a source in an unrecoverable intermediate state.
- [ ] Low-confidence/conflicting/unknown tags and failed/partial processing outputs are routed into visible review queues instead of requiring users to discover them manually.
- [ ] High-confidence extracted materials become searchable without waiting for manual tag review.
- [ ] Empty, loading, processing, failed, archived, and no-results states are visible, and any copyable diagnostics are redacted.
- [ ] `处理记录` shows stage history, affected counts, failures, warnings, recovered processing state, retry/rebuild availability, and copyable diagnostics for each source.
- [ ] `处理记录` can link or filter from warnings/failures to affected source/material/segment/slot ids when available, and retry/rebuild updates current status while retaining historical failure context.
- [ ] Copyable diagnostics are redacted before display/copy and exclude absolute local paths, usernames, environment variables, tokens, API keys, prompts, full source text, candidate text, and full chapter text.
- [ ] Corpus import/search/tag/archive/rebuild actions do not call chapter-use methods or `SaveContent`.
- [ ] Corpus state keeps separate selected ids from any chapter reference selection state.

**Verification:**

- [ ] `npm --prefix frontend run lint`
- [ ] `npm --prefix frontend run build`
- [ ] `npm --prefix frontend run test:corpus-library`
- [ ] Existing reference-anchor mock coverage remains green or is replaced with renamed corpus workflow coverage.

**Dependencies:** Task 1.

**Estimated scope:** M.

### Task 3: Create Corpus Material Review Tabs

**Description:** Add explicit tabs for processed material browsing, material detail inspection, and tag-review queues so users can inspect each processed corpus output.

**Acceptance criteria:**

- [ ] Users can browse processed materials across all accessible corpus sources without selecting a chapter.
- [ ] The default processed-material browse path calls `SearchReferenceMaterials` with `anchor_ids: []` unless the user explicitly filters to one or more sources.
- [ ] Users can filter active/archived materials and restore archived materials.
- [ ] Users can review unverified or low-confidence materials and apply batch corrections transactionally.
- [ ] High-confidence materials stay searchable while remaining inspectable/correctable; low-confidence, unknown, or conflicting tags appear in the tag-review queue with counts.
- [ ] Material rows show source identity, material id, source segment id, source hash, material type, tags, confidence, score components where available, verification state, archive state, and bounded preview without overflowing compact layouts.
- [ ] Every material row can open a read-only detail view without requiring chapter context or advanced mode.
- [ ] Material detail shows material id, anchor id, source identity, owner scope, license/source trust, source segment id, source hash, material type, tags, confidence, user-verified state, score components, extractor/build metadata, detected slots/placeholders, archive state, bounded material/source preview, and processing notes where available.
- [ ] Material detail is read-only except for explicit navigation to existing tag correction/archive/restore actions; it must not hide implicit writes in detail expansion.
- [ ] Material detail/source-segment/source-slot drill-down is covered by contract tests that reject `source_text`, local path, prompt, candidate text, and full chapter content leakage.
- [ ] List previews and detail previews use explicit length limits and preserve provenance ids so a user can inspect the material without exposing full source files.

**Verification:**

- [ ] `npm --prefix frontend run test:corpus-library`
- [ ] Mocked browser coverage for filters, pagination, detail drawer, selection, tag correction, archive, restore, empty states, and compact viewport.
- [ ] Existing backend tests for `SearchReferenceMaterials`, `UpdateReferenceMaterialsTags`, `DeleteReferenceMaterials`, and `RestoreReferenceMaterials` remain green.
- [ ] Contract tests cover any new or extended material-detail payloads and sensitive-field negative assertions.

**Dependencies:** Task 2.

**Estimated scope:** M.

### Task 4: Add Chapter Reference Panel Shell

**Description:** Add a collapsible chapter-scoped reference panel to the existing chapter editor surface.

**Acceptance criteria:**

- [ ] The panel appears only when the active content tab is a chapter file.
- [ ] The panel derives chapter number and title from the active tab/path when possible, reusing existing content-path helpers before adding new state.
- [ ] If chapter number/title derivation fails or is ambiguous, generation and insertion actions are disabled until the user explicitly selects or confirms the target chapter.
- [ ] The panel does not appear for `novelist.md`, skills, diff tabs, bookshelf, profile, or non-chapter activity pages.
- [ ] Opening/closing the panel does not resize unrelated layout unexpectedly and is usable in compact viewports.
- [ ] `ChatPanel` behavior, file diff approval, and normal editor save behavior are unchanged when the panel is closed.

**Verification:**

- [ ] `npm --prefix frontend run test:chapter-reference`
- [ ] Mocked browser coverage for opening the panel from a chapter, derivation failure recovery, switching to non-chapter tabs, and compact viewport layout.
- [ ] `npm --prefix frontend run test:layout-ui` remains green.

**Dependencies:** Task 1.

**Estimated scope:** M.

### Task 5: Move Default Orchestration into Chapter Reference Panel

**Description:** Rehost the default orchestration workflow inside the chapter panel, with simplified chapter-use controls and advanced controls hidden.

**Acceptance criteria:**

- [ ] Users can start a current-chapter orchestration run from chapter goal, known facts, forbidden facts, and optional style profile.
- [ ] Opening the panel from a valid chapter automatically prepares chapter context and can suggest relevant accessible materials without requiring manual anchor selection.
- [ ] Chapter goal, known facts, and forbidden facts are optional in the default path; missing values do not block material suggestion or the primary start action unless the orchestration service explicitly requires a decision.
- [ ] Runs use the shared accessible corpus by default, without requiring selected anchors.
- [ ] If no accessible corpus material exists, the panel shows an import-corpus call to action and recoverable empty state instead of presenting unusable generation controls.
- [ ] Chapter use may optionally restrict to selected sources, but that selection is chapter-local and does not mutate corpus-library tags, metadata, visibility, or archive state.
- [ ] Required decisions, approval summaries, event history, candidate ids, and readable audit findings are visible in the chapter panel.
- [ ] Manual blueprint/material-binding controls are hidden behind advanced mode.
- [ ] The default chapter path has one primary start/resume action and does not require users to select anchors, manually search materials, understand blueprint generation, bind materials, or open advanced mode before getting an audited candidate attempt.
- [ ] `approve_final_insertion` remains a stopping decision and cannot be resumed as an automatic final write.

**Verification:**

- [ ] `npm --prefix frontend run test:chapter-reference`
- [ ] Mocked browser coverage for automatic context preparation, suggested material search, starting a run from a chapter, resuming a required decision, viewing audit findings, and cancelling a run.
- [ ] Backend orchestration tests remain green.

**Dependencies:** Task 4.

**Estimated scope:** M.

### Task 6: Add Explicit Candidate Preview and Editor Insertion

**Description:** Provide safe candidate handling inside the chapter editor. Candidate insertion must be explicit, local to the editor buffer, and undoable.

**Acceptance criteria:**

- [ ] Candidate text is previewed before use.
- [ ] User can copy candidate, insert at cursor, append to chapter, or replace selection.
- [ ] Insertion changes the editor buffer through Monaco edit operations and participates in undo/redo.
- [ ] Insertion marks the tab dirty and relies on existing editor save behavior; no orchestration method calls `SaveContent`.
- [ ] No backend bridge method or agent tool is added for inserting candidate prose into a chapter file.
- [ ] If existing editor autosave runs after insertion, tests must prove `SaveContent` occurs only after the explicit insertion action and only for the active chapter path.
- [ ] If the product requires insertion without immediate autosave, add an explicit `dirtyOnly` or deferred-save editor path instead of bypassing the guardrail through orchestration code.
- [ ] Candidate insertion is disabled when audit status blocks use.

**Verification:**

- [ ] `npm --prefix frontend run test:chapter-reference`
- [ ] Mocked browser coverage for copy, insert-at-cursor, append, replace-selection, undo, dirty-state, and no implicit `SaveContent` before insertion confirmation.
- [ ] Existing editor save tests remain green.

**Dependencies:** Task 5.

**Estimated scope:** M.

### Task 7: Retire the Mixed `ReferenceAnchorView` Surface

**Description:** Replace the mixed page with the corpus-library page and remove duplicated chapter-use controls from the activity-level panel.

**Acceptance criteria:**

- [ ] Activity-level `素材库` no longer renders default orchestration or current-chapter blueprint/candidate controls.
- [ ] Chapter reference panel is the only default place to start current-chapter reference use.
- [ ] Advanced/debug reference workflows remain accessible only in an intentional advanced context.
- [ ] Old component exports are removed or kept as thin compatibility wrappers only when needed for incremental migration.

**Verification:**

- [ ] `rg` confirms current-chapter controls are not mounted in corpus page default path.
- [ ] `npm --prefix frontend run test:corpus-library`
- [ ] `npm --prefix frontend run test:chapter-reference`
- [ ] Mocked browser coverage proves corpus processing and chapter use can run independently in the same session.

**Dependencies:** Tasks 2-6.

**Estimated scope:** M.

### Task 8: Update Documentation, Help, Release Notes, and Agent Context

**Description:** Update user and developer documentation to reflect the new split.

**Acceptance criteria:**

- [ ] README describes `素材库` as shared corpus processing and `章节参考素材` as current-chapter use.
- [ ] Help dialog explains the two workflows separately.
- [ ] Developer docs record component boundaries, bridge reuse, and guardrails.
- [ ] Release notes summarize the user-facing IA change.

**Verification:**

- [ ] Documentation links resolve.
- [ ] `npm --prefix frontend run lint`
- [ ] `npm --prefix frontend run build`

**Dependencies:** Tasks 2-7.

**Estimated scope:** S.

## Checkpoints

### Checkpoint A: Corpus Library Usable Alone

- [ ] Tasks 1-3 complete.
- [ ] A user can import corpus sources, inspect processed materials, correct tags, archive/restore materials, and manage style profiles without seeing chapter-use controls.
- [ ] A successful import reaches searchable processed materials without manual segmentation, material creation, indexing, or advanced debug controls.
- [ ] Duplicate import, retry/rebuild, and interrupted processing recovery remain idempotent and preserve provenance/user corrections.
- [ ] Processing records and material details make automatic output auditable without exposing sensitive source/prompt/candidate data.
- [ ] Existing backend corpus/material guardrails remain green.

### Checkpoint B: Chapter Use Embedded

- [ ] Tasks 4-6 complete.
- [ ] A user can open a chapter, open the reference panel, retrieve shared-corpus materials, run orchestration, inspect candidates/audits, and explicitly insert a candidate into the editor buffer.
- [ ] The normal chapter path does not require advanced mode, manual anchor selection, manual material search, manual blueprint generation, or manual material binding before the first audited candidate attempt.
- [ ] Empty-corpus chapter use shows an import/recover path rather than dead-end controls.
- [ ] No implicit `SaveContent` occurs.

### Checkpoint C: Mixed Surface Retired

- [ ] Tasks 7-8 complete.
- [ ] The product has two clear surfaces: `素材库` for corpus processing and chapter-level `参考素材` for use.
- [ ] Documentation and mock workflows match the new IA.

## Verification Matrix

Run at minimum:

```text
npm --prefix frontend run lint
npm --prefix frontend run build
npm --prefix frontend run test:corpus-library
npm --prefix frontend run test:chapter-reference
npm --prefix frontend run test:reference-anchor
npm --prefix frontend run test:reference-style
npm --prefix frontend run test:reference-style:stress
npm --prefix frontend run test:app
dotnet test Novelist.slnx --no-restore -v minimal --filter "FullyQualifiedName~Reference|FullyQualifiedName~MafToolRegistryTests|FullyQualifiedName~BridgeFrontendContractTests|FullyQualifiedName~WorkspaceUtilityServiceTests"
```

Add focused mocked browser workflows and package scripts:

- `test:corpus-library`: corpus source import, source list, automatic processing, processing records, processed material browse/detail, tag correction, archive/restore, style profile management.
- `test:chapter-reference`: chapter panel open/close, chapter context derivation/recovery, current-chapter run start/resume/cancel, candidate preview, explicit insertion, no implicit save.
- Existing `test:reference-anchor` may remain as a compatibility umbrella during migration, but its assertions must eventually reflect the separated IA. It must not be the only Phase 16 browser gate.
- The package scripts above must be committed before the verification matrix is treated as runnable; `test:reference-anchor` must not silently stand in for either focused Phase 16 gate.

Corpus-library browser coverage must include:

- Import one source, batch import sources, and import a library-pack manifest without starting orchestration, blueprint generation, material binding, draft generation, or `SaveContent`.
- Assert imported sources automatically progress through parse/segment/extract/tag/index status updates and become searchable without manual material creation.
- Import the same source twice and assert the UI/service reuses or reports the existing source instead of creating duplicate searchable materials.
- Assert high-confidence materials become searchable without manual review, while low-confidence, conflicting, or unknown tags appear in the `标签校正` queue with visible counts.
- Inspect `处理记录` for stage history, material/segment/slot/vector counts, warnings, failures, recovered processing state, and retry/rebuild actions.
- Exercise partial failure, partial recovery, retry success, retry failure, rebuild, and app-restart/interrupted-processing recovery states; assert current status updates, old failure context remains inspectable, source/material/segment/slot ids remain stable where expected, user-verified corrections survive, duplicate searchable materials are not created, and affected ids can be linked or filtered when available.
- Assert copied diagnostics are redacted and do not contain absolute paths, usernames, environment variables, tokens, API keys, prompts, full source text, candidate text, or full chapter text.
- Browse `处理后语料` with no selected chapter and assert default `SearchReferenceMaterials` uses `anchor_ids: []`.
- Filter by source, owner scope, material type, active/archive state, tags, source trust, license, and keyword.
- Show owner scope so `novel` and `workspace_corpus` sources are visually distinct.
- Promote a private source to workspace corpus and keep the action explicit.
- Open a source detail and view its processed material rows with `material_id`, `anchor_id`, `source_segment_id`, `source_hash`, material type, tags, confidence, verification state, and bounded preview.
- Open a material detail drawer and assert provenance, source identity, source segment metadata, source hash, bounded preview, slots/placeholders, extractor/build metadata, score components, archive state, and processing notes are visible.
- Assert material detail payloads and UI never expose full source text, local source paths, prompts, candidate text, or full chapter text.
- Correct one material tag and multiple selected material tags; verify `user_verified` state updates.
- Archive selected materials, confirm they disappear from active browse, switch to archived view, restore them, and confirm selection is not lost after recoverable failure.
- Build, inspect, archive, restore, and compare style profiles as corpus-library assets without exposing chapter orchestration controls.

Chapter-reference browser coverage must include:

- Open chapter 5, open the `参考素材` drawer, and assert the panel derives chapter 5 from the active tab.
- Open a malformed or ambiguous chapter path and assert generation/insertion actions are disabled until the target chapter is explicitly confirmed.
- Switch to another chapter and assert chapter number, run list filtering, and candidate target context update.
- Switch to `novelist.md`, a skill tab, a diff tab, or an empty state and assert the chapter-use actions are disabled or hidden.
- Assert the drawer prepares chapter context and suggests accessible materials without requiring manual anchor selection.
- Assert chapter goal, known facts, and forbidden facts can be empty in the default path.
- Assert empty corpus state shows an import-corpus call to action and recovers after corpus processing completes.
- Start an orchestration run from chapter goal and fact boundaries without selecting corpus anchors.
- Assert default mode does not render manual source restriction, manual material search, blueprint, binding, or debug controls; those controls appear only after advanced mode is explicitly enabled.
- Resume source/fact, blueprint revision, blueprint approval, and high-risk decisions, while preserving the current final-insertion stop.
- Assert `SaveContent` is not called by material search, orchestration start, decision resume, candidate generation, audit display, copy, or preview.
- Assert insertion confirmation changes only the active editor buffer before normal save behavior runs.
- Assert chapter-reference controls never call corpus metadata/tag/archive/rebuild methods.
- Assert outline view can show reference context but disables direct prose insertion or requires switching back to 正文.
- Assert `ChatPanel` file-edit approval still opens diff tabs and is unaffected by the drawer.
- Assert 1280x840 and compact-width layouts do not overflow or hide critical action buttons.

Backend/contract coverage to keep or add:

- `BridgeFrontendContractTests.FrontendAppApiMethodsMatchBackendCompatibilityRegistry` must include any newly added helper methods.
- `ReferenceBridgeHandlerRoutingTests.ReferenceAnchorHandlersRouteEveryMethodToServiceOperations`, `ReferenceAnchoredDraftHandlersRouteEveryMethodToServiceOperations`, and `ReferenceStyleProfileHandlersRouteEveryMethodToServiceOperations` must route every corpus/style/chapter method to the correct service.
- `ReferenceAnchorContractTests.AnchoredDraftPayloadSerializesBeatCandidatesWithoutFullChapterAssembly` and `AnchoredDraftAuditPayloadSerializesReadableReportWithoutCandidateOrSourceText` must remain green.
- If `GetReferenceMaterialDetail`, `GetReferenceSourceProcessingDetail`, `GetReferenceBlueprintMaterialLinks`, `SelectReferenceBlueprintMaterialLinks`, `GetReferenceChapterUsage`, or other helper methods are added, add snake_case serialization tests, bridge routing tests, frontend API declarations, and negative assertions for `source_text`, `candidate_text`, `prompt`, local path, full source text, and full chapter content.
- Add or retain backend coverage proving duplicate import, retry, rebuild, and recovered/interrupted processing do not duplicate materials, lose provenance, expose archived materials unintentionally, or discard user-verified tag corrections.
- `ReferenceAnchorServiceTests.SearchMaterialsIncludesWorkspaceCorpusAnchorsWithoutLeakingOtherNovelPrivateAnchors`, `WorkspaceCorpusVisibilityFiltersAnchorsBeforeSearchAdaptAuditTagAndFeedback`, and `DeleteMaterialsArchivesSelectedMaterialsWithoutDeletingProvenance` must remain green.
- `ReferenceAnchoredDraftServiceTests.GenerateDraftFromBlueprintReturnsCandidatesWithoutMutatingChapterContent` and `ReferenceOrchestrationRunRejectsFinalInsertionResumeAndKeepsManualInsertionBoundary` must remain green.
- `WorkspaceUtilityServiceTests.SearchAllLeavesReferenceMaterialsInDedicatedSearch` must remain green so corpus material search does not leak into ordinary workspace search.
- `MafToolRegistryTests` must continue proving agent tools cannot import sources, read arbitrary local files, or approve final insertion.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Moving UI components breaks existing reference workflows | Regression in a complex feature area | Extract vertically, keep bridge calls unchanged first, preserve existing mock fixtures, and run backend reference tests unchanged |
| Chapter panel starts acting like another chat panel | Confusing duplicate assistance surfaces | Keep it auditable and structured: material results, orchestration state, candidate/audit, explicit insertion |
| Corpus page becomes too simplified and hides needed processing controls | Power users cannot inspect or fix bad material processing | Use tabs, not removal: normal corpus tabs plus explicit advanced/debug tab |
| Candidate insertion bypasses audit or save boundaries | Source-leak/user-data risk | Disable insertion on failed audit, insert only into editor buffer, assert no orchestration path calls `SaveContent` |
| Current chapter detection is wrong for restored tabs or imported chapters | Candidate generated for wrong chapter | Derive from active tab path and validate against `chapters/{n}.md`; show manual correction only when derivation fails |
| AI automation becomes a hidden black box | Users cannot trust or repair corpus output | Surface processing records, material detail, confidence, affected counts, review queues, and copyable diagnostics for every automatic stage |
| Automatic processing is not idempotent | Duplicate materials, lost corrections, or stuck processing states | Make import/retry/rebuild/resume service-owned and test duplicate import, retry, rebuild, and app-restart recovery paths |
| Shared corpus and per-novel/private corpus visibility become unclear | Incorrect material availability | Surface owner scope/visibility/source trust in corpus page and show chapter panel search policy summary |
| Large corpus material browsing overloads the UI | Slow or unreadable corpus page | Keep pagination, bounded previews, filters, lazy detail expansion, and compact viewport tests |
| Corpus selected material ids are reused as chapter binding ids | Wrong materials bound to a chapter or unexpected cross-workflow behavior | Keep corpus selection state and chapter selection state in separate hooks/stores; mock both workflows in one session |
| Chapter-reference drawer modifies corpus metadata for convenience | Public corpus is polluted by per-chapter needs | Do not pass tag/update/archive/rebuild actions into the drawer; use chapter-local selection and user feedback APIs instead |
| New overview/detail payloads leak source text, candidate text, prompts, local paths, or full chapter content | Source/license/privacy and prompt-leak risk | Make new helpers summary-only and add contract negative assertions for sensitive fields |
| Material detail is left at row preview only | Users cannot inspect or correct bad extraction accurately | Treat material detail as required Phase 16 scope, not an optional enhancement; add browser and contract coverage |
| Workspace-corpus visibility is filtered in frontend instead of service rules | Private sources from another novel may leak or shared sources may disappear | Reuse service search/list rules and keep workspace/private visibility integration tests in the required matrix |
| Normal workspace search starts returning reference materials | Users see corpus snippets outside reference workflow | Keep reference materials in dedicated search and retain `SearchAllLeavesReferenceMaterialsInDedicatedSearch` regression coverage |

## Open Questions

- Should the activity label be `素材库`, `参考素材库`, or `语料库`? Recommended: `素材库` for user familiarity, while docs can call it shared corpus library.
- Should the chapter reference panel live between `ContentPanel` and `ChatPanel`, or inside `ContentPanel` as a drawer? Recommended first implementation: inside `ContentPanel` so it follows the active chapter tab and can directly access editor insertion APIs.
- Should a corpus source imported with `visibility = workspace` become immediately available to all novels? Existing behavior supports workspace corpus; the UI must make the visibility choice explicit during corpus import.
- What exact preview length should material detail use for bounded source/material context? Decide during implementation and lock it in contract/browser tests.
- Should the old advanced reference activity remain reachable for internal debugging? Recommended: only behind `高级` within the chapter panel or a developer/debug flag, not as the default user path.

## Definition of Done

Phase 16 is complete when:

- [ ] Shared corpus-library processing is a standalone, understandable product surface.
- [ ] Explicitly imported corpus sources are automatically processed into searchable materials, with only low-confidence/conflicting/failure cases routed to focused human review queues.
- [ ] Corpus processing is idempotent and resumable across duplicate import, retry, rebuild, and app restart without duplicating materials or losing provenance/user corrections.
- [ ] Processed corpus materials are directly browsable, inspectable in read-only detail, and correctable through dedicated tabs.
- [ ] Processing records let users inspect parse/segment/extract/tag/index/build outcomes, failures, warnings, affected output counts, retry/rebuild state, and redacted copyable diagnostics.
- [ ] Current-chapter reference use is embedded in the chapter editor.
- [ ] Chapter reference use consumes the shared corpus by default and can run without selecting anchors.
- [ ] Chapter reference use derives or explicitly confirms chapter context before generation/insertion and suggests relevant corpus materials without manual anchor setup.
- [ ] Candidate generation remains audited, provenance-preserving, and unable to auto-save chapter content.
- [ ] Candidate insertion is explicit, local to the editor buffer, undoable, and covered by tests.
- [ ] Existing backend reference-anchor, orchestration, style-profile, source-leak, and agent-boundary tests remain green.
- [ ] Documentation explains the split clearly enough that future agents do not reintroduce a mixed corpus/chapter workbench as the default UI.
