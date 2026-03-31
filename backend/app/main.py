"""
FastAPI主应用 - AI IDE风格小说创作系统
统一WebSocket入口，整合所有生成功能到聊天界面
"""
from contextlib import asynccontextmanager
import logging
from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from app.core.database import init_db
from app.core.redis_service import redis_service
from app.core.exceptions import APIException

from app.auth import router as auth_router
from app.novels import router as novels_router
from app.characters import router as characters_router
from app.chapters import router as chapters_router
from app.plot_events import router as plot_events_router
from app.memory import router as memory_router
from app.rag import router as rag_router
from app.agents import router as agents_router
from app.consistency import router as consistency_router
from app.planning import router as planning_router
from app.mcp import router as mcp_router
from app.core.ws_chat import router as ws_chat_router
from app.generation import router as generation_router
from app.sessions import router as sessions_router
from app.editor.router import router as editor_router

from app.auth.models import User
from app.novels.models import Novel
from app.characters.models import Character
from app.chapters.models import Chapter
from app.plot_events.models import PlotEvent
from app.memory.models import MemoryChunk
from app.rag.models import RAGContext
from app.agents.models import AgentTaskRecord
from app.foreshadowing.models import Foreshadowing
from app.planning.models import PlotLine, PlotNode, PlotOutline
from app.editor.models import EditSession, EditChange
from app.chat.models import ChatSession, ChatMessage

logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    await init_db()
    
    try:
        await redis_service.connect()
        logger.info("Redis connected successfully")
    except Exception as e:
        logger.warning(f"Redis connection failed, running without cache: {e}")
    
    yield
    
    await redis_service.disconnect()


app = FastAPI(
    title="AI小说生成系统API",
    description="AI IDE风格小说创作系统 - 统一聊天界面整合所有功能",
    version="2.0.0",
    docs_url="/docs",
    redoc_url="/redoc",
    lifespan=lifespan
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.exception_handler(APIException)
async def api_exception_handler(request: Request, exc: APIException):
    return JSONResponse(
        status_code=exc.status_code,
        content={
            "success": False,
            "error": {
                "code": exc.code,
                "message": exc.message
            }
        }
    )


app.include_router(auth_router, prefix="/api/v1")
app.include_router(novels_router, prefix="/api/v1")
app.include_router(characters_router, prefix="/api/v1")
app.include_router(chapters_router, prefix="/api/v1")
app.include_router(plot_events_router, prefix="/api/v1")
app.include_router(memory_router, prefix="/api/v1")
app.include_router(rag_router, prefix="/api/v1")
app.include_router(agents_router, prefix="/api/v1")
app.include_router(consistency_router, prefix="/api/v1")
app.include_router(planning_router, prefix="/api/v1")
app.include_router(mcp_router, prefix="/api/v1")
app.include_router(generation_router, prefix="/api/v1")
app.include_router(sessions_router, prefix="/api/v1")
app.include_router(editor_router, prefix="/api/v1")
app.include_router(ws_chat_router)


@app.get("/")
async def root():
    return {
        "message": "AI小说生成系统API",
        "version": "2.0.0",
        "docs": "/docs",
        "status": "running",
        "features": [
            "ai_ide",
            "unified_chat",
            "realtime_editing",
            "tool_calls",
            "diff_preview",
            "generation_types: chapter/dialogue/description/outline/summary/character_profile"
        ],
        "websocket": {
            "endpoint": "/ws/chat",
            "description": "统一WebSocket入口，支持对话、生成、编辑"
        }
    }


@app.get("/health")
async def health_check():
    redis_status = "connected"
    try:
        if redis_service._redis:
            await redis_service.client.ping()
        else:
            redis_status = "not_configured"
    except Exception:
        redis_status = "disconnected"
    
    return {
        "success": True,
        "data": {
            "status": "healthy",
            "database": "connected",
            "redis": redis_status
        }
    }


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)
