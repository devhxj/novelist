# Novelist .NET 10 + Photino Migration Plan

## Status

Draft for review. This plan freezes the intended migration direction before implementation.

## Target

Rename the product line to **novelist** and migrate the current Go/Wails desktop app to:

- Existing React/Vite UI reused with minimal component rewrites.
- Photino.NET desktop shell.
- .NET 10 local application host.
- Microsoft Agent Framework for agent orchestration.
- SQLite + sqlite-vec for local vector search.
- OpenAI-compatible Embeddings API instead of local ONNX Runtime and `bge-small-zh-v1.5` int8.

External references checked on 2026-07-03:

- .NET 10 is an active LTS release supported until 2028-11-14: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- Photino.NET provides native desktop windows for .NET 5+ using the OS browser control, without bundling Chromium: https://github.com/tryphotino/photino.NET
- Photino.NET Quick Start documents the .NET desktop startup shape: https://docs.tryphotino.io/Quick-Start-for-.NET-Developers
- Photino.NET.Server and samples show serving local web assets for Photino applications: https://github.com/tryphotino/photino.Samples
- Microsoft Agent Framework supports production-grade agents and workflows for .NET and Python: https://learn.microsoft.com/en-us/agent-framework/overview/
- Microsoft Agent Framework repository: https://github.com/microsoft/agent-framework
- sqlite-vec is pre-v1 and may introduce breaking changes: https://github.com/asg017/sqlite-vec
- OpenAI-compatible Embeddings API supports batched input arrays and returns float vectors: https://platform.openai.com/docs/api-reference/embeddings/create

## Current Code Assessment

The current app is a Wails application. `app/handler.go` owns lifecycle and dependency assembly. `app/*.go` exposes roughly 60 frontend-callable methods. The React UI centralizes most calls in `frontend/src/hooks/useApp.ts`, which is a good migration boundary.

The current event model depends on Wails runtime events:

- `chat:started`
- `agent:{turnId}`
- `chat:session_created`
- `chat:title_updated`
- `file:changed`

These event names should be preserved in the first migration to reduce UI churn.

The Agent layer is not just an LLM wrapper. `internal/agent/agent.go` owns ReAct loop behavior, streaming, tool execution, subagents, cancellation, compression, token budget checks, and persistence. `internal/mcp_tools` defines the tool surface. MAF should replace orchestration gradually, not by deleting all existing semantics at once.

The RAG layer has a useful interface boundary in `internal/rag/types.go`, but the implementation is hard-bound to ONNX:

- `InitEmbedder(config.ModelsDir())`
- BGE tokenizer and 512-dimensional embeddings
- ONNX Runtime dynamic library resolution
- `vec0` table schema using `embedding float[512]`

Because a standard Embeddings API can return different dimensions depending on provider/model, existing ONNX vector tables cannot be reused safely. Rebuild is required.

## Architecture Decisions

### Desktop Shell

Use Photino.NET as the desktop shell. The primary executable is a .NET process that owns the window, lifecycle, native dialogs, external URL opening, frontend asset loading, and the primary frontend/backend bridge.

Do not use Electron or Electron.NET. Photino avoids bundling Chromium and keeps packaging centered on .NET plus platform WebView dependencies.

### Communication Model

Use Photino WebMessage as the default runtime communication layer, not loopback HTTP. The first compatibility adapter should emulate the Wails binding shape:

```ts
await window.novelist.invoke("GetNovels", payload)
window.novelist.events.on("agent:7", callback)
```

`Novelist.App` owns a bridge dispatcher that receives JSON-RPC-like messages from `window.external.sendMessage`, routes them to application services, and returns responses or events through `SendWebMessage`.

ASP.NET Core remains optional infrastructure for static asset hosting, debug endpoints, test hosting, and future remote-capable diagnostics. It is not the default business API transport in packaged desktop mode.

Primary transport responsibilities:

- Photino WebMessage RPC for Wails-compatible commands and queries.
- Photino WebMessage events for `chat:started`, `agent:{turnId}`, `file:changed`, title updates, and desktop runtime notifications.
- ASP.NET Core optional debug/test surface for `/health`, built frontend assets, and integration testing only.
- Background services stay in-process behind `Novelist.App` services, not behind HTTP endpoints.
- ProblemDetails-compatible error objects are preserved inside bridge error payloads.

### Project Layout

Proposed layout:

```text
frontend/
src/
  Novelist.App/
  Novelist.Contracts/
  Novelist.Core/
  Novelist.Infrastructure/
  Novelist.Agent/
tests/
  Novelist.Tests/
  Novelist.IntegrationTests/
docs/
```

`Novelist.slnx` should live at the repository root.

### Frontend Compatibility

Keep React components mostly intact. Replace generated Wails imports with a hand-owned client adapter:

```text
frontend/src/lib/novelist/api.ts
frontend/src/lib/novelist/events.ts
frontend/src/lib/novelist/runtime.ts
```

`useApp.ts` should continue exposing the same function names at first. Internally those functions call `window.novelist.invoke(...)` and subscribe to Photino bridge events.

### IPC and API Contract

Use a Wails-like Photino bridge for commands, queries, and events. Keep the method names from the generated Wails binding during the first migration.

Initial mapping examples:

| Current Wails method | Bridge method |
| --- | --- |
| `IsInitialized()` | `invoke("IsInitialized")` |
| `Initialize(dataDir)` | `invoke("Initialize", { data_dir })` |
| `GetNovels()` | `invoke("GetNovels")` |
| `CreateNovel(input)` | `invoke("CreateNovel", input)` |
| `GetChapters(novelId)` | `invoke("GetChapters", { novel_id })` |
| `GetContent(novelId, path)` | `invoke("GetContent", { novel_id, path })` |
| `SaveContent(input)` | `invoke("SaveContent", input)` |
| `Chat(input)` | `invoke("Chat", input)` |
| `ApproveTool(...)` | `invoke("ApproveTool", { tool_id, approved, feedback })` |
| `CancelChat(sessionId)` | `invoke("CancelChat", { session_id })` |
| `SearchAll(novelId, query)` | `invoke("SearchAll", { novel_id, query })` |
| `RebuildNovelIndex(novelId)` | `invoke("RebuildNovelIndex", { novel_id })` |

Bridge event envelope:

```json
{
  "kind": "event",
  "name": "agent:7",
  "payload": {}
}
```

Event payloads should preserve current frontend shapes first. Renaming can happen after parity.

### Local Security

Packaged desktop mode should not expose a business HTTP API by default. The bridge must validate every incoming message:

- reject unknown methods;
- reject malformed JSON;
- reject oversized payloads;
- validate payload shape at the bridge boundary;
- prevent path traversal and absolute path access before file operations;
- normalize errors into the bridge error envelope.

If optional loopback HTTP is enabled for diagnostics or dev workflows, it must listen only on `127.0.0.1` with a random port and token. That surface is explicitly secondary and must not become the default product transport.

## Data Migration Strategy

Keep the existing user data readable before introducing destructive changes.

Current important data:

- Global SQLite database: `novel-agent.db`
- Per-novel Git repositories: `novels/{id}/`
- Chapter files: `chapters/NNN.md`
- Outline files: `outlines/NNN.md`
- Skills: user-level and novel-level Markdown files
- Encrypted LLM config

Recommended policy:

1. Compatibility mode first: .NET reads the existing database and file layout.
2. New app data directory name: `Novelist`.
3. First run detects old `Goink` data and offers migration/copy.
4. Never mutate old data until the migrated app passes verification.
5. Write a `migration_manifest.json` with source path, target path, timestamp, schema version, and result.

Database migration should start by mirroring the current tables. Avoid entity renames during the first pass. Rename user-facing project terms after data compatibility is proven.

## RAG Migration

### New Interfaces

Introduce provider-independent contracts:

```csharp
public interface IEmbeddingClient
{
    Task<EmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<string> inputs,
        EmbeddingRequestOptions options,
        CancellationToken cancellationToken);
}

public interface IVectorIndex
{
    Task IndexChunksAsync(long novelId, IReadOnlyList<RagChunk> chunks, CancellationToken ct);
    Task<IReadOnlyList<RagSearchResult>> SearchAsync(long novelId, string query, int topK, RagFilter? filter, CancellationToken ct);
    Task RebuildNovelAsync(long novelId, CancellationToken ct);
}
```

### Embeddings API Behavior

Use OpenAI-compatible `POST /v1/embeddings`:

- Send arrays of strings for batch embedding.
- Reject empty strings before calling the provider.
- Store returned `model`, vector dimension, usage if available, and provider key.
- Support optional `dimensions` only when the configured model/provider supports it.
- Add retry/backoff for 429, 408, and 5xx.

### Chunking

Do not depend on the old BGE tokenizer. Introduce `ITextChunker` with a default chunker based on paragraphs, sentence boundaries, character count, and a conservative token estimate. Store `chunker_version` so indexes can be rebuilt when chunking changes.

Initial defaults:

- Content chunk target: 1,200 to 1,800 Chinese characters or provider-safe token equivalent.
- Overlap: 120 to 200 Chinese characters.
- Keep `summary`, `chapter_brief`, and `content` chunk types for tool compatibility.

### Vector Schema

Because sqlite-vec vector dimensions are part of the table definition, dimension changes require a new table or rebuild.

Proposed metadata tables:

```text
rag_index_state
  novel_id
  provider_key
  model_id
  dimensions
  chunker_version
  status
  last_error
  updated_at

rag_chunks
  chunk_id
  novel_id
  chapter_number
  chunk_type
  chunk_index
  start_position
  content
  content_hash
```

Proposed vector table naming:

```text
vec_novel_{novelId}_{dimensions}
```

Vector rows store `chunk_id` and `embedding`. Metadata stays in `rag_chunks`. Search joins vector results back to chunk metadata in application code.

### Reindex Policy

Trigger full rebuild when any of these change:

- Embedding provider
- Embedding model
- Embedding dimensions
- Chunker version
- sqlite-vec schema version

Old ONNX vector tables should be detected and dropped only after a successful API-backed rebuild.

## Agent Migration With Microsoft Agent Framework

### Preserve Tool Semantics First

Map current MCP-style tools to MAF tools without changing names or schemas:

- `edit`
- read/write tools
- novel CRUD
- character/location/timeline/story arc/reader tools
- `search_story_memory`
- web search/fetch tools
- subagent tools

The first MAF integration should use the existing tool prompt descriptions as source material. Any tool rename is deferred until after parity.

### Event Compatibility

MAF events should be adapted into the existing frontend `AgentEvent` shape:

- thinking delta
- content delta
- tool selected
- tool executing
- awaiting approval
- completed
- failed
- usage
- error

Do not make React components understand MAF-native event shapes in the first migration.

### Approval Flow

Keep the current blocking approval semantics:

1. Tool requests approval through `IApprovalService`.
2. Agent event emits `awaiting_approval`.
3. UI calls `invoke("ApproveTool", { tool_id, approved, feedback })`.
4. Tool resumes or returns user feedback as injected context.

### Context and Sessions

Port versioned message storage before replacing compression. The existing model has separate views for API, frontend, and audit. Keep that split.

MAF session/state features may be used internally, but persisted source of truth should remain the Novelist database until parity is proven.

## Phased Implementation Plan

### Phase 0: Contract Freeze

**Goal:** Turn the current Wails surface into an explicit migration contract.

Tasks:

- Inventory all `func (a *App) ...` methods in `app/`.
- Generate DTO list from `frontend/src/lib/wailsjs/go/models.ts`.
- Document current event names and payloads.
- Add golden JSON samples for core responses and agent events.

Acceptance:

- Every frontend call in `useApp.ts` has a mapped Photino bridge method.
- Every current Wails event has a Photino bridge event equivalent.
- No implementation code is changed.

### Phase 1: Scaffold Novelist

**Goal:** Create the new .NET/Photino skeleton without replacing current Go/Wails runtime.

Tasks:

- Create `Novelist.slnx`.
- Add `Novelist.App`, `Contracts`, `Core`, `Infrastructure`, `Agent`, and test projects.
- Add Photino.NET to the app host.
- Add dev scripts for frontend and the Photino app host.
- Add bridge protocol skeleton and optional health endpoint for test/debug hosting.

Acceptance:

- `dotnet test` passes.
- `npm --prefix frontend run build` still passes.
- Photino launches the existing UI against a bridge-capable stub backend.

### Phase 2: Frontend Adapter

**Goal:** Remove component dependence on Wails generated files.

Tasks:

- Implement `frontend/src/lib/novelist/bridge.ts`.
- Implement `frontend/src/lib/novelist/api.ts` on top of the bridge.
- Implement `frontend/src/lib/novelist/events.ts` on top of bridge events.
- Implement `frontend/src/lib/novelist/runtime.ts`.
- Update `useApp.ts` imports while preserving exported function names.
- Route window controls and external URLs through the Photino bridge.

Acceptance:

- App initialization, window controls, theme toggle, and shell layout work through the Photino bridge.
- React components do not import from `frontend/src/lib/wailsjs` except temporary compatibility shims.

### Phase 3: Data and File Compatibility

**Goal:** Make .NET read and write existing app data safely.

Tasks:

- Port platform data-dir resolution.
- Port SQLite schema models and migrations.
- Port per-novel Git repository handling.
- Port SafePath validation for `chapters/`, `outlines/`, `goink.md`, and skills.
- Port config and encrypted LLM config loading.

Acceptance:

- Existing `novel-agent.db` opens read-only in compatibility tests.
- A copied test data directory can create, read, edit, and commit chapter files.
- SafePath tests cover traversal and invalid paths.

### Phase 4: CRUD Feature Parity

**Goal:** Bring up non-agent workflows first.

Tasks:

- Port novels, chapters, content, preferences.
- Port characters and relationships.
- Port locations and relations.
- Port timeline, chapter plans, story arcs, reader perspective.
- Port skills listing/editing/extraction support as far as non-agent dependencies allow.
- Port export and writing stats.

Acceptance:

- Existing UI panels work against .NET bridge services.
- Go and .NET responses match golden samples for core queries.
- Integration tests cover create/update/delete/list for each domain.

### Phase 5: Chat Transport and LLM Providers

**Goal:** Restore chat streaming without tools first.

Tasks:

- Port provider config model and user settings.
- Implement OpenAI-compatible streaming client.
- Add provider adapters for current DeepSeek/Qwen/Kimi/MiniMax/MiMo differences.
- Implement Photino bridge event streaming for `chat:started`, `agent:{turnId}`, title updates, and cancellation.

Acceptance:

- UI can start a chat and receive streaming text.
- Cancellation stops the backend operation.
- Session and message persistence match current frontend history behavior.

### Phase 6: Embeddings API and sqlite-vec

**Goal:** Replace local ONNX embedding with API-backed RAG.

Tasks:

- Implement `IEmbeddingClient`.
- Implement sqlite-vec extension loading and vector table creation.
- Implement `ITextChunker`.
- Implement index state tracking and rebuild queue.
- Port exact search plus semantic search integration.
- Port `search_story_memory` backend service behavior.

Acceptance:

- No ONNX Runtime or local model files are required.
- Rebuilding a novel index succeeds through configured Embeddings API.
- `SearchAll` combines entity, exact content, and semantic results.
- Changing embedding model/dimension marks the index stale and requires rebuild.

### Phase 7: Microsoft Agent Framework Migration

**Goal:** Replace custom Agent orchestration while preserving product behavior.

Tasks:

- Register current tools as MAF tools.
- Implement tool allowlists for main/review/memory agents.
- Port approval service.
- Port subagent orchestration.
- Port slash command injection and always/auto/manual skills.
- Port compression and token budget handling.
- Adapt MAF events to current `AgentEvent` payloads.

Acceptance:

- Agent can call read/search/edit tools.
- File edit approval opens the existing diff flow.
- Approved edits write files, update DB metadata, emit `file:changed`, and queue vector refresh.
- Review and memory subagents produce nested events under the parent turn.

### Phase 8: Packaging and Runtime

**Goal:** Produce installable desktop builds.

Tasks:

- Package Photino app host, frontend assets, sqlite-vec native binaries, and Git runtime.
- Remove ONNX Runtime and model download steps from packaging.
- Add Windows build first, then macOS/Linux.
- Add smoke-test script for launching app, health check, and basic UI navigation.

Acceptance:

- Windows installer starts without requiring Go, Wails, Node, Python, or ONNX.
- App can open migrated data and run a basic chat.
- Packaged app does not expose a business HTTP API by default; optional diagnostic HTTP uses loopback-only token protection.

### Phase 9: Retire Go/Wails

**Goal:** Remove old implementation only after replacement parity.

Tasks:

- Freeze Go/Wails code after last compatibility release.
- Keep old code in branch/tag for rollback.
- Remove `wails.json`, Go build scripts, ONNX scripts, and Go-only runtime docs from mainline.
- Update README, AGENTS.md, build docs, and release docs.

Acceptance:

- No frontend imports depend on Wails.
- No packaging path downloads ONNX Runtime.
- No Go tests are required for the new product build.
- Migration guide exists for existing users.

## Checkpoints

### Checkpoint A: Shell Parity

- Photino launches UI.
- Photino bridge handshake works.
- Optional .NET health endpoint works in debug/test mode.
- Window controls and external links work.

### Checkpoint B: Data Parity

- Existing copied data loads.
- CRUD panels work.
- Git-backed chapter edits work.
- Export works.

### Checkpoint C: Search Parity

- Exact search works.
- Semantic search works with API embeddings.
- Index rebuild status is visible and recoverable.

### Checkpoint D: Agent Parity

- Streaming chat works.
- Tool calls work.
- Approval flow works.
- Subagents work.
- Compression/manual context maintenance works.

### Checkpoint E: Release Candidate

- Packaged Windows build runs cleanly.
- No ONNX dependency remains.
- End-to-end smoke test passes.
- User data migration has rollback instructions.

## Verification Matrix

| Area | Tests |
| --- | --- |
| Bridge contracts | Golden payload tests, bridge request/response/event envelope tests |
| SQLite | Migration tests against copied fixture DB |
| File safety | Path traversal, concurrent edit, stale read checks |
| Git | Init, add, commit, revert, log |
| RAG | Chunking, embedding mock, sqlite-vec query, stale index detection |
| Agent | Tool schema snapshots, approval wait/resume, cancellation, subagent event routing |
| UI | Playwright smoke tests for the frontend plus Photino bridge smoke launch |
| Packaging | Launch, health check, data-dir detection, native binary presence |

## Risks and Mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| MAF behavior differs from current ReAct loop | High | Keep event and tool contracts stable; migrate tools first, then orchestration |
| Provider-specific thinking/tool streaming differs | High | Preserve provider adapters and add golden SSE fixtures |
| sqlite-vec .NET native loading differs by OS | High | Validate Windows first; isolate loading behind `IVectorExtensionLoader` |
| Embeddings API cost and latency | Medium | Batch requests, cache content hashes, background indexing, expose status |
| Embedding dimensions vary | High | Track dimension in index state; rebuild on change |
| Existing data corruption during migration | High | Copy-first migration, manifest, backups, read-only fixture tests |
| Photino/WebView lifecycle bugs | Medium | Bridge handshake, explicit startup/shutdown ownership, optional health checks in debug/test mode |
| Bridge message overload | High | Request limits, event batching, streaming throttles, cancellation, and backpressure tests |
| UI hidden Wails dependency | Medium | Replace `wailsjs` imports behind adapter, then grep-block them in CI |

## Open Questions

- Which Embeddings provider should be the default template: OpenAI, Qwen/DashScope, DeepSeek-compatible, or user-defined only?
- Should chat and embeddings share provider configuration, or should embeddings have a separate settings section?
- Should the first release migrate old `Goink` data in place or copy to `Novelist` and leave the source untouched?
- Should `goink.md` be renamed in new novels, or preserved for compatibility?
- Is Windows the only required first target, or must macOS/Linux packaging remain in the first milestone?

## Immediate Next Step

Start with Phase 0. The first implementation PR should not introduce .NET code. It should commit only API/event contract documentation and golden samples extracted from the current Wails surface. That gives every later slice a stable target and prevents the migration from turning into an uncontrolled rewrite.
