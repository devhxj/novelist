"""
情节事件管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel, ConfigDict
from typing import Any
from datetime import datetime


class PlotEventBase(BaseModel):
    event_type: str | None = None
    description: str | None = None
    characters_involved: list[int] | None = None
    timeline: datetime | None = None
    consequences: dict[str, Any] | None = None


class PlotEventCreate(PlotEventBase):
    novel_id: int
    chapter_id: int | None = None


class PlotEventUpdate(BaseModel):
    chapter_id: int | None = None
    event_type: str | None = None
    description: str | None = None
    characters_involved: list[int] | None = None
    timeline: datetime | None = None
    consequences: dict[str, Any] | None = None


class PlotEventResponse(PlotEventBase):
    id: int
    novel_id: int
    chapter_id: int | None = None
    created_at: datetime
    
    model_config = ConfigDict(from_attributes=True)
