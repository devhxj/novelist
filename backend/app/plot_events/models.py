"""
情节事件管理模块 - 数据库模型
"""
from __future__ import annotations

from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Index, func
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, List, Dict, Any, TYPE_CHECKING

from app.core.database import Base

if TYPE_CHECKING:
    from app.novels.models import Novel
    from app.chapters.models import Chapter


class PlotEvent(Base):
    """情节事件模型 - 存储小说情节发展"""
    __tablename__ = "plot_events"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Mapped[Optional[int]] = mapped_column(ForeignKey("chapters.id", ondelete="SET NULL"))
    event_type: Mapped[Optional[str]] = mapped_column(String(50), index=True)
    description: Mapped[Optional[str]] = mapped_column(Text)
    characters_involved: Mapped[Optional[List[int]]] = mapped_column(JSON)
    timeline: Mapped[Optional[datetime]] = mapped_column()
    consequences: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())

    novel: Mapped["Novel"] = relationship(back_populates="plot_events")
    chapter: Mapped["Chapter"] = relationship(back_populates="plot_events")

    __table_args__ = (
        Index('idx_plot_novel_type', 'novel_id', 'event_type'),
    )
