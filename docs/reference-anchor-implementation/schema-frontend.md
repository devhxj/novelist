# Reference Anchor Frontend Surface

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to schema and integration index](schema-and-integration.md).

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
frontend/src/components/reference-anchor/OrchestrationPanel.tsx
frontend/src/components/reference-anchor/BlueprintDetail.tsx
frontend/src/components/reference-anchor/blueprintRevision.ts
frontend/src/components/reference-anchor/referenceAnchorStyles.ts
```

Current shell integration:

```text
frontend/src/components/shell/ActivityBar.tsx
frontend/src/views/WorkspaceView.tsx
```

Current main-panel workflow:

- list anchors for active novel
- create anchor from local `.txt`/`.md` path
- rebuild anchor
- show build state and counts
- search material bank with filters
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
- show draft text alongside source material, blueprint beat, rewrite level, and audit result
- preview candidates; no automatic insertion in phase 1
- start the default low-intervention orchestration run from chapter goal, known facts, and forbidden facts without requiring selected anchors
- optionally constrain an orchestration run to selected anchors as an advanced override
- list orchestration runs, inspect the active run, view required decisions, approval summary, candidate ids, and local event history
- resume required source/fact, blueprint revision, blueprint approval, and high-risk-stop decisions from the reference panel
- cancel orchestration runs
- stop at final insertion with candidate ids visible; the final-insertion decision is not resumable from the panel, and no frontend path automatically inserts candidate prose into chapter content

Current frontend status:

- `frontend/src/components/reference-anchor/ReferenceAnchorView.tsx` exists as the first full main-panel implementation.
- `OrchestrationPanel.tsx` is mounted at the top of the reference-anchor main panel as the default Phase 11 workflow entry.
- `ActivityBar.tsx` has a `reference` activity entry.
- `WorkspaceView.tsx` renders `ReferenceAnchorView` for the active novel.
- The first panel supports anchor create/rebuild/list, native reference source file selection with raw path fallback, material search with score-component explanations, default orchestration start/inspect/resume/cancel, blueprint generate/list/detail/review/approve, field-level beat editing through `ReviseReferenceChapterBlueprint`, typed `slot_plan` rows, material binding with score-component explanations, and draft candidate preview.
- Remaining UI hardening belongs to Phase 10 and later: dedicated side-panel list/filter behavior, Playwright mock-bridge screenshot/DOM coverage for the full reference-anchor workflow, minimal real Photino runtime smoke, and copy-to-clipboard or insertion-confirmation affordances for final candidate handling.

Stale blueprint behavior is resolved for the current UI: stale blueprints stay visible as read-only comparison artifacts, show a regeneration prompt, and cannot be reviewed, approved, revised, bound to materials, or used for draft candidates.

Optional model-assisted material tagging/adaptation is resolved for the current implementation: it is not enabled. The current UI and bridge expose deterministic material tagging, declared-slot adaptation, rewrite-level classification, and reuse audit only. A future model-assisted path must be explicit opt-in and cannot bypass deterministic review, binding, rewrite-level, or audit gates.

The direct reference source file picker is implemented as a desktop runtime capability through the Photino bridge. Raw path entry remains available for development and fallback workflows.

### Desktop Debugging Plan

The reference-anchor feature should not change the desktop launch contract. If Photino reports that `frontend/dist/index.html` cannot be found, fix the frontend asset workflow:

- run `npm --prefix frontend run build` before `make dev` when loading local built assets;
- or start Vite and launch the app with the existing `--start-url=` development path;
- improve the launch error message if needed so it points to the missing frontend build;
- do not add ASP.NET Core/Kestrel merely to mask a missing local asset;
- keep bridge business calls independent from whether assets are loaded from `file://`, packaged dist, or Vite dev server.
