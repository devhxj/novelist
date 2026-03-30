"""
伏笔管理模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON, Index, func, Enum
from sqlalchemy.orm import relationship
from datetime import datetime
from typing import Optional, Dict, Any
import enum

from app.core.database import Base


class ForeshadowingStatus(str, enum.Enum):
    """伏笔状态枚举"""
    UNRESOLVED = "unresolved"
    RESOLVED = "resolved"
    ABANDONED = "abandoned"


class ForeshadowingType(str, enum.Enum):
    """伏笔类型枚举"""
    PLOT = "plot"
    CHARACTER = "character"
    ITEM = "item"
    MYSTERY = "mystery"
    OTHER = "other"


class Foreshadowing(Base):
    """伏笔模型 - 追踪挖坑和填坑"""
    __tablename__ = "foreshadowings"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    created_chapter_id: Optional[int] = Column(Integer, ForeignKey("chapters.id", ondelete="SET NULL"))
    resolved_chapter_id: Optional[int] = Column(Integer, ForeignKey("chapters.id", ondelete="SET NULL"))
    
    title: str = Column(String(255), nullable=False)
    description: Optional[str] = Column(Text)
    foreshadowing_type: str = Column(String(50), default=ForeshadowingType.OTHER.value, index=True)
    status: str = Column(String(50), default=ForeshadowingStatus.UNRESOLVED.value, index=True)
    
    importance: int = Column(Integer, default=1)
    resolution_notes: Optional[str] = Column(Text)
    extra_metadata: Optional[Dict[str, Any]] = Column(JSON)
    
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    resolved_at: Optional[datetime] = Column(TIMESTAMP)
    updated_at: Optional[datetime] = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    
    novel = relationship("Novel", back_populates="foreshadowings")
    created_chapter = relationship("Chapter", foreign_keys=[created_chapter_id])
    resolved_chapter = relationship("Chapter", foreign_keys=[resolved_chapter_id])
    
    __table_args__ = (
        Index('idx_foreshadowing_novel_status', 'novel_id', 'status'),
        Index('idx_foreshadowing_novel_type', 'novel_id', 'foreshadowing_type'),
    )
