"""
RAG检索模块 - Pydantic Schemas
"""
from pydantic import BaseModel, Field
from typing import Optional, List, Dict, Any
from datetime import datetime


class RAGQueryRequest(BaseModel):
    query: str = Field(..., min_length=1, max_length=1000)
    context_type: str = Field(default="writing")
    top_k: int = Field(default=5, ge=1, le=20)
    include_chapters: Optional[List[int]] = None
    include_characters: Optional[List[int]] = None


class RAGContextChunk(BaseModel):
    chunk_id: str
    content: str
    source_type: str
    source_id: Optional[int] = None
    relevance_score: float
    metadata: Optional[Dict[str, Any]] = None


class RAGContextResponse(BaseModel):
    context_id: int
    novel_id: int
    context_type: str
    query: Optional[str] = None
    context_content: str
    chunks: List[RAGContextChunk]
    total_chunks: int
    created_at: datetime
    
    class Config:
        from_attributes = True


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
    previous_summary: Optional[str] = None
    characters: List[Dict[str, Any]]
    plot_hints: List[Dict[str, Any]]
    context_length: int
