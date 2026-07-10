# Novelist Photino Bridge Contract

## Purpose

This is the primary frontend/backend contract for the Photino.NET migration. It replaces loopback REST/SignalR as the default packaged-app transport so novelist keeps Photino's lightweight runtime and avoids unnecessary localhost HTTP overhead.

References:

- Photino.NET supports JavaScript-to-.NET messages through `window.external.sendMessage(...)` and .NET-to-JavaScript messages through `SendWebMessage(...)`: https://github.com/tryphotino/photino.Samples
- Golden payload fixtures: [contracts/golden](./contracts/golden/)

## Transport Rules

- Packaged desktop mode uses Photino WebMessage for commands, queries, events, and desktop runtime operations.
- Packaged desktop mode does not start ASP.NET Core/Kestrel. Built frontend assets load from local `frontend/dist`, and business calls use the bridge.
- Development may point Photino at Vite with `--start-url=http://localhost:5173/`; that URL is only a frontend asset source, not a business API transport.
- React components must not call `window.external` directly. They use `frontend/src/lib/novelist/bridge.ts`.
- The bridge keeps stable method names where they are part of the owned frontend adapter contract.

## Message Envelopes

### Request

```json
{
  "kind": "request",
  "id": "req_01JZ8Q0T6H2M7JQGFX4VQJ8K0P",
  "method": "GetNovels",
  "payload": {},
  "deadline_ms": 30000
}
```

### Success Response

```json
{
  "kind": "response",
  "id": "req_01JZ8Q0T6H2M7JQGFX4VQJ8K0P",
  "ok": true,
  "result": []
}
```

### Error Response

```json
{
  "kind": "response",
  "id": "req_01JZ8Q0T6H2M7JQGFX4VQJ8K0P",
  "ok": false,
  "error": {
    "code": "INVALID_PATH",
    "message": "The path must stay inside the novel workspace.",
    "details": {
      "path": "Absolute paths and parent directory segments are not allowed."
    },
    "retryable": false
  }
}
```

### Event

```json
{
  "kind": "event",
  "name": "agent:7",
  "payload": {
    "turn_id": 7,
    "seq": 1,
    "type": 2,
    "data": "text delta",
    "timestamp": "2026-07-03T09:30:04Z"
  }
}
```

### Cancellation

Long-running bridge requests support explicit cancellation:

```json
{
  "kind": "cancel",
  "id": "req_01JZ8Q0T6H2M7JQGFX4VQJ8K0P"
}
```

`CancelChat` remains a compatibility method for existing UI behavior and must cancel the active chat operation by `session_id`.

## Dispatcher Requirements

- Unknown `method` returns `METHOD_NOT_FOUND`.
- Malformed JSON returns `INVALID_MESSAGE`.
- Payload validation errors return `VALIDATION_ERROR`.
- File path violations return `INVALID_PATH`.
- Unhandled internal failures return `INTERNAL_ERROR` without stack traces or secrets.
- Responses must preserve request `id`.
- Events do not require request ids.
- `seq`, when present on agent events, is monotonic per turn.
- Bridge calls must run off the UI thread when work can block.
- Long-running operations must accept cancellation tokens.

## Limits And Backpressure

Initial defaults:

| Limit | Default | Reason |
| --- | --- | --- |
| Max message bytes | 1 MiB | Prevent accidental UI bridge overload |
| Default deadline | 30 seconds | Bound normal commands |
| Chat deadline | None while streaming | Chat completion is controlled by cancellation |
| Event batch window | 16 ms | Avoid flooding the WebView with token-by-token messages |
| Max queued events per turn | 1000 | Prevent unbounded memory growth |

Large binary payloads, such as avatar images or exports, should use file handles or chunked bridge messages rather than a single oversized WebMessage. `GetCover` is the current bounded exception: it returns base64 only when the stored cover is within `NovelCoverConstraints.MaxBridgeBytes`.

## Compatibility Method Coverage

Frontend App API methods map to bridge methods with the same names. The block below is a historical core-method snapshot, not an exhaustive current inventory; the bridge method catalog/dispatcher registrations and their contract tests are authoritative, including newer reference-corpus analysis and blueprint-session methods.

```text
ApproveTool, CancelChat, Chat, CompressContext,
CreateArcNode, CreateChapter, CreateCharacter, CreateLocation, CreateNovel,
CreatePreference, CreateReaderPerspective, CreateStoryArc, CreateTimelineEntry,
DeleteArcNode, DeleteCharacter, DeleteCover, DeleteLocation, DeleteNovel,
DeletePreference, DeleteReaderPerspective, DeleteSkill, DeleteStoryArc,
DeleteTimelineEntry, DiscoverModels, ExportNovel, ExtractStyle,
GetAppConfig, GetArcNodes, GetChapterPlans, GetChapters,
GetCharacterRelations, GetCharacters, GetContent, GetCover,
GetEmbeddingConfig, GetLLMConfig,
GetLocationRelations, GetLocations, GetMaxChapterNumber, GetModels,
GetNovels, GetPlatform, GetPreferences, GetReaderPerspectives,
GetReferenceAnchorBuildStatus, GetReferenceAnchors,
GetReferenceAnchoredDraftAudits,
GetReferenceChapterBlueprint, GetReferenceChapterBlueprints,
GetReferenceOrchestrationRun, GetReferenceOrchestrationRuns,
GetReferenceUserFeedback, GetSession, GetSessionMessages, GetSessions,
GetSettings, GetSqliteVecStatus, GetStoryArcs,
GetTimelineEntries, GetWritingActivity, GetWritingStats,
Initialize, IsInitialized, ListSkills, ListSlashCommands,
ImportReferenceAnchor, RebuildNovelIndex, RebuildReferenceAnchor,
RecordReferenceUserFeedback, ResumeReferenceOrchestrationRun,
ReviseReferenceChapterBlueprint, ReviewReferenceChapterBlueprint,
SaveAvatar, SaveContent, SaveCover, SaveEmbeddingConfig,
SaveLLMConfig, SaveSettings, SaveUserName, SearchAll, SearchReferenceMaterials,
SearchStoryMemory, SetActiveNovel, SetApprovalMode,
SetChatPanelWidth, SetLastSession, SetReasoningEffort, SetSelectedModel,
StartReferenceOrchestrationRun, TestConnection, TestEmbeddingConnection,
UpdateArcNode, UpdateChapterPlan, UpdateChapterTitle,
UpdateCharacter, UpdateDataDir, UpdateReferenceMaterialTags,
UpdateLocation, UpdateNovel,
UpdatePreference, UpdateReaderPerspective, UpdateStoryArc,
UpdateTimelineEntry,
AdaptReferenceMaterial, ApproveReferenceChapterBlueprint,
AuditReferenceAnchoredDraft, AuditReferenceReuse,
BindReferenceBlueprintMaterials, CancelReferenceOrchestrationRun,
GenerateReferenceAnchoredDraft, GenerateReferenceChapterBlueprint
```

Phase 15 adds product-specific methods to the same Photino app bridge. They are grouped here because future agents should not re-create legacy Wails bindings or route these through an HTTP side channel:

```text
PickNovelImportFile,
StartNovelImport, CancelNovelImport, GetNovelImportRun,
GetNovelImportRecoveryStatus, ReconcileNovelImportRuns,
CreateStyleSample, UpdateStyleSample, DeleteStyleSample,
GetStyleSample, SearchStyleSamples,
ExtractStyleSkillFromSamples, CancelStyleSkillExtraction,
GetStyleSkillExtractionRun,
StartNarrativePatternExtraction, CancelNarrativePatternExtraction,
GetNarrativePatternRun, GetNarrativePatternTrace,
GetGitCommits, GetGitCommitFiles, GetGitFileDiff,
GetGitAuthorSettings, SaveGitAuthorSettings,
CheckForUpdates, GetUpdateCheckSettings, SaveUpdateCheckSettings,
GetLayoutSettings, SaveLayoutSettings,
GetWindowSettings, SaveWindowSettings
```

These methods live in the current `.NET 10 + Photino.NET + React/Vite` architecture. The legacy `goink-master` tree remains a read-only behavior reference; do not add new implementations under legacy `app/`, `internal/`, `python-master/`, or `frontend/src/lib/wailsjs/`, and do not reintroduce Go/Wails build commands for Phase 15 behavior.

Runtime-only desktop methods stay under the `runtime.*` namespace and are not app data methods:

```text
runtime.window.minimize,
runtime.window.toggleMaximize,
runtime.window.isMaximized,
runtime.window.getBounds,
runtime.app.quit,
runtime.shell.openExternal
```

`runtime.window.getBounds` returns `{ x, y, width, height, maximized }` from the active Photino window. The frontend uses it before `SaveWindowSettings` so desktop coordinates come from the native window when available, with browser viewport fallback only for Vite/browser-only development.

## Event Coverage

Bridge event names remain compatible:

- `chat:started`
- `agent:{turnId}`
- `file:changed`
- `chat:session_created`
- `chat:title_updated`
- `novel_import:progress`
- `style_skill_extraction:progress`
- `narrative_pattern_extraction:progress`

The `AgentEvent.type` numeric enum remains unchanged: `0` thinking, `1` thinking done, `2` content, `3` tool call, `4` usage, `5` error, `6` compression.

## Phase 15 Guardrails

- Novel import accepts only desktop-picked or drag/dropped local files that pass kind, path, readability, and size validation. Source paths are not exposed in progress messages or persisted as raw paths.
- Import failures before durable completion use compensating cleanup; startup recovery reconciles incomplete runs before normal workspace use.
- Style sample extraction and narrative pattern extraction produce validated Skill previews. Saving a Skill is an explicit user action, and neither path calls `SaveContent`.
- Git history bridge methods are read-only. Revert, checkout, reset, cherry-pick, restore, and arbitrary Git mutation are out of scope.
- Update checks use configured release endpoints and explicit `ShellOpenExternal` for release pages; startup checks must remain timeout-bounded and non-blocking.
- Agent tools must not expose Phase 15 import, file-picker, update, Git history, style-sample CRUD, style extraction, narrative pattern extraction, or final-insertion abilities unless a later authority design explicitly changes that boundary.

Historical Phase 15 scope note:

- Task 21 is closed at its recorded Phase 15 boundary. New error-lifecycle and usability regressions are tracked by focused frontend workflows and the current feature plan rather than by reopening the legacy-surface checklist.

## Verification

- Contract tests parse and validate all envelope samples.
- Dispatcher tests cover success, unknown method, malformed message, validation failure, cancellation, and event dispatch.
- Frontend bridge tests cover request id correlation, timeout, unsubscribe, and error conversion.
- Golden fixtures continue to validate payload shapes; their `api` filenames are historical and do not imply HTTP transport.
- Phase 15 bridge guardrails are covered by mocked Playwright workflows and backend registry tests; `npm --prefix frontend run test:phase15` exercises import/style/pattern/Git/update/error surfaces without reviving Wails bindings.
