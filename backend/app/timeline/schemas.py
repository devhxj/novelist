"""
故事时间线模块 - Pydantic验证模型
"""
from pydantic import BaseModel, Field, ConfigDict
from typing import Any
from datetime import datetime
from enum import Enum


class TimelineEntryCategory(str, Enum):
    """时间线条目分类。注意：foreshadowing(伏笔) 与 PlotLine/PlotNode(情节线) 是完全独立的两个系统：
    - foreshadowing: Layer4 伏笔管理，追踪"埋下的钩子何时被回收"
    - plot_node: Layer4 情节里程碑，标记关键转折点（与Layer2的PlotLine系统无关）
    - PlotLine/PlotNode: Layer2 独立的情节规划结构（main/sub线+节点），有自己独立的表和API"""
    FORESHADOWING = "foreshadowing"
    PLOT_NODE = "plot_node"
    CHAPTER_PLAN = "chapter_plan"
    USER_DIRECTIVE = "user_directive"


class TimelineEntryStatus(str, Enum):
    PENDING = "pending"
    ACTIVE = "active"
    COMPLETED = "completed"
    RESOLVED = "resolved"
    ABANDONED = "abandoned"
    DEFERRED = "deferred"


class TimeHorizon(str, Enum):
    NEXT = "next"
    NEAR_TERM = "near_term"
    LONG_TERM = "long_term"
    UNDEFINED = "undefined"


class TimelineEntryCreate(BaseModel):
    """创建时间线条目请求"""
    category: TimelineEntryCategory = Field(..., description="条目分类")
    title: str = Field(..., min_length=1, max_length=255, description="标题")
    description: str | None = Field(default=None, description="描述")
    detail_json: dict[str, Any] | None = Field(default=None, description="结构化详情（因 category 而异）")
    target_chapter: int | None = Field(default=None, description="目标章节号")
    time_horizon: TimeHorizon | None = Field(default=None, description="时间范围：next/near_term/long_term")
    importance: int = Field(default=3, ge=1, le=5, description="重要程度 1-5")
    source: str = Field(default="ai", description="来源：ai_generated/user_created/user_edited")
    source_chapter_id: int | None = Field(default=None, description="来源章节 ID")
    related_entry_ids: list[int] | None = Field(default=None, description="关联条目 ID 列表")
    tags: list[str] | None = Field(default=None, description="标签列表")


class TimelineEntryUpdate(BaseModel):
    """更新时间线条目请求"""
    title: str | None = Field(default=None, min_length=1, max_length=255)
    description: str | None = Field(default=None)
    detail_json: dict[str, Any] | None = Field(default=None)
    target_chapter: int | None = Field(default=None)
    time_horizon: TimeHorizon | None = Field(default=None)
    status: TimelineEntryStatus | None = Field(default=None)
    importance: int | None = Field(default=None, ge=1, le=5)
    related_entry_ids: list[int] | None = Field(default=None)
    tags: list[str] | None = Field(default=None)


class TimelineEntryResolve(BaseModel):
    """解决/完成时间线条目请求"""
    resolved_chapter_id: int | None = Field(default=None, description="解决时关联的章节 ID（可选）")
    resolution_notes: str | None = Field(default=None, description="解决说明")


class TimelineEntryResponse(BaseModel):
    """时间线条目响应"""
    id: int
    novel_id: int
    category: str
    status: str
    title: str
    description: str | None
    detail_json: dict[str, Any] | None
    target_chapter: int | None
    time_horizon: str | None
    importance: int
    source: str
    source_chapter_id: int | None
    resolved_chapter_id: int | None
    related_entry_ids: list[int] | None
    tags: list[str] | None
    version: int
    last_editor: str | None
    original_ai_output: dict[str, Any] | None
    extra_metadata: dict[str, Any] | None
    created_at: datetime
    updated_at: datetime
    resolved_at: datetime | None

    model_config = ConfigDict(from_attributes=True)


class TimelineListResponse(BaseModel):
    """时间线列表响应"""
    items: list[TimelineEntryResponse]
    total: int
    page: int
    page_size: int


class TimelineContextRequest(BaseModel):
    """获取 AI 上下文用精简时间线的请求"""
    current_chapter: int = Field(..., description="当前章节号")
    max_entries: int = Field(default=15, ge=1, le=50, description="最大返回条数")
    include_categories: list[TimelineEntryCategory] | None = Field(
        default=None, description="包含的分类，为空则全部包含"
    )
    include_statuses: list[TimelineEntryStatus] | None = Field(
        default=None, description="包含的状态，默认为 pending+active+unresolved"
    )


class TimelineContextResponse(BaseModel):
    """AI 上下文用精简时间线响应"""
    entries: list[TimelineEntryResponse]
    total_available: int
    truncated: bool
    summary: str | None = Field(default=None, description="时间线摘要文本")
