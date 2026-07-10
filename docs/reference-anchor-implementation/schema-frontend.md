# Reference Anchor Frontend Surface

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to schema and integration index](schema-and-integration.md).

> **Current status (2026-07-10):** The corpus-library/chapter-editor split below exists as a thin slice. The later “main-panel workflow” and “current frontend status” sections record the older reference-panel implementation for regression history. `ChapterReferencePanel` now uses the persistent blueprint session for the default path; automatic and expert controls are layered rather than concurrent defaults. `ReferenceAnchorView` owns the persistent background-analysis start and recovery surface. Current M9 acceptance is defined in the corpus-driven writing plan, with real-user evidence still open.

## Frontend Surface

Current API/type layer:

```text
frontend/src/lib/novelist/types.ts
frontend/src/lib/novelist/api.ts
frontend/src/hooks/useApp.ts
```

The owned TypeScript bridge exposes the `reference` namespace with:

```ts
export namespace reference {
  export interface Anchor { ... }
  export interface CreateAnchorInput { ... }
  export interface CreateAnchorsInput { ... }
  export interface BuildStatus { ... }
  export interface Material { ... }
  export interface UpdateMaterialTagsInput { ... }
  export interface SearchMaterialsInput { ... }
  export interface ReuseCandidate { ... }
  export interface ReuseAudit { ... }
  export interface ChapterBlueprint { ... }
  export interface ChapterBlueprintAnalysisTrack { ... }
  export interface ChapterBlueprintExecutionTrack { ... }
  export interface ChapterBlueprintBeat { ... }
  export interface ChapterBlueprintReview { ... }
  export interface AnchoredDraft { ... }
  export interface AnchoredDraftAudit { ... }
  export interface StartOrchestrationRunInput { ... }
  export interface OrchestrationRun { ... }
  export interface OrchestrationRequiredDecision { ... }
  export interface OrchestrationRunEvent { ... }
  export interface ResumeOrchestrationRunInput { ... }
  export interface CancelOrchestrationRunInput { ... }
}
```

Current view components:

```text
frontend/src/components/reference-anchor/ReferenceAnchorView.tsx
frontend/src/components/reference-use/ChapterReferencePanel.tsx
frontend/src/components/content/ContentPanel.tsx
frontend/src/components/reference-anchor/OrchestrationPanel.tsx
frontend/src/components/reference-anchor/BlueprintDetail.tsx
frontend/src/components/reference-anchor/blueprintRevision.ts
frontend/src/components/reference-anchor/referenceAnchorStyles.ts
```

## Phase 16 Target And Thin-Slice Boundary

Phase 16 splits the reference-anchor UI into two default product surfaces:

- `ReferenceAnchorView.tsx` is the shared `素材库` activity. It owns source import, batch/library-pack import, source list and processing detail, material browse/detail, source-segment detail, tag review, archive/restore, source metadata, rebuild/retry affordances, style-profile library work, and the ready-source “开始分析” action. That action calls `EnqueueReferenceCorpusAnalysisJob` for the standard feature-analysis job and moves the user to the persistent task view; it must not expose current-chapter orchestration, blueprint generation, candidate generation, or final insertion controls in the default path.
- `ChapterReferencePanel.tsx` is mounted from `ContentPanel.tsx` for chapter editor tabs. It owns current-chapter context detection, material recommendations, orchestration start/resume/cancel, blueprint/high-risk decisions, audited candidate preview, and explicit editor-buffer copy/insert/append/replace actions. It must not mutate corpus metadata, import source files, update material tags, archive/restore materials, rebuild anchors, or call backend chapter save as part of reference use.
- Dormant activity-level chapter-writing/debug controls, if still compiled during migration, must stay behind the explicit development flag `VITE_REFERENCE_ACTIVITY_CHAPTER_DEBUG=true`. Browser guardrails must prove they are not reachable from the default `素材库` activity, including the `高级` corpus tab.
- `GetReferenceMaterialTagReviewQueue` is the only frontend source for tag-review queue eligibility, counts, and pagination. The UI must not reconstruct that queue from the currently visible material search page. Current queue eligibility is unverified, low-confidence, or unknown tags; conflict entries require a durable analyzer conflict signal before they can be displayed or asserted.
- `GetReferenceMaterialDetail`, `GetReferenceSourceProcessingDetail`, and `GetReferenceSourceSegmentDetail` are read-only inspection helpers. Detail drawers must display bounded previews, source/provenance ids, counts, attempts, affected ids, and redacted diagnostics without showing `source_path`, raw source text, `source_text`, `candidate_text`, prompts, local paths, full source text, or full chapter content.
- Final candidate use from the chapter panel is UI/editor-buffer only. Copy, insert-at-cursor, append, and replace-selection may update the active Monaco model and dirty state; they must not call `SaveContent`, `GenerateReferenceAnchoredDraft`, or direct material adaptation as an implicit write path.

Current shell integration:

```text
frontend/src/components/shell/ActivityBar.tsx
frontend/src/views/WorkspaceView.tsx
```

Historical reference-main-panel workflow:

- separate the current page into a left-side corpus-library management area and a right-side reference-writing retrieval area
- list anchors for active novel
- create anchor from local `.txt`/`.md` path
- bulk import multiple local `.txt`/`.md` source paths or a simple JSON library-pack manifest through `CreateReferenceAnchors`, reusing the current corpus metadata fields and deriving per-source titles when needed
- edit anchor/corpus metadata: title, author, license status, visibility, source trust, and user tags
- browse a paginated focused material preview for a specific anchor/corpus row using `SearchReferenceMaterials`
- correct a browsed material row's function, emotion, scene, POV, and technique tags through `UpdateReferenceMaterialTags`
- rebuild anchor
- show build state and counts
- filter the anchor/corpus list by owner scope, title/author/tag/path/metadata query, license status, visibility, and source trust
- search material bank with filters, including story-context `narrative_duties`, `emotion_transitions`, and `prose_duties`
- preview source/adapted/diff/audit
- generate a chapter blueprint for a selected `chapter_number`
- show the five blueprint analysis tracks: logic, emotion, narration, character, and reference use
- show the execution track separately: paragraph intention, execution mode, anti-screenplay duty, source-backed detail target, and candidate rejection rule
- show blueprint beats, causality chain, transition plan, emotion trajectory, character state deltas, POV constraints, forbidden facts, prose duties, and execution duties
- run blueprint review and block draft generation until it passes
- expose review defects as actionable fields rather than a single free-form critique
- invalidate approval after user edits a beat or analysis track
- bind reference materials to blueprint beats
- generate draft candidates only from approved blueprints
- show draft text alongside source material, blueprint beat, rewrite level, audit result, and the source-text-free readable audit report with candidate ids, finding category/severity, and required action
- preview candidates; no automatic insertion in phase 1
- start the default low-intervention orchestration run from chapter goal, known facts, and forbidden facts without requiring selected anchors
- show that default orchestration retrieves materials by `story_context` from accessible workspace corpus materials, including max-results, license-status, and include/exclude anchor policy for the active run
- optionally constrain an orchestration run to selected anchors as an advanced override
- list orchestration runs, inspect the active run, view required decisions, approval summary, candidate ids, and local event history
- resume required source/fact, blueprint revision, blueprint approval, and high-risk-stop decisions from the reference panel
- cancel orchestration runs
- stop at final insertion with candidate ids visible; the final-insertion decision is not resumable from the panel, and no frontend path automatically inserts candidate prose into chapter content
- keep manual material search plus blueprint generate/revise/review/approve/bind/draft controls behind `高级模式` so the default surface stays orchestration-first while debugging and strict editorial review workflows remain available

Historical Phase 11/12 frontend status:

- `frontend/src/components/reference-anchor/ReferenceAnchorView.tsx` exists as the first full main-panel implementation.
- `OrchestrationPanel.tsx` is mounted at the top of the reference-anchor main panel as the default Phase 11 workflow entry.
- `ActivityBar.tsx` has a `reference` activity entry.
- `WorkspaceView.tsx` renders `ReferenceAnchorView` for the active novel.
- The owned TypeScript bridge adapter exposes both `CreateReferenceAnchor` and `CreateReferenceAnchors`; the current UI supports one-source import, newline/semicolon batch path import, and JSON library-pack manifests as either a top-level array or `{ "sources": [...] }`.
- The first panel now labels the left column as `语料库管理`, with `导入语料来源`, `库条目`, and `材料库` sections, and labels the right column as `参考写作检索`. It supports anchor create/rebuild/list, native reference source file selection with raw path fallback, batch source-path import and JSON library-pack manifest import through `CreateReferenceAnchors`, corpus metadata entry for `visibility`, `source_trust`, and `user_tags` at creation time, row-level metadata editing through `UpdateReferenceAnchorMetadata`, list-row display including `owner_scope`, owner-scope segmented filtering/counts for all, current-novel, and workspace-corpus anchors, local list filtering by title/author/tag/path/metadata plus license, visibility, and source trust, paginated focused row-level material browsing via `SearchReferenceMaterials`, single-material tag correction through `UpdateReferenceMaterialTags`, current-page selected bulk material tag correction through `UpdateReferenceMaterialsTags`, independent material-library search with no selected-anchor prerequisite, material-library current-page filtering/sorting, material-library cross-page selected bulk tag correction, material-library selected material soft archive through `DeleteReferenceMaterials`, material-library archived-material filtering through `SearchReferenceMaterials.archive_filter`, selected archived-material restore through `RestoreReferenceMaterials`, and a single-anchor `PromoteReferenceAnchorToWorkspaceCorpus` action for turning an owned per-novel anchor into workspace corpus without rebuilding materials. It also supports default orchestration start/inspect/resume/cancel with `story_context`, unrestricted anchors, and `user_provided` as the default license policy, plus advanced-mode material search with score-component explanations, story-context prose-duty filters, blueprint generate/list/detail/review/approve, field-level beat editing through `ReviseReferenceChapterBlueprint`, typed `slot_plan` rows, material binding with score-component explanations, and draft candidate preview. This remains a targeted corpus-management thin slice; the current panel has not yet become the full corpus library management UI.
- The Playwright mock-bridge workflow covers the default orchestration surface, asserts the corpus-library and reference-writing retrieval section headings, asserts story-context workspace-corpus retrieval without selected `anchor_ids` and with `license_statuses: ["user_provided"]`, asserts create-anchor corpus metadata and `owner_scope` payload/list display, exercises batch source import and JSON library-pack manifest import through `CreateReferenceAnchors`, exercises the single-anchor promotion action and bridge payload, edits anchor/corpus metadata through `UpdateReferenceAnchorMetadata`, opens a row-level material browser and verifies page summary, material id/text/score components, next-page navigation, anchor-scoped `SearchReferenceMaterials` payloads, single-material tag correction through `UpdateReferenceMaterialTags`, current-page selected bulk material tag correction through `UpdateReferenceMaterialsTags`, and independent material-library search/filter/sort/cross-page correction plus selected material archive and restore through `SearchReferenceMaterials` with `anchor_ids: []`, `archive_filter`, `UpdateReferenceMaterialsTags`, `DeleteReferenceMaterials`, and `RestoreReferenceMaterials`; it verifies owner-scope filtering between current-novel and workspace-corpus rows, verifies local anchor-list query/license/source-trust filters against updated metadata, asserts manual controls are hidden until advanced mode is opened, then exercises manual material search with `prose_duties`, blueprint revision/review/approval, binding, draft generation, readable audit report display, and stale-blueprint disabled controls.
- Remaining UI hardening belongs to later product work: dedicated side-panel list/filter behavior and copy-to-clipboard or insertion-confirmation affordances for final candidate handling.

Stale blueprint behavior is resolved for the current UI: stale blueprints stay visible as read-only comparison artifacts, show a regeneration prompt, and cannot be reviewed, approved, revised, bound to materials, or used for draft candidates.

Optional model-assisted material tagging/adaptation is resolved for the current implementation: it is not enabled. The current UI and bridge expose deterministic material tagging, declared-slot adaptation, rewrite-level classification, and reuse audit only. A future model-assisted path must be explicit opt-in and cannot bypass deterministic review, binding, rewrite-level, or audit gates.

The direct reference source file picker is implemented as a desktop runtime capability through the Photino bridge. Raw path entry remains available for development and fallback workflows.

### Desktop Debugging Plan

The reference-anchor feature should not change the desktop launch contract. If Photino reports that `frontend/dist/index.html` cannot be found, fix the frontend asset workflow:

- run `npm --prefix frontend run build` before launching the Photino host when loading local built assets;
- or start Vite and launch the app with the existing `--start-url=` development path;
- improve the launch error message if needed so it points to the missing frontend build;
- do not add ASP.NET Core/Kestrel merely to mask a missing local asset;
- keep bridge business calls independent from whether assets are loaded from `file://`, packaged dist, or Vite dev server.
