"""
章节管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel
from typing import Optional
from datetime import datetime


class ChapterBase(BaseModel):
    chapter_number: int
    title: Optional[str] = None
    content: Optional[str] = None
    summary: Optional[str] = None


class ChapterCreate(ChapterBase):
    novel_id: int


class ChapterUpdate(BaseModel):
    title: Optional[str] = None
    content: Optional[str] = None
    summary: Optional[str] = None
    status: Optional[str] = None


class ChapterResponse(ChapterBase):
    id: int
    novel_id: int
    status: str
    created_at: datetime
    updated_at: Optional[datetime] = None
    
    class Config:
        from_attributes = True
