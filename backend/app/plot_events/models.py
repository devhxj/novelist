"""
情节事件管理模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON, Index, func
from sqlalchemy.orm import relationship
from datetime import datetime
from typing import Optional, List, Dict, Any

from app.core.database import Base


class PlotEvent(Base):
    """情节事件模型 - 存储小说情节发展"""
    __tablename__ = "plot_events"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Optional[int] = Column(Integer, ForeignKey("chapters.id", ondelete="SET NULL"))
    event_type: Optional[str] = Column(String(50), index=True)
    description: Optional[str] = Column(Text)
    characters_involved: Optional[List[int]] = Column(JSON)
    timeline: Optional[datetime] = Column(TIMESTAMP)
    consequences: Optional[Dict[str, Any]] = Column(JSON)
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    
    novel = relationship("Novel", back_populates="plot_events")
    chapter = relationship("Chapter", back_populates="plot_events")
    
    __table_args__ = (
        Index('idx_plot_novel_type', 'novel_id', 'event_type'),
    )
