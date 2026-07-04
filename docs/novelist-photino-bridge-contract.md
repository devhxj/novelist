# Novelist Photino Bridge Contract

## Purpose

This is the primary frontend/backend contract for the Photino.NET migration. It replaces loopback REST/SignalR as the default packaged-app transport so novelist keeps Photino's lightweight runtime and avoids unnecessary localhost HTTP overhead.

References:

- Photino.NET supports JavaScript-to-.NET messages through `window.external.sendMessage(...)` and .NET-to-JavaScript messages through `SendWebMessage(...)`: https://github.com/tryphotino/photino.Samples
- Current Wails contract inventory: [novelist-api-event-contract.md](./novelist-api-event-contract.md)
- Golden payload fixtures: [contracts/golden](./contracts/golden/)

## Transport Rules

- Packaged desktop mode uses Photino WebMessage for commands, queries, events, and desktop runtime operations.
- Packaged desktop mode does not start ASP.NET Core/Kestrel. Built frontend assets load from local `frontend/dist`, and business calls use the bridge.
- Development may point Photino at Vite with `--start-url=http://localhost:5173/`; that URL is only a frontend asset source, not a business API transport.
- React components must not call `window.external` directly. They use `frontend/src/lib/novelist/bridge.ts`.
- The bridge preserves current Wails method names during the compatibility phase.

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

All current frontend App API methods map to bridge methods with the same names:

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
GetSession, GetSessionMessages, GetSessions, GetSettings, GetStoryArcs,
GetSqliteVecStatus, GetTimelineEntries, GetWritingActivity, GetWritingStats,
Initialize, IsInitialized, ListSkills, ListSlashCommands,
RebuildNovelIndex, SaveAvatar, SaveContent, SaveCover, SaveEmbeddingConfig,
SaveLLMConfig, SaveSettings, SaveUserName, SearchAll, SearchStoryMemory,
SetActiveNovel, SetApprovalMode,
SetChatPanelWidth, SetLastSession, SetReasoningEffort, SetSelectedModel,
TestEmbeddingConnection, TestConnection, UpdateArcNode, UpdateChapterPlan, UpdateChapterTitle,
UpdateCharacter, UpdateDataDir, UpdateLocation, UpdateNovel,
UpdatePreference, UpdateReaderPerspective, UpdateStoryArc,
UpdateTimelineEntry
```

## Event Coverage

Bridge event names remain compatible:

- `chat:started`
- `agent:{turnId}`
- `file:changed`
- `chat:session_created`
- `chat:title_updated`

The `AgentEvent.type` numeric enum remains unchanged: `0` thinking, `1` thinking done, `2` content, `3` tool call, `4` usage, `5` error, `6` compression.

## Verification

- Contract tests parse and validate all envelope samples.
- Dispatcher tests cover success, unknown method, malformed message, validation failure, cancellation, and event dispatch.
- Frontend bridge tests cover request id correlation, timeout, unsubscribe, and error conversion.
- Golden fixtures continue to validate payload shapes; their `api` filenames are historical and do not imply HTTP transport.
