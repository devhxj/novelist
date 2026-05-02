# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AI-powered novel creation and collaborative editing platform. IDE-like chat interface with multi-agent orchestration (LangGraph), MCP tool ecosystem, layered RAG context engine, and real-time collaborative editing via WebSocket.

## Common Commands

### Backend
```bash
pip install -r requirements.txt                    # Install dependencies
cp .env.example .env                                # Configure env vars
python database/scripts/init_db.py                  # Initialize database
uvicorn backend.app.main:app --reload --host 0.0.0.0 --port 8000  # Dev server
```

### Frontend
```bash
cd frontend && npm install     # Install dependencies
npm run dev                    # Dev server (Vite, port 5173)
npm run build                  # TypeScript check + production build (tsc -b && vite build)
npm run lint                   # ESLint check
```

### Testing
No test suite exists. No pytest config, no test files, no test dependencies in requirements.txt.

## Architecture

### Backend (`backend/app/`)

**Entry point**: `main.py` — FastAPI app with lifespan management for DB/Redis.

**Core infrastructure** (`core/`):
- `database.py` — Async SQLAlchemy (MySQL via aiomysql), session factory, `get_async_session` dependency
- `redis_service.py` — Redis connection pool for caching/pub-sub/session storage
- `llm_service.py` — DeepSeek streaming API wrapper, with model config and error handling
- `ws_chat.py` — **The central hub**: unified WebSocket chat handler that integrates session management, context building, LLM streaming, edit mode, and MCP tool calls. This is the most complex file in the system (~1400+ lines)
- `websocket.py` — WebSocket connection manager with client tracking and progress reporting
- `context_builder.py` — 4-layer RAG context assembly (STATIC → STABLE → SLIDING → DYNAMIC)
- `vector_store.py` — ChromaDB operations for semantic search
- `session_manager.py` — Chat session state machine (novel/chapter/free scopes), message history, context compression
- `session_storage.py` — Session persistence via database
- `prompt_templates.py` — System/user prompt templates separated by generation type, LLM model enum
- `edit_mode.py` / `diff_engine.py` — Collaborative editing state machine and text diff/patch
- `auth.py` / `jwt.py` — JWT authentication utilities
- `exceptions.py` — Unified exception hierarchy (`APIException`, `NotFoundException`, etc.)
- `dependencies.py` / `permissions.py` — FastAPI dependency injection and permission checks
- `response.py` — Standardized API response format
- `chapter_post_processor.py` / `chapter_summary.py` — Chapter post-processing and summary generation

**Domain modules** (each with `models.py`, `schemas.py`, `router.py`, `service.py`):
- `novels/` — Novel CRUD and profile management
- `characters/` — Character creation, profiles, relationships
- `chapters/` — Chapter content management
- `timeline/` — Timeline entry management
- `locations/` — Location management (full CRUD module)
- `memory/` — Long-term memory for narrative consistency
- `rag/` — RAG context retrieval endpoints
- `consistency/` — Narrative consistency checking
- `editor/` — Collaborative edit session management
- `sessions/` — Session persistence API
- `chat/` — Chat message models (data layer backing `session_manager`)
- `planning/` — Plot outlining (PlotLine, PlotNode CRUD)
- `generation/` — AI text generation endpoints (both streaming and non-streaming)
- `auth/` — Authentication routes (login, register, token refresh)
- `text/` — Text processing router/service
- `agents/` — Agent system (see below)
- `workflows/` — LangGraph workflow (see below)

**Agent system** (`agents/`):
- `base.py` — Base agent class and Task data structures
- `coordinator.py` — Main orchestrator agent with 8-layer task chain
- `writer.py` — Content generation agent with 30+ parameter prompt building
- `reviewer.py` — Quality review agent
- `memory.py` — Agent memory module for cross-session recall
- `context.py` / `context_provider.py` — Agent-specific context assembly
- `factory.py` / `registry.py` — Agent creation and registration
- `models.py` — Agent data models
- `router.py` — HTTP endpoints for agent task submission/status

**LangGraph workflow** (`workflows/langgraph_workflow.py`):
- 7-node pipeline: Context Prep → Generate → Review → Consistency Check → Revise → Save → Memory Update
- Conditional routing: auto-revise up to 3 iterations based on review scores
- State persistence via MemorySaver checkpoints for resume capability

**MCP tools** (`mcp/`):
- `server.py` — FastMCP server (SSE + StdIO dual transport)
- `base.py` — `BaseMCPTool` abstract class with JSON Schema parameter validation
- `registry.py` — Plugin-style tool registry
- `router.py` — HTTP proxy endpoint for MCP calls
- Tool modules: `novel_tools.py`, `character_tools.py`, `editing_tools.py`, `memory_tools.py`, `consistency_tools.py`, `location_tools.py`, `timeline_tools.py`

### Frontend (`frontend/`)

Vite + React 19 + TypeScript + Ant Design 6 + Zustand + Monaco Editor.

**Pages** (`src/pages/`): `chat/ChatPage.tsx` (main IDE-like chat UI, ~1300+ lines), `editor/EditorPage.tsx` (Monaco editor), plus dedicated pages for: auth, chapter, character, consistency, generation, novel, planning, progress, timeline, workflow.

**Services** (`src/services/`): REST clients for each domain (novel, chapter, character, etc.) plus two WebSocket services — `wsGenerationService.ts` (chat/streaming) and `wsEditorService.ts` (collaborative editing).

**Stores** (`src/stores/`): Zustand stores — `authStore.ts` (auth state, token persistence), `novelStore.ts` (current novel context).

**Components** (`src/components/`): Reusable UI components organized by domain (auth, chapter, character, common, layout, novel) plus a `Markdown.tsx` renderer.

**State flow**: User input → ChatPage → WebSocket → backend ws_chat.py → LLM/MCP tools → streamed response back to ChatPage.

### Key Data Flow
1. User sends message via WebSocket (`ws/chat`) → `ws_chat.py` routes it
2. Session manager loads/creates session with proper scope context
3. ContextBuilder assembles 4-layer RAG context for the LLM
4. For generation tasks: LangGraph workflow orchestrates Writer→Reviewer→Consistency agents
5. MCP tools used for structured operations (CRUD, retrieval, consistency checks)
6. Streamed response (`content_chunk` events) renders in ChatPage progressively
7. Edit operations go through EditSession state machine with diff/patch

### Dependencies
- **Backend**: FastAPI, SQLAlchemy+aiomysql, LangChain+LangGraph, ChromaDB, Redis, MCP SDK
- **Frontend**: React 19, Ant Design 6, Monaco Editor, Zustand, Axios, react-markdown
- **AI**: DeepSeek V4 (primary), OpenAI/Anthropic as fallback

### Environment
Required: `DATABASE_URL` (MySQL), `DEEPSEEK_API_KEY`, `SECRET_KEY` (JWT).
Optional: `REDIS_URL` (cache/pub-sub, degrades gracefully).

## Git Conventions

- Do NOT include `Co-Authored-By` trailers in commit messages.
