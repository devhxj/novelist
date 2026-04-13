"""
情节规划模块 - Pydantic验证模型
"""
from pydantic import BaseModel, Field, ConfigDict
from typing import Any
from datetime import datetime
from enum import Enum


class PlotLineType(str, Enum):
    MAIN = "main"
    SUB = "sub"
    CHARACTER = "character"
    BACKGROUND = "background"


class PlotNodeStatus(str, Enum):
    PLANNED = "planned"
    IN_PROGRESS = "in_progress"
    COMPLETED = "completed"
    SKIPPED = "skipped"


class PlotLineCreate(BaseModel):
    """创建情节线请求"""
    name: str = Field(..., min_length=1, max_length=255, description="情节线名称")
    description: str | None = Field(default=None, description="情节线描述")
    line_type: PlotLineType = Field(default=PlotLineType.SUB, description="情节线类型")
    start_chapter: int | None = Field(default=None, ge=1, description="起始章节")
    end_chapter: int | None = Field(default=None, ge=1, description="结束章节")
    importance: int = Field(default=1, ge=1, le=5, description="重要程度")
    metadata: dict[str, Any] | None = Field(default=None, description="额外元数据")


class PlotLineUpdate(BaseModel):
    """更新情节线请求"""
    name: str | None = Field(default=None, min_length=1, max_length=255)
    description: str | None = None
    line_type: PlotLineType | None = None
    start_chapter: int | None = Field(default=None, ge=1)
    end_chapter: int | None = Field(default=None, ge=1)
    importance: int | None = Field(default=None, ge=1, le=5)
    status: str | None = None
    metadata: dict[str, Any] | None = None


class PlotLineResponse(BaseModel):
    """情节线响应"""
    id: int
    novel_id: int
    name: str
    description: str | None
    line_type: str
    start_chapter: int | None
    end_chapter: int | None
    importance: int
    status: str
    metadata: dict[str, Any] | None
    created_at: datetime
    updated_at: datetime | None

    model_config = ConfigDict(from_attributes=True)


class PlotNodeCreate(BaseModel):
    """创建情节节点请求"""
    plot_line_id: int = Field(..., description="情节线ID")
    title: str = Field(..., min_length=1, max_length=255, description="节点标题")
    description: str | None = Field(default=None, description="节点描述")
    chapter_number: int | None = Field(default=None, ge=1, description="章节号")
    sequence: int = Field(default=0, ge=0, description="顺序")
    characters_involved: list[int] | None = Field(default=None, description="涉及角色ID列表")
    prerequisites: list[int] | None = Field(default=None, description="前置节点ID列表")
    consequences: dict[str, Any] | None = Field(default=None, description="后果")
    notes: str | None = Field(default=None, description="备注")
    metadata: dict[str, Any] | None = Field(default=None, description="额外元数据")


class PlotNodeUpdate(BaseModel):
    """更新情节节点请求"""
    title: str | None = Field(default=None, min_length=1, max_length=255)
    description: str | None = None
    chapter_number: int | None = Field(default=None, ge=1)
    sequence: int | None = Field(default=None, ge=0)
    status: PlotNodeStatus | None = None
    characters_involved: list[int] | None = None
    prerequisites: list[int] | None = None
    consequences: dict[str, Any] | None = None
    notes: str | None = None
    metadata: dict[str, Any] | None = None


class PlotNodeResponse(BaseModel):
    """情节节点响应"""
    id: int
    plot_line_id: int
    novel_id: int
    title: str
    description: str | None
    chapter_number: int | None
    sequence: int
    status: str
    characters_involved: list[int] | None
    prerequisites: list[int] | None
    consequences: dict[str, Any] | None
    notes: str | None
    metadata: dict[str, Any] | None
    created_at: datetime
    updated_at: datetime | None

    model_config = ConfigDict(from_attributes=True)


class PlotOutlineCreate(BaseModel):
    """创建情节大纲请求"""
    title: str = Field(..., min_length=1, max_length=255, description="大纲标题")
    premise: str | None = Field(default=None, description="故事前提")
    theme: str | None = Field(default=None, max_length=255, description="主题")
    act_structure: dict[str, Any] | None = Field(default=None, description="幕结构")
    beginning: str | None = Field(default=None, description="开端")
    middle: str | None = Field(default=None, description="发展")
    climax: str | None = Field(default=None, description="高潮")
    ending: str | None = Field(default=None, description="结局")
    total_chapters: int | None = Field(default=None, ge=1, description="总章节数")
    notes: str | None = Field(default=None, description="备注")
    metadata: dict[str, Any] | None = Field(default=None, description="额外元数据")


class PlotOutlineUpdate(BaseModel):
    """更新情节大纲请求"""
    title: str | None = Field(default=None, min_length=1, max_length=255)
    premise: str | None = None
    theme: str | None = Field(default=None, max_length=255)
    act_structure: dict[str, Any] | None = None
    beginning: str | None = None
    middle: str | None = None
    climax: str | None = None
    ending: str | None = None
    total_chapters: int | None = Field(default=None, ge=1)
    current_chapter: int | None = Field(default=None, ge=1)
    notes: str | None = None
    metadata: dict[str, Any] | None = None


class PlotOutlineResponse(BaseModel):
    """情节大纲响应"""
    id: int
    novel_id: int
    title: str
    premise: str | None
    theme: str | None
    act_structure: dict[str, Any] | None
    beginning: str | None
    middle: str | None
    climax: str | None
    ending: str | None
    total_chapters: int | None
    current_chapter: int
    notes: str | None
    metadata: dict[str, Any] | None
    created_at: datetime
    updated_at: datetime | None

    model_config = ConfigDict(from_attributes=True)


class PlotSuggestionRequest(BaseModel):
    """情节建议请求"""
    context: str = Field(..., description="当前上下文")
    chapter_number: int = Field(..., ge=1, description="目标章节号")
    plot_line_id: int | None = Field(default=None, description="情节线ID")


class PlotSuggestionResponse(BaseModel):
    """情节建议响应"""
    suggestions: list[dict[str, Any]] = Field(default_factory=list, description="情节建议列表")
    reasoning: str | None = Field(default=None, description="推理过程")
