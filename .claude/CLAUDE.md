# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is an AI-powered novel creation and collaborative editing platform. The system provides an IDE-like experience for writing novels with AI assistance, featuring real-time collaboration, tool calls via Model Context Protocol (MCP), and a unified WebSocket chat interface.

## Common Development Tasks

### Backend Setup
1. Install dependencies: `pip install -r requirements.txt`
2. Copy environment variables: `cp .env.example .env` and configure database, Redis, and DeepSeek API keys
3. Initialize database: `python database/scripts/init_db.py`
4. Run the FastAPI server: `uvicorn backend.app.main:app --reload --host 0.0.0.0 --port 8000`

### Frontend Setup
1. Navigate to frontend directory: `cd frontend`
2. Install dependencies: `npm install`
3. Run development server: `npm run dev`

### Database Management
- Database URL format: `mysql+pymysql://user:password@host:3306/database_name`
- Tables are managed via SQLAlchemy ORM in `backend/app/*/models.py`
- Database schema is created automatically via `Base.metadata.create_all()`
- Migrations directory exists at `database/migrations/` but appears to be unused; schema changes require manual updates to models

### Testing
No project-specific test suite was found. The `dev_test/` directory contains experimental scripts.

## Architecture

### Backend Structure (`backend/app/`)
- **main.py**: FastAPI application entry point with lifespan management for database and Redis
- **Core modules** (`core/`):
  - `database.py`: Async SQLAlchemy setup with MySQL/AIOMySQL
  - `redis_service.py`: Redis connection pool for caching and session management
  - `llm_service.py`: DeepSeek API integration with streaming support
  - `websocket.py` & `ws_chat.py`: WebSocket manager and unified chat handler for real-time interactions
  - `vector_store.py`: ChromaDB integration for RAG
  - `diff_engine.py`: Text diff/patch utilities for collaborative editing
  - `edit_mode.py`: Edit session state management

- **Domain modules** (each with `models.py`, `schemas.py`, `router.py`, `service.py`):
  - `novels/`: Novel management and creative profiles
  - `characters/`: Character creation and development
  - `chapters/`: Chapter content and organization
  - `plot_events/`: Plot point tracking
  - `memory/`: Long-term memory storage for narrative consistency
  - `rag/`: Retrieval-augmented generation context
  - `agents/`: AI agent coordination (writer, reviewer, coordinator)
  - `planning/`: Plot outlining and structure
  - `consistency/`: Narrative consistency checking
  - `editor/`: Collaborative edit session management
  - `sessions/`: User session persistence
  - `generation/`: AI text generation endpoints

- **MCP Integration** (`mcp/`):
  - `server.py`: MCP server implementation exposing tools via SSE/stdio
  - `base.py`: Tool registry and execution framework
  - `router.py`: HTTP endpoint for MCP tool calls
  - Tool categories: `novel_tools.py`, `editing_tools.py`, `memory_tools.py`, `consistency_tools.py`

- **Workflows** (`workflows/`):
  - `langgraph_workflow.py`: LangGraph-based orchestration for multi-agent novel writing

### Frontend Structure (`frontend/`)
- Vite + React + TypeScript application
- Ant Design component library
- Monaco Editor for code/text editing
- Zustand for state management
- WebSocket client for real-time chat and editing
- Source organized in `src/` with typical React app structure

### Data Flow
1. User interacts via WebSocket chat or HTTP API
2. Requests are routed to appropriate domain service
3. AI agents coordinate via LangGraph workflows for complex tasks
4. MCP tools provide structured access to novel creation functions
5. Real-time edits are managed through edit sessions with diff/patch
6. Redis caches frequent queries and session data
7. ChromaDB stores embeddings for RAG context

### Key Dependencies
- **Backend**: FastAPI, SQLAlchemy, LangChain, LangGraph, ChromaDB, Redis, MCP
- **Frontend**: React, Ant Design, Monaco Editor, Zustand, Axios
- **AI**: DeepSeek API (primary LLM), OpenAI/Anthropic fallback support

## Development Notes

### Edit Session System
- Edit sessions are managed via `EditSession` model with PENDING/ACCEPTED/REJECTED states
- WebSocket messages: `start_edit`, `apply_edit`, `accept_edit`, `reject_edit`, `end_session`
- Edit operations are idempotent with `already_processed` flags to handle duplicate messages
- Real-time collaboration uses diff/patch algorithms for minimal bandwidth

### MCP Tool Patterns
- Tools inherit from `BaseMCPTool` with standardized `execute()` method
- Tool results follow `MCPToolResult(success, data, error, metadata)` schema
- Database sessions are automatically rolled back on tool execution errors
- Tools are organized by category and registered in `MCPToolRegistry`

### Agent Coordination
- Multiple AI agents (writer, reviewer, coordinator) work together via LangGraph
- Each agent has specialized capabilities and memory
- Workflow state is persisted and can be resumed

### Environment Configuration
Required services:
- MySQL database (configure via `DATABASE_URL`)
- Redis (configure via `REDIS_URL`) 
- DeepSeek API key (`DEEPSEEK_API_KEY`)
- JWT secret (`SECRET_KEY`)

The system can run without Redis but will lack caching and real-time features.