"""
聊天会话数据库模型 - 永久持久化存储
"""
from datetime import datetime
from typing import Optional, Dict, Any, List
from sqlalchemy import String, Text, DateTime, JSON, ForeignKey, Index
from sqlalchemy.dialects import mysql
from sqlalchemy.orm import Mapped, mapped_column, relationship

from core.database import Base


class ChatSession(Base):
    """聊天会话 - 永久存储"""
    __tablename__ = "chat_sessions"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    session_id: Mapped[str] = mapped_column(String(64), unique=True, nullable=False, index=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"), nullable=False, index=True)
    novel_id: Mapped[Optional[int]] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=True, index=True)

    title: Mapped[Optional[str]] = mapped_column(String(100), nullable=True)
    model: Mapped[str] = mapped_column(String(32), default="deepseek-v4-flash")

    summary: Mapped[Optional[str]] = mapped_column(Text, nullable=True)
    novel_context: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON, nullable=True)
    chapter_context: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON, nullable=True)
    pending_changes: Mapped[Optional[List]] = mapped_column(JSON, default=list)
    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON, nullable=True)

    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.now, index=True)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.now, onupdate=datetime.now, index=True)

    messages: Mapped[list["ChatMessage"]] = relationship("ChatMessage", back_populates="session", cascade="all, delete-orphan", order_by="ChatMessage.created_at")

    __table_args__ = (
        Index('idx_chat_session_user_novel', 'user_id', 'novel_id'),
        Index('idx_chat_session_user_updated', 'user_id', 'updated_at'),
    )

    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "session_id": self.session_id,
            "user_id": self.user_id,
            "novel_id": self.novel_id,
            "title": self.title,
            "model": self.model,
            "summary": self.summary,
            "novel_context": self.novel_context,
            "chapter_context": self.chapter_context,
            "pending_changes": self.pending_changes or [],
            "metadata": self.extra_metadata,
            "created_at": self.created_at.isoformat() if self.created_at else None,
            "updated_at": self.updated_at.isoformat() if self.updated_at else None
        }


class ChatMessage(Base):
    """聊天消息 - 永久存储"""
    __tablename__ = "chat_messages"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    session_id: Mapped[int] = mapped_column(ForeignKey("chat_sessions.id", ondelete="CASCADE"), nullable=False, index=True)

    role: Mapped[str] = mapped_column(String(16), nullable=False, index=True)
    content: Mapped[str] = mapped_column(Text().with_variant(mysql.MEDIUMTEXT(), 'mysql'), nullable=False)

    token_count: Mapped[int] = mapped_column(default=0)
    importance: Mapped[int] = mapped_column(default=50)
    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON, nullable=True)

    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.now, index=True)

    session: Mapped["ChatSession"] = relationship(back_populates="messages")

    __table_args__ = (
        Index('idx_chat_message_session_created', 'session_id', 'created_at'),
    )

    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "session_id": self.session_id,
            "role": self.role,
            "content": self.content,
            "token_count": self.token_count,
            "importance": self.importance,
            "metadata": self.extra_metadata,
            "created_at": self.created_at.isoformat() if self.created_at else None
        }
