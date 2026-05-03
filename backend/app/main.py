"""
FastAPI主应用 - AI IDE风格小说创作系统
统一WebSocket入口，整合所有生成功能到聊天界面
"""
from contextlib import asynccontextmanager
import logging
from fastapi import FastAPI, Request, HTTPException
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from app.core.database import init_db
from app.core.redis_service import redis_service
from app.core.exceptions import APIException, BusinessError, SystemError
from app.core.llm_service import LLMServiceError

import app.agents.writer
import app.agents.reviewer
import app.agents.memory

from app.auth.router import router as auth_router
from app.novels.router import router as novels_router
from app.characters.router import router as characters_router
from app.locations.router import router as locations_router
from app.chapters.router import router as chapters_router
from app.memory.router import router as memory_router
from app.rag.router import router as rag_router
from app.agents.router import router as agents_router
from app.consistency.router import router as consistency_router
from app.story_arcs.router import router as story_arcs_router
from app.mcp.router import router as mcp_router
from app.mcp.server import get_mcp_transport
from app.core.ws_chat import router as ws_chat_router
from app.generation.router import router as generation_router
from app.sessions.router import router as sessions_router
from app.editor.router import router as editor_router
from app.timeline.router import router as timeline_router

from app.auth.models import User
from app.novels.models import Novel
from app.characters.models import Character
from app.chapters.models import Chapter
from app.memory.models import MemoryChunk
from app.rag.models import RAGContext
from app.agents.models import AgentTaskRecord
from app.story_arcs.models import StoryArc
from app.editor.models import EditSession, EditChange
from app.timeline.models import TimelineEntry
from app.novels.models import UserCreativeProfile, NovelStoryState, ReaderPerspective
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
    
    from app.core.memory_retry import start_retry_background_task, stop_retry_background_task
    start_retry_background_task()
    
    yield
    
    stop_retry_background_task()
    
    try:
        from app.core.vector_store import vector_store
        vector_store.close()
    except Exception as e:
        logger.warning(f"VectorStore shutdown cleanup failed: {e}")

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


@app.exception_handler(BusinessError)
async def business_exception_handler(request: Request, exc: BusinessError):
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


@app.exception_handler(SystemError)
async def system_exception_handler(request: Request, exc: SystemError):
    logger.error(f"System error: code={exc.code}, message={exc.message}")
    return JSONResponse(
        status_code=exc.status_code,
        content={
            "success": False,
            "error": {
                "code": exc.code,
                "message": "服务器异常，请稍后重试。"
            }
        }
    )


@app.exception_handler(HTTPException)
async def http_exception_handler(request: Request, exc: HTTPException):
    return JSONResponse(
        status_code=exc.status_code,
        content={
            "success": False,
            "error": {
                "code": "HTTP_ERROR",
                "message": str(exc.detail)
            }
        }
    )


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    logger.warning(f"Request validation error: {exc.errors()}")
    return JSONResponse(
        status_code=422,
        content={
            "success": False,
            "error": {
                "code": "REQUEST_VALIDATION_ERROR",
                "message": "请求参数不合法，请检查输入内容。"
            }
        }
    )


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception):
    logger.error(f"Unhandled server error: {exc}", exc_info=True)
    return JSONResponse(
        status_code=500,
        content={
            "success": False,
            "error": {
                "code": "INTERNAL_SERVER_ERROR",
                "message": "服务器处理请求时出现异常，请稍后重试。"
            }
        }
    )


app.include_router(auth_router, prefix="/api/v1")
app.include_router(novels_router, prefix="/api/v1")
app.include_router(characters_router, prefix="/api/v1")
app.include_router(locations_router, prefix="/api/v1")
app.include_router(chapters_router, prefix="/api/v1")
app.include_router(memory_router, prefix="/api/v1")
app.include_router(rag_router, prefix="/api/v1")
app.include_router(agents_router, prefix="/api/v1")
app.include_router(consistency_router, prefix="/api/v1")
app.include_router(story_arcs_router, prefix="/api/v1")
app.include_router(mcp_router, prefix="/api/v1")
app.include_router(generation_router, prefix="/api/v1")
app.include_router(sessions_router, prefix="/api/v1")
app.include_router(editor_router, prefix="/api/v1")
app.include_router(timeline_router, prefix="/api/v1")
app.include_router(ws_chat_router)
app.mount("/mcp", get_mcp_transport())


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
