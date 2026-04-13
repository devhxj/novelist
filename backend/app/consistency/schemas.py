"""
一致性检查 - Pydantic模型
"""
from pydantic import BaseModel, Field, ConfigDict
from typing import Any
from enum import Enum


class SeverityLevel(str, Enum):
    info = "info"
    warning = "warning"
    error = "error"


class IssueType(str, Enum):
    character = "character"
    plot = "plot"
    timeline = "timeline"
    foreshadowing = "foreshadowing"
    unknown = "unknown"


class ConsistencyIssue(BaseModel):
    """一致性问题"""
    issue_type: str = Field(..., description="问题类型")
    severity: str = Field(default="info", description="严重程度")
    chapter_id: int | None = Field(default=None, description="章节ID")
    chapter_number: int | None = Field(default=None, description="章节号")
    description: str = Field(..., description="问题描述")
    details: dict[str, Any] | None = Field(default=None, description="详细信息")
    suggestion: str | None = Field(default=None, description="修改建议")

    model_config = ConfigDict(from_attributes=True)


class ConsistencyCheckRequest(BaseModel):
    """一致性检查请求"""
    chapter_ids: list[int] | None = Field(default=None, description="指定检查的章节ID列表")
    check_types: list[str] | None = Field(
        default=None,
        description="检查类型列表 [character, plot, timeline, foreshadowing]"
    )
