# Novelist API and Event Migration Contract

## Purpose

This document freezes the current frontend-facing Wails inventory before migrating to Photino.NET + .NET 10. The primary product transport is now the Photino bridge documented in [novelist-photino-bridge-contract.md](./novelist-photino-bridge-contract.md).

The route tables below are retained as a historical grouping aid and optional diagnostic HTTP sketch. They are not the default packaged-app communication contract.

Source of truth used for this inventory:

- Generated Wails binding: `frontend/src/lib/wailsjs/go/app/App.d.ts`
- Current frontend adapter: `frontend/src/hooks/useApp.ts`
- Runtime event usage: `app/`, `internal/`, `frontend/src`
- Agent event schema: `internal/agent/events.go` and `frontend/src/components/chat/types.ts`

## Contract Rules

- Keep current frontend function names in `useApp()` during the first migration.
- Use Photino WebMessage RPC for request/response commands and queries.
- Use Photino WebMessage events for backend-pushed events.
- Use the Photino bridge for desktop runtime functions.
- Keep DTO field names snake_case where the current Wails JSON contract uses snake_case.
- Do not expose persistence entities directly once .NET contracts exist; contracts may mirror current shapes initially, but must live in `Novelist.Contracts`.
- Return API failures as ProblemDetails-compatible responses. The frontend adapter may convert them to rejected promises to match Wails behavior.

## Target Frontend Adapter

Replace direct Wails imports with stable app-owned modules:

```text
frontend/src/lib/novelist/api.ts
frontend/src/lib/novelist/events.ts
frontend/src/lib/novelist/runtime.ts
```

`frontend/src/hooks/useApp.ts` should continue exporting the same callable names during compatibility.

## Runtime Boundary

Current direct Wails runtime dependencies:

| Current import | Current consumers | Photino replacement |
| --- | --- | --- |
| `EventsOn` | `ChatPanel`, `ContentPanel`, `ChapterList` | `novelist/events.ts`, backed by Photino bridge events |
| `WindowMinimise` | `WorkspaceView` | `window.novelist.window.minimize()`, implemented through Photino messages |
| `WindowToggleMaximise` | `WorkspaceView` | `window.novelist.window.toggleMaximize()`, implemented through Photino messages |
| `WindowIsMaximised` | `WorkspaceView` | `window.novelist.window.isMaximized()`, implemented through Photino messages |
| `Quit` | `WorkspaceView` | `window.novelist.app.quit()`, implemented through Photino messages |
| `BrowserOpenURL` | `GitHubLink`, `BuiltinProviderPane` | `window.novelist.shell.openExternal(url)`, implemented through Photino messages |

The bridge must validate inputs. `openExternal` only accepts `https://` URLs unless a future reviewed requirement adds another scheme.

## Photino Bridge Event Contract

The frontend compatibility adapter exposes:

```ts
EventsOn(eventName: string, callback: (payload: unknown) => void): () => void
```

Under the hood, Photino sends event envelopes such as:

```ts
{ kind: 'event', name: string, payload: unknown }
```

The adapter dispatches by `name` so the React code can keep current event names.

### Events

| Event name | Producer | Current consumers | Payload |
| --- | --- | --- | --- |
| `chat:started` | `app.Chat` | `ChatPanel` | `{ session_id?: string, turn_id: number }` |
| `agent:{turnId}` | Agent loop, compression, token budget | `ChatPanel` | `AgentEvent` |
| `file:changed` | edit/write tools | `ContentPanel`, `ChapterList` | `{ novel_id: number, path: string }` |
| `chat:session_created` | `loadOrCreateSession` | None currently | `Session` |
| `chat:title_updated` | title generator | None currently | `{ session_id: string, title: string }` |

Unconsumed events must still be emitted during compatibility. Removing them is a separate cleanup after frontend audit.

### AgentEvent Payload

`AgentEvent.type` numeric values must remain compatible during the first migration:

| Value | Name | Meaning |
| --- | --- | --- |
| `0` | `Thinking` | Model reasoning/thinking text delta |
| `1` | `ThinkingDone` | Thinking block completed |
| `2` | `Content` | Assistant text delta |
| `3` | `ToolCall` | Tool status update |
| `4` | `Usage` | Token usage update |
| `5` | `Error` | Terminal or recoverable stream error |
| `6` | `Compression` | Context compression status |

Required JSON fields:

```ts
interface AgentEvent {
  turn_id: number
  sub_task_id?: string
  seq?: number
  type: 0 | 1 | 2 | 3 | 4 | 5 | 6
  data?: string
  tool_name?: string
  tool_id?: string
  phase?: 'selected' | 'executing' | 'awaiting_approval' | 'completed' | 'failed' | 'cancelled' | 'loop_detected'
  tool_args?: Record<string, unknown>
  success?: boolean
  error?: string
  display_text?: string
  activity_kind?: string
  metadata?: Record<string, unknown>
  usage?: Record<string, unknown>
  compression_phase?: 'compressing' | 'done'
  summary?: string
  timestamp: string
}
```

Ordering rule: `seq` is optional but, when present, must be monotonically increasing per turn. The existing UI buffers out-of-order events.

## Optional Diagnostic HTTP Route Sketch

These route shapes are no longer the first-pass product target. Keep them only as a possible debug/test surface if HTTP diagnostics are explicitly enabled. The packaged desktop app should route business calls through the Photino bridge.

### App and Platform

| Current binding | Target |
| --- | --- |
| `IsInitialized` | `GET /api/app/initialized` |
| `Initialize` | `POST /api/app/initialize` |
| `GetAppConfig` | `GET /api/app/config` |
| `UpdateDataDir` | `PUT /api/app/data-dir` |
| `GetPlatform` | `GET /api/app/platform` |

Lifecycle-only Go methods `OnStartup` and `OnShutdown` are not frontend bindings. They map to the Photino desktop process and window lifecycle.

### Settings and User Profile

| Current binding | Target |
| --- | --- |
| `GetSettings` | `GET /api/settings` |
| `SaveSettings` | `PUT /api/settings` |
| `SetSelectedModel` | `PUT /api/settings/selected-model` |
| `SetReasoningEffort` | `PUT /api/settings/reasoning-effort` |
| `SetChatPanelWidth` | `PUT /api/settings/chat-panel-width` |
| `SetLastSession` | `PUT /api/settings/last-session` |
| `SetApprovalMode` | `PUT /api/settings/approval-mode` |
| `SaveUserName` | `PUT /api/profile/name` |
| `SaveAvatar` | `PUT /api/profile/avatar` |
| `GetWritingActivity` | `GET /api/profile/writing-activity?months=...` |
| `GetWritingStats` | `GET /api/profile/writing-stats` |

### LLM Configuration

| Current binding | Target |
| --- | --- |
| `GetModels` | `GET /api/llm/models` |
| `GetLLMConfig` | `GET /api/llm/config` |
| `SaveLLMConfig` | `PUT /api/llm/config` |
| `DiscoverModels` | `POST /api/llm/discover-models` |
| `TestConnection` | `POST /api/llm/test-connection` |

Provider-specific request transforms must stay server-side. Frontend must not know DeepSeek/Qwen/Kimi/MiniMax/MiMo quirks.

### Novels and Preferences

| Current binding | Target |
| --- | --- |
| `GetNovels` | `GET /api/novels` |
| `CreateNovel` | `POST /api/novels` |
| `UpdateNovel` | `PATCH /api/novels/{novelId}` |
| `DeleteNovel` | `DELETE /api/novels/{novelId}` |
| `SetActiveNovel` | `PUT /api/novels/active` |
| `ExportNovel` | `POST /api/novels/{novelId}/export` |
| `SaveCover` | `PUT /api/novels/{novelId}/cover` |
| `DeleteCover` | `DELETE /api/novels/{novelId}/cover` |
| `GetPreferences` | `GET /api/novels/{novelId}/preferences` |
| `CreatePreference` | `POST /api/novels/{novelId}/preferences` |
| `UpdatePreference` | `PATCH /api/preferences/{id}` |
| `DeletePreference` | `DELETE /api/preferences/{id}` |

### Chapters and Content

| Current binding | Target |
| --- | --- |
| `GetChapters` | `GET /api/novels/{novelId}/chapters` |
| `GetMaxChapterNumber` | `GET /api/novels/{novelId}/chapters/max-number` |
| `CreateChapter` | `POST /api/chapters` |
| `UpdateChapterTitle` | `PUT /api/novels/{novelId}/chapters/{chapterNumber}/title` |
| `GetContent` | `GET /api/novels/{novelId}/content?path=...` |
| `SaveContent` | `POST /api/content/save` |

`path` must pass the existing SafePath equivalent before file access. No route may accept arbitrary absolute paths.

### Characters

| Current binding | Target |
| --- | --- |
| `GetCharacters` | `GET /api/novels/{novelId}/characters` |
| `GetCharacterRelations` | `GET /api/novels/{novelId}/character-relations` |
| `CreateCharacter` | `POST /api/novels/{novelId}/characters` |
| `UpdateCharacter` | `PATCH /api/novels/{novelId}/characters/{charId}` |
| `DeleteCharacter` | `DELETE /api/novels/{novelId}/characters/{charId}` |

### Locations

| Current binding | Target |
| --- | --- |
| `GetLocations` | `GET /api/novels/{novelId}/locations` |
| `GetLocationRelations` | `GET /api/novels/{novelId}/location-relations` |
| `CreateLocation` | `POST /api/novels/{novelId}/locations` |
| `UpdateLocation` | `PATCH /api/novels/{novelId}/locations/{locId}` |
| `DeleteLocation` | `DELETE /api/novels/{novelId}/locations/{locId}` |

### Timeline

| Current binding | Target |
| --- | --- |
| `GetChapterPlans` | `GET /api/novels/{novelId}/chapter-plans` |
| `GetTimelineEntries` | `GET /api/novels/{novelId}/timeline?fromChapter=...&toChapter=...` |
| `UpdateChapterPlan` | `PUT /api/novels/{novelId}/chapter-plans` |
| `CreateTimelineEntry` | `POST /api/novels/{novelId}/timeline` |
| `UpdateTimelineEntry` | `PATCH /api/novels/{novelId}/timeline/{entryId}` |
| `DeleteTimelineEntry` | `DELETE /api/novels/{novelId}/timeline/{entryId}` |

### Story Arcs

| Current binding | Target |
| --- | --- |
| `GetStoryArcs` | `GET /api/novels/{novelId}/story-arcs` |
| `GetArcNodes` | `GET /api/novels/{novelId}/arc-nodes?fromChapter=...&toChapter=...` |
| `CreateStoryArc` | `POST /api/novels/{novelId}/story-arcs` |
| `UpdateStoryArc` | `PATCH /api/novels/{novelId}/story-arcs/{arcId}` |
| `DeleteStoryArc` | `DELETE /api/novels/{novelId}/story-arcs/{arcId}` |
| `CreateArcNode` | `POST /api/novels/{novelId}/arc-nodes` |
| `UpdateArcNode` | `PATCH /api/novels/{novelId}/arc-nodes/{nodeId}` |
| `DeleteArcNode` | `DELETE /api/novels/{novelId}/arc-nodes/{nodeId}` |

### Reader Perspective

| Current binding | Target |
| --- | --- |
| `GetReaderPerspectives` | `GET /api/novels/{novelId}/reader-perspectives` |
| `CreateReaderPerspective` | `POST /api/novels/{novelId}/reader-perspectives` |
| `UpdateReaderPerspective` | `PATCH /api/novels/{novelId}/reader-perspectives/{id}` |
| `DeleteReaderPerspective` | `DELETE /api/novels/{novelId}/reader-perspectives/{id}` |

### Chat, Sessions, and Approval

| Current binding | Target |
| --- | --- |
| `Chat` | `POST /api/chat` |
| `CancelChat` | `POST /api/chat/{sessionId}/cancel` |
| `CompressContext` | `POST /api/chat/compress` |
| `ApproveTool` | `POST /api/approvals/{toolId}` |
| `GetSessions` | `GET /api/novels/{novelId}/sessions?page=...&size=...&search=...` |
| `GetSession` | `GET /api/sessions/{sessionId}` |
| `GetSessionMessages` | `GET /api/sessions/{sessionId}/messages` |

`Chat` returns only after the turn completes, matching current Wails behavior. Streaming happens through Photino bridge events before the returned promise resolves.

### Search and RAG

| Current binding | Target |
| --- | --- |
| `SearchAll` | `GET /api/novels/{novelId}/search?query=...` |
| `RebuildNovelIndex` | `POST /api/novels/{novelId}/rag/rebuild` |

`SearchAll` must keep combining entity search, exact content search, and semantic search. The semantic component must tolerate missing/stale vector index and return other result types instead of failing the whole query.

### Skills and Slash Commands

| Current binding | Target |
| --- | --- |
| `ListSkills` | `GET /api/skills?novelId=...&scope=...` |
| `ListSlashCommands` | `GET /api/slash-commands?novelId=...` |
| `ExtractStyle` | `POST /api/skills/extract-style` |
| `DeleteSkill` | `DELETE /api/skills` |

Skill paths must preserve the current three-layer behavior: builtin, user, novel.

## Generated Binding Coverage

The generated Wails binding exposes 80 functions. All are covered above:

```text
ApproveTool, CancelChat, Chat, CompressContext,
CreateArcNode, CreateChapter, CreateCharacter, CreateLocation, CreateNovel,
CreatePreference, CreateReaderPerspective, CreateStoryArc, CreateTimelineEntry,
DeleteArcNode, DeleteCharacter, DeleteCover, DeleteLocation, DeleteNovel,
DeletePreference, DeleteReaderPerspective, DeleteSkill, DeleteStoryArc,
DeleteTimelineEntry, DiscoverModels, ExportNovel, ExtractStyle,
GetAppConfig, GetArcNodes, GetChapterPlans, GetChapters,
GetCharacterRelations, GetCharacters, GetContent, GetLLMConfig,
GetLocationRelations, GetLocations, GetMaxChapterNumber, GetModels,
GetNovels, GetPlatform, GetPreferences, GetReaderPerspectives,
GetSession, GetSessionMessages, GetSessions, GetSettings, GetStoryArcs,
GetTimelineEntries, GetWritingActivity, GetWritingStats,
Initialize, IsInitialized, ListSkills, ListSlashCommands,
RebuildNovelIndex, SaveAvatar, SaveContent, SaveCover, SaveLLMConfig,
SaveSettings, SaveUserName, SearchAll, SetActiveNovel, SetApprovalMode,
SetChatPanelWidth, SetLastSession, SetReasoningEffort, SetSelectedModel,
TestConnection, UpdateArcNode, UpdateChapterPlan, UpdateChapterTitle,
UpdateCharacter, UpdateDataDir, UpdateLocation, UpdateNovel,
UpdatePreference, UpdateReaderPerspective, UpdateStoryArc,
UpdateTimelineEntry
```

## Direct Frontend Wails Imports To Remove

| File | Dependency |
| --- | --- |
| `frontend/src/hooks/useApp.ts` | generated App functions and model namespaces |
| `frontend/src/views/WorkspaceView.tsx` | `search` model namespace, window runtime functions |
| `frontend/src/components/chat/ChatPanel.tsx` | `EventsOn` |
| `frontend/src/components/content/ContentPanel.tsx` | `EventsOn` |
| `frontend/src/components/sidebar/ChapterList.tsx` | `EventsOn` |
| `frontend/src/components/profile/ProfileView.tsx` | `config` model namespace |
| `frontend/src/components/search/SearchPanel.tsx` | `SearchAll`, `search` model namespace |
| `frontend/src/components/settings/ModelDiscoveryPanel.tsx` | `DiscoverModels` |
| `frontend/src/components/settings/BuiltinProviderPane.tsx` | `BrowserOpenURL` |
| `frontend/src/components/shell/GitHubLink.tsx` | `BrowserOpenURL` |

First migration adapter task: replace these imports with `@/lib/novelist/*` while keeping component behavior unchanged.

## Non-Negotiable Compatibility Checks

- `useApp()` still returns all callable names used by existing components.
- `ChatPanel` can subscribe to `chat:started`, then `agent:{turnId}`.
- `ContentPanel` and `ChapterList` refresh on `file:changed`.
- `AgentEvent.type` values remain numeric-compatible.
- `ApproveTool` unblocks a waiting tool call exactly once.
- `CancelChat` cancels the active backend operation for the session.
- Runtime URL opening and window controls do not require direct native access from React components.

## Golden Samples

Golden contract fixtures live in `docs/contracts/golden/`. They cover:

- `GET /api/app/initialized`
- `GET /api/settings`
- `GET /api/novels`
- `GET /api/novels/{novelId}/chapters`
- `POST /api/chat` request/response
- `chat:started`, `chat:session_created`, `chat:title_updated`, and representative `agent:{turnId}` events
- `file:changed`
- failed ProblemDetails response

These files are stable snapshot fixtures for bridge payload shapes and frontend compatibility adapter tests. Their filenames still include `api` from the earlier route sketch; that does not imply HTTP is the product transport. Keep them free of secrets and machine-specific absolute paths.

## Next Contract Work

P0.4 should add a migration contract verification checklist covering generated binding coverage, JSON fixture validity, bridge method parity, bridge event parity, ProblemDetails-compatible bridge failures, Photino bridge checks, and remaining direct Wails import removal checks.
