"""
小说管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel
from typing import Optional
from datetime import datetime


class NovelBase(BaseModel):
    title: str
    genre: Optional[str] = None
    description: Optional[str] = None
    author_id: Optional[int] = None


class NovelCreate(NovelBase):
    pass


class NovelUpdate(BaseModel):
    title: Optional[str] = None
    genre: Optional[str] = None
    description: Optional[str] = None
    status: Optional[str] = None


class NovelResponse(NovelBase):
    id: int
    status: str
    created_at: datetime
    updated_at: Optional[datetime] = None
    
    class Config:
        from_attributes = True
