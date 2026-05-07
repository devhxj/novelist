"""
章节管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel, Field, ConfigDict, computed_field
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
    outline_json: dict | None = None


class ChapterResponse(ChapterBase):
    id: int
    novel_id: int
    chapter_number: int
    status: str
    word_count: int
    outline_json: dict | None = None
    writing_status: str
    created_at: datetime
    updated_at: datetime | None = None

    model_config = ConfigDict(from_attributes=True)

    @computed_field
    @property
    def outline_text(self) -> str | None:
        if not self.outline_json:
            return None
        from chapters.utils import _format_outline
        return _format_outline(self.outline_json)


class NextChapterNumberResponse(BaseModel):
    next_chapter_number: int
    message: str = "下一个可用章节号"
