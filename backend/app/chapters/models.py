"""
章节管理模块 - 数据库模型
"""
from sqlalchemy import String, Text, Integer, ForeignKey, UniqueConstraint, Index, func
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, List

from app.core.database import Base


class Chapter(Base):
    """章节模型 - 存储小说章节内容"""
    __tablename__ = "chapters"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_number: Mapped[int] = mapped_column(nullable=False)
    title: Mapped[Optional[str]] = mapped_column(String(255))
    content: Mapped[Optional[str]] = mapped_column(Text)
    summary: Mapped[Optional[str]] = mapped_column(Text)
    status: Mapped[str] = mapped_column(String(50), default='draft', index=True)
    word_count: Mapped[int] = mapped_column(default=0)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())

    novel: Mapped["Novel"] = relationship(back_populates="chapters")
    edit_sessions: Mapped[list["EditSession"]] = relationship(back_populates="chapter", cascade="all, delete-orphan")

    __table_args__ = (
        UniqueConstraint('novel_id', 'chapter_number', name='uk_novel_chapter'),
        Index('idx_chapter_novel_number', 'novel_id', 'chapter_number'),
    )

    def to_dict(self) -> dict:
        return {
            "id": self.id,
            "novel_id": self.novel_id,
            "chapter_number": self.chapter_number,
            "title": self.title,
            "content": self.content,
            "summary": self.summary,
            "status": self.status,
            "word_count": self.word_count,
            "created_at": self.created_at.isoformat() if self.created_at else None,
            "updated_at": self.updated_at.isoformat() if self.updated_at else None
        }
