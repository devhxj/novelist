"""
情节规划模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON, Index, func, Boolean
from sqlalchemy.orm import relationship
from datetime import datetime
from typing import Optional, List, Dict, Any
import enum

from app.core.database import Base


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
    """情节线模型 - 管理多条情节线"""
    __tablename__ = "plot_lines"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    
    name: str = Column(String(255), nullable=False)
    description: Optional[str] = Column(Text)
    line_type: str = Column(String(50), default=PlotLineType.SUB.value, index=True)
    
    start_chapter: Optional[int] = Column(Integer)
    end_chapter: Optional[int] = Column(Integer)
    
    importance: int = Column(Integer, default=1)
    status: str = Column(String(50), default="active")
    
    metadata: Optional[Dict[str, Any]] = Column(JSON)
    
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    updated_at: Optional[datetime] = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    
    novel = relationship("Novel", back_populates="plot_lines")
    nodes = relationship("PlotNode", back_populates="plot_line", cascade="all, delete-orphan")
    
    __table_args__ = (
        Index('idx_plot_line_novel_type', 'novel_id', 'line_type'),
    )


class PlotNode(Base):
    """情节节点模型 - 管理关键情节节点"""
    __tablename__ = "plot_nodes"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    plot_line_id: int = Column(Integer, ForeignKey("plot_lines.id", ondelete="CASCADE"), nullable=False, index=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    
    title: str = Column(String(255), nullable=False)
    description: Optional[str] = Column(Text)
    
    chapter_number: Optional[int] = Column(Integer, index=True)
    sequence: int = Column(Integer, default=0)
    
    status: str = Column(String(50), default=PlotNodeStatus.PLANNED.value, index=True)
    
    characters_involved: Optional[List[int]] = Column(JSON)
    prerequisites: Optional[List[int]] = Column(JSON)
    consequences: Optional[Dict[str, Any]] = Column(JSON)
    
    notes: Optional[str] = Column(Text)
    metadata: Optional[Dict[str, Any]] = Column(JSON)
    
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    updated_at: Optional[datetime] = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    
    plot_line = relationship("PlotLine", back_populates="nodes")
    novel = relationship("Novel", back_populates="plot_nodes")
    
    __table_args__ = (
        Index('idx_plot_node_novel_chapter', 'novel_id', 'chapter_number'),
        Index('idx_plot_node_line_sequence', 'plot_line_id', 'sequence'),
    )


class PlotOutline(Base):
    """情节大纲模型 - 整体情节规划"""
    __tablename__ = "plot_outlines"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, unique=True, index=True)
    
    title: str = Column(String(255), nullable=False)
    premise: Optional[str] = Column(Text)
    theme: Optional[str] = Column(String(255))
    
    act_structure: Optional[Dict[str, Any]] = Column(JSON)
    
    beginning: Optional[str] = Column(Text)
    middle: Optional[str] = Column(Text)
    climax: Optional[str] = Column(Text)
    ending: Optional[str] = Column(Text)
    
    total_chapters: Optional[int] = Column(Integer)
    current_chapter: int = Column(Integer, default=1)
    
    notes: Optional[str] = Column(Text)
    metadata: Optional[Dict[str, Any]] = Column(JSON)
    
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    updated_at: Optional[datetime] = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    
    novel = relationship("Novel", back_populates="plot_outline", uselist=False)
