# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AI-powered novel creation and collaborative editing platform. IDE-like chat interface with multi-agent orchestration (LangGraph), MCP tool ecosystem, layered RAG context engine, and real-time collaborative editing via WebSocket.

## Common Commands

### Backend
```bash
cd backend && python -m venv .venv && source .venv/bin/activate  # Create and activate venv
pip install -r requirements.txt                                  # Install dependencies
cd ..
cp .env.example .env                                # Configure env vars
python database/scripts/init_db.py                  # Initialize database
uvicorn backend.main:app --reload --host 0.0.0.0 --port 8000  # Dev server
```

### Frontend
```bash
cd frontend && npm install     # Install dependencies
npm run dev                    # Dev server (Vite, port 5173)
npm run build                  # TypeScript check + production build (tsc -b && vite build)
npm run lint                   # ESLint check
```

### Testing
No test suite exists. No pytest config, no test files, no test dependencies in backend/requirements.txt.

## Architecture

### Backend (`backend/`)

**Entry point**: `main.py` — FastAPI app with lifespan management for DB/Redis.

**Core infrastructure** (`core/`):
- `database.py` — Async SQLAlchemy (MySQL via aiomysql+pymysql), session factory, `get_async_session` dependency
- `redis_service.py` — Redis connection pool for caching/pub-sub/session storage
- `llm_service.py` — DeepSeek streaming API wrapper, with model config, error handling, and stats tracking

**Chat & WebSocket** (`chat/`):
- `ws_chat.py` — **The central hub**: unified WebSocket chat handler that integrates session management, context building, LLM streaming, edit mode, and MCP tool calls. Tool-use loop with `__appended__` skip for workflow tools
- `ws_generation.py` — Legacy generation WebSocket handler (chapter/dialogue/description etc.)
- `edit_mode.py` — Collaborative editing state machine, system prompt (AGENT_SYSTEM_PROMPT), intent detection for chapter workflow
- `diff_engine.py` — Text diff/patch engine
- `session_manager.py` — Chat session state machine, message history, context compression
- `session_storage.py` — Session persistence via database

**Context injection** (`context/`):
- `context_builder.py` — 3-layer context injection: Layer 1 (system2, conversation start), Layer 2 (detailed for outline), Layer 3 (precise for writing) + 4-layer RAG (STATIC→STABLE→SLIDING→DYNAMIC)
- `prompt_templates.py` — System/user prompt templates, chapter outline prompt (`CHAPTER_OUTLINE_SYSTEM_PROMPT`), review prompt
- `vector_store.py` — ChromaDB operations for semantic search

**Domain modules**:
- `novels/` — Novel CRUD, creative profile, story state, reader perspective
- `chapters/` — Chapter CRUD, **`workflow.py`** (LangGraph chapter creation: build_layer2 → generate_outline → interrupt approval → build_layer3 → write_chapter → post_process)
- `characters/` — Character creation, profiles, relationships
- `timeline/` — Timeline entry management (foreshadowing, plot nodes, chapter plans, user directives)
- `story_arcs/` — Story arc management (main/sub/character/background arcs)
- `locations/` — Location management
- `plot_events/` — Plot event tracking
- `consistency/` — Narrative consistency checking
- `editor/` — Collaborative edit session management
- `rag/` — RAG context retrieval endpoints
- `memory/` — Long-term vector memory
- `sessions/` — Session persistence API
- `auth/` — Authentication routes (login, register, token refresh)
- `text/` — Text processing router/service
- `generation/` — AI text generation endpoints (HTTP + WebSocket via ws_generation.py)

**MCP tools** (`mcp_tools/`):
- `base.py` — `BaseMCPTool` abstract class with JSON Schema validation, `MCPToolRegistry`
- `registry.py` — Tool registration (all tools registered on startup)
- `workflow_tools.py` — **`create_chapter_workflow`** — LLM calls this to start LangGraph chapter creation pipeline
- Tool modules: `novel_tools.py`, `character_tools.py`, `editing_tools.py`, `memory_tools.py`, `consistency_tools.py`, `location_tools.py`, `timeline_tools.py`, `story_arc_tools.py`, `story_state_tools.py`, `reader_perspective_tools.py`

**Agent system** (`agents/`):
- `base.py` — Base agent class and Task data structures
- `coordinator.py` — Main orchestrator agent
- `writer.py` — Content generation agent
- `reviewer.py` — Quality review agent (reused in chapter workflow post_process)
- `memory.py` — Agent memory module for cross-session recall
- `factory.py` / `registry.py` — Agent creation and registration
- `router.py` — HTTP endpoints for agent task submission/status

### Frontend (`frontend/`)

Vite + React 19 + TypeScript + Ant Design 6 + Zustand + Monaco Editor.

**Pages** (`src/pages/`): `chat/ChatPage.tsx` (main IDE-like chat UI, ~1300+ lines), `editor/EditorPage.tsx` (Monaco editor), plus dedicated pages for: auth, chapter, character, consistency, generation, novel, planning, progress, timeline, workflow.

**Services** (`src/services/`): REST clients for each domain (novel, chapter, character, etc.) plus two WebSocket services — `wsGenerationService.ts` (chat/streaming) and `wsEditorService.ts` (collaborative editing).

**Stores** (`src/stores/`): Zustand stores — `authStore.ts` (auth state, token persistence), `novelStore.ts` (current novel context).

**Components** (`src/components/`): Reusable UI components organized by domain (auth, chapter, character, common, layout, novel) plus a `Markdown.tsx` renderer.

**State flow**: User input → ChatPage → WebSocket → backend ws_chat.py → LLM/MCP tools → streamed response back to ChatPage.

### Key Data Flow
1. User sends message via WebSocket (`ws/chat`) → `ws_chat.py` routes it
2. Session manager loads/creates session with system1 + system2 context
3. LLM tool-use loop: LLM decides which MCP tools to call (CRUD, retrieval, etc.)
4. For new chapter creation: LLM calls `create_chapter_workflow` → tool runs LangGraph:
   Layer2 → outline → interrupt(ws.receive approval) → Layer3 → write chapter → post-process
5. Tool returns `__appended__`, loop skips duplicate tool_result, LLM sees full results
6. Streamed response (`content_chunk` events) renders in ChatPage progressively
7. Edit operations go through EditSession state machine with diff/patch

### Dependencies
- **Backend**: FastAPI, SQLAlchemy+aiomysql+pymysql, LangGraph, ChromaDB, Redis, MCP SDK
- **Frontend**: React 19, Ant Design 6, Monaco Editor, Zustand, Axios, react-markdown
- **AI**: DeepSeek V4 (primary), OpenAI/GLM as fallback

### Environment
Required: `DATABASE_URL` (MySQL), `DEEPSEEK_API_KEY`, `SECRET_KEY` (JWT).
Optional: `REDIS_URL` (cache/pub-sub, degrades gracefully), `OPENAI_API_KEY`, `GLM_API_KEY`.

## Module Rules

If a module directory contains a `rules.md` file (e.g., `mcp_tools/rules.md`), you **must** read it before modifying any code in that directory. These files define conventions, patterns, and constraints that are not enforced by the linter.

## Coding Standards

### Type Annotations (Python)

Must use modern syntax for all type annotations (ruff rules UP045, UP006):
- `X | None` instead of `Optional[X]`
- `list[X]` instead of `List[X]`
- `dict[K, V]` instead of `Dict[K, V]`
- `tuple[X]` instead of `Tuple[X]`
- `set[X]` instead of `Set[X]`

Only `Any`, `TYPE_CHECKING`, `Callable`, `Annotated`, `Literal`, `TypedDict` etc. are allowed from `typing` module.

### Lint (CI)

GitHub Actions runs `ruff check --select UP045,UP006,UP035,F401` on push/PR to master.
Run locally: `ruff check --select UP045,UP006,UP035,F401 --fix backend/`

## Git Conventions

- Do NOT include `Co-Authored-By` trailers in commit messages.

## Critical Rules

- **NEVER delete or modify any `logger.info` / `logger.debug` / `logger.warning` / `logger.error` statements** without explicit user permission. These are vital for debugging.
- **NEVER delete or modify comments** without explicit user permission. Comments capture intent and design rationale.
- When refactoring or extracting code, preserve ALL existing log statements and comments. If they need to move to a different location, move them — do not remove them.
