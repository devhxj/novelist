"""
记忆管理模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON, Index, func, Float
from datetime import datetime
from typing import Optional, Dict, Any

from app.core.database import Base


class MemoryChunk(Base):
    """记忆块模型 - 存储向量化后的内容片段"""
    __tablename__ = "memory_chunks"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Optional[int] = Column(Integer, ForeignKey("chapters.id", ondelete="SET NULL"))
    chunk_type: str = Column(String(50), nullable=False, index=True)
    content: str = Column(Text, nullable=False)
    chunk_index: int = Column(Integer, nullable=False)
    embedding_id: Optional[str] = Column(String(100))
    chunk_metadata: Optional[Dict[str, Any]] = Column(JSON)
    relevance_score: Optional[float] = Column(Float)
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    
    __table_args__ = (
        Index('idx_memory_novel_type', 'novel_id', 'chunk_type'),
        Index('idx_memory_chapter', 'chapter_id'),
    )
