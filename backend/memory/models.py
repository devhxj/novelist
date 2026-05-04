"""
记忆管理模块 - 数据库模型
"""
from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Index, func, Float
from sqlalchemy.orm import Mapped, mapped_column
from datetime import datetime
from typing import Optional, Dict, Any

from core.database import Base


class MemoryChunk(Base):
    """记忆块模型 - 存储向量化后的内容片段"""
    __tablename__ = "memory_chunks"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Mapped[Optional[int]] = mapped_column(ForeignKey("chapters.id", ondelete="SET NULL"))
    chunk_type: Mapped[str] = mapped_column(String(50), nullable=False, index=True)
    content: Mapped[str] = mapped_column(Text, nullable=False)
    chunk_index: Mapped[int] = mapped_column(nullable=False)
    embedding_id: Mapped[Optional[str]] = mapped_column(String(100))
    chunk_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    relevance_score: Mapped[Optional[float]] = mapped_column(Float)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())

    __table_args__ = (
        Index('idx_memory_novel_type', 'novel_id', 'chunk_type'),
        Index('idx_memory_chapter', 'chapter_id'),
    )
