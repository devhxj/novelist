"""
角色管理模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, JSON, TIMESTAMP, ForeignKey, Index, func
from sqlalchemy.orm import relationship
from datetime import datetime
from typing import Optional, List, Dict, Any

from app.core.database import Base


class Character(Base):
    """角色模型 - 存储小说角色信息"""
    __tablename__ = "characters"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    name: str = Column(String(100), nullable=False, index=True)
    personality: Optional[Dict[str, Any]] = Column(JSON)
    relationships: Optional[Dict[str, List[int]]] = Column(JSON)
    abilities: Optional[List[str]] = Column(JSON)
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    
    novel = relationship("Novel", back_populates="characters")
    
    __table_args__ = (
        Index('idx_character_novel_name', 'novel_id', 'name'),
    )
