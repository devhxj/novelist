"""
文本编辑模块 - Pydantic验证模型
"""
from datetime import datetime
from typing import Any
from pydantic import BaseModel, Field, ConfigDict
from enum import Enum


class ChangeTypeEnum(str, Enum):
    FULL_REPLACE = "full_replace"
    PARTIAL_EDIT = "partial_edit"
    INSERT = "insert"
    DELETE = "delete"


class ChangeStatusEnum(str, Enum):
    PENDING = "pending"
    ACCEPTED = "accepted"
    REJECTED = "rejected"


class TextChangeCreate(BaseModel):
    session_id: str = Field(..., description="会话ID")
    chapter_id: int = Field(..., description="章节ID")
    change_type: ChangeTypeEnum = Field(..., description="变更类型")
    new_content: str = Field(..., description="新内容")
    start_line: int | None = Field(default=None, description="起始行号（部分编辑时）")
    end_line: int | None = Field(default=None, description="结束行号（部分编辑时）")
    reason: str | None = Field(default=None, description="变更原因")


class DiffHunkSchema(BaseModel):
    old_start: int
    old_lines: int
    new_start: int
    new_lines: int
    changes: list[dict[str, Any]]


class DiffDataSchema(BaseModel):
    change_type: str
    hunks: list[DiffHunkSchema]
    old_content: str
    new_content: str
    summary: dict[str, int]


class TextChangeResponse(BaseModel):
    change_id: str
    session_id: str
    chapter_id: int
    change_type: str
    diff_data: DiffDataSchema | None = None
    old_content: str | None = None
    new_content: str | None = None
    start_line: int | None = None
    end_line: int | None = None
    status: str
    reason: str | None = None
    created_at: datetime
    resolved_at: datetime | None = None
    
    model_config = ConfigDict(from_attributes=True)


class TextChangeListResponse(BaseModel):
    changes: list[TextChangeResponse]
    total: int
    pending_count: int


class DiffPreviewResponse(BaseModel):
    change_id: str
    chapter_id: int
    diff: DiffDataSchema
    preview_old: str
    preview_new: str


class ChapterEditorResponse(BaseModel):
    chapter_id: int
    chapter_number: int
    title: str
    content: str
    word_count: int
    status: str
    has_pending_changes: bool
    pending_changes_count: int
