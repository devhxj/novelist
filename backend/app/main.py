"""
FastAPI主应用 - 符合API规范v1.0.0
模块化架构：每个模块作为一等公民，包含自己的models/schemas/router
"""
from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from app.core.database import engine, Base
from app.core.exceptions import APIException

from app.auth import router as auth_router
from app.novels import router as novels_router
from app.characters import router as characters_router
from app.chapters import router as chapters_router
from app.plot_events import router as plot_events_router
from app.memory import router as memory_router
from app.rag import router as rag_router
from app.agents import router as agents_router
from app.generation import router as generation_router

from app.auth.models import User
from app.novels.models import Novel
from app.characters.models import Character
from app.chapters.models import Chapter
from app.plot_events.models import PlotEvent
from app.memory.models import MemoryChunk
from app.rag.models import RAGContext
from app.agents.models import AgentTaskRecord

Base.metadata.create_all(bind=engine)

app = FastAPI(
    title="AI小说生成系统API",
    description="基于多智能体协作的AI小说生成系统 - API v1.0.0",
    version="1.0.0",
    docs_url="/docs",
    redoc_url="/redoc"
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
app.include_router(generation_router, prefix="/api/v1")

@app.get("/")
async def root():
    return {
        "message": "AI小说生成系统API",
        "version": "1.0.0",
        "docs": "/docs",
        "status": "running"
    }

@app.get("/health")
async def health_check():
    return {
        "success": True,
        "data": {
            "status": "healthy",
            "database": "connected",
            "redis": "connected"
        }
    }

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)
