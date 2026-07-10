# Reference Anchor Tasks: Phase 16

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

> **Status boundary (2026-07-10):** This file preserves Phase 16 implementation checkpoints. The corpus-driven-writing M9 work has since closed the automated single default chapter path, persistent blueprint-session recovery, background-task recovery, and accessibility/viewport evidence. These checkpoints still do not prove real-user task success; the remaining acceptance work is authoritative in `docs/corpus-driven-writing/development-plan.md` and M9 in `tasks.md`.
>
> **Historical task-ledger note (2026-07-11):** Unchecked Phase 16 boxes below preserve the original planning checkpoint and are not a second current completion count. The authoritative current ledger is `docs/corpus-driven-writing/tasks.md`: its only open atomic task is the five-person, unprompted M9 usability study. Keep the historical boxes for traceability; do not use them to claim a separate open default-UI implementation backlog.

## Phase 16: Corpus Library and Chapter Reference-Use Separation

**Status:** Historical implementation checkpoint; M9 real-user validation remains in progress. This phase corrects the product information architecture of the reference-anchor surface. It separates the shared corpus-library processing workflow from current-chapter reference use, while preserving the existing strict reference-anchor implementation: auditable source provenance, deterministic material extraction, style profiles, reviewed blueprints, material binding, draft audit, source-leak checks, and explicit user approval before any chapter content is inserted or saved.

**Current checkpoint:** The default IA split is implemented in the current working tree: the activity label is `素材库`, corpus-library task tabs live in `ReferenceAnchorView`, and `ChapterReferencePanel` is mounted from `ContentPanel` for chapter content tabs. Ready source rows now expose one “开始分析” action that invokes `EnqueueReferenceCorpusAnalysisJob` for the standard feature-analysis job, moves focus to the persistent task tab, and is covered by the browser path “导入 → 启动 → 离开 → 返回”. Material detail and source-processing detail helpers are implemented for UI/MAF, with redaction, bounded previews, negative tests, and visible UI entry points from material rows, chapter recommendations, source rows, and processing records. Chapter recommendation cards can inspect read-only material detail without calling direct adaptation or saving content. Final-insertion stops now load audited candidate previews through a UI-only read helper, and explicit copy/insert/append/replace actions update only the Monaco editor buffer with undo coverage and no chapter-reference `SaveContent`. The server-owned `GetReferenceMaterialTagReviewQueue` is implemented and wired through frontend, mock bridge, and browser guardrails as a cross-page, paginated tag-correction queue for unverified, low-confidence, and unknown material tags. Batch source import and library-pack manifest import now use `CreateReferenceAnchorsWithResult`: successful sources are imported independently, failed entries return per-source redacted diagnostics and path-free stable `source_identity`, successful anchors remain usable/searchable, and partial-failure UI keeps the input available for retry. Initial source read failures before material output now persist a real `failed_import` anchor/build record with zero segment/material/slot/vector counts, redacted processing detail, and no fake material rows; duplicate import of that terminal failure returns the existing failed anchor, and explicit rebuild recovers it after the source is fixed. Focused recovery slices now cover service-owned startup recovery after interrupted embedding and recoverable pre-embedding durable stages, duplicate-import recovery after interrupted pre-embedding and embedding stages, terminal failed/cancelled duplicate-import safety, in-process concurrent duplicate-import reuse across service instances, SQLite-backed duplicate-import uniqueness for the persisted normalized source identity, failed import rebuild diagnostics with retained prior output counts/material ids/archive state/user corrections/active-only search/detail drill-down, failed import retry success after the source is restored, failed import retry failure, cancelled embedding explicit rebuild success/failure, failed embedding retry success, failed segmenting retry failure, failed extraction retry failure, failed embedding retry failure, failed slotting retry failure, and failed embedding rebuild rollback: app initialization can reconcile `created`, `importing`, `source_imported`, `segmenting`, `segments_built`, `extracting_materials`, `materials_extracted`, `detecting_slots`, `slots_detected`, `embedding`, and `stale` states without requiring the user to repeat the import; early `created` / `importing` / `source_imported` / `segmenting` restart recovery also works when no segment or material rows existed yet; startup recovery preserves material ids, archive state, user-verified tag corrections, and active-only default search while avoiding duplicate searchable rows; `detecting_slots`, `slots_detected`, and `stale` startup recovery now also prove active-only vector indexing resumes after restart while slot records, material ids, archive state, and user corrections stay intact; missing-source recovery from those slotted pre-embedding states degrades to `failed_import` while retaining material/slot/vector counts and affected material/source-segment/slot drill-down ids; workspace-corpus interrupted embedding startup recovery now also proves consuming novels can default-search the recovered shared corpus while archived materials stay hidden and active materials receive vectors; duplicate import of recoverable pre-embedding states (`segments_built`, `extracting_materials`, `materials_extracted`, `detecting_slots`, `slots_detected`, `stale`) and embedding states reuses the existing anchor, completes rebuilding through the service, preserves material ids, archive state, user-verified tag corrections, active-only search, and processing history, and avoids duplicate searchable rows; duplicate import of failed/cancelled terminal states does not silently rebuild; explicit rebuild can recover `failed_import`, `cancelled`, or `failed_embedding` to ready while retaining historical failure/cancellation events; retry failure updates current diagnostics while preserving source segments or prior material ids and lexical searchability where material output exists; duplicate imports for the same persisted source identity return one anchor/material set instead of creating duplicate searchable corpus rows; failed import rebuild keeps the previous searchable corpus counts and affected material/source-segment links visible in processing detail when the source cannot be reread; failed embedding rebuild restores the prior searchable corpus and manual corrections while keeping failure diagnostics visible. Conflict entries should be added only after the analyzer persists a durable conflict signal. Dormant activity-level chapter writing/debug controls are no longer reachable by default; they are isolated behind an explicit development-only flag, `VITE_REFERENCE_ACTIVITY_CHAPTER_DEBUG=true`.

**Current hardening update:** The tag-correction tab now supports the first-use path directly from the server-owned queue: selecting the current queue exposes the bulk correction controls without requiring a separate material-library search. Partial batch/library-pack import failures are covered by browser workflow assertions for visible redacted diagnostics, copyable diagnostic detail, retained retry input, successful-source refresh, persisted failed-import source rows, failed source processing detail, and duplicate resubmission of the same source identities without adding second ready or failed source rows. Corpus-library browser coverage now also opens `failed_import` sources from `处理记录`, verifies blocked reason/current attempt/copyable diagnostics without paths or full text, runs explicit rebuild for both recovered and still-failed outcomes, and verifies recovered attempt/history plus retry-failure diagnostic refresh state. It also covers a recovered app-restart/interrupted-embedding source from `处理记录`, including current/prior attempts, recovered-from ids, affected material/source-segment links, redacted copied diagnostics, explicit rebuild, and a one-row searchable-material assertion for the recovered material id. Slots-detected startup-recovery browser coverage opens a recovered source from `处理记录`, verifies slots-detected prior attempt metadata, recovered-from ids, affected material/source-segment/slot ids, redacted copyable diagnostics, and exactly one searchable indexed material row. Missing-source startup-recovery browser coverage now opens a `failed_import` source that retained prior material output, verifies current/prior attempts, blocked reason, affected material/source-segment ids, retained material detail, bounded source-segment detail, redacted copyable diagnostics, explicit rebuild to ready, and exactly one searchable retained material row. Failed-extraction browser coverage now opens a `failed_extraction` source with no material rows, verifies bounded source-segment detail from the affected id, copies redacted diagnostics, rebuilds explicitly to `ready`, and confirms the recovered material id appears as exactly one searchable row. Failed-slotting browser coverage opens the retained material detail while the source is still `failed_slotting`, verifies material/source-segment affected ids and redacted diagnostics, runs explicit rebuild, and confirms the retained material id appears as exactly one searchable row after recovery. `AuditReferenceReuse` bridge and MAF exits now run through `ReferencePayloadSanitizer.SanitizeReuseAudit`; material detail drawers ignore stale out-of-order responses; content save diagnostics carry only content length/hash metadata instead of full chapter text; explicit tag-review `anchor_ids` are bounded to avoid oversized SQLite parameter lists; and `npm --prefix frontend run verify` now includes `test:phase16`. The reference-anchor SQLite schema now quarantines legacy duplicate import identities and creates a unique expression index on `(visibility, COALESCE(novel_id, 0), source_path, source_kind, source_file_hash)` so duplicate inserts are rejected even if a second app process bypasses the in-process lock; create/import paths catch that constraint, re-read the existing anchor, and reuse or recover it. Frontend copyable diagnostics now also redact local paths and plain-string `source_text`, `candidate_text`, `prompt`, `chapter_text`, and `full_content` assignments. Processing history events now persist representative affected `material_id`, `source_segment_id`, and slot id when durable material output exists, including successful vector indexing, failed embedding, and failed rebuilds that retain prior searchable corpus; those ids can be used to open bounded material/source-segment detail instead of sending users to logs. Source-processing detail now exposes first-class, redacted attempt metadata: attempt count, current attempt, prior attempts, recovered-from attempt/build ids, and blocked reason. Attempt/build summaries are now persisted in `reference_anchor_processing_attempts`, processing events carry attempt/build references, and schema upgrade backfills older event-only histories without changing visible material provenance; explicit recovery from `failed_import`, `failed_segmenting`, `failed_extraction`, and `failed_slotting` now has integration assertions for current attempt, prior failed attempt, recovered-from ids, and blocked reason. Startup recovery from a recoverable pre-embedding state now also covers the missing-source edge case: initialization completes, the anchor becomes `failed_import`, old material ids/archive state/user corrections remain searchable or hidden according to active/archive state, and diagnostics stay redacted. Startup recovery from slotted pre-embedding states (`detecting_slots`, `slots_detected`, and `stale`) now covers active-vector indexing after restart: initialization completes to `ready`, active materials receive vectors, archived materials stay hidden, slot records survive, material ids remain stable, and user corrections are preserved. Missing-source recovery from those same slotted states now preserves existing slot/vector counts and records affected slot ids in processing detail instead of losing drill-down context. Startup recovery from no-output early states also covers missing source: initialization completes, the anchor becomes `failed_import`, zero segment/material/slot/vector counts remain zero, no fake rows are created, and processing detail stays redacted with retry/rebuild available. Initial import retry failure now updates stale diagnostics while keeping zero segment/material fake output. Slot-detection failures after durable material extraction now persist `failed_slotting` instead of rolling back material rows: users can search/open bounded material detail, duplicate import returns the same failed anchor without silent recovery, and explicit rebuild can recover to `ready` while retaining material ids, prior failure history, and user-verified tag corrections. If a later rebuild fails during slot detection after a prior usable corpus existed, the service now restores the previous segments, materials, slots, archive markers, vector count, source hash, and user-verified corrections before recording `failed_slotting`. Segmenting and material-extraction failures now also persist first-class `failed_segmenting` and `failed_extraction` states with redacted diagnostics and processing attempts: initial segmenting failures create inspectable failed anchors with zero output, initial extraction failures preserve source-segment provenance, explicit rebuild recovers both states, failed segmenting retry failure updates diagnostics while keeping zero segment/material fake output, failed extraction retry failure updates diagnostics while preserving source-segment detail and zero fake material rows, and failed rebuilds preserve the prior searchable corpus, prior source hash/provenance, material ids, archive state, and user-verified corrections instead of replacing usable output with partial failed output. `GetReferenceSourceSegmentDetail` now provides a bounded, path-free drill-down for affected `source_segment_id` records, including failed-extraction events where no material row exists yet; bridge, frontend, and MAF entries use `anchor_id + segment_id` with runtime-injected `novel_id`.

**Latest recovery slice:** Explicit retry from `failed_extraction` now covers the missing-source edge case: the service degrades the anchor to `failed_import`, keeps zero fake material rows, preserves the durable source-segment rows, records an affected `source_segment_id`, and leaves `GetReferenceSourceSegmentDetail` available with redacted processing notes. Explicit retry from `failed_slotting` now covers the same missing-source downgrade while preserving material ids, archive state, user-verified corrections, active-only search behavior, affected material/source-segment ids, and read-only material detail. Explicit rebuild from embedding terminal states (`failed_embedding` and `cancelled`) now also covers missing-source downgrade to `failed_import`, preserving material ids, slot counts, affected material/source-segment/slot ids, archive state, user corrections, active-only search behavior, and material detail. Cancelled embedding explicit rebuild success now also covers the archived-slotted-material edge case: active-only search and vector indexing continue to ignore archived material rows, while processing records fall back to a retained archived material with slot detail so users can still open bounded material detail from affected ids.

**Historical hardening risk (superseded as an active release gate):** At the original Phase 16 checkpoint, the corpus processing retry/recovery matrix was the main open release concern: partial recovery, retry success/failure beyond the covered import/cancelled/segmenting/extraction/embedding/slotting slices, rebuild success/failure beyond the covered import/segmenting/extraction/embedding/slotting slices, and app restart/interrupted processing for diagnostics/searchable activation edge cases must be service-owned, with stable provenance, stable material ids where expected, preserved user-verified corrections, and no duplicate searchable materials. Startup recovery for no-output early recoverable stages, missing-source no-output early recoverable stages, recoverable pre-embedding and embedding states, missing-source failure from a recoverable pre-embedding state, missing-source failure from slotted pre-embedding states with affected slot ids, slotted pre-embedding active-vector indexing after restart, pre-embedding active-only default search activation, workspace-corpus interrupted embedding searchable activation, interrupted-embedding duplicate-import, in-process and SQLite-backed duplicate-import uniqueness, initial failed-import persistence before material output, failed-import retry success/failure, cancelled explicit rebuild success/failure, failed-segmenting/failed-extraction persistence and rebuild recovery, failed-segmenting retry failure with zero fake output, failed-extraction retry failure with source-segment detail, failed-extraction missing-source retry downgraded to `failed_import` with retained source-segment detail, failed-embedding retry success/failure, failed-slotting retry failure, failed-embedding rebuild rollback, failed-slotting rebuild rollback, durable `failed_slotting` material-output preservation/rebuild recovery, representative affected ids for material-producing processing events, persisted attempt metadata, bounded source-segment detail for non-material affected ids, and batch/library-pack per-source result reporting slices are implemented, but the full matrix is still required. Remaining recovery work should focus on broader retry/recovery permutations and restart/searchable activation cases not covered by the early/no-output, missing-source no-output, pre-embedding prior-output, missing-source prior-output, slotted pre-embedding missing-source/vector-activation, and embedding slices rather than re-solving duplicate identity, attempt-history persistence, affected segment inspectability, or workspace-corpus embedding activation. The server-owned tag-review queue is no longer a release blocker: backend, frontend, mock bridge, browser workflow, inaccessible-id rollback coverage, and low-confidence regression coverage are in place. The dormant activity-level chapter-reference code is now gated behind `VITE_REFERENCE_ACTIVITY_CHAPTER_DEBUG=true`; removing it is desirable cleanup, but it is no longer a default-UX blocker while the flag defaults off and browser guardrails prove the default corpus activity cannot reach it. This paragraph remains a historical risk register, not a second current release criterion; follow the corpus-driven-writing M9 ledger for active scope.

## Design Review Status

Phase 16 partially answers the three product-quality goals, with the remaining release risk now concentrated in explicitly listed recovery and durability gaps:

- **High quality, robust, and auditable:** the IA split, bounded material/source-processing detail helpers, persisted attempt metadata, `ReferencePayloadSanitizer`, source-path/full-text negative assertions, server-owned tag-review queue, SQLite-backed duplicate-import uniqueness, and Monaco-buffer-only candidate insertion preserve provenance and safety boundaries. Remaining robustness risk is concentrated in retry/recovery and restart behavior; conflict review must wait for a persisted analyzer conflict signal instead of being inferred from transient UI state.
- **Convenient and easy to operate:** users can inspect processed material detail, source-segment detail, and processing-record detail from visible row actions instead of advanced/debug surfaces, including retained material detail for a source that failed after material extraction. Processing detail now shows current/prior attempts and blocked reasons without requiring users to infer recovery state from raw event history. The corpus page owns import, processing, material browse, tag correction, archive/restore, and style profile management, while the chapter drawer owns chapter-scoped recommendation, orchestration, audit, and explicit insertion.
- **High AI automation with minimal manual intervention:** explicit corpus imports should continue through parse, segment, extract, tag, index, and diagnostics without manual splitting or anchor setup. Manual work is narrowed to focused review queues, failed/partial processing recovery, visibility/trust decisions, archive/restore, high-risk chapter approvals, and final insertion.

Remaining risk and acceptance focus:

- Retry/recovery/app-restart coverage must continue proving idempotency, stable provenance, preserved verified corrections, and no duplicate searchable materials beyond the duplicate-import uniqueness slice.
- `GetReferenceMaterialTagReviewQueue` frontend and mock workflows now prove queue results are server-owned, paginated, cross-page, and not reconstructed from the visible material result page.
- Tag-review queue coverage should assert unverified, low-confidence, and unknown materials now; conflict entries are accepted only after analyzer output persists a conflict signal.
- Material, source-segment, and processing-detail views must stay bounded, path-free, prompt-free, candidate-free, and full-text-free across UI, bridge, and MAF exits.
- Candidate use is accepted only when copy/insert/append/replace touches the active Monaco buffer with undo support and no chapter-reference `SaveContent`.

## Problem Statement

The legacy `ReferenceAnchorView` mixed two different product domains in one page:

- **Shared corpus-library processing:** importing reference novels/materials, building anchor/material indexes, browsing processed materials, correcting tags, archiving/restoring materials, and building style profiles.
- **Current-chapter reference use:** using the shared corpus to support a specific chapter through chapter goals, fact boundaries, orchestration runs, blueprint review, material binding, candidate generation, and draft audit.

That made the product feel unusable because a user who wanted to process a shared material library was confronted with current-chapter controls, and a user writing a chapter had to leave the chapter editor and operate a separate reference-debug surface. The first IA correction is now in place; remaining work is hardening, workflow completion, and retiring or gating legacy code paths so the mixed workbench does not return as the default surface.

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

Manual intervention should be focused into service-owned review queues:

- unverified, low-confidence, or unknown material tags;
- conflicting material tags after analyzer output persists a durable conflict signal;
- failed or partially recovered source processing;
- visibility/license/source-trust promotion decisions;
- explicit archive/restore and destructive-adjacent administration;
- chapter-use approvals, high-risk recoveries, and final candidate insertion.

The chapter reference panel should also prefer automation: when opened from a valid chapter, it should derive the chapter context, suggest relevant accessible corpus materials, use the shared corpus by default, and make the common orchestration path one primary action. Manual source restriction, manual blueprint controls, and beat-to-material binding remain available only as advanced controls.

If no accessible corpus material exists, the chapter panel must show a corpus-import call to action and a recoverable empty state instead of a dead-end orchestration surface. After import/processing succeeds, returning to the chapter panel should use the newly searchable corpus without requiring advanced setup.

### Service-Owned Processing and Recovery Model

Corpus processing must be modeled as a service-owned workflow, not as a frontend interpretation of visible rows. The UI may display progress and recommended actions, but source identity, idempotency, retry eligibility, and recovery decisions belong in the backend.

- Use a stable import identity for duplicate detection: corpus visibility, stored novel/workspace scope, canonical source path, source kind, and source file hash. Re-importing the same identity must return or recover the existing anchor rather than creating a second searchable source.
- Persist enough attempt/build metadata to explain current state and history: `anchor_id`, source hash, build/version id where available, stage, status, attempt count, started/updated timestamps, redacted last error, recovered-from attempt/build id where available, and affected source/material/segment/slot/vector counts.
- Treat durable stages as explicit commit boundaries: source import, segmentation, material extraction, tag/confidence extraction, slot detection, vector/index provisioning, diagnostics summary, and searchable activation.
- On app startup or duplicate import, reconcile recoverable intermediate states in the service. Frontend code must not infer recovery from the currently visible source list, material page, or tag-review page.
- Failed or user-cancelled terminal states require an explicit retry/rebuild action; duplicate import must not silently override them.
- Keep processing recovery queue and material tag-review queue separate. High-confidence materials become searchable automatically; unverified/low-confidence/unknown tags enter the tag queue; failed, cancelled, blocked, or partially recovered processing enters the recovery path.
- Recovery must preserve source hash, source segment ids, material ids where the material hash is unchanged, archive state, user-verified corrections, and processing history. It must not duplicate searchable material rows.
- Rebuild must have rollback-friendly semantics: a failed rebuild must leave the prior usable corpus inspectable unless the source had no successful prior output. Successful rebuild may reuse, retire, or archive old material rows only according to explicit service rules shown in processing/material detail.
- Processing detail must expose the current attempt, prior attempts, recovered attempts, blocked reason, retry/rebuild availability, affected ids, and redacted copyable diagnostics so users can resolve failures without developer tools.

Required recovery matrix:

| Case | Required service behavior |
| --- | --- |
| Duplicate import of a ready source | Return the existing anchor and preserve metadata/material state. |
| Duplicate import of a recoverable intermediate source | Resume or rebuild the same anchor; do not create duplicate materials. The interrupted-embedding case is implemented. |
| Concurrent duplicate imports | Implemented for in-process service instances and SQLite-backed duplicate insert rejection for the persisted source identity; continue to verify as the retry/recovery matrix expands. |
| Failure before durable material output | Implemented for initial source read failures: create a real `failed_import` anchor/build/event with zero output counts and no fake material rows; duplicate import returns the terminal failure, and explicit rebuild recovers after the source is fixed. |
| Failure after partial material output | Preserve inspectable partial output, mark it non-final or recoverable as appropriate, and avoid duplicate searchable rows on retry. |
| Retry success | Keep old failure context inspectable, update current status, preserve provenance/user corrections, and activate one deduplicated searchable result set. |
| Retry failure | Update current failure context without deleting prior usable corpus or verified corrections. |
| Rebuild success/failure | Preserve prior usable corpus on failure; on success, explain reused/retired/archived materials by material/build detail. Failed rebuild rollback is covered for import, segmenting, extraction, slotting, and embedding paths. |
| App restart during each durable stage | Reconcile in the service to ready, failed, blocked, cancelled, or recoverable state with explicit retry/rebuild availability. Early no-output `created`, `importing`, `source_imported`, and `segmenting` states recover to ready/searchable output without fake rows. Recoverable pre-embedding stages are covered for prior-output recovery with material ids, archived-material hiding, active-only default search, and preserved user corrections. Interrupted embedding is covered for private anchors and workspace corpus, including default consuming-novel search activation, archived-material hiding, vector counts for active material, and preserved user corrections. |
| Batch or library-pack partial failure | Implemented for `CreateReferenceAnchorsWithResult`: successful sources import independently, failed entries return redacted diagnostics and path-free stable `source_identity`, failed initial imports persist inspectable `failed_import` anchors, and explicit rebuild recovers restored failed sources without duplicating successful anchors. |

Status/action matrix:

| Status group | Searchable? | Queue destination | Default user action | Detail requirement |
| --- | --- | --- | --- | --- |
| `ready` with high-confidence materials | Yes | None by default | Browse/search material, optionally correct tags | Show material/source counts, source hash, build version, and successful attempt event. |
| `ready` with unverified/low-confidence/unknown tags | Yes | Tag-review queue | Correct tags when convenient | Keep material searchable and show why each item is in the tag queue. |
| Recoverable intermediate (`created`, `importing`, `source_imported`, `segmenting`, `segments_built`, `extracting_materials`, `materials_extracted`, `detecting_slots`, `slots_detected`, `embedding`, `stale`) | Only prior successful output, if any | Processing recovery path | Service-owned resume/rebuild; duplicate import may recover | Show stuck stage, prior usable counts, affected ids, and retry/rebuild availability. |
| `failed_*` after prior usable output | Prior usable output remains searchable unless explicitly archived/deleted | Processing recovery path | Explicit retry/rebuild | Show redacted current diagnostic, retained material/segment/vector counts, historical failure events, and links to affected output. |
| `failed_*` before material output | No fake material rows | Processing recovery path | Explicit retry/rebuild after fixing cause | Show zero output counts, stable source identity if persisted, and copyable redacted diagnostics. |
| `cancelled` | Prior usable output remains searchable if it existed | Processing recovery path | Explicit retry/rebuild only | Show cancellation as terminal for duplicate import and keep history visible. |
| `blocked` or future policy hold | Prior usable output remains searchable if policy allows | Processing recovery path, not tag queue | Resolve listed blocker | Show blocker reason, safe actions, and why automation stopped. |

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
- Local source paths are allowed only as explicit import inputs or internal rebuild state. Bridge/UI/agent responses for already imported sources must be path-free; if a compatibility `source_path` field remains, it must be empty or otherwise redacted.
- Material browse/search and tag-update bridge/UI/agent responses must return summary payloads with bounded `text_preview`/`text_truncated`; full material `text` is service-internal and must only be used behind explicit adaptation/audit/detail boundaries.
- Preserve owner scope, visibility, license status, source trust, material ids, source hashes, and audit provenance when moving UI components.
- Preserve advanced blueprint/material-binding/debug workflows, but move them behind an explicit advanced mode in the chapter-use context.
- Preserve the existing final-insertion stop: orchestration resume must not auto-approve final insertion.
- Agent tools must not gain corpus import, arbitrary file-read, corpus delete/rebuild, or final-insertion approval capabilities as part of this UI correction.
- Reference materials must remain in dedicated corpus/reference search. Do not mix them into ordinary workspace full-text search results.
- Existing `goink-master` remains a read-only behavior reference only; do not reintroduce Go/Wails/Python paths.

Surface/API allowlist:

| Surface | Allowed capabilities | Forbidden capabilities and fields |
| --- | --- | --- |
| Corpus library UI | Explicit import, source list/detail, processing detail, retry/rebuild, metadata edit, promote, material browse/detail, tag correction, archive/restore, style profiles. | Chapter orchestration, blueprint generation, candidate generation, candidate insertion, `SaveContent`. |
| Chapter reference drawer | Chapter context, bounded recommendations, orchestration run start/resume/cancel, blueprint review, material-link review, audited candidate preview, explicit editor-buffer copy/insert/append/replace. | Corpus import, source file read, material tag update, source metadata update, archive/restore, rebuild/delete/promote, backend chapter-file writes. |
| Bridge handlers | UI-needed corpus/chapter methods with scoped DTOs, owner/visibility checks, bounded previews, redacted diagnostics, and route tests. | Unscoped file/path reads, full source text in summary payloads, prompt/candidate leaks, insertion methods that mutate chapter files. |
| MAF/agent tools | Read-only bounded corpus/search/detail/audit outputs and chapter orchestration controls that cannot approve final insertion. | Corpus import, delete/rebuild/archive/restore/promote, arbitrary file access, local paths, full source text, prompts, candidate text, final insertion approval. |
| Mock/browser workflows | Contract-shaped responses and guardrail assertions for default UX. | Reconstructing server-owned queues from visible pages, silently enabling debug chapter controls, or masking missing backend contracts. |

Forbidden response fields/content across UI, bridge, MAF, diagnostics, and mocks: `source_path` after import, `source_text`, `candidate_text`, `prompt`, full source files, full chapter content, absolute local paths, UNC paths, usernames, environment variable values, tokens, API keys, and raw exception details. Compatibility fields that cannot be removed yet must be empty or redacted.

Import/library-pack inputs must continue through SafePath and validation rules: canonicalize paths before identity comparison, reject or downgrade unsafe symlinks/UNC/file-URI inputs, bound file size and manifest entry count, handle duplicate manifest entries idempotently, and treat hash collisions or malicious manifest fields as blocked inputs with redacted diagnostics.

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
   - After successful import, show a clear completion state without advanced mode: source ready/searchable state, searchable material count, tag-review count, warning/failure count, and the next recommended action.
   - Make import/retry/rebuild idempotent and resumable across app restart, preserving provenance and user corrections while preventing duplicate searchable materials.
   - Display source rows with title, author, owner scope, license status, visibility, source trust, tags, build state, material counts, and last processed time.
   - Open a read-only source detail/processing detail entry from every source row. The detail must show anchor id, source hash, owner scope, visibility, license/source trust, current build attempt, material/segment/slot/vector counts, last processed time, retry/rebuild/archive/promote availability, links to processing detail, and a filter into that source's processed materials. It must not display local paths, full source text, prompts, candidate text, or full chapter text.
   - Support source metadata edit, explicit promotion to workspace corpus, rebuild, delete/archive, and retry failed processing.

2. **处理后语料**
   - Browse processed materials across accessible corpus sources.
   - Filter by source, owner scope, archive state, material type, function tag, emotion tag, scene tag, POV tag, technique tag, narrative duty, prose duty, source trust, license, and keyword.
   - Show material preview, material id, source segment id, source hash, score components when available, tag confidence/user-verified state, and archive state.
   - Open a read-only material detail drawer for every material row through a visible row action, not an advanced/debug-only control. The detail must show provenance, tags/confidence, verification state, score components, extractor/build metadata, bounded source/material preview, detected slots/placeholders, source segment metadata, archive state, and processing notes where available.
   - Keep list and detail previews bounded. The UI must never expose full source files, local source paths, prompts, candidate text, or full chapter text through material detail.
   - Material/source previews must use named length limits, expose `text_truncated`, and have contract/browser tests that lock max preview length and forbidden-field redaction.
   - Support current-page and cross-page selected material tag correction.
   - Support archive/restore of materials without deleting provenance.

3. **标签校正**
   - A focused, server-owned review queue for unverified, low-confidence, or unknown tags, backed by `GetReferenceMaterialTagReviewQueue`.
   - The queue must be paginated, cross-page, and independent from the currently visible processed-material result page.
   - Conflicting tags should enter the queue only after analyzer output persists a durable conflict signal; do not infer conflict state from transient frontend comparisons.
   - High-confidence extracted tags should not block normal corpus search or chapter use; they remain inspectable in material detail and can still be corrected later.
   - Batch correction with transactional rollback on inaccessible material ids.
   - Review notes and correction origin should be captured where existing payloads support it.

4. **风格画像**
   - Build, inspect, compare, archive, and restore reference style profiles from selected sources or style samples.
   - Show feature coverage, evidence counts, analyzer source, aggregate confidence, and active/archive status.
   - Make clear that style profiles become optional inputs for chapter use; they do not bypass blueprint approval or source-leak audit.

5. **处理记录**
   - Show source build/rebuild status, stage history, material/segment/slot/vector counts, failures, warnings, recovered processing state, and copyable diagnostics.
   - Provide a per-source processing detail view from each source row so users can inspect processing details without switching to advanced mode or reading logs. It must show which stages succeeded, which stages produced partial output, and which material/detail records are affected by a warning or failure.
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
   - Read-only bounded material preview with provenance, tags, and fit explanation; recommendation cards must not render full material text, full source text, local source paths, prompts, candidate text, or full chapter text.
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
| Material tag correction | `ReferenceAnchorView.tsx` | `GetReferenceMaterialTagReviewQueue`, `saveMaterialTags()`, `saveBulkMaterialTags()`, `saveBulkLibraryMaterialTags()` | Corpus library, `标签校正`; queue is server-owned and cross-page |
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
- `GetReferenceMaterialTagReviewQueue` is implemented on the backend as the server-owned, paginated, cross-page eligibility source for `标签校正`. It currently covers unverified, low-confidence, and unknown material tags. Conflict eligibility should be added only after the analyzer persists a durable conflict signal.
- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs` already contains the chapter-use payloads for blueprint, review, material binding, draft candidates, orchestration runs, decisions, and audits.
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs` already merges current-novel private anchors with visible workspace-corpus anchors for anchor listing/search, and existing tests cover workspace-corpus visibility without leaking other novels' private anchors.
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs` already supports chapter-number filtering for blueprints and orchestration runs.
- `GenerateDraftFromBlueprintAsync` returns draft candidates and must not mutate chapter content.
- The final insertion resume path is intentionally rejected by the orchestration service and must remain rejected after the UI move.

Required contract decisions and gaps to close during implementation:

- Processed-material detail: `GetReferenceMaterialDetail` is the preferred read-only detail path for material rows. Keep this contract summary-oriented: bounded material/source previews, source segment metadata, detected slots/placeholders, tag confidence/user verification, score components, extractor/build metadata, archive state, and processing notes are allowed; full source text, local source paths, prompts, candidate text, and full chapter content are not allowed.
- Source-segment detail: `GetReferenceSourceSegmentDetail` is the preferred read-only detail path for `affected_segment_id` values when no material row exists yet, especially `failed_extraction`. It takes `novel_id + anchor_id + segment_id`, enforces the same private/workspace corpus visibility rules as material detail, and returns only source summary, segment metadata, bounded `text_preview`, `text_truncated`, text hash, and redacted processing notes. It must not return `source_path`, raw `text`, `source_text`, prompts, candidate text, local paths, full source text, or full chapter content.
- Processing diagnostics: `GetReferenceSourceProcessingDetail` is the preferred read-only source-processing detail path. It must remain usable from source rows without advanced/debug mode and must show stage status, affected source/material/segment/slot ids, counts, warnings, failures, retry/rebuild availability, and redacted copyable diagnostics.
- Tag-review queue: `GetReferenceMaterialTagReviewQueue` must remain the source of truth for queue eligibility, pagination, counts, and cross-page review. Frontend code must not rebuild the queue from the currently visible `SearchReferenceMaterials` result page. The current backend queue covers unverified, low-confidence, and unknown materials; conflict review requires a persisted analyzer signal before being advertised or asserted.
- Summary-only corpus responses: `SearchReferenceMaterials`, material browse/list payloads, recommendation cards, and tag-update responses must keep returning `MaterialSummary`-style payloads with bounded `text_preview`/`text_truncated`. Full material `text` remains service-internal except behind explicit, bounded detail/adaptation/audit boundaries.
- Processing idempotency and resume: if existing anchor/build status data cannot distinguish new import, duplicate import, retry, rebuild, partial failure, recovered output, and app-restart recovery states, extend the read-only status/diagnostic payloads before relying on frontend-only interpretation. Backend service rules, not frontend filtering, must prevent duplicate searchable materials and preserve provenance/user corrections across retry/rebuild.
- Chapter material-link recovery: `BindReferenceBlueprintMaterials` returns links, but there is no standalone `GetReferenceBlueprintMaterialLinks`. Add it only if the chapter drawer must restore selected links after reload without rebinding.
- Manual material selection: `BindReferenceBlueprintMaterialsPayload` currently has `select_top_candidate`. If the drawer needs explicit beat-to-material selection, add `SelectReferenceBlueprintMaterialLinks` or extend the binding payload; do not encode chapter selection by changing corpus tags.
- Chapter-use overview: if the drawer needs one efficient bootstrap call, add read-only `GetReferenceChapterUsage(novel_id, chapter_number)` that aggregates blueprints, runs, selected links, candidate/audit summaries, and search-policy summaries without returning `source_text`, `candidate_text`, prompts, local paths, or full chapter text.
- Do not add `insert_reference_anchored_draft` or any bridge method that writes candidate prose into chapter files. Candidate use is a frontend editor-buffer action plus normal save behavior.

Target frontend component split:

Current acceptable incremental state: corpus tabs still live inside `ReferenceAnchorView`, and the chapter toolbar trigger lives inside `ContentPanel`. That is acceptable while the Phase 16 behavior is being hardened. The target split below remains useful as a follow-up refactor once the browser and backend gates are stable:

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
- `GetReferenceMaterialTagReviewQueue`
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

Implemented read-only helpers and queues:

- `GetReferenceMaterialDetail`: implemented for corpus rows, chapter recommendation cards, and MAF inspection. It must remain read-only, summary-oriented, bounded, and path-free.
- `GetReferenceSourceSegmentDetail`: implemented for processing events with an affected source segment, including extraction failures with no material row. It remains read-only and returns a bounded segment preview plus source summary and redacted processing notes; bridge/frontend/MAF coverage blocks `source_path`, raw text, `source_text`, prompt, candidate text, and arbitrary file access.
- `GetReferenceSourceProcessingDetail`: implemented for source rows/processing records and MAF inspection. It remains read-only and returns redacted diagnostics, stage history, counts, retry/rebuild availability, representative affected ids, attempt count, current/prior attempts, recovered-from build/attempt ids, and blocked reason.
- `GetReferenceMaterialTagReviewQueue`: implemented end to end for cross-page/paginated tag-review eligibility. Frontend mainline integration, mock bridge coverage, browser guardrails, inaccessible-id rollback coverage, and low-confidence-after-verification regression coverage are in place. Keep the queue scoped to unverified, low-confidence, and unknown tags until conflict analysis is persisted.
- Agent-facing material/source-segment/source-processing inspection tools wrap those helpers with runtime-injected `novel_id`, id-only parameters, and negative assertions for `source_path`, arbitrary file paths, full source text, prompt, candidate text, corpus import, rebuild, delete/archive, and final insertion controls.
- `AdaptReferenceMaterial` still exists as a legacy/debug bridge and MAF tool, but bridge/MAF exits must return only a bounded/redacted text preview and audit. Full material text remains service-internal and must not be recoverable through adaptation with empty or no-op slots.

Optional new bridge helpers should be added only where existing payloads cannot support the required UX:

- `GetReferenceCorpusOverview`: aggregate source/material/build counts for the corpus page header.
- `GetReferenceSourceMaterials`: thin wrapper over `SearchReferenceMaterials` for a single source if the UI would otherwise duplicate complex filter payload construction.
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
- [ ] Unverified, low-confidence, unknown tags and failed/partial processing outputs are routed into visible review queues instead of requiring users to discover them manually; conflicting tags join only after analyzer output persists a conflict signal.
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

### Task 2A: Implement Corpus Processing Retry, Recovery, and Restart Matrix

**Description:** Make corpus processing durable, idempotent, and recoverable across duplicate import, partial failure, retry, rebuild, batch import, and app restart. This is a release blocker because it determines whether automatic corpus processing is safe enough to require little manual intervention.

**Acceptance criteria:**

- [ ] Duplicate import of the same source identity returns or recovers the existing anchor; it never creates duplicate searchable material rows.
- [ ] Duplicate import of an anchor stuck in a recoverable intermediate stage resumes or rebuilds through the service and returns the recovered anchor state. Duplicate import recovery is covered for pre-embedding `segments_built`, `extracting_materials`, `materials_extracted`, `detecting_slots`, `slots_detected`, and `stale` states, plus interrupted embedding after restart.
- [ ] Duplicate import of failed or user-cancelled terminal states returns the existing terminal state and does not silently rebuild; explicit retry/rebuild remains required.
- [ ] Concurrent duplicate imports are serialized by service-owned identity and result in one anchor/build output. In-process service-instance concurrency and SQLite-backed duplicate insert rejection are currently covered.
- [ ] Cross-process duplicate import is either covered by a durable database uniqueness/lock strategy, or explicitly out of scope because the desktop host enforces a single active process.
- [ ] Retry and rebuild preserve source provenance, source hash, source segment ids, material ids where the material hash is unchanged, archive state, user-verified tag corrections, and historical failure context. Cancelled embedding explicit rebuild success/failure is covered for preserved material ids, archived-material hiding, user corrections, affected ids, searchable active materials, and archived slotted-material detail drill-down from processing records.
- [ ] Retry success updates current status and searchable material/vector counts without deleting prior diagnostics. Failed import after source restore, cancelled embedding explicit rebuild, and failed embedding explicit rebuild success are currently covered.
- [ ] Retry failure updates current failure context without losing prior usable corpus output or verified corrections. Failed import, failed segmenting, failed extraction, failed embedding, and failed slotting retry failure slices are currently covered; initial failed import and failed segmenting specifically keep zero fake segment/material rows, and failed extraction preserves source-segment detail and zero fake material rows when no material output exists yet.
- [ ] Missing-source retry from `failed_extraction` degrades to `failed_import` without losing durable source-segment detail or creating fake material rows. Covered by `RebuildAnchorFailedExtractionMissingSourceMarksFailedImportAndPreservesSourceSegmentDetail`.
- [ ] Missing-source retry from `failed_slotting` degrades to `failed_import` without losing material detail, archive state, user corrections, active-only search behavior, or affected material/source-segment ids. Covered by `RebuildAnchorFailedSlottingMissingSourceMarksFailedImportAndPreservesMaterialDetail`.
- [ ] Missing-source rebuild from embedding terminal states (`failed_embedding` and `cancelled`) degrades to `failed_import` without losing material detail, slot ids, archive state, user corrections, or active-only search behavior. Covered by `RebuildAnchorEmbeddingTerminalMissingSourceMarksFailedImportAndPreservesMaterialDetail`.
- [ ] Failed rebuild leaves the previous usable corpus searchable and inspectable unless the anchor had no prior successful output. Failed import rebuild now retains prior output counts/diagnostics, material ids, archive state, user corrections, active-only search behavior, and affected material/source-segment detail; failed segmenting, extraction, slotting, and embedding rebuild rollback slices are currently covered.
- [ ] App restart reconciliation covers import/source row, segmentation, material extraction, tag/confidence extraction, slot detection, vector/index provisioning, diagnostics summary, and searchable activation stages. Startup recovery for no-output early recoverable stages, recoverable pre-embedding stages, pre-embedding active-only default search activation, an interrupted embedding stage, and workspace-corpus interrupted embedding searchable activation is currently covered.
- [x] Batch import and library-pack manifest import report per-source success/failure, keep successful sources usable, and make failed entries retryable by stable source identity. Initial source read failures now persist `failed_import` anchors with inspectable processing detail and zero fake output rows.
- [ ] Recovery states are visible in `处理记录` detail with current attempt, historical attempts, recovered-from attempt/build id where available, affected ids, retry/rebuild availability, blocked reason, and redacted copyable diagnostics. Explicit recovery from failed import, failed segmenting, failed extraction, failed slotting, and failed embedding now has focused backend assertions for current attempt, prior attempts, recovered-from ids, or blocked reason metadata. Browser coverage now exercises failed-import, failed-extraction, failed-slotting, and recovered app-restart detail paths, including copyable diagnostics, explicit rebuild, recovered attempt/history state, bounded material/source-segment drill-downs, and no duplicate searchable material rows.
- [ ] Frontend code never derives retry/recovery eligibility from the currently visible source/material/tag-review page; it displays service-owned status and actions.

**Verification:**

- [ ] Integration tests cover duplicate import ready, duplicate import recoverable, interrupted embedding after service restart, user-verified correction preservation, retry success, retry failure, rebuild success, rebuild failure, batch/manifest partial failure, and app restart at durable stages. Batch/manifest per-source partial failure is now covered by `CreateAnchorsWithResultReportsPerSourceFailuresAndKeepsSuccessfulImportsUsable`; duplicate-import pre-embedding recovery is covered by `CreateAnchorRecoversPreEmbeddingStageForDuplicateImportWithoutDuplicateMaterials`; failed-import rebuild preservation/detail coverage is handled by `RebuildAnchorRecordsFailedImportStatusWithRedactedError`; failed-import recovered attempt metadata is covered by `RebuildAnchorRecoversFailedImportAfterSourceRestoredAndKeepsHistory`; failed-import retry failure is covered by `RebuildAnchorInitialFailedImportRetryFailureUpdatesDiagnosticWithoutFakeOutput`; cancelled rebuild success, including archived slotted-material affected-id fallback, is covered by `RebuildAnchorRecoversCancelledEmbeddingAndKeepsHistory`; cancelled rebuild failure is covered by `RebuildAnchorCancelledEmbeddingFailureUpdatesDiagnosticAndPreservesMaterials`; failed-segmenting recovered attempt metadata is covered by `CreateAnchorFailedSegmentingPersistsTerminalFailureAndExplicitRebuildRecovers`; failed-segmenting retry failure is covered by `RebuildAnchorFailedSegmentingRetryFailureUpdatesDiagnosticWithoutFakeOutput`; failed-extraction recovered attempt metadata is covered by `CreateAnchorFailedExtractionPreservesSegmentsAndExplicitRebuildRecovers`; failed-extraction retry failure is covered by `RebuildAnchorFailedExtractionRetryFailureUpdatesDiagnosticAndPreservesSourceSegments`; failed-slotting recovered attempt metadata is covered by `CreateAnchorFailedSlottingPreservesMaterialDetailAndExplicitRebuildRecovers`; failed-slotting retry failure is covered by `RebuildAnchorFailedSlottingRetryFailureUpdatesDiagnosticAndPreservesMaterials`; failed-segmenting/failed-extraction rebuild rollback including archive-state preservation is covered by `RebuildAnchorSegmentingOrExtractionFailurePreservesPreviousSearchableCorpus`; failed-slotting rebuild rollback is covered by `RebuildAnchorSlottingFailurePreservesPreviousSearchableCorpus`; no-output early startup recovery is covered by `StartupInitializationRecoversEarlyRecoverableStageWithoutPriorOutput`; no-output early startup recovery with a missing source is covered by `StartupInitializationMarksMissingSourceEarlyRecoverableStageFailedImportWithoutFakeOutput`; pre-embedding restart/search activation is covered by `StartupInitializationRecoversRecoverablePreEmbeddingStageWithoutDuplicateMaterials`; slotted pre-embedding startup vector activation is covered by `StartupInitializationRecoversSlottedPreEmbeddingStageAndBuildsActiveVectors`; slotted pre-embedding missing-source startup failure with retained affected slot ids is covered by `StartupInitializationMarksMissingSourceSlottedStageFailedImportAndPreservesAffectedSlotDetail`; startup recovery when the source disappeared during a recoverable pre-embedding state is covered by `StartupInitializationMarksMissingSourceRecoverableStageFailedImportAndPreservesPriorCorpus`; workspace-corpus interrupted embedding restart/search activation is covered by `StartupInitializationRecoversWorkspaceCorpusEmbeddingStageAndKeepsSearchableState`.
- [ ] `RebuildAnchorFailedExtractionMissingSourceMarksFailedImportAndPreservesSourceSegmentDetail`
- [ ] `RebuildAnchorFailedSlottingMissingSourceMarksFailedImportAndPreservesMaterialDetail`
- [ ] `RebuildAnchorEmbeddingTerminalMissingSourceMarksFailedImportAndPreservesMaterialDetail`
- [ ] Browser coverage exercises duplicate import, failed/partial processing detail, retry, rebuild, and recovered processing state without exposing local paths or full source text. Current browser slices cover duplicate batch-source resubmission without duplicate ready/failed rows, partial batch failure diagnostics with persisted failed source detail, ready processing detail with material/source-segment drill-down, failed-import processing detail, explicit rebuild recovery, explicit rebuild retry-failure diagnostic refresh, recovered app-restart/interrupted-embedding processing detail with affected material/source-segment links and no duplicate searchable material row, slots-detected startup recovery with recovered-from ids, redacted copied diagnostics, and one searchable indexed material row, missing-source startup recovery degraded to failed import with retained material detail, bounded source-segment detail, and explicit rebuild to one searchable retained material row, failed-extraction processing detail with bounded source-segment drill-down plus explicit rebuild to one searchable recovered material row, and failed-slotting processing detail with retained material detail plus explicit rebuild to one searchable retained material row; broader restart/recovery browser paths still need coverage.
- [ ] `dotnet test Novelist.slnx --no-restore -v minimal --filter "FullyQualifiedName~ReferenceAnchorServiceTests"`
- [ ] `npm --prefix frontend run test:corpus-library`
- [ ] `git diff --check`

**Dependencies:** Task 2 service contracts and existing material/detail guardrails.

**Estimated scope:** L.

### Task 3: Create Corpus Material Review Tabs

**Description:** Add explicit tabs for processed material browsing, material detail inspection, and tag-review queues so users can inspect each processed corpus output.

**Acceptance criteria:**

- [ ] Users can browse processed materials across all accessible corpus sources without selecting a chapter.
- [ ] The default processed-material browse path calls `SearchReferenceMaterials` with `anchor_ids: []` unless the user explicitly filters to one or more sources.
- [ ] Users can filter active/archived materials and restore archived materials.
- [ ] Users can review unverified, low-confidence, or unknown materials through the server-owned queue and apply batch corrections transactionally.
- [ ] High-confidence materials stay searchable while remaining inspectable/correctable; unverified, low-confidence, or unknown tags appear in the tag-review queue with counts.
- [ ] Conflicting tags appear in the queue only after analyzer output persists a durable conflict signal; until then the UI must not claim conflict-review coverage.
- [ ] Material rows show source identity, material id, source segment id, source hash, material type, tags, confidence, score components where available, verification state, archive state, and bounded preview without overflowing compact layouts.
- [ ] Every material row can open a read-only detail view without requiring chapter context or advanced mode.
- [ ] Material detail shows material id, anchor id, source identity, owner scope, license/source trust, source segment id, source hash, material type, tags, confidence, user-verified state, score components, extractor/build metadata, detected slots/placeholders, archive state, bounded material/source preview, and processing notes where available.
- [ ] Material detail exposes lineage sufficient for audit: source hash, build/version id where available, segment id, material id, tag/confidence record, slot records, vector/index count where available, and processing attempt linkage.
- [ ] Material detail explains why the item is searchable or why it entered a review/recovery queue: unverified, low confidence, unknown tag, persisted conflict signal, archived state, failed processing linkage, or blocked recovery reason.
- [ ] Rebuilt material detail shows whether the material was reused from a stable material hash, newly created, retired, or archived, so users can understand old/new output after recovery.
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

**Current implementation checkpoint:** The material library now defaults to `素材来源`, exposes task tabs with ARIA tab semantics, has visible read-only material detail and source-processing detail drawers, supports redacted processing-diagnostic copy, uses bounded previews, and has source-path/full-text negative assertions. Processing-record affected ids can open material detail, filter the material library, or locate the source row. Material search/tag-update/list responses are summary-only (`text_preview`/`text_truncated`), and Bridge/MAF detail/status/adaptation/audit exits now run a secondary redaction and length-bound pass for JSON-shaped `source_text`/`candidate_text`/`prompt`/secret assignments, local paths, UNC paths, build-status diagnostics, anchor titles/authors/tags, adaptation text previews, reuse-audit arrays, and full-text sentinels. The server-owned `GetReferenceMaterialTagReviewQueue` now provides the cross-page/paginated queue source for unverified, low-confidence, and unknown tags; the `标签校正` tab can select and correct queue items without first running material search; frontend UI, mock workflow, browser guardrails, inaccessible-id transactional rollback, bounded-preview assertions, partial-import failure assertions, and low-confidence-after-verification regression coverage are in place. Conflict queueing remains future work until the analyzer persists a durable conflict signal.

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

**Current implementation checkpoint:** The chapter drawer opens from the chapter editor, derives chapter context, auto-suggests bounded corpus materials, and can open read-only material detail from recommendation cards. It can start/resume/cancel orchestration without selected anchors. The drawer no longer exposes direct `AdaptReferenceMaterial` candidate generation from recommendation cards and cannot save chapter content. Final-insertion remains a stop, but the stop now exposes audited candidate preview and explicit editor-buffer actions without adding a backend chapter-write path.

### Task 5: Move Default Orchestration into Chapter Reference Panel

**Description:** Rehost the default orchestration workflow inside the chapter panel, with simplified chapter-use controls and advanced controls hidden.

**Acceptance criteria:**

- [ ] Users can start a current-chapter orchestration run from chapter goal, known facts, forbidden facts, and optional style profile.
- [ ] Opening the panel from a valid chapter automatically prepares chapter context and can suggest relevant accessible materials without requiring manual anchor selection.
- [ ] Chapter recommendation cards can open read-only bounded material detail so the author can inspect provenance, source segment, confidence, score components, and processing notes before approving source/fact boundaries.
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

**Current state:** Implemented in the current working tree. `GetReferenceDraftCandidates` is a UI bridge helper on the anchored-draft service, scoped by `novel_id`, `blueprint_id`, and explicit `candidate_ids`; bridge output is redacted/bounded and the helper is not exposed as a MAF tool. The chapter drawer loads candidate previews and draft audits at the `approve_final_insertion` stop, disables insertion unless the candidate and audit passed, and offers copy, insert-at-cursor, append, and replace-selection actions. Editor insertion is done through Monaco edit operations in `ContentPanel`, participates in undo/redo, marks the tab dirty, and uses a deferred-save editor path so the chapter-reference workflow itself does not call `SaveContent`.

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
- [ ] Dormant legacy chapter-reference code is removed or gated behind an explicit developer/debug flag with browser coverage proving it is not reachable in the default corpus-library activity.

**Current state:** Default-surface retirement is functionally satisfied for the default runtime path: the activity-level corpus page renders corpus tabs only, the `高级` tab contains corpus-only guidance, and activity-level chapter-writing/debug controls remain compiled in `ReferenceAnchorView` but are unreachable unless `VITE_REFERENCE_ACTIVITY_CHAPTER_DEBUG=true`. Treat removal as follow-up cleanup; treat accidental default reachability, missing browser guardrails, or any production/dev default enabling of the flag as a Task 7 blocker. Default browser coverage must keep proving that switching through corpus tabs, including `高级`, does not reveal `参考写作检索`, `章节蓝图`, `生成蓝图`, orchestration controls, or candidate generation controls, and does not call chapter-use bridge methods.

**Verification:**

- [ ] Browser coverage proves the legacy reference-writing section is not reachable in the default corpus-library activity.
- [ ] `npm --prefix frontend run test:corpus-library`
- [ ] `npm --prefix frontend run test:chapter-reference`
- [ ] Mocked browser coverage proves corpus processing and chapter use can run independently in the same session.

**Dependencies:** Tasks 2-6.

**Estimated scope:** M.

### Task 8: Update Documentation, Help, Release Notes, and Agent Context

**Description:** Update user and developer documentation to reflect the new split.

**Acceptance criteria:**

- [x] README describes `素材库` as shared corpus processing and `章节参考素材` as current-chapter use.
- [x] Help dialog explains the two workflows separately.
- [x] Developer docs record component boundaries, bridge reuse, and guardrails.
- [x] Release notes summarize the user-facing IA change.

**Current implementation checkpoint:** README, in-app help, release notes, `schema-frontend.md`, and `schema-bridge-api.md` now describe the Phase 16 split: `素材库` owns shared corpus import/processing/material inspection/tag correction/archive/style-profile work, while chapter-level `参考素材` owns chapter context, recommendations, orchestration, audit, and explicit editor-buffer candidate insertion. The docs also record that tag-review queues are server-owned, conflict review waits for a persisted analyzer signal, source/material/source-segment detail helpers are bounded and path-free, and chapter reference use must not mutate corpus metadata or call `SaveContent`.

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
- [ ] Tag correction uses the server-owned `GetReferenceMaterialTagReviewQueue` path for unverified, low-confidence, and unknown materials instead of deriving the queue from the visible browse page.
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
npm --prefix frontend run test:phase16
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
- `test:chapter-reference`: chapter panel open/close, chapter context derivation/recovery, current-chapter run start/resume/cancel, final-insertion stop, bounded material recommendations, read-only material detail, audited candidate preview, copy, insert-at-cursor, append, replace-selection, undo, no direct material adaptation, and no implicit save.
- Existing `test:reference-anchor` may remain as a compatibility umbrella during migration, but its assertions must eventually reflect the separated IA. It must not be the only Phase 16 browser gate.
- `test:phase16` is the focused umbrella for the two Phase 16 browser gates; `test:reference-anchor` must not silently stand in for either focused Phase 16 gate.

Corpus-library browser coverage must include:

- Import one source, batch import sources, and import a library-pack manifest without starting orchestration, blueprint generation, material binding, draft generation, or `SaveContent`.
- Assert imported sources automatically progress through parse/segment/extract/tag/index status updates and become searchable without manual material creation.
- Import the same source twice and assert the UI/service reuses or reports the existing source instead of creating duplicate searchable materials.
- Assert high-confidence materials become searchable without manual review, while unverified, low-confidence, or unknown tags appear in the server-owned `标签校正` queue with visible counts.
- Assert `标签校正` queue pagination and counts come from `GetReferenceMaterialTagReviewQueue`, not from the currently visible `处理后语料` result page.
- Assert conflicting tags are not advertised as covered until analyzer output persists a conflict signal; after that signal exists, add conflict entries to the same server-owned queue contract.
- Inspect `处理记录` for stage history, material/segment/slot/vector counts, warnings, failures, recovered processing state, and retry/rebuild actions.
- Exercise partial failure, partial recovery, retry success, retry failure, rebuild, and app-restart/interrupted-processing recovery states; assert current status updates, old failure context remains inspectable, source/material/segment/slot ids remain stable where expected, user-verified corrections survive, duplicate searchable materials are not created, and affected ids can be linked or filtered when available.
- Assert copied diagnostics are redacted and do not contain absolute paths, usernames, environment variables, tokens, API keys, prompts, full source text, candidate text, or full chapter text.
- Browse `处理后语料` with no selected chapter and assert default `SearchReferenceMaterials` uses `anchor_ids: []`.
- Filter by source, owner scope, material type, active/archive state, tags, source trust, license, and keyword.
- Show owner scope so `novel` and `workspace_corpus` sources are visually distinct.
- Promote a private source to workspace corpus and keep the action explicit.
- Open a source detail and view its processed material rows with `material_id`, `anchor_id`, `source_segment_id`, `source_hash`, material type, tags, confidence, verification state, and bounded preview.
- Open a material detail drawer and assert provenance, source identity, source segment metadata, source hash, bounded preview, slots/placeholders, extractor/build metadata, score components, archive state, and processing notes are visible.
- Assert processed-material list cards render bounded previews and do not expose full material text, full source text, local source paths, prompts, candidate text, or full chapter text.
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
- Assert chapter recommendation cards render bounded previews and do not expose full material text, full source text, local source paths, prompts, candidate text, or full chapter text.
- Open read-only material detail from a chapter recommendation card and assert provenance, source segment preview, score components, processing notes, and owner scope are visible without exposing full material text, full source text, local paths, prompts, candidate text, or full chapter text.
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
- If `GetReferenceMaterialDetail`, `GetReferenceSourceSegmentDetail`, `GetReferenceSourceProcessingDetail`, `GetReferenceMaterialTagReviewQueue`, `GetReferenceBlueprintMaterialLinks`, `SelectReferenceBlueprintMaterialLinks`, `GetReferenceChapterUsage`, or other helper methods are added, add snake_case serialization tests, bridge routing tests, frontend API declarations, and negative assertions for `source_path`, raw `text`, `source_text`, `candidate_text`, `prompt`, local path, full source text, and full chapter content where the payload can carry previews or diagnostics.
- `GetReferenceMaterialTagReviewQueue` coverage must prove server-side pagination, cross-page eligibility, unverified/low-confidence/unknown inclusion, inaccessible-id transactional rollback for batch correction, and no dependency on the visible material browse page.
- `AdaptReferenceMaterial` bridge and MAF exits must keep returning only bounded/redacted previews and audit metadata. Negative tests must prove empty/no-op slots cannot recover full material text, paths, prompts, `source_text`, or `candidate_text`.
- Add or retain backend coverage proving duplicate import, retry, rebuild, and recovered/interrupted processing do not duplicate materials, lose provenance, expose archived materials unintentionally, or discard user-verified tag corrections.
- `ReferenceAnchorServiceTests.SearchMaterialsIncludesWorkspaceCorpusAnchorsWithoutLeakingOtherNovelPrivateAnchors`, `WorkspaceCorpusVisibilityFiltersAnchorsBeforeSearchAdaptAuditTagAndFeedback`, and `DeleteMaterialsArchivesSelectedMaterialsWithoutDeletingProvenance` must remain green.
- `ReferenceAnchoredDraftServiceTests.GenerateDraftFromBlueprintReturnsCandidatesWithoutMutatingChapterContent` and `ReferenceOrchestrationRunRejectsFinalInsertionResumeAndKeepsManualInsertionBoundary` must remain green.
- `WorkspaceUtilityServiceTests.SearchAllLeavesReferenceMaterialsInDedicatedSearch` must remain green so corpus material search does not leak into ordinary workspace search.
- `MafToolRegistryTests` must continue proving agent tools cannot import sources, read arbitrary local files, or approve final insertion.
- Agent read-only material/source-processing detail tools, if present, must inject `novel_id`, expose only id parameters, and carry negative assertions for `source_path`, `source_text`, `candidate_text`, `prompt`, arbitrary path fields, and write/approval actions.

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
| Conflict tag review is claimed before analyzer signals are persisted | False queue counts and misleading human-review workload | Keep current queue eligibility to unverified, low-confidence, and unknown tags; add conflict only after a durable analyzer conflict field exists |
| Workspace-corpus visibility is filtered in frontend instead of service rules | Private sources from another novel may leak or shared sources may disappear | Reuse service search/list rules and keep workspace/private visibility integration tests in the required matrix |
| Normal workspace search starts returning reference materials | Users see corpus snippets outside reference workflow | Keep reference materials in dedicated search and retain `SearchAllLeavesReferenceMaterialsInDedicatedSearch` regression coverage |

## Open Questions

- Resolved for the current implementation: the activity label is `素材库`.
- Resolved for the current implementation: the chapter reference panel lives inside `ContentPanel` so it follows the active chapter tab and can later use editor insertion APIs.
- Should a corpus source imported with `visibility = workspace` become immediately available to all novels? Existing behavior supports workspace corpus; the UI must make the visibility choice explicit during corpus import.
- What exact preview length should material detail use for bounded source/material context long-term? Current bridge/MAF exits enforce bounded previews; keep constants locked by contract/browser tests before changing them.
- Should the old advanced reference activity remain reachable for internal debugging? Recommended: only behind `高级` within the chapter panel or a developer/debug flag, not as the default user path.

## Definition of Done

Phase 16 is complete when:

- [ ] Shared corpus-library processing is a standalone, understandable product surface.
- [ ] Explicitly imported corpus sources are automatically processed into searchable materials, with only unverified, low-confidence, unknown, persisted-conflict, or failure cases routed to focused human review queues.
- [ ] Corpus processing is idempotent and resumable across duplicate import, retry, rebuild, and app restart without duplicating materials or losing provenance/user corrections.
- [ ] Processed corpus materials are directly browsable, inspectable in read-only detail, and correctable through dedicated tabs.
- [ ] Processing records let users inspect parse/segment/extract/tag/index/build outcomes, failures, warnings, affected output counts, retry/rebuild state, and redacted copyable diagnostics.
- [ ] AI tools can inspect bounded material detail and source processing detail by stable ids without importing sources, reading arbitrary files, exposing paths/full text/prompts/candidates, or mutating corpus/chapter state.
- [ ] Current-chapter reference use is embedded in the chapter editor.
- [ ] Chapter reference use consumes the shared corpus by default and can run without selecting anchors.
- [ ] Chapter reference use derives or explicitly confirms chapter context before generation/insertion and suggests relevant corpus materials without manual anchor setup.
- [ ] Candidate generation remains audited, provenance-preserving, and unable to auto-save chapter content.
- [ ] Candidate insertion is explicit, local to the editor buffer, undoable, and covered by tests.
- [ ] Existing backend reference-anchor, orchestration, style-profile, source-leak, and agent-boundary tests remain green.
- [ ] Documentation explains the split clearly enough that future agents do not reintroduce a mixed corpus/chapter workbench as the default UI.
