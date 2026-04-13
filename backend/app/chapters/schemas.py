"""
章节管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel, Field, ConfigDict
from datetime import datetime


class ChapterBase(BaseModel):
    title: str | None = None
    content: str | None = None
    summary: str | None = None


class ChapterCreate(ChapterBase):
    novel_id: int
    chapter_number: int | None = Field(default=None, description="章节号，不传则自动获取下一个")


class ChapterUpdate(BaseModel):
    title: str | None = None
    content: str | None = None
    summary: str | None = None
    status: str | None = None


class ChapterResponse(ChapterBase):
    id: int
    novel_id: int
    chapter_number: int
    status: str
    word_count: int
    created_at: datetime
    updated_at: datetime | None = None
    
    model_config = ConfigDict(from_attributes=True)


class NextChapterNumberResponse(BaseModel):
    next_chapter_number: int
    message: str = "下一个可用章节号"
