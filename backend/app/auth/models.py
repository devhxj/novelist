"""
认证模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, TIMESTAMP, Index, func
from sqlalchemy.orm import relationship
from datetime import datetime

from app.core.database import Base


class User(Base):
    """用户模型 - 存储用户账户信息"""
    __tablename__ = "users"
    
    id: int = Column(Integer, primary_key=True, autoincrement=True)
    username: str = Column(String(50), nullable=False, unique=True, index=True)
    email: str = Column(String(100), nullable=False, unique=True, index=True)
    password_hash: str = Column(String(255), nullable=False)
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    
    __table_args__ = (
        Index('idx_user_username_email', 'username', 'email'),
    )
