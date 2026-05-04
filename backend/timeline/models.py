"""
故事时间线模块 - 数据库模型
TimelineEntry: 统一的时间线条目，替代分散的 Foreshadowing 系统
"""
from __future__ import annotations

from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Index, func
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, Dict, Any, List, TYPE_CHECKING
import enum

from core.database import Base

if TYPE_CHECKING:
    from novels.models import Novel
    from chapters.models import Chapter
    from story_arcs.models import StoryArc


class TimelineEntryCategory(str, enum.Enum):
    FORESHADOWING = "foreshadowing"
    PLOT_NODE = "plot_node"
    CHAPTER_PLAN = "chapter_plan"
    USER_DIRECTIVE = "user_directive"


class TimelineEntryStatus(str, enum.Enum):
    PENDING = "pending"
    ACTIVE = "active"
    COMPLETED = "completed"
    RESOLVED = "resolved"
    ABANDONED = "abandoned"
    DEFERRED = "deferred"


class TimeHorizon(str, enum.Enum):
    NEXT = "next"
    NEAR_TERM = "near_term"
    LONG_TERM = "long_term"
    UNDEFINED = "undefined"


class TimelineEntry(Base):
    __tablename__ = "timeline_entries"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)

    category: Mapped[str] = mapped_column(String(50), nullable=False, index=True)
    status: Mapped[str] = mapped_column(String(50), default=TimelineEntryStatus.PENDING.value, index=True)

    title: Mapped[str] = mapped_column(String(255), nullable=False)
    description: Mapped[Optional[str]] = mapped_column(Text)
    detail_json: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    target_chapter: Mapped[Optional[int]] = mapped_column(Integer, index=True)
    time_horizon: Mapped[Optional[str]] = mapped_column(String(20))

    arc_id: Mapped[Optional[int]] = mapped_column(ForeignKey("story_arcs.id", ondelete="SET NULL"), index=True)
    sequence: Mapped[int] = mapped_column(Integer, default=0)

    importance: Mapped[int] = mapped_column(Integer, default=3)
    source: Mapped[str] = mapped_column(String(50), default="ai")
    source_chapter_id: Mapped[Optional[int]] = mapped_column(ForeignKey("chapters.id", ondelete="SET NULL"))

    resolved_chapter_id: Mapped[Optional[int]] = mapped_column(ForeignKey("chapters.id", ondelete="SET NULL"))
    related_entry_ids: Mapped[Optional[List[int]]] = mapped_column(JSON)
    tags: Mapped[Optional[List[str]]] = mapped_column(JSON)

    version: Mapped[int] = mapped_column(Integer, default=1)
    last_editor: Mapped[Optional[str]] = mapped_column(String(50))
    original_ai_output: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(server_default=func.now(), onupdate=func.now())
    resolved_at: Mapped[Optional[datetime]] = mapped_column()

    novel: Mapped["Novel"] = relationship(back_populates="timeline_entries")
    source_chapter: Mapped["Chapter"] = relationship(foreign_keys=[source_chapter_id])
    resolved_chapter: Mapped["Chapter"] = relationship(foreign_keys=[resolved_chapter_id])
    arc: Mapped[Optional["StoryArc"]] = relationship(foreign_keys=[arc_id])

    __table_args__ = (
        Index('idx_timeline_novel_category', 'novel_id', 'category'),
        Index('idx_timeline_novel_status', 'novel_id', 'status'),
        Index('idx_timeline_novel_chapter', 'novel_id', 'target_chapter'),
        Index('idx_timeline_horizon', 'time_horizon'),
        Index('idx_timeline_arc', 'arc_id'),
    )
