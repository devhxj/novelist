"""
聊天会话数据库模型 - 永久持久化存储
"""
from datetime import datetime
from typing import Optional, Dict, Any, List
from sqlalchemy import Column, Integer, String, Text, DateTime, JSON, ForeignKey, Index
from sqlalchemy.orm import relationship

from app.core.database import Base


class ChatSession(Base):
    """聊天会话 - 永久存储"""
    __tablename__ = "chat_sessions"
    
    id = Column(Integer, primary_key=True, autoincrement=True)
    session_id = Column(String(64), unique=True, nullable=False, index=True)
    user_id = Column(Integer, ForeignKey("users.id"), nullable=False, index=True)
    novel_id = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=True, index=True)
    
    scope_type = Column(String(16), default="novel", index=True)
    chapter_start = Column(Integer, nullable=True)
    chapter_end = Column(Integer, nullable=True)
    
    title = Column(String(100), nullable=True)
    model = Column(String(32), default="deepseek-chat")
    
    summary = Column(Text, nullable=True)
    novel_context = Column(JSON, nullable=True)
    chapter_context = Column(JSON, nullable=True)
    pending_changes = Column(JSON, default=list)
    extra_metadata = Column(JSON, nullable=True)
    
    created_at = Column(DateTime, default=datetime.now, index=True)
    updated_at = Column(DateTime, default=datetime.now, onupdate=datetime.now, index=True)
    
    messages = relationship("ChatMessage", back_populates="session", cascade="all, delete-orphan", order_by="ChatMessage.created_at")
    
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
            "scope_type": self.scope_type,
            "chapter_start": self.chapter_start,
            "chapter_end": self.chapter_end,
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
    
    id = Column(Integer, primary_key=True, autoincrement=True)
    session_id = Column(Integer, ForeignKey("chat_sessions.id", ondelete="CASCADE"), nullable=False, index=True)
    
    role = Column(String(16), nullable=False, index=True)
    content = Column(Text, nullable=False)
    
    token_count = Column(Integer, default=0)
    importance = Column(Integer, default=50)
    extra_metadata = Column(JSON, nullable=True)
    
    created_at = Column(DateTime, default=datetime.now, index=True)
    
    session = relationship("ChatSession", back_populates="messages")
    
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
