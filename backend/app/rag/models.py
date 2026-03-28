"""
RAG检索模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON, Index, func, Float
from datetime import datetime
from typing import Optional, Dict, Any

from app.core.database import Base


class RAGContext(Base):
    """RAG上下文模型 - 存储构建的上下文信息"""
    __tablename__ = "rag_contexts"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Optional[int] = Column(Integer, ForeignKey("chapters.id", ondelete="SET NULL"))
    context_type: str = Column(String(50), nullable=False, index=True)
    query: Optional[str] = Column(Text)
    context_content: str = Column(Text, nullable=False)
    source_chunks: Optional[Dict[str, Any]] = Column(JSON)
    relevance_score: Optional[float] = Column(Float)
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    
    __table_args__ = (
        Index('idx_rag_novel_type', 'novel_id', 'context_type'),
    )
