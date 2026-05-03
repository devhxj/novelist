"""
小说管理模块 - 数据库模型
"""
from __future__ import annotations

from sqlalchemy import String, Text, Integer, Index, func, ForeignKey, JSON
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, Dict, Any, List, TYPE_CHECKING

from app.core.database import Base

if TYPE_CHECKING:
    from app.characters.models import Character
    from app.chapters.models import Chapter
    from app.story_arcs.models import StoryArc
    from app.timeline.models import TimelineEntry
    from app.locations.models import Location


class Novel(Base):
    """小说模型 - 存储小说基本信息"""
    __tablename__ = "novels"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    title: Mapped[str] = mapped_column(String(255), nullable=False, index=True)
    genre: Mapped[Optional[str]] = mapped_column(String(100), index=True)
    description: Mapped[Optional[str]] = mapped_column(Text)
    author_id: Mapped[Optional[int]] = mapped_column()
    status: Mapped[str] = mapped_column(String(50), default='draft', index=True)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())

    characters: Mapped[list["Character"]] = relationship(back_populates="novel")
    chapters: Mapped[list["Chapter"]] = relationship(back_populates="novel")
    story_arcs: Mapped[list["StoryArc"]] = relationship(back_populates="novel", cascade="all, delete-orphan")
    creative_profile: Mapped["NovelCreativeProfile"] = relationship(back_populates="novel", uselist=False, cascade="all, delete-orphan")
    timeline_entries: Mapped[list["TimelineEntry"]] = relationship(back_populates="novel", cascade="all, delete-orphan")
    locations: Mapped[list["Location"]] = relationship(back_populates="novel")

    __table_args__ = (
        Index('idx_novel_title_genre', 'title', 'genre'),
    )


class NovelCreativeProfile(Base):
    """作者创作偏好与协作配置 + 故事大纲 都是小说级别的"""
    __tablename__ = "novel_creative_profiles"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, unique=True, index=True)

    author_intent: Mapped[Optional[str]] = mapped_column(Text)
    preferred_tone: Mapped[Optional[str]] = mapped_column(String(255))
    collaboration_style: Mapped[Optional[str]] = mapped_column(String(100), default="ai_ide")
    scene_planning_notes: Mapped[Optional[str]] = mapped_column(Text)

    must_keep: Mapped[Optional[List[str]]] = mapped_column(JSON)
    must_avoid: Mapped[Optional[List[str]]] = mapped_column(JSON)
    long_term_goals: Mapped[Optional[List[str]]] = mapped_column(JSON)

    premise: Mapped[Optional[str]] = mapped_column(Text)
    theme: Mapped[Optional[str]] = mapped_column(String(255))
    act_structure: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    beginning: Mapped[Optional[str]] = mapped_column(Text)
    middle: Mapped[Optional[str]] = mapped_column(Text)
    climax: Mapped[Optional[str]] = mapped_column(Text)
    ending: Mapped[Optional[str]] = mapped_column(Text)
    total_chapters: Mapped[Optional[int]] = mapped_column(Integer)
    current_chapter: Mapped[int] = mapped_column(Integer, default=1)

    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())

    novel: Mapped["Novel"] = relationship(back_populates="creative_profile")


class UserCreativeProfile(Base):
    """作者全局创作偏好（跨书生效） 用户级别的"""
    __tablename__ = "user_creative_profiles"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    user_id: Mapped[int] = mapped_column(unique=True, nullable=False, index=True)

    global_writing_style: Mapped[Optional[str]] = mapped_column(Text)
    preferred_sentence_length: Mapped[Optional[str]] = mapped_column(String(50))
    default_pov: Mapped[Optional[str]] = mapped_column(String(50))
    global_must_keep: Mapped[Optional[List[str]]] = mapped_column(JSON)
    global_must_avoid: Mapped[Optional[List[str]]] = mapped_column(JSON)
    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(server_default=func.now(), onupdate=func.now())


class NovelStoryState(Base):
    """故事状态文档（CLAUDE.md 风格）- 每本小说一条，存 markdown 文本"""
    __tablename__ = "novel_story_states"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, unique=True, index=True)
    content: Mapped[str] = mapped_column(Text, default="")
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())


class ReaderPerspective(Base):
    """读者认知条目 - 记录读者已知信息、活跃悬念、读者误知"""
    __tablename__ = "reader_perspectives"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    type: Mapped[str] = mapped_column(String(20), nullable=False)  # known / suspense / misconception
    content: Mapped[str] = mapped_column(Text, nullable=False)
    related_truth: Mapped[Optional[str]] = mapped_column(Text)  # 仅 misconception：真实情况
    planted_chapter: Mapped[int] = mapped_column(Integer, nullable=False)
    revealed_chapter: Mapped[Optional[int]] = mapped_column(Integer)
    last_mentioned_chapter: Mapped[Optional[int]] = mapped_column(Integer)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())

    __table_args__ = (
        Index('idx_rp_novel_type', 'novel_id', 'type'),
    )
