# Novelist Migration Contract Verification Checklist

## Purpose

Use this checklist before completing each migration slice from Go/Wails to novelist on Photino.NET + .NET 10. A slice is not complete until the relevant checks pass or a documented exception exists in the progress tracker.

Primary references:

- [API and event contract](./novelist-api-event-contract.md)
- [Photino bridge contract](./novelist-photino-bridge-contract.md)
- [Golden fixtures](./contracts/golden/)
- [Migration progress](./novelist-migration-progress.md)

## Baseline Checks

Run these whenever the Wails compatibility surface changes.

| Check | Verification |
| --- | --- |
| Generated frontend binding count remains understood | `rg -n '^export function' frontend/src/lib/wailsjs/go/app/App.d.ts` and compare with the 80-function baseline in the contract |
| Exported Go `App` methods remain inventoried until retired | `rg -n '^func \(a \*App\) [A-Z]' app` and confirm lifecycle-only methods are not treated as frontend bindings |
| Every callable binding has a target Photino bridge method | Review `Compatibility Method Coverage` in `docs/novelist-photino-bridge-contract.md`; missing count must be `0` |
| Direct Wails imports are tracked | `rg -n 'wailsjs|@wailsapp|runtime' frontend/src` and compare with the documented import-removal list |

## Golden Fixture Checks

Run after editing any file under `docs/contracts/golden/`.

| Check | Verification |
| --- | --- |
| All fixtures parse as JSON | Run the JSON parse command below; exit code must be `0` |
| Fixtures contain no secrets | Run the secret scan below; it must return no matches |
| Fixtures contain no machine-specific paths | Run the path scan below; it must return no matches |
| Fixtures contain no traversal paths | Run the traversal scan below; it must return no matches |

```powershell
Get-ChildItem 'docs/contracts/golden' -Filter '*.json' | ForEach-Object {
  Get-Content -Raw $_.FullName | ConvertFrom-Json | Out-Null
}

rg -n 'api_key|apikey|authorization|bearer|sk-[A-Za-z0-9]|secret' docs/contracts/golden
rg -n '\b[A-Za-z]:[\\/]|/Users/|/home/' docs/contracts/golden
rg -n '\.\.(/|\\)' docs/contracts/golden
```

Required fixture coverage:

- `get-api-app-initialized.response.json`
- `get-api-settings.response.json`
- `get-api-novels.response.json`
- `get-api-novel-chapters.response.json`
- `post-api-chat.request.json`
- `post-api-chat.response.json`
- `event-chat-session-created.payload.json`
- `event-chat-started.payload.json`
- `event-chat-title-updated.payload.json`
- `event-agent-turn.payloads.json`
- `event-file-changed.payload.json`
- `problem-details.response.json`

## Bridge Contract Checks

| Area | Verification |
| --- | --- |
| Envelope shape | Requests, responses, errors, events, and cancels match `docs/novelist-photino-bridge-contract.md` |
| Request correlation | Every response preserves the request `id`; duplicate or missing ids are rejected |
| Field naming | Results preserve existing snake_case and exact legacy fields such as `ID` on settings |
| Error shape | Failures return bridge error objects with stable `code`, `message`, optional `details`, and no stack traces |
| Path inputs | `GetContent`, `SaveContent`, tools, and export paths reject absolute paths and `..` traversal |
| Long-running commands | `Chat`, `CompressContext`, `RebuildNovelIndex`, and export operations support cancellation or bounded timeouts |
| Approval control | `ApproveTool` resumes exactly one waiting tool call and rejects duplicate completions |
| Chat cancellation | `CancelChat` cancels the active operation for the requested session without cancelling unrelated sessions |
| Search degradation | Semantic search failures do not fail exact/entity search results |
| Backpressure | Agent streaming events are batched or throttled so the WebView bridge is not flooded |

## Bridge Event Checks

| Event | Verification |
| --- | --- |
| `chat:started` | Emitted before the first `agent:{turnId}` payload for the same turn |
| `agent:{turnId}` | Preserves numeric `AgentEvent.type` values `0` through `6`; `seq` is monotonic when present |
| `file:changed` | Emitted after approved file writes and includes relative `path` plus `novel_id` |
| `chat:session_created` | Still emitted for compatibility even though current components do not consume it |
| `chat:title_updated` | Still emitted after async title generation |
| Unsubscribe behavior | Frontend adapter returns a cleanup function matching Wails `EventsOn` behavior |

## Frontend Adapter Checks

| Check | Verification |
| --- | --- |
| `useApp()` compatibility | Existing component call sites compile without renaming exported functions |
| Runtime bridge isolation | React components do not call `window.external` directly; all calls go through `frontend/src/lib/novelist/bridge.ts` and wrappers |
| External URL safety | `openExternal` accepts only reviewed schemes; initial policy is `https://` only |
| Window controls | Minimize, maximize toggle, maximize state, and quit are exposed through the Photino bridge, not Wails runtime |

## Completion Rule

Before marking a task complete in `docs/novelist-migration-progress.md`:

1. Record the exact verification command or review method.
2. Link any new fixture, contract, or test file.
3. Note any deferred incompatibility explicitly with owner task and phase.
4. Update current task and next task immediately.
