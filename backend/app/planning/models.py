"""
情节规划模块 - 数据库模型
"""
from __future__ import annotations

from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Index, func, Boolean
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, List, Dict, Any, TYPE_CHECKING
import enum

from app.core.database import Base

if TYPE_CHECKING:
    from app.novels.models import Novel


class PlotLineType(str, enum.Enum):
    """情节线类型"""
    MAIN = "main"
    SUB = "sub"
    CHARACTER = "character"
    BACKGROUND = "background"


class PlotNodeStatus(str, enum.Enum):
    """情节节点状态"""
    PLANNED = "planned"
    IN_PROGRESS = "in_progress"
    COMPLETED = "completed"
    SKIPPED = "skipped"


class PlotLine(Base):
    """情节线模型 - 管理多条情节线（Layer2，与TimelineEntry/Layer4的foreshadowing完全独立）

    区分说明：
    - PlotLine(本类): 情节规划骨架，如"主线：复仇之路""支线：主角感情线"
    - TimelineEntry.foreshadowing: 伏笔追踪，如"第3章埋下的神秘信件→第15章揭晓"
    - 两者是不同层级的概念，PlotLine是宏观结构，foreshadowing是微观追踪"""
    __tablename__ = "plot_lines"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)

    name: Mapped[str] = mapped_column(String(255), nullable=False)
    description: Mapped[Optional[str]] = mapped_column(Text)
    line_type: Mapped[str] = mapped_column(String(50), default=PlotLineType.SUB.value, index=True)

    start_chapter: Mapped[Optional[int]] = mapped_column(Integer)
    end_chapter: Mapped[Optional[int]] = mapped_column(Integer)

    importance: Mapped[int] = mapped_column(Integer, default=1)
    status: Mapped[str] = mapped_column(String(50), default="active")

    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())

    novel: Mapped["Novel"] = relationship(back_populates="plot_lines")
    nodes: Mapped[list["PlotNode"]] = relationship(back_populates="plot_line", cascade="all, delete-orphan")

    __table_args__ = (
        Index('idx_plot_line_novel_type', 'novel_id', 'line_type'),
    )


class PlotNode(Base):
    """情节节点模型 - 管理关键情节节点"""
    __tablename__ = "plot_nodes"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    plot_line_id: Mapped[int] = mapped_column(ForeignKey("plot_lines.id", ondelete="CASCADE"), nullable=False, index=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)

    title: Mapped[str] = mapped_column(String(255), nullable=False)
    description: Mapped[Optional[str]] = mapped_column(Text)

    chapter_number: Mapped[Optional[int]] = mapped_column(Integer, index=True)
    sequence: Mapped[int] = mapped_column(Integer, default=0)

    status: Mapped[str] = mapped_column(String(50), default=PlotNodeStatus.PLANNED.value, index=True)

    characters_involved: Mapped[Optional[List[int]]] = mapped_column(JSON)
    prerequisites: Mapped[Optional[List[int]]] = mapped_column(JSON)
    consequences: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    notes: Mapped[Optional[str]] = mapped_column(Text)
    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())

    plot_line: Mapped["PlotLine"] = relationship(back_populates="nodes")
    novel: Mapped["Novel"] = relationship(back_populates="plot_nodes")

    __table_args__ = (
        Index('idx_plot_node_novel_chapter', 'novel_id', 'chapter_number'),
        Index('idx_plot_node_line_sequence', 'plot_line_id', 'sequence'),
    )


class PlotOutline(Base):
    """情节大纲模型 - 整体情节规划"""
    __tablename__ = "plot_outlines"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, unique=True, index=True)

    title: Mapped[str] = mapped_column(String(255), nullable=False)
    premise: Mapped[Optional[str]] = mapped_column(Text)
    theme: Mapped[Optional[str]] = mapped_column(String(255))

    act_structure: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    beginning: Mapped[Optional[str]] = mapped_column(Text)
    middle: Mapped[Optional[str]] = mapped_column(Text)
    climax: Mapped[Optional[str]] = mapped_column(Text)
    ending: Mapped[Optional[str]] = mapped_column(Text)

    total_chapters: Mapped[Optional[int]] = mapped_column(Integer)
    current_chapter: Mapped[int] = mapped_column(Integer, default=1)

    notes: Mapped[Optional[str]] = mapped_column(Text)
    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())

    novel: Mapped["Novel"] = relationship(back_populates="plot_outline", uselist=False)
