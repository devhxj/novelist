"""
记忆管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel, Field, ConfigDict
from typing import Any
from datetime import datetime


class MemorySearchRequest(BaseModel):
    query: str = Field(..., min_length=1, max_length=500)
    search_type: str = Field(default="semantic")
    filters: dict[str, Any] | None = None
    top_k: int = Field(default=10, ge=1, le=50)


class MemoryChunkResponse(BaseModel):
    id: int
    type: str
    content: str
    chapter_id: int | None = None
    relevance_score: float
    metadata: dict[str, Any] | None = None
    
    model_config = ConfigDict(from_attributes=True)


class MemorySearchResponse(BaseModel):
    results: list[MemoryChunkResponse]
    total: int
    search_time: float


class MemoryIndexRequest(BaseModel):
    chapter_id: int
    chunk_size: int = Field(default=500, ge=100, le=2000)
    overlap: int = Field(default=50, ge=0, le=200)


class MemoryIndexResponse(BaseModel):
    chapter_id: int
    chunks_created: int
    status: str
    message: str
