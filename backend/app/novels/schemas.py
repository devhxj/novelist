"""
小说管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel, Field, ConfigDict
from typing import Any
from datetime import datetime


class NovelBase(BaseModel):
    title: str
    genre: str | None = None
    description: str | None = None
    author_id: int | None = None


class NovelCreate(NovelBase):
    pass


class NovelUpdate(BaseModel):
    title: str | None = None
    genre: str | None = None
    description: str | None = None
    status: str | None = None


class NovelResponse(NovelBase):
    id: int
    status: str
    created_at: datetime
    updated_at: datetime | None = None
    
    model_config = ConfigDict(from_attributes=True)

class CreativeProfileBase(BaseModel):
    author_intent: str | None = Field(default=None, description="作者长期创作意图")
    preferred_tone: str | None = Field(default=None, description="默认语气/文风偏好")
    collaboration_style: str | None = Field(default=None, description="协作风格，例如 ai_ide")
    scene_planning_notes: str | None = Field(default=None, description="场景推进与章节规划备注")
    must_keep: list[str] | None = Field(default=None, description="长期必须保留或遵守的规则")
    must_avoid: list[str] | None = Field(default=None, description="长期明确避免的内容")
    long_term_goals: list[str] | None = Field(default=None, description="长线创作目标")
    extra_metadata: dict[str, Any] | None = Field(default=None, description="额外配置")


class CreativeProfileUpdate(CreativeProfileBase):
    pass


class CreativeProfileResponse(CreativeProfileBase):
    id: int
    novel_id: int
    created_at: datetime
    updated_at: datetime | None = None

    model_config = ConfigDict(from_attributes=True)
