# Reference Anchor Tasks: Phase 15

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

## Phase 15: Goink-Master Feature Merge, Import Pipeline, Style Library, Narrative Pattern Extraction, Git History UI, and Product Robustness

**Status:** Open as of 2026-07-07. Task 1 is complete: Phase 15 payload contracts, compatibility method names, owned frontend adapter method declarations, representative serialization/front-back registry tests, long-running `task_id` coverage, and app/build update-check endpoint configuration are in place. Task 2 is complete for the storage and migration model: app-settings persistence, style-sample storage, narrative pattern run storage, import-run storage/state-machine behavior, and copy-first/additive migration preservation are implemented and covered. Task 3 is complete for the safe import boundary: the dedicated novel-import picker is registered in the Photino bridge, supported import extensions are filtered at picker level, `StartNovelImport` validates real readable local files, extension/kind matching, traversal/device/URI rejection, and default size limits before storing a run, and the bookshelf import UI rejects unsafe drag/drop payloads with visible feedback while routing accepted paths only to `StartNovelImport`. Task 4 is complete for deterministic TXT/MD parsing: strict encoding detection, conservative UTF-16 no-BOM heuristics, GB18030 fallback, robust chapter splitting, structured diagnostics, and large fixture coverage are implemented and tested. Task 5 is complete for EPUB parsing: container/OPF/spine traversal, safe internal path resolution, readable XHTML extraction, skipped-chapter diagnostics, no-readable-chapter failure, and zip-slip/absolute-path guardrails are implemented and tested. Task 6 is complete for the backend transaction-like import workflow: TXT/MD/EPUB imports create novels and chapters, defer per-chapter Git commits into one import commit, apply configured Git author identity, preserve data on final Git commit failure, coordinate `CancelNovelImport` with active task cancellation and cleanup, clean caught pre-completion metadata/write/cancel failures, normalize cross-platform import-run path separators, queue RAG stale state only after the indexing stage, and rely on Task 7 startup recovery to close process-death cleanup gaps. Task 7 is complete for startup import recovery and reconciliation: app initialization runs import recovery before normal workspace use, `ReconcileNovelImportRuns` is registered, `GetAppConfig.import_recovery` exposes startup results, safe partial workspaces are cleaned idempotently, pending RAG/reference build side effects for the recovered import novel are removed without touching other novels, corrupted cleanup paths are blocked with diagnostics, durable indexing/Git-commit interruptions are preserved as warnings, unknown/manual-review states are blocked instead of guessed, the simulated process-death matrix is covered, and the workspace shows copyable startup diagnostics for repaired/blocked runs. Task 8 is complete for import progress and frontend experience: backend imports emit typed `novel_import:progress` events with task ids, stages, counts, current-chapter metadata, warning/error/cleanup states, and no source-path exposure; the workspace-level import controller ignores stale task events, supports cancellation, keeps cleanup states visible, refreshes successful imports, opens the first imported chapter, and avoids selecting failed or cleaned-up phantom novels; mocked app workflow coverage exercises success, cancel, parser failure, write cleanup, Git warning, skipped EPUB chapters, and drag/drop rejection. Task 9 is complete for style sample storage and deterministic statistics: style samples can be created/updated/deleted/searched through bridge handlers, support global and per-novel scopes, normalize pasted tag delimiters, return paged summaries without full content, expose detail content only through `GetStyleSample`, compute schema-versioned v2 deterministic stats including word count, sentence distributions, standard deviation, quote and paragraph metrics, and automatically recalculate stale v1 rows on read. Task 10 is complete for the style material library UI: the workspace exposes a coherent style-sample surface with list/detail/create/edit/delete, tag and scope filtering, search, pagination, selected sample state, deterministic stats display, non-optimistic delete failure recovery, empty/loading/error states, compact viewport coverage, and mocked bridge workflow coverage. Task 11 is complete for style-sample based skill extraction: authorized selected samples can be extracted with provider/model/reasoning settings, prompts use deterministic stats and bounded sample text only, generated skills are strictly validated, filename collisions are handled, previews record source sample ids/hashes, saving uses the existing skill content boundary, cancellation avoids partial saves, and mocked workflow coverage proves validation/save failures and no chapter mutation. Task 12 is complete for Phase 14 style-profile integration: selected style samples can build sample-backed Reference Style Profiles through explicit `style_sample` provenance, overlapping deterministic stats map into the style feature vocabulary, sample evidence stores ids/hashes/offsets without duplicated text, global/per-novel sample scope is enforced, style-skill extraction remains independently usable, and mocked workflow guardrails prove no chapter mutation or reference blueprint approval/binding bypass. Task 13 is complete for the reusable multi-range chapter selector: range normalization/merge/rejection helpers produce backend `chapter_ranges`, selection summaries remain compact, the UI supports all/custom ranges, search, individual toggles, invert/clear, lock/disabled state, and mocked browser coverage verifies multi-range and compact viewport behavior without starting narrative extraction. Task 14 is complete for the narrative pattern extraction backend pipeline: selected chapters are loaded and validated, all/range/id selection is supported, model boundary/summary/phase JSON is strictly validated with content-hash freshness, recursive compression is bounded and context-window aware, progress events carry LLM/batch/round/token/count details, cancellation is wired through active task tokens, and final skill Markdown is validated before preview persistence. Task 15 is complete for the narrative pattern extraction UI: the workspace surface starts/cancels extraction with model settings, shows ordered progress and trace inspection, previews and saves validated skills through the skill catalog path, exposes copyable diagnostics for failures, and mocked workflow coverage proves happy path, insufficient chapters, invalid model output, cancellation, progress ordering, preview/save, and no chapter mutation. Task 16 is complete for the backend Git history service and read-only bridge boundary: detailed commit summaries, cursor paging, changed-file lists, original/modified diff content, rename/delete/binary handling, empty and partially initialized repositories, configured Git author identity, no-local-Git LibGit2Sharp execution, stable Git bridge errors, and Windows/macOS/Linux native libgit2 runtime coverage are implemented and covered. Task 17 is complete for the read-only Git history UI: the workspace exposes a Git history activity panel with first-page loading, cursor paging, lazy changed-file expansion, lazy read-only diffs for added/modified/deleted/renamed/binary files, rename markers, truncated-diff messaging, empty repo state, Git failure and retry states, compact viewport coverage, and mocked bridge guardrails proving only read-only Git history methods are called. Task 18 is complete for configurable Git author identity: settings expose name/email validation, empty values fall back to safe defaults, persisted settings are applied as repo-local Git config before initialization/commit, imports and normal Git history commits use the configured identity, and backend plus mocked settings workflows cover validation/persistence/failure cases. Task 19 is partial: sidebar/chat panel drag persistence, compact clamping, corrupted setting fallback, viewport/maximized saving, and startup width/height/maximized launch settings are implemented and covered; true desktop window position restore remains open because the current runtime path does not yet capture reliable window coordinates. Task 20 is complete for update checks: the service uses configured release endpoints with fake-HTTP coverage, automatic checks are non-blocking and dismissible, manual checks surface all expected outcomes, release notes render through sanitized markdown, and release-page opening requires an explicit runtime bridge call. Task 21 is partial: shared diagnostic redaction/copy helpers, a reusable error callout, representative visible failure coverage, copy-button accessible-name/focus checks, `role="alert"` checks, and reference-anchor default/advanced surface interactive-name checks are implemented; remaining work is the full legacy-surface error lifecycle review for accidental clearing beyond the representative reference-anchor retry/close coverage. Task 22 is complete for relative-time refresh and localized formatting: chat recent sessions, session history, and Git history share locale-aware time helpers, refresh mounted relative labels on bounded timers, clean up timers on unmount, use `Intl` formatting for exact timestamps/counts, and have unit plus fake-clock Playwright coverage. Task 23 is complete after moving version history to LibGit2Sharp: repository initialization/history no longer depends on `git.exe`, `NOVELIST_GIT_PATH`, bundled Git candidates, or PATH; empty repositories, initial commits, lock-file refusal, invalid metadata errors, save commits, and import commits are covered. Task 24 is complete for the Agent and Skill boundary: agents can use bounded chapter/skill read tools and approved reference inspection/candidate-preparation tools, but no Phase 15 import/file-picker/update/open-external-url/Git/history/style-sample/style-extraction/pattern-extraction/final-insertion tools are exposed, and `web_fetch` describes SSRF-protected read-only fetching rather than external URL opening. Remaining Phase 15 bridge/UI methods intentionally stay at the compatibility boundary until their corresponding service tasks land.

**Task 21 update:** Novel create/update/delete dialogs, style sample list/detail/create/update/delete failures, story arc and timeline metadata CRUD failures, reader perspective and preference metadata CRUD failures, settings/provider failures, editor save failures, export failures, skill edit saves, the legacy style extraction dialog, and representative reference-anchor create/rebuild/search/blueprint review/approval/binding failures are now covered. Copyable callouts are checked for `role="alert"`, named/focusable copy buttons, no unnamed visible controls inside the callout, and persistence across copy-feedback rerenders. The reference-anchor workflow also audits default/advanced surfaces for named visible interactive controls, verifies keyboard opening of advanced mode, verifies a create failure survives unrelated form edits until explicit close, and verifies blueprint generate/review/approve failures clear only after successful retry. Task 21 remains partial until the same error lifecycle review is closed for the remaining legacy surfaces.

**Description:** Phase 15 merges the latest user-facing product capabilities from the legacy `goink-master` snapshot into the current Novelist .NET 10 + Photino.NET + React/Vite architecture. The source snapshot is useful as a behavior reference, not as an implementation target. The merge must port product semantics, data contracts, progress reporting, edge-case handling, and regression coverage into Novelist modules without reviving legacy Go/Wails/Python runtime paths.

The major feature groups are:

- one-step EPUB/TXT/MD novel import with encoding detection, live progress, Git initialization/commit, and cleanup on failure;
- a first-class style material library that lets users save prose samples, inspect deterministic text statistics, tag/filter by novel or global scope, and generate reusable imitation skills from selected samples;
- narrative pattern extraction that analyzes selected chapters or a whole novel through a visible multi-stage pipeline and produces reusable narrative guidance skills;
- Git visualization with a commit-history panel, changed-file browsing, renamed-file markers, and lazy diff viewing;
- desktop and UI experience hardening: resizable sidebars, remembered window/layout state, update checks, configurable Git author identity, multi-range chapter selectors, and better error feedback;
- bug-fix consolidation around macOS Git, empty repositories, hidden CRUD failures, stale relative-time labels, localized numeric formatting, and copyable error details.

This phase is deliberately broad because these features interact at product boundaries: import creates novels, chapters, files, Git history, search/RAG/indexing, reference materials, and style/narrative sources; style and narrative extraction create skills consumed by chat and slash commands; Git visualization depends on robust repository state; error feedback must be consistent across all affected workflows.

## Legacy Source Map

Treat these `goink-master` files as references for behavior and test cases:

- Import pipeline: `goink-master/app/import_novel.go`, `goink-master/internal/import/import_flow.go`, `goink-master/internal/import/import.go`, `goink-master/internal/import/txt.go`, `goink-master/internal/import/epub.go`, `goink-master/frontend/src/hooks/useImportNovel.ts`.
- Style library: `goink-master/app/style.go`, `goink-master/internal/style/*`, `goink-master/internal/text/stats.go`, `goink-master/frontend/src/components/style/*`.
- Narrative pattern extraction: `goink-master/app/pattern_api.go`, `goink-master/internal/pattern/*`, `goink-master/frontend/src/hooks/usePatternProgress.ts`, `goink-master/frontend/src/components/pattern/*`.
- Git history UI: `goink-master/app/history.go`, `goink-master/internal/git/*`, `goink-master/frontend/src/components/git/*`.
- Layout/update/settings/error UX: `goink-master/frontend/src/hooks/useLayoutState.ts`, `goink-master/frontend/src/hooks/useWindowState.ts`, `goink-master/internal/update/checker.go`, `goink-master/app/update.go`, `goink-master/frontend/src/components/update/UpdateDialog.tsx`, `goink-master/frontend/src/components/settings/GeneralConfigTab.tsx`.

Do not add new code under legacy `app/`, `internal/`, `python-master/`, or `frontend/src/lib/wailsjs/`. Do not run Go/Wails or old Python build commands. Compatibility behavior belongs in the Photino bridge, .NET contracts/services, current SQLite/file-system infrastructure, and owned TypeScript adapter under `frontend/src/lib/novelist/`.

## Architecture Decisions

- **Port behavior, not runtime architecture.** The legacy Go/Wails implementation is a product reference. Phase 15 implementations must live in `Novelist.Contracts`, `Novelist.Core`, `Novelist.Infrastructure`, `Novelist.App/Desktop`, `Novelist.Agent` where needed, and current React/TypeScript surfaces.
- **Import is a transaction-like workflow with compensating cleanup.** File parsing, novel creation, chapter file writes, metadata persistence, index updates, and Git commits must be sequenced so failures leave no partial novel unless the user is explicitly told which durable work completed.
- **Import recovery is startup-reconciled.** A crash or process kill during import is treated as an incomplete import run. Startup must reconcile pending runs before normal workspace use by either completing a safe cleanup or surfacing a blocked recovery state with exact paths/ids.
- **Progress is a first-class bridge event.** Long-running import, style extraction, and narrative pattern extraction must report typed progress events with task ids, stage names, counts, and user-readable messages. The frontend must ignore stale events by task id.
- **Style samples and Phase 14 style profiles are related, not duplicate systems.** Style sample cards are user-curated source material. Deterministic sample statistics should use the same feature vocabulary where practical as `ReferenceStyleFeatureVectorPayload`. LLM-generated imitation skills must not bypass Phase 14 source-leak and provenance rules when used for reference-anchored drafting.
- **Narrative pattern extraction produces guidance skills, not hidden prose authority.** Pattern extraction can summarize structure and generate a reusable skill document, but it must not mutate chapter content, approve blueprints, insert prose, or bypass reference workflow gates.
- **Git history is read-only visualization.** Phase 15 adds log, file-list, and diff inspection. Revert, checkout, reset, cherry-pick, and restore actions are out of scope unless a later phase designs explicit approvals.
- **Errors must be copyable and structured.** Bridge errors should expose stable code, message, optional detail, and correlation/event id where available. UI surfaces should show the human message and allow copying diagnostic details without exposing secrets.
- **Networked update checks are opt-in-safe.** Automatic update checks must be timeout-bounded, dismissible, cache-aware, and easy to disable in tests. Manual checks can show network failures; startup checks should not block writing.

## Preflight Decisions

These decisions close the issues that would otherwise block contract and storage work:

- **Post-import indexing:** successful novel import must queue or run the same workspace search/RAG indexing path used after normal chapter writes. It must not automatically promote imported novel text into the reference corpus or style/profile libraries. If product UX later wants "also use this novel as reference corpus", that is an explicit opt-in action with the normal reference-source policy and provenance.
- **Style sample storage:** style samples live in the main app metadata/storage boundary through `IStyleSampleService`. Reference style profiles remain in the Phase 14 reference-style storage boundary. A later adapter may build a profile from selected samples, but it must record sample provenance and avoid duplicating full sample text into style-profile evidence rows.
- **Generated skill save policy:** style extraction and narrative pattern extraction always produce a previewable validated skill draft first. Saving to the skill catalog is a separate explicit user action.
- **Update-check target:** the release endpoint is a configurable product setting supplied by app/build configuration. Tests must not hard-code a live GitHub URL. The legacy `sigpanic/goink` endpoint is not the default unless the product configuration explicitly selects it.
- **Import size limits:** default limits are 50 MB for TXT/MD source files, 100 MB for compressed EPUB files, and 250 MB for total uncompressed EPUB text/resources inspected during parse. Rejections must be structured, visible, and copyable. These constants can be raised later only with stress coverage.
- **Encoding strategy:** TXT/MD decoding order is UTF-8 BOM, UTF-16 LE/BE BOM, valid UTF-8, UTF-16 LE/BE no-BOM heuristic based on NUL-byte distribution, then GB18030 via .NET code-page provider. Binary-looking files and low-confidence decodes fail instead of importing corrupted text.

## Non-Negotiable Scope

- Preserve SafePath/path traversal protections for every import, file picker, Git diff, and chapter write.
- Import must support `.epub`, `.txt`, `.md`, and `.markdown`.
- TXT/MD import must detect UTF-8 with BOM, UTF-8 without BOM, UTF-16 LE/BE with BOM, UTF-16 LE/BE no-BOM by conservative heuristic, GB18030/GBK-family Chinese text, and fail with a clear message when decoding is unsafe.
- Import must reject files above the Phase 15 default size limits unless a later tested configuration explicitly raises those limits.
- EPUB import must parse `META-INF/container.xml`, OPF metadata/spine/manifest, XHTML/HTML chapter content, URL-escaped internal paths, and missing/empty chapter cases without crashing.
- Import progress must be visible from file selection through parse, novel creation, chapter writes, metadata/index updates, Git staging/commit, done, and failure cleanup.
- Failed import before durable completion must delete created chapter files, Git repo data, DB rows, build-status rows, RAG/reference side effects, and temporary files.
- Incomplete import runs left by crash/process kill must be reconciled on startup before normal workspace editing proceeds.
- Git commit failure after successful import must not delete valid user data; it must surface a warning and make the repository recoverable on next save/commit.
- Style samples must support global and per-novel scope, tags, search/filter, pagination, create/edit/delete, full-content detail, and deterministic statistics.
- Style extraction must validate selected sample ids, load only authorized samples, use configured LLM settings, support cancellation, validate generated skill frontmatter, sanitize skill filenames, and never save unvalidated model output as an executable skill.
- Narrative pattern extraction must support all chapters and selected chapter ranges, show stage progress, cache/reuse chapter summaries when safe, validate model tool JSON, retry only bounded transient empty outputs, support cancellation, and produce a validated skill document.
- Git history must support paged/infinite loading, changed-file list per commit, added/modified/deleted/renamed markers, lazy diff loading, empty repo handling, and Windows/macOS/Linux native libgit2 runtime coverage.
- Sidebar width, chat panel width, window size, and maximized state must persist without breaking compact/mobile-like viewports or inaccessible off-screen windows.
- CRUD failures for characters, locations, skills, chapters, dialogs, and metadata edits must be visible to users.
- Relative time labels in chat/session/history panels must refresh while the app stays open.
- Error copy buttons must copy useful diagnostics and redact API keys, bearer tokens, local secrets, and overly long source text.

## Out of Scope

- Directly migrating old Go packages, Wails bindings, generated `wailsjs`, or Python services.
- A full Git restore/revert UI.
- Hard-deleting reference corpus/style evidence beyond existing archive semantics.
- Importing DRM-protected EPUBs.
- Automatically uploading crash/error reports.
- Letting style or narrative extraction save prose into chapter files.
- Replacing Phase 14 style audit with prompt-only style imitation.

## Dependency Graph

```text
Contracts and migrations
  -> closed preflight decisions
  -> safe file/dialog/runtime services
  -> import parser and transaction workflow
  -> crash-safe import run recovery
  -> progress event bus and task cancellation
  -> frontend import UI and drag/drop
  -> style sample storage/statistics
  -> style extraction skill generation
  -> narrative pattern extraction pipeline
  -> Git log/file/diff service
  -> Git history UI
  -> settings/update/layout/error UX
  -> app-wide Playwright and integration stress gates
```

Work should be delivered in vertical slices where possible, but schema/contracts must land before frontend surfaces depend on them.

## Task 1: Phase 15 Contract Inventory and Compatibility Boundary

**Status:** Complete for the contract/compatibility boundary. Backend payload contracts exist for import, style samples, style skill extraction, narrative pattern extraction, Git history, update/layout/window settings, copyable diagnostics, and app/build update-check configuration; 30 Phase 15 method names are registered in `BridgeCompatibilityAppMethods`; owned frontend adapter method/type declarations are synced in `frontend/src/lib/novelist/api.ts` and `types.ts`. These methods currently route through the stable compatibility not-implemented handler; real service handlers are tracked by later tasks.

**Description:** Define all new Phase 15 bridge contracts, method names, progress events, and compatibility registrations before implementation. This task prevents ad hoc payload drift across backend and frontend.

**Acceptance criteria:**

- [x] Contracts exist for import requests/results, skipped chapters, import progress events, style samples, style stats, style extraction requests/results/progress, pattern extraction requests/results/progress/trace, Git commit summaries, commit file entries, file diffs, update-check results, Git author settings, layout/window settings, and structured copyable errors.
- [x] Contracts exist for import run states, startup recovery/reconciliation results, import size-limit errors, encoding diagnostics, and post-import index warnings.
- [x] JSON uses stable snake_case for new product payloads unless an existing current Novelist payload already has a different convention. Representative serialization coverage exists for import, style, pattern, Git, update, and diagnostics payloads; broader null/empty-list cases can expand with service-specific tests.
- [x] Bridge method names are added to `BridgeCompatibilityAppMethods`, backend handler registration tests, and `frontend/src/lib/novelist/api.ts`.
- [x] Every long-running operation carries a client-supplied or generated `task_id`.
- [x] Release/update-check endpoint configuration is represented as app/build configuration, not hard-coded into frontend code. `GetAppConfig` exposes `update_check` from `Novelist.App` MSBuild metadata/startup arguments via `AppInitializationOptions`; default builds keep the endpoint empty and automatic checks disabled unless product configuration explicitly selects an endpoint.

**Verification:**

- [x] Contract serialization tests prove stable field names for representative Phase 15 payloads and assert no raw source/candidate/prompt content fields in diagnostic/import records.
- [x] Bridge routing tests cover every new method through compatibility registration and the stable not-implemented boundary.
- [x] Frontend contract tests fail if `api.ts` and compatibility method registrations diverge.
- [x] App initialization and frontend adapter tests prove update-check endpoint configuration comes from backend app/build configuration and no live release endpoint is hard-coded into the owned frontend bridge adapter.

**Dependencies:** None.

**Files likely touched:**

- `src/Novelist.Contracts/App/*Import*Payloads.cs`
- `src/Novelist.Contracts/App/*StyleSample*Payloads.cs`
- `src/Novelist.Contracts/App/*Pattern*Payloads.cs`
- `src/Novelist.Contracts/App/*Git*Payloads.cs`
- `src/Novelist.Contracts/App/*Update*Payloads.cs`
- `src/Novelist.Contracts/App/AppSettingsPayload.cs`
- `src/Novelist.Contracts/Bridge/*`
- `src/Novelist.Core/Bridge/BridgeCompatibilityAppMethods.cs`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- `tests/Novelist.Tests/**/*ContractTests.cs`

**Estimated scope:** M.

## Task 2: Storage and Migration Model for Import, Style Samples, Pattern Runs, and App Settings

**Description:** Add durable storage for Phase 15 features while preserving existing user data and current reference-anchor databases.

**Acceptance criteria:**

- [x] Style samples store `id`, optional `novel_id`, `is_global`, `name`, `content`, `preview`, `tags`, deterministic stats JSON/schema version, `created_at`, `updated_at`, and optional source metadata.
- [x] Pattern extraction runs store run state, selected chapter ids/ranges, stage trace, generated skill metadata, error state, and cancellation/failure timestamps.
- [x] Import runs store enough state to diagnose failures and prove cleanup, without retaining full imported source text unnecessarily: task id, source path hash/display name, parser type, created novel id, created file roots, current durable phase, cleanup state, warning state, and timestamps.
- [x] Import run state transitions are monotonic and explicit: `created`, `parsing`, `creating_novel`, `writing_files`, `saving_metadata`, `indexing`, `git_commit`, `completed`, `completed_with_warning`, `cleanup_pending`, `cleanup_completed`, `cleanup_blocked`, `failed`, `cancelled`.
- [x] Settings persist Git author name/email, update-check preferences/dismissed version, side/chat panel widths, and window bounds/maximized state using current app settings mechanisms.
- [x] Migrations are copy-first or additive and preserve existing novels, chapters, skills, settings, reference anchors, and Phase 14 style profiles.

**Verification:**

- [x] Migration tests from pre-Phase 15 app data.
- [x] Migration tests with partially populated Phase 14 reference-style databases.
- [x] Import-run state machine tests prove invalid backwards transitions fail.
- [x] Import-run storage tests cover source path hashing without raw path persistence, parser type/display name retention, created novel/file roots, diagnostics, skipped chapters, warning state, cleanup state, terminal timestamps, recovery pending/blocked classification, bridge routing, and invalid payload rejection.
- [x] Pattern extraction storage tests cover durable run persistence, selected range retention, progress updates, trace append/read, generated skill preview metadata, cancellation/failure diagnostics, terminal timestamps, bridge routing, and invalid payload rejection.
- [x] Style sample storage tests cover scope filtering, persistence, deterministic stats, bridge validation, update, and delete.
- [x] Settings persistence tests for defaults, invalid stored values, bridge payload validation, and updates.

**Dependencies:** Task 1.

**Files likely touched:**

- `src/Novelist.Infrastructure/App/FileSystemAppSettingsService.cs`
- `src/Novelist.Infrastructure/App/*StyleSample*Service.cs`
- `src/Novelist.Infrastructure/App/*Pattern*Service.cs`
- `src/Novelist.Infrastructure/App/*NovelImport*Service.cs`
- `tests/Novelist.IntegrationTests/**/*Migration*Tests.cs`

**Estimated scope:** M.

## Task 3: Safe File Picking and Drag-Drop Import Boundary

**Description:** Add a desktop-safe file-selection and drag/drop path for importing EPUB/TXT/MD novels.

**Acceptance criteria:**

- [x] Desktop file picker filters `.epub`, `.txt`, `.md`, `.markdown`.
- [x] Drag/drop accepts only supported file extensions and rejects folders, URLs, multiple unsupported files, and empty drops with visible feedback.
- [x] Dropped paths are passed only to the import service, not to generic arbitrary file-read bridge methods.
- [x] Path validation blocks traversal, device paths where unsupported, unreadable files, overly large files above configured limit, and non-file inputs.
- [x] Tests can inject temporary fixture paths without touching real user projects.

**Verification:**

- [x] Integration tests for extension, path, readability, kind-mismatch, traversal/device/URI, and size-limit validation.
- [x] Desktop/bridge tests for picker cancellation and selected path payload.
- [x] Playwright tests for drag/drop accepted/rejected states using mocked bridge.

**Dependencies:** Tasks 1-2.

**Files likely touched:**

- `src/Novelist.Core/App/INovelImportService.cs`
- `src/Novelist.Core/App/IWorkspaceUtilityServices.cs`
- `src/Novelist.App/Desktop/*FilePicker*.cs`
- `src/Novelist.Core/Bridge/*Import*BridgeHandlers.cs`
- `frontend/src/views/WorkspaceView.tsx`
- `frontend/src/components/novel/**/*Import*.tsx`

**Estimated scope:** S.

## Task 4: TXT/MD Parser with Encoding Detection and Robust Chapter Splitting

**Description:** Implement deterministic TXT/MD parsing with encoding detection and conservative chapter-boundary extraction.

**Acceptance criteria:**

- [x] UTF-8 BOM, UTF-16 LE/BE BOM, valid UTF-8, UTF-16 LE/BE no-BOM heuristic, GB18030/GBK-family Chinese text, CRLF/LF/CR newlines, and markdown headings parse correctly.
- [x] .NET code-page provider registration for GB18030 is explicit, test-covered, and safe when unavailable.
- [x] Decoder returns encoding name, confidence, BOM/heuristic source, replacement-character count, and binary/low-confidence diagnostics.
- [x] Low-confidence decoding and binary-looking files fail rather than importing corrupted text.
- [x] Chapter headers support `第N章`, Chinese numerals, `第N卷/部`, `Chapter N`, and optional markdown `#` prefixes.
- [x] False positives are reduced by title-length limits, line-start anchoring, and body-line heuristics.
- [x] Files without chapter headers import as a single chapter with a derived title.
- [x] Empty files, undecodable files, binary-looking files, and huge malformed files fail with structured errors.
- [x] Parser returns skipped/diagnostic items without losing valid chapters.

**Verification:**

- [x] Parser tests for UTF-8, UTF-8 BOM, UTF-16 LE/BE BOM, UTF-16 no-BOM heuristic, GB18030, mixed punctuation, markdown headings, no-header single chapter, false-positive body lines, empty file, binary-like bytes, low-confidence decode, and invalid bytes.
- [x] Stress test for large TXT/MD fixture without unbounded memory growth.

**Dependencies:** Tasks 1-3.

**Files likely touched:**

- `src/Novelist.Infrastructure/App/NovelImportTextParser.cs`
- `tests/Novelist.Tests/**/*NovelImport*Tests.cs`

**Estimated scope:** M.

## Task 5: EPUB Parser with Spine Order, Metadata, and Safe HTML Text Extraction

**Description:** Implement EPUB import support using .NET zip/XML/HTML parsing with path safety and resilient chapter extraction.

**Acceptance criteria:**

- [x] Parser locates `META-INF/container.xml`, resolves the OPF rootfile, reads metadata title, manifest, and spine order.
- [x] Internal hrefs handle URL escaping, spaces, relative paths, and case mismatch fallback within the zip only.
- [x] XHTML/HTML text extraction ignores `script`, `style`, and `head`, preserves paragraph/headings/list line breaks, normalizes whitespace, and produces readable chapter text.
- [x] Missing spine items, missing files, invalid HTML, and empty chapters are skipped with reasons when other valid chapters exist.
- [x] EPUB with no readable chapters fails with a clear structured error.
- [x] Zip-slip and absolute internal path tricks cannot escape the archive.

**Verification:**

- [x] Parser tests with minimal generated EPUB fixtures for title/spine order, nested OPF paths, URL-escaped hrefs, missing chapters, empty chapters, invalid container, and no readable chapters.
- [x] Security tests for zip-slip-like internal names.

**Dependencies:** Tasks 1-3.

**Files likely touched:**

- `src/Novelist.Infrastructure/App/NovelImportEpubParser.cs`
- `tests/Novelist.Tests/**/*Epub*Tests.cs`

**Estimated scope:** M.

## Task 6: Transaction-Like Novel Import Workflow with Cleanup

**Status:** Complete for the backend transaction-like import workflow. The current service parses TXT/MD/EPUB, creates novels and chapter files, computes chapter metadata, initializes Git through existing repository services, batches import changes into a single final commit, delays RAG stale notifications until the indexing stage, reports post-import index/Git warnings without deleting user data, coordinates running-task cancellation through `CancelNovelImport`, normalizes cross-platform import-run path separators, cleans caught metadata/write/cancellation failures, and is covered by Task 7 startup reconciliation for process-death cleanup.

**Description:** Build the full import workflow: parse file, create novel, write chapter files, persist metadata, update indexes, initialize Git, and report durable completion or cleanup.

**Acceptance criteria:**

- [x] Import creates a novel with title, description/source note, chapter metadata, chapter files, and initial `novelist.md`/workspace files consistent with current Novelist conventions.
- [x] Word counts are computed using current text-stat logic and stored consistently with manually created chapters.
- [x] Repository is initialized with configured Git author identity and a meaningful import commit when changes exist.
- [x] If failure occurs before metadata/file workflow is complete, the service removes the created novel row, chapter rows, files, repo, import run side effects, pending RAG/reference build state, and temporary data.
- [x] If Git commit fails after successful import, imported user data remains and a warning is returned with recovery instructions.
- [x] Successful import queues/runs the same search/RAG indexing behavior used by normal chapter writes and records index warnings separately from import failure.
- [x] Successful import does not automatically add the imported novel as reference corpus/style material.
- [x] Import is cancellable before durable commit; cancellation performs the same cleanup as failure.
- [x] Concurrent imports cannot corrupt the same app data directory and have independent progress task ids.

**Verification:**

- [x] Integration tests for successful TXT, MD, EPUB imports.
- [x] Fault-injection tests for parse failure, novel-create failure, chapter-write failure, metadata failure, index failure, Git init failure, Git commit failure, and cancellation.
- [x] Cleanup tests assert no orphaned rows/files remain after pre-completion failures.
- [x] Tests prove post-import index failure produces warning/retry state, not import data deletion.
- [x] Tests prove successful import does not create reference anchors, style samples, or reference style profiles without explicit user action.
- [x] Cross-platform path tests for Windows/macOS/Linux path separators.

**Dependencies:** Tasks 1-5.

**Files likely touched:**

- `src/Novelist.Core/App/INovelImportService.cs`
- `src/Novelist.Infrastructure/App/FileSystemNovelImportService.cs`
- `src/Novelist.Infrastructure/App/FileSystemNovelService.cs`
- `src/Novelist.Infrastructure/App/FileSystemChapterContentService.cs`
- `src/Novelist.Infrastructure/App/GitVersionControlService.cs`
- `tests/Novelist.IntegrationTests/**/*NovelImport*Tests.cs`

**Estimated scope:** M.

## Task 7: Startup Import Recovery and Reconciliation

**Status:** Complete for startup import recovery and reconciliation. `FileSystemNovelImportRecoveryService` reads the import-run store tolerantly enough to block unsafe corrupted roots, validates cleanup paths against both app data and the recorded novel workspace, deletes known partial novel rows/workspaces idempotently, removes pending RAG/reference build side effects only for the recovered import novel, preserves durable imports interrupted during indexing/Git commit as `completed_with_warning`, blocks unknown/manual-review states instead of guessing, runs from app initialization before normal workspace use, and exposes results through both `ReconcileNovelImportRuns` and `GetAppConfig.import_recovery`. The workspace shows startup recovery results with copyable diagnostics.

**Description:** Add crash-safe reconciliation for incomplete import runs before normal workspace use. This closes the gap between caught exceptions and process death during import.

**Acceptance criteria:**

- [x] Startup scans import runs in non-terminal states and classifies each as safe-to-clean, completed-with-warning, cleanup-blocked, or unknown/manual-review.
- [x] Safe-to-clean runs remove created novel rows, chapter rows, known created file roots, Git repositories, pending index/reference state, temporary directories, and import-run side effects.
- [x] Cleanup is idempotent: running reconciliation multiple times produces the same final state and does not delete unrelated novels or user files.
- [x] Cleanup path validation verifies every resolved path remains inside the app data directory and the recorded novel workspace before deletion.
- [x] If cleanup cannot safely prove ownership of a path or row, reconciliation marks `cleanup_blocked`, shows a startup recovery message, and exposes copyable diagnostics instead of guessing.
- [x] Runs already past the durable completion boundary are never deleted by startup recovery; Git/index warnings remain recoverable.
- [x] Recovery emits a bridge/startup result so the frontend can show repaired, blocked, or warning states before the user edits the workspace.

**Verification:**

- [x] Integration tests simulate process death after novel row creation, after directory creation, after partial chapter file writes, after metadata writes, during index update, and during Git commit.
- [x] Idempotency tests run reconciliation twice against the same partial state.
- [x] Path-safety tests prove corrupted import-run paths outside the workspace are not deleted and become `cleanup_blocked`.
- [x] Playwright startup test shows cleanup-completed and cleanup-blocked states with copyable diagnostics.

**Dependencies:** Tasks 1-2 and Task 6.

**Files likely touched:**

- `src/Novelist.Core/App/INovelImportRecoveryService.cs`
- `src/Novelist.Infrastructure/App/FileSystemNovelImportRecoveryService.cs`
- `src/Novelist.Infrastructure/App/FileSystemAppInitializationService.cs`
- `src/Novelist.Core/Bridge/AppInitializationBridgeHandlers.cs`
- `frontend/src/App.tsx`
- `tests/Novelist.IntegrationTests/**/*NovelImportRecovery*Tests.cs`

**Estimated scope:** M.

## Task 8: Import Progress Events and Frontend Import Experience

**Description:** Add visible progress UI for picker and drag/drop imports, including skipped chapters and cleanup errors.

**Acceptance criteria:**

- [x] Progress stages cover selecting, parsing, creating novel, writing chapters, saving metadata, indexing/reference follow-up if applicable, Git staging/commit, done, warning, error, cleanup.
- [x] UI shows percent, current/total counts, stage text, current chapter when available, skipped chapter count/details, and final imported novel summary.
- [x] Stale progress events from old task ids are ignored.
- [x] User can close completed dialogs, but cannot accidentally hide an in-progress destructive cleanup state without a clear status.
- [x] After success, novel/chapter lists refresh and the first imported chapter can open without manual reload.
- [x] After failure, no phantom novel remains selected.

**Verification:**

- [x] Playwright import tests for success, user cancel, parser failure, write failure with cleanup, Git warning, skipped EPUB chapters, and drag/drop rejection.
- [x] Bridge-call guardrail test proves no arbitrary file read method is exposed.

**Dependencies:** Tasks 6-7.

**Files likely touched:**

- `frontend/src/components/novel/NovelImportDialog.tsx`
- `frontend/src/hooks/useNovelImport.ts`
- `frontend/src/components/sidebar/NovelList.tsx`
- `frontend/src/views/WorkspaceView.tsx`
- `frontend/tests/**/*import*.spec.ts`

**Estimated scope:** M.

## Task 9: Style Sample Library Contracts, Storage, and Deterministic Statistics

**Description:** Build the style material card system as a durable, paged library with deterministic statistics.

**Acceptance criteria:**

- [x] Users can create style samples from pasted text, selected editor text if available, or manual entry.
- [x] Samples support global scope and per-novel scope; per-novel views can include global samples by default and filter to current novel only.
- [x] Tags support create/edit/remove and semicolon/comma/newline paste normalization.
- [x] List calls are paginated and do not load full sample content unless detail is requested.
- [x] Stats include total characters/words, sentence count, sentence-length distribution, average sentence length, sentence-length standard deviation, punctuation density, quote density, paragraph count, average paragraph length, and analyzer/stat schema version.
- [x] Statistics are recomputed on content update and can be recalculated for older rows after schema upgrades.

**Verification:**

- [x] Unit tests for sentence splitting, punctuation density, paragraph rhythm, Chinese/English mixed text, empty/short text, and deterministic output.
- [x] Integration tests for CRUD, pagination, global/per-novel filters, tag search, and stats persistence.

**Dependencies:** Tasks 1-2.

**Files likely touched:**

- `src/Novelist.Contracts/App/StyleSamplePayloads.cs`
- `src/Novelist.Core/App/IStyleSampleService.cs`
- `src/Novelist.Infrastructure/App/SqliteStyleSampleService.cs`
- `src/Novelist.Infrastructure/App/StyleTextStatistics.cs`
- `tests/Novelist.Tests/**/*StyleSample*Tests.cs`
- `tests/Novelist.IntegrationTests/**/*StyleSample*Tests.cs`

**Estimated scope:** M.

## Task 10: Style Sample Library UI and Filtering

**Status:** Complete for the style sample library UI. The style material surface is reachable from the workspace activity bar, preserves existing Skills and Reference Anchor flows, supports list/detail/create/edit/delete, selected samples, scope toggles, search, tag filters, pagination, deterministic statistics, compact layout, and visible backend failure recovery through mocked Photino bridge coverage.

**Description:** Add a production-quality style material library surface consistent with current Novelist UI patterns.

**Acceptance criteria:**

- [x] UI supports sample list, detail preview, create, edit, delete, tag editing, scope toggle, search, pagination, and selected-sample state.
- [x] Cards display preview, tags, scope, word count, updated time, and key stats without overcrowding.
- [x] Detail view shows full deterministic statistics with readable distributions.
- [x] Delete failures are visible and do not remove the card optimistically unless backend confirms.
- [x] Empty, loading, error, and compact viewport states are covered.
- [x] Style library is reachable from a coherent app location without hiding existing Skills or Reference Anchor workflows.

**Verification:**

- [x] Playwright tests for create/edit/delete, filter by global/current novel, tag search, stats display, pagination, backend failure recovery, and compact viewport.
- [x] Frontend build and lint.

**Dependencies:** Task 9.

**Files likely touched:**

- `frontend/src/components/style/**/*`
- `frontend/src/components/sidebar/SidePanel.tsx`
- `frontend/src/views/WorkspaceView.tsx`
- `frontend/src/lib/novelist/types.ts`
- `frontend/tests/**/*style-sample*.spec.ts`

**Estimated scope:** M.

## Task 11: Style Extraction to Validated Reusable Skills

**Status:** Complete. `ExtractStyleSkillFromSamples`, `CancelStyleSkillExtraction`, and `GetStyleSkillExtractionRun` now route to a durable style-skill extraction service; the style material library exposes a production UI for selected-sample extraction, preview, cancellation, validation failures, save failures, and saving through the existing skill content boundary. Backend and mocked browser workflows prove source-scope authorization, bounded prompt construction, strict frontmatter validation, safe filename collision handling, source sample provenance in previews, cancellation without partial writes, and no chapter mutation.

**Description:** Generate imitation skills from selected style samples using deterministic stats plus configured LLM analysis, while validating output and preserving safety boundaries.

**Acceptance criteria:**

- [x] User selects one or more authorized style samples and starts extraction with provider/model/reasoning settings.
- [x] Extraction prompt includes deterministic stats and bounded sample text; it does not include unrelated novel files or hidden source text.
- [x] LLM output must be a valid skill markdown file with required frontmatter: name, description, category, mode, author, version.
- [x] Skill filename is sanitized and collision-handled.
- [x] User can preview generated skill before saving; saving uses existing skill catalog service and records source sample ids.
- [x] Cancellation stops the model call and marks the run cancelled without saving partial output.
- [x] Failed validation surfaces precise errors and copyable diagnostics.

**Verification:**

- [x] Unit tests for skill parsing, filename sanitization, collision behavior, and invalid frontmatter rejection.
- [x] Integration tests with fake LLM provider for success, empty output, invalid markdown, invalid frontmatter, cancellation, and save failure.
- [x] Playwright tests for extract, cancel, preview, save, validation failure, and no chapter mutation.

**Dependencies:** Tasks 9-10 and Phase 14 style-audit foundations where applicable.

**Files likely touched:**

- `src/Novelist.Core/App/IStyleExtractionService.cs`
- `src/Novelist.Infrastructure/App/StyleExtractionService.cs`
- `src/Novelist.Infrastructure/App/FileSystemSkillCatalogService.cs`
- `frontend/src/components/skill/ExtractStyleDialog.tsx`
- `frontend/src/components/style/StyleExtractionPanel.tsx`
- `tests/Novelist.IntegrationTests/**/*StyleExtraction*Tests.cs`

**Estimated scope:** M.

## Task 12: Style Library Integration with Phase 14 Reference Style Profiles

**Status:** Complete. Selected style samples now flow through the existing Reference Style Profile service boundary as explicit `style_sample` sources rather than a parallel style system. Sample-backed profiles preserve `style_sample_ids`/`source_style_sample_ids`, map overlapping deterministic statistics into the Phase 14 style feature schema, persist sample source/evidence metadata without copying full sample text, enforce global/per-novel access scope, and expose a style-library UI/mock workflow path that proves generated skills do not bypass chapter mutation or reference blueprint approval gates.

**Description:** Connect user-curated style samples to the Phase 14 style anchoring model without creating parallel incompatible style systems.

**Acceptance criteria:**

- [x] Style sample stats can be mapped to the current style feature vocabulary where dimensions overlap.
- [x] Users can build a Reference Style Profile from selected style samples only after the samples are converted into controlled reference-style evidence records or an explicitly documented sample-profile source type.
- [x] Profile evidence does not duplicate full sample text outside the approved storage boundary.
- [x] Style samples can be used for skill extraction even when no reference style profile is built.
- [x] Reference-anchored drafting still requires approved style contracts and audit; a generated imitation skill alone cannot bypass those gates.

**Verification:**

- [x] Contract tests for sample-backed style profile payloads if a new source type is introduced.
- [x] Integration tests proving sample profile access respects global/per-novel scope.
- [x] Guardrail tests proving style sample extraction cannot call `SaveContent` or approve reference blueprints.

**Dependencies:** Tasks 9-11 and Phase 14 profile contracts.

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceStylePayloads.cs`
- `src/Novelist.Core/App/IReferenceStyleProfileService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceStyleProfileService.cs`
- `tests/Novelist.IntegrationTests/**/*ReferenceStyle*Tests.cs`

**Estimated scope:** M.

## Task 13: Multi-Range Chapter Selector Component

**Status:** Complete. `MultiRangeChapterSelector` is available from the Narrative Pattern surface and backed by reusable `chapterRange` helpers that normalize and merge ranges, reject invalid bounds, convert selection state to backend `chapter_ranges`, expand ranges to ids, invert selections, and produce compact summaries. Mocked browser coverage exercises all/custom selection, overlapping range merge, search, individual toggles, invert/clear, disabled lock state, and verifies the selector alone does not start narrative extraction.

**Description:** Build a reusable chapter selection component for narrative pattern extraction and future analysis workflows.

**Acceptance criteria:**

- [x] Supports all chapters, single range, multiple ranges, individual chapter toggles, invert/clear, and search by chapter title/number.
- [x] Ranges normalize, merge overlaps, reject out-of-bounds values, and preserve deterministic ordering.
- [x] Selection summary is compact and readable for hundreds of chapters.
- [x] Component works with keyboard and screen-reader accessible labels.
- [x] Contract helper converts selected ranges to explicit chapter ids or range payloads accepted by backend.

**Verification:**

- [x] Unit tests for range normalization.
- [x] Playwright tests for selecting multiple ranges, overlapping ranges, clearing, all-chapter mode, compact viewport, and disabled state.

**Dependencies:** Task 1.

**Files likely touched:**

- `frontend/src/components/chapter/MultiRangeChapterSelector.tsx`
- `frontend/src/components/chapter/chapterRange.ts`
- `frontend/tests/**/*chapter-range*.spec.ts`

**Estimated scope:** S.

## Task 14: Narrative Pattern Extraction Pipeline Contracts and Backend

**Status:** Complete for the backend pipeline. `StartNarrativePatternExtraction` now runs load/validate, boundary detection, per-chapter summary extraction, recursive phase compression, and final skill preview generation through the current .NET/Photino service stack with fake-LLM regression coverage and no live network dependency in default tests.

**Description:** Implement the four-stage narrative pattern extraction pipeline in .NET with visible progress and robust LLM JSON validation.

**Acceptance criteria:**

- [x] Pipeline stages are: load/validate chapters, identify structure boundaries, extract per-chapter summaries, recursively compress into narrative phases, generate final narrative guidance skill.
- [x] Supports all chapters or selected chapter ids/ranges.
- [x] Requires a minimum viable chapter count/content size and returns clear validation errors when insufficient.
- [x] Boundary, summary, and phase outputs are parsed from structured tool/function JSON and validated for chapter ranges, non-empty text, ordering, and source coverage.
- [x] Existing chapter summaries may be reused only when tied to the same content hash or a clearly documented freshness policy.
- [x] Recursive compression has bounded retries, convergence checks, maximum rounds, context-window-aware batching, and cancellation checks.
- [x] Final skill output is parsed and validated before save/preview.
- [x] Progress events include current stage, message, LLM status, round, batch index/total, token estimate, boundary count, summary count, and phase count.

**Verification:**

- [x] Unit tests for batching, range normalization, boundary normalization, phase normalization, token budget logic, convergence/stall behavior, and invalid JSON rejection.
- [x] Integration tests with fake LLM provider for success, invalid boundary JSON, invalid summary JSON, empty phase output retry, compression stall, cancellation, and final skill validation failure.
- [x] No-live-network default tests.

**Dependencies:** Tasks 1, 2, 13.

**Files likely touched:**

- `src/Novelist.Contracts/App/NarrativePatternPayloads.cs`
- `src/Novelist.Core/App/INarrativePatternExtractionService.cs`
- `src/Novelist.Infrastructure/App/NarrativePatternExtractionService.cs`
- `tests/Novelist.Tests/**/*NarrativePattern*Tests.cs`
- `tests/Novelist.IntegrationTests/**/*NarrativePattern*Tests.cs`

**Estimated scope:** M.

## Task 15: Narrative Pattern Extraction UI

**Status:** Complete. The Narrative Pattern workspace surface now combines the reusable multi-range chapter selector with provider/model/reasoning settings, `StartNarrativePatternExtraction`/`CancelNarrativePatternExtraction`, live `narrative_pattern_extraction:progress` timeline handling, trace inspection grouped by boundary/summary/phase stages, validated skill preview/save through the existing `SaveContent` skill path, and copyable run/trace diagnostics for failure states.

**Description:** Add a user-facing pattern extraction surface with chapter selection, live pipeline progress, trace inspection, cancellation, preview, and skill save.

**Acceptance criteria:**

- [x] UI allows selecting chapter ranges, provider/model/reasoning options, and starting extraction.
- [x] Progress timeline shows each stage and current batch/round details.
- [x] Boundary hints, summaries, and phase chunks are inspectable without overwhelming the default view.
- [x] User can cancel extraction and see cancelled state.
- [x] Generated skill can be previewed and saved through existing skill catalog flow.
- [x] Errors expose clear messages plus copyable diagnostics.
- [x] No generated pattern output mutates chapter files.

**Verification:**

- [x] Playwright tests for happy path, insufficient chapters, invalid model output, cancellation, progress event ordering, preview/save, and no chapter `SaveContent` call.
- [x] Frontend build and lint.

**Dependencies:** Task 14.

**Files likely touched:**

- `frontend/src/components/pattern/**/*`
- `frontend/src/hooks/usePatternProgress.ts`
- `frontend/src/components/skill/**/*`
- `frontend/tests/**/*pattern*.spec.ts`

**Estimated scope:** M.

## Task 16: Git Service Expansion for Detailed Log, File Lists, Diffs, Renames, and Empty Repos

**Status:** Complete for the backend Git history service and read-only bridge boundary. `IVersionControlService` now exposes paged detailed commit summaries, changed-file lists, and file diffs with original/modified content. `GitVersionControlService` handles empty history without creating baseline commits on read-only calls, repairs partially initialized repositories through normal `EnsureRepositoryAsync`, applies configured author identity before commits, uses LibGit2Sharp plus NuGet native libgit2 assets instead of spawning `git.exe`, normalizes repository failures into stable `VersionControlException` messages, and registers read-only Git history bridge handlers. Frontend bridge types are synced for cursor paging, insertion/deletion counts, and diff content fields. The Git history UI itself remains Task 17.

**Description:** Expand the current `IVersionControlService` from simple log support to detailed commit-history inspection.

**Acceptance criteria:**

- [x] `EnsureRepositoryAsync` works on Windows, macOS, and Linux, including empty or partially initialized repositories.
- [x] Repository-level Git author name/email are configurable and applied before commits.
- [x] Detailed log supports paging by cursor hash, latest-first ordering, author name/email, short hash, message, commit time, file count, insertions, and deletions.
- [x] Commit file list supports added, modified, deleted, renamed entries and old path for renames.
- [x] File diff returns original and modified content for added/modified/deleted/renamed files.
- [x] Git operations run through LibGit2Sharp without shelling out to local Git; repository paths stay safe, errors are normalized, and no `git.exe`/PATH dependency remains.
- [x] Empty repos return an empty log instead of throwing unhandled errors.

**Verification:**

- [x] Unit tests for log/diff parsers.
- [x] Integration tests with temporary repos for empty repo, initial commit, normal modifications, deletion, rename, paging, custom author identity, ignored `NOVELIST_GIT_PATH`, invalid metadata, and lock-file refusal.
- [x] Cross-platform Git runtime coverage is provided by `LibGit2Sharp.NativeBinaries` package assets instead of app-bundle Git path candidates.

**Dependencies:** Tasks 1-2.

**Files likely touched:**

- `src/Novelist.Core/App/IVersionControlService.cs`
- `src/Novelist.Infrastructure/App/GitVersionControlService.cs`
- `src/Novelist.Contracts/App/GitHistoryPayloads.cs`
- `tests/Novelist.Tests/**/*Git*Tests.cs`
- `tests/Novelist.IntegrationTests/**/*Git*Tests.cs`

**Estimated scope:** M.

## Task 17: Git History Bridge and Read-Only UI

**Status:** Complete. `GitHistoryView` is reachable from the workspace activity bar and uses only `GetGitCommits`, `GetGitCommitFiles`, and `GetGitFileDiff`. The UI supports first-page loading, cursor-based older-page loading, lazy commit file expansion, lazy read-only diff loading, added/modified/deleted/renamed/binary states, old-to-new rename markers, truncated-diff messaging, empty history, Git failure retry, diagnostics copy, and compact viewport rendering. The app mock workflow now includes deterministic Git fixtures plus `npm --prefix frontend run test:git` coverage for the Git history path.

**Description:** Add a left-side Git history panel and diff view using the expanded Git service.

**Acceptance criteria:**

- [x] User can open commit history for the active novel from the sidebar/activity layout.
- [x] History list loads first page quickly and infinite-scrolls older commits.
- [x] Expanding a commit lazy-loads changed files.
- [x] Selecting a file lazy-loads a read-only diff view.
- [x] Renamed files show old path to new path and a distinct marker.
- [x] Loading, empty repo, Git unavailable, command failure, and retry states are visible.
- [x] Diff viewer handles large files by truncating or paging with a clear message instead of freezing.
- [x] No Git mutation commands are exposed by this UI.

**Verification:**

- [x] Playwright tests for history paging, expand commit, select added/modified/deleted/renamed file, empty repo, Git failure, retry, and compact viewport.
- [x] Bridge guardrail tests prove only read-only Git history methods are called.

**Dependencies:** Task 16.

**Files likely touched:**

- `src/Novelist.Core/Bridge/GitHistoryBridgeHandlers.cs`
- `frontend/src/components/git/**/*`
- `frontend/src/components/sidebar/SidePanel.tsx`
- `frontend/src/components/shell/ActivityBar.tsx`
- `frontend/tests/**/*git-history*.spec.ts`

**Estimated scope:** M.

## Task 18: Configurable Git Commit Author Identity

**Status:** Complete. Git author settings are persisted, validated, exposed in settings UI, and applied to repository-local Git config before new or existing repository commits.

**Description:** Let users set the Git author name/email used for per-novel commits.

**Acceptance criteria:**

- [x] Settings UI exposes Git author name and email with validation.
- [x] Empty values fall back to safe defaults.
- [x] Existing repositories receive updated repo-local Git config after settings save or before next commit.
- [x] New repositories created by novel creation/import use configured identity.
- [x] Invalid email/name cannot be saved silently; errors are visible.

**Verification:**

- [x] Settings service tests for persistence and defaults.
- [x] Git integration test proves commits carry configured author.
- [x] Playwright settings test for save, validation failure, and persistence after reload.

**Dependencies:** Tasks 2 and 16.

**Files likely touched:**

- `src/Novelist.Contracts/App/AppSettingsPayload.cs`
- `src/Novelist.Core/App/IAppSettingsService.cs`
- `src/Novelist.Infrastructure/App/FileSystemAppSettingsService.cs`
- `src/Novelist.Infrastructure/App/GitVersionControlService.cs`
- `frontend/src/components/settings/GeneralConfigTab.tsx`

**Estimated scope:** S.

## Task 19: Sidebar, Chat Panel, and Window State Persistence

**Status:** Partial. Resizable sidebar/chat panels, unified layout settings persistence, reload and compact viewport clamping, corrupted layout fallback, viewport/maximized state saving, and startup window width/height/maximized launch settings are implemented and covered. True desktop window position capture/restore remains open; the current runtime path does not expose reliable window coordinates to persist.

**Description:** Add robust layout persistence for resizable panels and desktop window state.

**Acceptance criteria:**

- [x] Sidebar and chat panel widths can be dragged with min/max constraints.
- [x] Widths persist and are clamped on reload or when viewport shrinks.
- [ ] Window size/position/maximized state persist through current Photino runtime APIs where available.
- [ ] Restored window bounds are clamped to visible screen area to avoid off-screen windows.
- [x] Local storage or settings corruption falls back to defaults without runtime errors.
- [x] Keyboard/mouse interactions do not select text while dragging and do not cause layout jumps.

**Verification:**

- [x] Unit tests for clamp/load helpers.
- [x] Playwright tests for resize, reload persistence, compact viewport clamp, corrupted stored values, and no text overlap.
- [ ] Targeted Photino desktop test if window APIs are available in test harness.

**Dependencies:** Task 2.

**Files likely touched:**

- `frontend/src/hooks/useLayoutState.ts`
- `frontend/src/hooks/useWindowState.ts`
- `frontend/src/lib/layout.ts`
- `frontend/src/components/sidebar/SidePanel.tsx`
- `frontend/src/components/chat/ChatPanel.tsx`
- `frontend/src/views/WorkspaceView.tsx`
- `src/Novelist.App/Desktop/PhotinoLaunchMode.cs`
- `src/Novelist.App/Desktop/PhotinoWindowFactory.cs`
- `src/Novelist.App/Desktop/PhotinoWindowSettings.cs`

**Estimated scope:** S.

## Task 20: Update Check Service and Dismissible Update Dialog

**Status:** Complete. Backend service, bridge route, settings UI, startup/manual update UX, dismiss cache, explicit external URL guardrail, fake-HTTP tests, and mocked browser workflow are implemented.

**Description:** Add automatic and manual update checking with clear UX and bounded network behavior.

**Acceptance criteria:**

- [x] Service checks the latest release from the configured official repository endpoint with a short timeout and user agent.
- [x] Semantic version comparison handles `v` prefixes, missing patch components, and pre-release suffixes conservatively.
- [x] Automatic checks are dismissible per version and never block app startup.
- [x] Manual checks report no update, update available, and network/parse failures.
- [x] Release notes render as sanitized markdown.
- [x] Download/open-release action uses the existing external URL opener and requires explicit user action.
- [x] Tests disable live network by default and use fake HTTP responses.

**Verification:**

- [x] Unit tests for semver comparison and response validation.
- [x] Integration tests with fake HTTP handler for latest release, no releases, non-200, timeout, malformed JSON, dismissed version.
- [x] Playwright tests for automatic notification, dismiss, manual check no update, manual check failure, and external URL guardrail.

**Dependencies:** Tasks 1-2.

**Files likely touched:**

- `src/Novelist.Contracts/App/UpdatePayloads.cs`
- `src/Novelist.Core/App/IUpdateCheckService.cs`
- `src/Novelist.Infrastructure/App/GitHubUpdateCheckService.cs`
- `src/Novelist.App/Desktop/SystemExternalUrlOpener.cs`
- `frontend/src/components/update/UpdateDialog.tsx`
- `frontend/src/components/settings/GeneralConfigTab.tsx`

**Estimated scope:** S.

## Task 21: Unified Visible Error Feedback and Copyable Diagnostics

**Status:** Partial. Shared diagnostic formatting/redaction helpers, shared clipboard fallback, a reusable `ErrorCallout`, and representative UI integrations are in place. Character and location main views plus sidebar lists, skill list, story arc create/update/delete, arc-node create/update/delete/quick-status, timeline plan save and entry create/update/delete/quick-status, reader perspective create/update/delete/quick-reveal failures, preference create/update/delete failures, settings/provider load/save/test/discovery failures, chapter title rename, editor save failure, export failure, skill edit save failure, import failure, novel create/update/delete dialogs, style sample list/detail/create/update/delete failures, legacy style extraction dialog failures, narrative pattern bridge failure, style extraction failure, Git history failure, UpdateDialog release-open failure, update settings save/manual-check failures, and representative reference-anchor create/rebuild/search/blueprint review/approval/binding failures now show visible callouts with copyable redacted diagnostics. Automated accessibility coverage now checks `role="alert"`, named/focusable copy buttons, no unnamed visible controls inside copyable callouts, and named visible controls on reference-anchor default/advanced surfaces. Remaining work is the full legacy-surface error lifecycle review for accidental clearing outside the representative reference-anchor retry/close coverage.

**Description:** Fix silent failures and inconsistent error presentation across CRUD dialogs, metadata panels, import, style, pattern, Git, and update workflows.

**Acceptance criteria:**

- [x] Character, location, skill, story arc, timeline, reader perspective, preference, settings/provider, chapter-title rename, editor save, export, dialog save/delete, import, style sample CRUD/library, style extraction, pattern extraction, Git history, update-check, and representative reference-anchor failures all show visible errors. Character/location/skill/story arc create-update-delete/arc-node create-update-delete-quick-status/timeline plan save/timeline entry create-update-delete-quick-status/reader perspective create-update-delete-quick-reveal/preference create-update-delete/settings provider load-save-test-discovery/chapter/editor-save/export/import/novel dialog save-delete/style sample list-detail-create-update-delete/legacy style extraction/style extraction/pattern/Git, UpdateDialog release-open, update settings save/manual-check failure, and reference anchor create/rebuild/search/blueprint review/approval/binding representative paths are covered.
- [x] Error components include a copy button that copies structured diagnostics: code, message, detail, operation, task id/run id, timestamp, and optional bridge method.
- [x] Diagnostics redact API keys, bearer tokens, local secret-like values, and long raw source content.
- [x] English locale numeric formatting no longer displays malformed values in error text.
- [ ] Error state is cleared only on retry/success or explicit close, not accidentally by unrelated rerenders. Shared copyable callouts now assert the callout remains visible after copy-feedback rerenders; reference-anchor create failure now persists through unrelated form edits until explicit close, and blueprint generate/review/approve failures clear after successful retry. Remaining legacy surfaces still need the same lifecycle review.
- [x] Existing Markdown code-copy behavior remains intact.

**Verification:**

- [x] Unit tests for diagnostic redaction and localized number formatting.
- [x] Playwright tests for representative failures in character delete, location delete, skill delete, story arc create/update/delete, arc-node create/update/delete/quick-status, timeline plan save, timeline entry create/update/delete/quick-status, reader perspective create/update/delete/quick-reveal, preference create/update/delete, settings provider load/save/test/discovery, chapter rename, editor save, export, import failure, novel create/update/delete dialogs, style sample list/detail/create/update/delete, legacy style extraction/save, style extraction failure, pattern failure, update release-open failure, update settings save failure, manual update-check failure, reference-anchor create/rebuild/search/blueprint review/approval/binding failure, copy button behavior, copy-feedback rerender persistence, explicit close, and successful retry clearing. Character/location/skill/story-arc/timeline/reader/preference/chapter/editor-save/export/import/novel dialogs/style sample CRUD/legacy style extraction/style extraction/pattern and copy/redaction are covered by `test:error-ui`; reference-anchor lifecycle coverage is covered by `test:error-ui` through the `@error` workflow; settings/provider copy diagnostics are covered by the `@surface` workflow; Git history failure copy diagnostics are covered by the `@git` workflow; update-check copy diagnostics are covered by the `@update` workflow.
- [x] Accessibility check for copy buttons and error region labels. `assertErrorCalloutAccessibility()` checks `role="alert"`, named visible controls inside each copyable callout, and keyboard focusability for the `复制错误诊断` copy action; `verifyReferenceAccessibilityReview()` checks reference-anchor default and advanced surfaces for named visible interactive controls and verifies advanced mode opens via keyboard.

**Dependencies:** Task 1.

**Files likely touched:**

- `frontend/src/components/shared/ErrorCallout.tsx`
- `frontend/src/lib/diagnostics.ts`
- `frontend/src/lib/clipboard.ts`
- `frontend/src/components/character/**/*`
- `frontend/src/components/location/**/*`
- `frontend/src/components/preference/PreferenceView.tsx`
- `frontend/src/components/reader/ReaderView.tsx`
- `frontend/src/components/storyarc/ArcListView.tsx`
- `frontend/src/components/timeline/TimelineView.tsx`
- `frontend/src/components/skill/**/*`
- `frontend/src/components/content/ContentPanel.tsx`
- `frontend/src/components/export/ExportDialog.tsx`
- `frontend/src/components/skill/SkillEditForm.tsx`
- `frontend/src/components/skill/ExtractStyleDialog.tsx`
- `frontend/src/components/sidebar/ChapterList.tsx`
- `frontend/src/components/novel/**/*Import*.tsx`
- `frontend/src/components/novel/NovelEditDialog.tsx`
- `frontend/src/components/novel/NovelDeleteDialog.tsx`
- `frontend/src/components/style/StyleSampleLibraryView.tsx`
- `frontend/src/components/style/StyleExtractionPanel.tsx`
- `frontend/src/components/pattern/NarrativePatternView.tsx`
- `frontend/src/components/git/GitHistoryView.tsx`
- `frontend/src/components/update/UpdateDialog.tsx`
- `frontend/src/components/settings/GeneralConfigTab.tsx`
- `frontend/src/hooks/useNovelImport.ts`
- `frontend/src/hooks/usePatternProgress.ts`
- `frontend/scripts/app-mock-workflow.mjs`
- `frontend/scripts/app-mock-workflow/error-feedback.mjs`
- `frontend/tests/diagnostics.test.mjs`
- `src/Novelist.Contracts/Bridge/BridgeError.cs`
- `src/Novelist.Core/Bridge/BridgeDispatcher.cs`

**Estimated scope:** M.

## Task 22: Relative-Time Refresh and Localized Formatting Audit

**Status:** Complete. Shared time formatting helpers, mounted relative-time refresh timers, chat/session/Git UI integration, unit coverage, and fake-clock Playwright coverage are implemented.

**Description:** Fix stale "time ago" displays and audit localized formatting across chat history, recent sessions, Git history, and update/error surfaces.

**Acceptance criteria:**

- [x] Relative-time labels refresh on a timer while panels are mounted.
- [x] Timer frequency balances freshness and CPU use: faster for under-hour labels, slower for older labels.
- [x] Timers clean up on unmount and do not leak after navigation.
- [x] Locale-specific date/number formatting uses `Intl` consistently and never mixes raw placeholders with formatted strings.
- [x] Tests cover English and Chinese labels where existing i18n supports both.

**Verification:**

- [x] Unit tests for relative-time formatting boundaries.
- [x] Playwright fake-clock tests for chat/session/Git relative time refreshing.
- [x] i18n lint or targeted checks for missing keys used by Phase 15 surfaces.

**Dependencies:** Task 21 can run in parallel after shared helpers are defined.

**Files likely touched:**

- `frontend/src/lib/time.ts`
- `frontend/src/components/chat/RecentSessions.tsx`
- `frontend/src/components/chat/SessionHistory.tsx`
- `frontend/src/components/git/GitHistoryList.tsx`
- `frontend/src/i18n/locales/*.json`

**Estimated scope:** S.

## Task 23: LibGit2Sharp Git Runtime and Empty Repository Hardening

**Status:** Complete. Version history now uses `LibGit2Sharp` 0.31.0 and its `LibGit2Sharp.NativeBinaries` dependency rather than a local Git executable. The service initializes and opens repositories through libgit2, preserves empty-history behavior for read-only calls, creates the baseline commit through `EnsureRepositoryAsync`, applies repo-local author settings before commits, walks `HEAD` by first-parent order for latest-first history, rejects stale lock files without deleting them, reports invalid repository metadata through stable version-control errors, and ignores the old `NOVELIST_GIT_PATH`/PATH execution path.

**Description:** Consolidate Git bug fixes so repository initialization and history features work across platforms and empty states.

**Acceptance criteria:**

- [x] macOS app bundles no longer need Git candidate path probing; libgit2 native assets are supplied by the NuGet runtime package.
- [x] Windows and Linux no longer depend on system Git resolution; the old `NOVELIST_GIT_PATH` path is ignored by the service.
- [x] Empty repository initialization creates a valid first commit through `EnsureRepositoryAsync`, while read-only history calls keep the documented empty history state.
- [x] Read-only file attributes and stale lock files are handled conservatively with visible errors when automatic recovery is unsafe.
- [x] LibGit2Sharp/IO failures are normalized enough for bridge error classification without relying on localized Git CLI stderr.

**Verification:**

- [x] No-local-Git regression proving a configured/missing `NOVELIST_GIT_PATH` does not affect commits.
- [x] Integration tests for empty repo log/file-list/diff behavior, invalid metadata, and stale lock-file refusal.
- [x] Regression tests for commit after import and commit after normal save.

**Dependencies:** Task 16.

**Files likely touched:**

- `src/Novelist.Infrastructure/App/GitVersionControlService.cs`
- `tests/Novelist.Tests/**/*Git*Tests.cs`
- `tests/Novelist.IntegrationTests/**/*Git*Tests.cs`

**Estimated scope:** S.

## Task 24: Agent and Skill Boundary Review

**Status:** Complete. MAF registry coverage now proves Phase 15 desktop/bridge-only capabilities are not exposed as agent tools: import/recovery, file pickers, update checks, external URL opening, Git/history operations, style sample CRUD, style extraction, narrative pattern extraction, reference-source import/metadata/archive/rebuild, reference style profile mutation, orchestration resume/decision approval, and final insertion/save helpers are absent. Existing agent tools remain limited to bounded chapter/skill reads, approved structured metadata operations, web search/fetch, and reference inspection/candidate-preparation paths with explicit no-direct-chapter-mutation/no-arbitrary-file-read descriptions.

**Description:** Decide which Phase 15 features agents may inspect or use, and explicitly block unsafe operations.

**Acceptance criteria:**

- [x] Agents may list/read existing skills and possibly inspect style sample summaries only if source text and scope policy are safe. Current agent exposure keeps skill access inside the bounded `read` tool (`skills/<name>.md`, user skills, and built-ins) and does not expose style sample CRUD/detail tools.
- [x] Agents cannot import arbitrary local files, pick files, run update checks, open external URLs, mutate Git history, or approve final insertion through Phase 15 features.
- [x] Narrative pattern and style extraction remain user/bridge initiated unless a separate agent authority design is approved.
- [x] Tool descriptions make no-direct-chapter-mutation and no-arbitrary-file-read boundaries explicit.

**Verification:**

- [x] MAF registry tests for allowed/forbidden Phase 15 tool exposure.
- [x] Guardrail tests proving no import/source-path/update/Git-mutating tools are exposed to agents.

**Dependencies:** Tasks 9, 11, 14, 16.

**Files likely touched:**

- `src/Novelist.Agent/**/*`
- `tests/Novelist.Tests/MafToolRegistryTests.cs`

**Estimated scope:** S.

## Task 25: End-to-End Import, Style, Pattern, Git, and Error Playwright Suite

**Status:** Partial. `npm --prefix frontend run test:phase15` now exists and runs focused mocked Photino bridge slices for Phase 15 surface/import/style/settings, reference-anchor error lifecycle, pattern, Git history, layout persistence, relative time, update checks, and unified error feedback. The app mock harness now supports `--phase=phase15`, so screenshots, diagnostics, bridge-call logs, traces, and fixture artifacts land under `output/playwright/phase15/` while existing Phase 13 commands keep their default output path. Remaining work is the dedicated stress coverage for large TXT/MD import plus large Git history/diff fixtures and the full compact-viewport matrix across each named Phase 15 surface.

**Description:** Extend the Phase 13 app-wide QA approach with Phase 15-specific product workflows and regression artifacts.

**Acceptance criteria:**

- [x] Add `npm --prefix frontend run test:phase15` or equivalent focused command.
- [x] Suite uses mocked Photino bridge and deterministic fixtures for import/style/pattern/Git/update/error workflows.
- [x] Captures screenshots, console diagnostics, bridge-call logs, traces, and failure artifacts under `output/playwright/phase15/`.
- [x] Asserts no implicit `SaveContent` during import/style/pattern/Git/update workflows except explicit editor save paths.
- [ ] Stress path includes a large TXT/MD import fixture and a large Git history/diff fixture.
- [ ] Compact viewport checks cover import dialog, style library, pattern progress, Git history, settings, and error callouts.

**Verification:**

- [x] `npm --prefix frontend run test:phase15`
- [ ] Existing `test:app`, `test:app:full`, `test:app:stress`, `test:app:usability`, and `test:reference-anchor` remain green.

**Dependencies:** Tasks 7, 8, 10, 15, 17, 18, 19, 20, 21, 22.

**Files likely touched:**

- `frontend/package.json`
- `frontend/tests/**/*phase15*.spec.ts`
- `frontend/scripts/*playwright*.mjs`
- `output/playwright/phase15/` generated at runtime only
- `frontend/package.json`
- `frontend/scripts/app-mock-workflow/app-harness.mjs`
- `frontend/scripts/app-mock-workflow/bridge-guardrails.mjs`
- `frontend/scripts/app-mock-workflow/reference-error-feedback.mjs`
- `frontend/scripts/app-mock-workflow/runtime.mjs`
- `frontend/scripts/app-mock-workflow/suite-runners.mjs`

**Estimated scope:** M.

## Task 26: Backend Regression, Stress, and Fault-Injection Gate

**Description:** Add backend verification that Phase 15 is robust under malformed inputs, large files, partial failures, and platform differences.

**Acceptance criteria:**

- [ ] Import parsers and workflow have unit/integration coverage for malformed EPUB, malformed encodings, large TXT/MD, chapter-boundary edge cases, write failure, DB failure, Git failure, and cleanup.
- [x] Startup import recovery has integration coverage for simulated process death after each durable import phase, idempotent cleanup, and cleanup-blocked path corruption.
- [ ] Encoding tests include UTF-16 LE/BE with BOM, conservative UTF-16 no-BOM detection, GB18030 provider availability, binary-looking files, low-confidence decode failure, and visible diagnostics.
- [ ] Size-limit tests cover TXT/MD source limits, compressed EPUB limits, and uncompressed EPUB expansion limits.
- [ ] Style stats/extraction tests cover malformed model output, cancellation, invalid skill output, and scoped authorization.
- [ ] Pattern extraction tests cover invalid tool JSON, retry boundaries, compression stall, selected ranges, cancellation, and no-live-network default composition.
- [ ] Git tests cover empty repos, missing/corrupt native runtime or repository metadata, lock files, renames, deleted files, binary/large diff handling, and custom author identity.
- [ ] Update tests use fake HTTP and never depend on GitHub live availability.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter 'Import|StyleSample|StyleExtraction|NarrativePattern|Git|Update|Error' -v minimal`
- [ ] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'Import|StyleSample|StyleExtraction|NarrativePattern|Git|Update' -v minimal`
- [ ] Full `dotnet test Novelist.slnx --no-restore -v minimal`.

**Dependencies:** All backend feature tasks.

**Files likely touched:**

- `tests/Novelist.Tests/**/*`
- `tests/Novelist.IntegrationTests/**/*`
**Estimated scope:** M.

## Task 27: Documentation, Release Notes, and User-Facing Help

**Description:** Update docs so Phase 15 behavior is discoverable and future agents do not reintroduce legacy runtime code.

**Acceptance criteria:**

- [ ] README/README_EN mention supported import formats, style material library, narrative pattern extraction, Git history, update checks, and configurable Git author identity after implementation lands.
- [ ] Release notes summarize user-facing changes and verification commands.
- [ ] Help/settings copy explains import cleanup/recovery behavior, encoding/size-limit rejection, update-check behavior, Git author settings, and style/pattern extraction limitations.
- [ ] Developer docs record Phase 15 bridge methods, guardrails, and known deferred scope.
- [ ] Docs explicitly keep `goink-master` as a read-only legacy reference, not an implementation target.

**Verification:**

- [ ] Documentation links resolve.
- [ ] `rg` confirms no new references to `frontend/src/lib/wailsjs`, Go/Wails build commands, or legacy app/internal implementation paths outside explanatory docs.

**Dependencies:** Implementation tasks complete enough that docs match behavior.

**Files likely touched:**

- `README.md`
- `README_EN.md`
- `docs/releases/release-notes.md`
- `docs/reference-anchor-implementation/*.md`
- `frontend/src/components/help/HelpDialog.tsx`

**Estimated scope:** S.

## Checkpoints

### Checkpoint A: Contracts, Storage, and Safety Boundary

- [x] Tasks 1-3 complete.
- [x] Preflight decisions are closed and reflected in contracts/settings defaults.
- [x] New bridge contracts are registered and covered.
- [x] No legacy Wails/Go/Python implementation path is introduced.
- [x] Settings migrations preserve existing user data.

### Checkpoint B: Import Pipeline

- [x] Tasks 4-8 complete.
- [x] TXT/MD/EPUB import works with progress, cancellation, caught-failure cleanup, crash recovery, size-limit rejection, encoding diagnostics, and Git warning behavior.
- [x] Import tests prove no orphaned DB/files remain after pre-completion failures or simulated process death.

### Checkpoint C: Style and Pattern Workflows

- [x] Tasks 9-15 complete.
- [x] Style samples are durable, searchable, statistically inspectable, and can produce validated skills.
- [x] Pattern extraction supports selected chapter ranges, progress, cancellation, trace inspection, and validated skill generation.
- [x] Neither workflow mutates chapter content.

### Checkpoint D: Git, Settings, Update, and Layout UX

- [ ] Tasks 16-23 complete.
- [ ] Git history UI is read-only, paged, diff-capable, and robust for empty repos.
- [ ] Git author identity, update checks, layout/window persistence, relative-time refresh, and visible copyable errors are implemented.

### Checkpoint E: Guardrails, QA, and Documentation

- [ ] Tasks 24-27 complete.
- [x] Agent boundaries are explicit and tested.
- [ ] Phase 15 Playwright suite and backend stress/fault-injection tests pass.
- [ ] README/release/help docs match implemented behavior.

## Definition of Done

Phase 15 is complete only when all of the following are true:

- [ ] EPUB/TXT/MD import works from file picker and drag/drop with encoding detection, progress, cancellation, skipped-chapter reporting, Git integration, and cleanup on failure.
- [ ] TXT/MD import handles UTF-8, UTF-16 LE/BE with BOM, conservative UTF-16 no-BOM detection, and GB18030 without silent mojibake.
- [ ] Import size limits are enforced with visible structured errors.
- [ ] Startup import recovery reconciles incomplete import runs idempotently and blocks unsafe cleanup with diagnostics instead of guessing.
- [ ] The import workflow preserves user data when only the post-import Git commit fails and reports that warning clearly.
- [ ] Successful import triggers normal search/RAG indexing behavior but does not implicitly create reference anchors, style samples, or style profiles.
- [ ] Style sample library supports CRUD, tags, global/per-novel filtering, pagination, deterministic stats, and LLM-backed validated skill extraction with cancellation.
- [ ] Style sample behavior is aligned with Phase 14 style profiles and does not create a prompt-only bypass of style audit/provenance gates.
- [ ] Narrative pattern extraction supports all chapters and multi-range selection, visible staged progress, validated LLM JSON, recursive compression safeguards, cancellation, trace inspection, and validated skill preview/save.
- [ ] Git history panel supports paged commits, changed-file lists, renamed-file markers, lazy read-only diffs, empty repo handling, and cross-platform libgit2 runtime coverage.
- [ ] Git author name/email can be configured and applied to new and existing repositories.
- [ ] Sidebar/chat/window layout persistence works and is clamped safely.
- [ ] Update checks are timeout-bounded, dismissible, manually retryable, testable without live network, and use explicit external URL opening.
- [ ] CRUD/dialog failures no longer fail silently; copyable diagnostics exist and redact sensitive content.
- [ ] Relative-time labels refresh while the app remains open.
- [x] Agent tools cannot import arbitrary files, run file pickers, mutate Git history, trigger update checks, open URLs, or bypass final insertion/SaveContent boundaries.
- [ ] Full regression matrix passes:

```text
npm --prefix frontend run build
npm --prefix frontend run lint
npm --prefix frontend run test:reference-anchor
npm --prefix frontend run test:app
npm --prefix frontend run test:app:full
npm --prefix frontend run test:app:stress
npm --prefix frontend run test:app:usability
npm --prefix frontend run test:phase15
dotnet test Novelist.slnx --no-restore -v minimal
```

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Import leaves orphaned files or rows after partial failure | Data corruption and confusing UI | Transaction-like sequencing, compensating cleanup, fault-injection tests, import run state |
| App crashes during import before cleanup runs | Orphaned novels/files and broken startup state | Durable import-run state machine, startup reconciliation, idempotent cleanup, cleanup-blocked diagnostics |
| Encoding detection corrupts Chinese text silently | Imported chapters become unusable | Strict UTF-8 validation, GB18030 fallback, binary heuristics, fixture coverage, user-visible decoder diagnostics |
| UTF-16 Windows TXT files are rejected or misdecoded | Common imported files fail or become corrupted | BOM support, conservative NUL-distribution heuristic, low-confidence rejection tests |
| Oversized imports exhaust memory | White screen, app hang, or process crash | Default file and EPUB expansion limits, streaming/bounded reads where practical, stress tests |
| EPUB parser mishandles malicious internal paths | Path traversal or crash risk | Only read zip entries, normalize internal paths with archive-bound lookup, zip-slip tests |
| Style sample library duplicates Phase 14 style profile concepts | Two incompatible style systems | Map overlapping stats/features, document boundaries, require profile/audit gates for reference-anchored drafting |
| LLM style/pattern output is malformed or unsafe | Bad skills and misleading guidance | Validate markdown/frontmatter/tool JSON, preview before save, fake-provider tests, copyable validation errors |
| Pattern extraction consumes too much context or loops | Long hangs and poor UX | Context-window batching, convergence checks, max rounds, cancellation, progress events |
| Git history freezes on large diffs | UI hangs | Lazy diff loading, size limits/truncation, Monaco read-only config, Playwright stress |
| libgit2 native asset missing in packaged app | Versioning unavailable for users on that RID | Rely on `LibGit2Sharp.NativeBinaries` publish assets, keep invalid-runtime errors visible, and add packaged desktop smoke tests |
| Update check creates startup/network fragility | App feels broken offline | Timeout, non-blocking startup, dismiss cache, fake-HTTP tests |
| Error copy leaks secrets | Security/privacy issue | Central redaction helper, tests for keys/tokens/source text, conservative truncation |
| Layout persistence restores off-screen windows | App appears lost | Screen-bound clamping, invalid setting fallback, compact viewport tests |

## Open Questions

No blocking open questions remain for implementation planning. The former open questions are closed in **Preflight Decisions** above. New product questions discovered during implementation should be added as explicit decision records before changing contracts or storage.

## Parallelization Opportunities

- Tasks 4 and 5 can run in parallel after contracts/path boundary.
- Task 7 must follow the import-run state model from Tasks 2 and 6, but its frontend startup-state handling can be developed in parallel once recovery payloads exist.
- Tasks 9-11 can run while import frontend work proceeds, after storage contracts exist.
- Tasks 16-18 can run independently of style/pattern once settings contracts are defined.
- Tasks 19-22 are mostly frontend/platform hardening and can run in parallel with backend feature work if shared error/layout helpers are agreed first.
- Task 24 should wait until bridge methods are stable enough to audit.
- Tasks 25-26 should start early with harness scaffolding but close only after feature slices land.

## Files Likely Touched

- `src/Novelist.Contracts/App/*Import*Payloads.cs`
- `src/Novelist.Contracts/App/*StyleSample*Payloads.cs`
- `src/Novelist.Contracts/App/*NarrativePattern*Payloads.cs`
- `src/Novelist.Contracts/App/*Git*Payloads.cs`
- `src/Novelist.Contracts/App/*Update*Payloads.cs`
- `src/Novelist.Core/App/INovelImportService.cs`
- `src/Novelist.Core/App/INovelImportRecoveryService.cs`
- `src/Novelist.Core/App/IStyleSampleService.cs`
- `src/Novelist.Core/App/IStyleExtractionService.cs`
- `src/Novelist.Core/App/INarrativePatternExtractionService.cs`
- `src/Novelist.Core/App/IVersionControlService.cs`
- `src/Novelist.Core/App/IUpdateCheckService.cs`
- `src/Novelist.Core/Bridge/*Import*BridgeHandlers.cs`
- `src/Novelist.Core/Bridge/*Style*BridgeHandlers.cs`
- `src/Novelist.Core/Bridge/*Pattern*BridgeHandlers.cs`
- `src/Novelist.Core/Bridge/*Git*BridgeHandlers.cs`
- `src/Novelist.Core/Bridge/*Update*BridgeHandlers.cs`
- `src/Novelist.Infrastructure/App/*NovelImport*.cs`
- `src/Novelist.Infrastructure/App/*Style*.cs`
- `src/Novelist.Infrastructure/App/*NarrativePattern*.cs`
- `src/Novelist.Infrastructure/App/GitVersionControlService.cs`
- `src/Novelist.Infrastructure/App/FileSystemAppSettingsService.cs`
- `src/Novelist.App/Desktop/*FilePicker*.cs`
- `src/Novelist.App/Desktop/PhotinoWindowSettings.cs`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- `frontend/src/components/novel/**/*Import*.tsx`
- `frontend/src/components/style/**/*`
- `frontend/src/components/pattern/**/*`
- `frontend/src/components/git/**/*`
- `frontend/src/components/settings/GeneralConfigTab.tsx`
- `frontend/src/components/shared/ErrorCallout.tsx`
- `frontend/src/hooks/useLayoutState.ts`
- `frontend/src/hooks/useWindowState.ts`
- `frontend/tests/**/*phase15*.spec.ts`
- `tests/Novelist.Tests/**/*`
- `tests/Novelist.IntegrationTests/**/*`
