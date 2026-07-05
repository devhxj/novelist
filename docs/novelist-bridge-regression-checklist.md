# Novelist Bridge Regression Checklist

## Purpose

Use this checklist when changing the active Novelist Photino bridge, frontend DTOs, bridge payloads, or desktop runtime behavior. The goal is to prevent regressions in the .NET 10 + Photino product path.

Primary references:

- [Photino bridge contract](./novelist-photino-bridge-contract.md)
- [Golden fixtures](./contracts/golden/)

## Active Baseline Checks

Run these whenever bridge methods, frontend DTOs, desktop runtime startup, packaging, or compatibility behavior changes.

| Check | Verification |
| --- | --- |
| No retired Wails surface returns to active code | `rg -n 'wailsjs|@wailsapp|wails build|wails dev|frontend/src/lib/wailsjs' frontend/src src tests scripts build .github Makefile --glob '!**/bin/**' --glob '!**/obj/**' --glob '!build/bin/**'` returns no matches |
| No Go runtime source returns to mainline | `rg --files | rg '(^|/)(main\.go|go\.mod|go\.sum|wails\.json)$|^app/|^internal/|frontend/src/lib/wailsjs|download-onnx'` returns no tracked active source files |
| Every frontend bridge method has a backend registration | `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge -v minimal` |
| Frontend DTOs compile against the owned bridge adapter | `npm --prefix frontend run build` |
| Packaged desktop path stays Photino-only | `rg -n 'Microsoft\.AspNetCore|WebApplication|MapGet|MapHub|SignalR|HealthResponse|WebApplicationFactory|Mvc\.Testing' src tests --glob '!**/bin/**' --glob '!**/obj/**'` returns no matches |
| Local frontend assets remain file-loadable | `npm --prefix frontend run build` and confirm `frontend/dist/index.html` references `./assets/` |
| Runtime documentation matches build scripts | Review `README.md`, `README_EN.md`, `docs/build-setup.md`, and `docs/build/cross-platform-build.md` when `Makefile`, package scripts, or launch arguments change |

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
| Field naming | Results preserve stable frontend DTO field names, including exact fields such as `ID` on settings |
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
| Unsubscribe behavior | Frontend adapter returns a cleanup function for each event subscription |

## Frontend Adapter Checks

| Check | Verification |
| --- | --- |
| `useApp()` compatibility | Existing component call sites compile without renaming exported functions |
| Runtime bridge isolation | React components do not call `window.external` directly; all calls go through `frontend/src/lib/novelist/bridge.ts` and wrappers |
| External URL safety | `openExternal` accepts only reviewed schemes; initial policy is `https://` only |
| Window controls | Minimize, maximize toggle, maximize state, and quit are exposed through the Photino bridge, not Wails runtime |

## Completion Rule

Before marking bridge, runtime, packaging, or feature-specific contract work complete:

1. Record the exact verification command or review method.
2. Link any new fixture, contract, or test file.
3. Note any deferred incompatibility explicitly with owner task and target phase.
4. Update the feature-specific implementation plan when one exists.
