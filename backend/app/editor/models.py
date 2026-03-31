"""
文本编辑模型 - 支持副本编辑机制
"""
from datetime import datetime
from typing import Optional, Dict, Any, List
from sqlalchemy import Column, Integer, String, Text, DateTime, JSON, ForeignKey
from sqlalchemy.orm import relationship

from app.core.database import Base


class EditSessionStatus(str):
    PENDING = "pending"
    ACCEPTED = "accepted"
    REJECTED = "rejected"


class ChangeSource(str):
    AI = "ai"
    USER = "user"


class EditSession(Base):
    """编辑会话 - 副本"""
    __tablename__ = "edit_sessions"
    
    id = Column(Integer, primary_key=True, autoincrement=True)
    edit_session_id = Column(String(64), unique=True, nullable=False, index=True)
    chapter_id = Column(Integer, ForeignKey("chapters.id"), nullable=False, index=True)
    ws_session_id = Column(String(64), nullable=False, index=True)
    
    original_content = Column(Text, nullable=True)
    working_content = Column(Text, nullable=True)
    
    status = Column(String(16), default=EditSessionStatus.PENDING, index=True)
    change_count = Column(Integer, default=0)
    
    created_at = Column(DateTime, default=datetime.now, index=True)
    accepted_at = Column(DateTime, nullable=True)
    rejected_at = Column(DateTime, nullable=True)
    
    extra_metadata = Column(JSON, nullable=True)
    
    chapter = relationship("Chapter", back_populates="edit_sessions")
    changes = relationship("EditChange", back_populates="edit_session", cascade="all, delete-orphan")
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "edit_session_id": self.edit_session_id,
            "chapter_id": self.chapter_id,
            "ws_session_id": self.ws_session_id,
            "status": self.status,
            "change_count": self.change_count,
            "created_at": self.created_at.isoformat() if self.created_at else None,
            "accepted_at": self.accepted_at.isoformat() if self.accepted_at else None,
            "rejected_at": self.rejected_at.isoformat() if self.rejected_at else None
        }


class EditChange(Base):
    """单次修改记录"""
    __tablename__ = "edit_changes"
    
    id = Column(Integer, primary_key=True, autoincrement=True)
    edit_session_id = Column(Integer, ForeignKey("edit_sessions.id"), nullable=False, index=True)
    
    change_type = Column(String(32), nullable=False)
    source = Column(String(16), default=ChangeSource.AI)
    
    old_content = Column(Text, nullable=True)
    new_content = Column(Text, nullable=True)
    
    start_line = Column(Integer, nullable=True)
    end_line = Column(Integer, nullable=True)
    
    diff_data = Column(JSON, nullable=True)
    reason = Column(String(500), nullable=True)
    
    created_at = Column(DateTime, default=datetime.now)
    
    edit_session = relationship("EditSession", back_populates="changes")
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "edit_session_id": self.edit_session_id,
            "change_type": self.change_type,
            "source": self.source,
            "start_line": self.start_line,
            "end_line": self.end_line,
            "diff_summary": self.diff_data.get("summary", {}) if self.diff_data else {},
            "reason": self.reason,
            "created_at": self.created_at.isoformat() if self.created_at else None
        }
