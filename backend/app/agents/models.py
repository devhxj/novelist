"""
Agent任务持久化模型
"""
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON, Index, func
from datetime import datetime
from typing import Optional, Dict, Any

from app.core.database import Base


class AgentTaskRecord(Base):
    """Agent任务记录 - 持久化任务状态"""
    __tablename__ = "agent_tasks"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    task_id: str = Column(String(100), unique=True, nullable=False, index=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Optional[int] = Column(Integer, ForeignKey("chapters.id", ondelete="SET NULL"))
    task_type: str = Column(String(50), nullable=False, index=True)
    status: str = Column(String(50), nullable=False, default='pending', index=True)
    parameters: Optional[Dict[str, Any]] = Column(JSON)
    context: Optional[Dict[str, Any]] = Column(JSON)
    result: Optional[Dict[str, Any]] = Column(JSON)
    error: Optional[str] = Column(Text)
    agent_id: Optional[str] = Column(String(100))
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    updated_at: Optional[datetime] = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    completed_at: Optional[datetime] = Column(TIMESTAMP)
    
    __table_args__ = (
        Index('idx_agent_task_novel_status', 'novel_id', 'status'),
        Index('idx_agent_task_type_status', 'task_type', 'status'),
    )
