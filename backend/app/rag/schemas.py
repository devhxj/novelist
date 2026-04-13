"""
RAG检索模块 - Pydantic Schemas
"""
from pydantic import BaseModel, Field, ConfigDict
from typing import Any
from datetime import datetime


class RAGQueryRequest(BaseModel):
    query: str = Field(..., min_length=1, max_length=1000)
    context_type: str = Field(default="writing")
    top_k: int = Field(default=5, ge=1, le=20)
    include_chapters: list[int] | None = None
    include_characters: list[int] | None = None


class RAGContextChunk(BaseModel):
    chunk_id: str
    content: str
    source_type: str
    source_id: int | None = None
    relevance_score: float
    metadata: dict[str, Any] | None = None


class RAGContextResponse(BaseModel):
    context_id: int
    novel_id: int
    context_type: str
    query: str | None = None
    context_content: str
    chunks: list[RAGContextChunk]
    total_chunks: int
    created_at: datetime
    
    model_config = ConfigDict(from_attributes=True)


class WritingContextRequest(BaseModel):
    chapter_id: int
    context_size: int = Field(default=3000, ge=500, le=10000)
    include_previous_chapters: bool = True
    include_characters: bool = True
    include_plot_events: bool = True


class WritingContextResponse(BaseModel):
    chapter_id: int
    novel_id: int
    context: str
    previous_summary: str | None = None
    characters: list[dict[str, Any]]
    plot_hints: list[dict[str, Any]]
    context_length: int
