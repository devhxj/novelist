"""
叙事弧线模块
StoryArc: 管理跨越多章节的叙事弧线（主线/支线/角色线/背景线）
"""
from __future__ import annotations

from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Index, func
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, Dict, Any, TYPE_CHECKING
import enum

from core.database import Base

if TYPE_CHECKING:
    from novels.models import Novel


class StoryArcType(str, enum.Enum):
    MAIN = "main"
    SUB = "sub"
    CHARACTER = "character"
    BACKGROUND = "background"


class StoryArcStatus(str, enum.Enum):
    ACTIVE = "active"
    PAUSED = "paused"
    COMPLETED = "completed"
    ABANDONED = "abandoned"


class StoryArc(Base):
    """叙事弧线模型 - 管理跨越多章节的故事线

    与 TimelineEntry 的关系：
    - StoryArc: 宏观叙事弧线，如"主线：复仇之路""支线：感情线"
    - TimelineEntry(category=plot_node): 弧线内的具体情节节点
    - TimelineEntry.arc_id 关联到 StoryArc，表示该节点属于哪条弧线
    """
    __tablename__ = "story_arcs"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)

    name: Mapped[str] = mapped_column(String(255), nullable=False)
    description: Mapped[Optional[str]] = mapped_column(Text)
    arc_type: Mapped[str] = mapped_column(String(50), default=StoryArcType.SUB.value, index=True)

    start_chapter: Mapped[Optional[int]] = mapped_column(Integer)
    end_chapter: Mapped[Optional[int]] = mapped_column(Integer)

    importance: Mapped[int] = mapped_column(Integer, default=1)
    status: Mapped[str] = mapped_column(String(50), default=StoryArcStatus.ACTIVE.value)

    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())

    novel: Mapped["Novel"] = relationship(back_populates="story_arcs")

    __table_args__ = (
        Index('idx_story_arc_novel_type', 'novel_id', 'arc_type'),
    )
