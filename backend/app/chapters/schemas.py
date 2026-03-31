"""
章节管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel, Field
from typing import Optional
from datetime import datetime


class ChapterBase(BaseModel):
    title: Optional[str] = None
    content: Optional[str] = None
    summary: Optional[str] = None


class ChapterCreate(ChapterBase):
    novel_id: int
    chapter_number: Optional[int] = Field(None, description="章节号，不传则自动获取下一个")


class ChapterUpdate(BaseModel):
    title: Optional[str] = None
    content: Optional[str] = None
    summary: Optional[str] = None
    status: Optional[str] = None


class ChapterResponse(ChapterBase):
    id: int
    novel_id: int
    chapter_number: int
    status: str
    word_count: int
    created_at: datetime
    updated_at: Optional[datetime] = None
    
    class Config:
        from_attributes = True


class NextChapterNumberResponse(BaseModel):
    next_chapter_number: int
    message: str = "下一个可用章节号"
