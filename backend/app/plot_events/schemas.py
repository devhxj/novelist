"""
情节事件管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel
from typing import Optional, List, Dict, Any
from datetime import datetime


class PlotEventBase(BaseModel):
    event_type: Optional[str] = None
    description: Optional[str] = None
    characters_involved: Optional[List[int]] = None
    timeline: Optional[datetime] = None
    consequences: Optional[Dict[str, Any]] = None


class PlotEventCreate(PlotEventBase):
    novel_id: int
    chapter_id: Optional[int] = None


class PlotEventUpdate(BaseModel):
    chapter_id: Optional[int] = None
    event_type: Optional[str] = None
    description: Optional[str] = None
    characters_involved: Optional[List[int]] = None
    timeline: Optional[datetime] = None
    consequences: Optional[Dict[str, Any]] = None


class PlotEventResponse(PlotEventBase):
    id: int
    novel_id: int
    chapter_id: Optional[int] = None
    created_at: datetime
    
    class Config:
        from_attributes = True
