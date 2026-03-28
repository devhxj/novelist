"""
章节管理模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, UniqueConstraint, Index, func
from sqlalchemy.orm import relationship
from datetime import datetime
from typing import Optional

from app.core.database import Base


class Chapter(Base):
    """章节模型 - 存储小说章节内容"""
    __tablename__ = "chapters"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_number: int = Column(Integer, nullable=False)
    title: Optional[str] = Column(String(255))
    content: Optional[str] = Column(Text)
    summary: Optional[str] = Column(Text)
    status: str = Column(String(50), default='draft', index=True)
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    updated_at: Optional[datetime] = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    
    novel = relationship("Novel", back_populates="chapters")
    plot_events = relationship("PlotEvent", back_populates="chapter")
    
    __table_args__ = (
        UniqueConstraint('novel_id', 'chapter_number', name='uk_novel_chapter'),
        Index('idx_chapter_novel_number', 'novel_id', 'chapter_number'),
    )
