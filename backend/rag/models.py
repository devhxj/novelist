"""
RAG检索模块 - 数据库模型
"""
from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Index, func, Float
from sqlalchemy.orm import Mapped, mapped_column
from datetime import datetime
from typing import Optional, Dict, Any

from core.database import Base


class RAGContext(Base):
    """RAG上下文模型 - 存储构建的上下文信息"""
    __tablename__ = "rag_contexts"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Mapped[Optional[int]] = mapped_column(ForeignKey("chapters.id", ondelete="SET NULL"))
    context_type: Mapped[str] = mapped_column(String(50), nullable=False, index=True)
    query: Mapped[Optional[str]] = mapped_column(Text)
    context_content: Mapped[str] = mapped_column(Text, nullable=False)
    source_chunks: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    relevance_score: Mapped[Optional[float]] = mapped_column(Float)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())

    __table_args__ = (
        Index('idx_rag_novel_type', 'novel_id', 'context_type'),
    )
