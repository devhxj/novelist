"""
地点管理模块 - Pydantic验证模型
"""
from typing import Any
from pydantic import BaseModel, Field, ConfigDict
from enum import Enum
from datetime import datetime

class LocationType(str, Enum):
    CITY = "city"
    TOWN = "town"
    FOREST = "forest"
    MOUNTAIN = "mountain"
    BUILDING = "building"
    ROOM = "room"
    SEA = "sea"
    RIVER = "river"
    ROAD = "road"
    CASTLE = "castle"
    TEMPLE = "temple"
    VILLAGE = "village"
    DUNGEON = "dungeon"
    PALACE = "palace"
    MARKET = "market"
    INN = "inn"
    OTHER = "other"


class LocationBase(BaseModel):
    name: str = Field(..., min_length=1, max_length=200)
    location_type: LocationType = Field(default=LocationType.OTHER)
    description: str | None = None
    geo_info: dict[str, Any] | None = None
    related_characters: list[int] | None = None
    tags: list[str] | None = None
    parent_location_id: int | None = None
    first_appearance_chapter_id: int | None = None
    extra_metadata: dict[str, Any] | None = None


class LocationCreate(LocationBase):
    pass


class LocationUpdate(BaseModel):
    name: str | None = Field(default=None, min_length=1, max_length=200)
    location_type: LocationType | None = None
    description: str | None = None
    geo_info: dict[str, Any] | None = None
    related_characters: list[int] | None = None
    tags: list[str] | None = None
    parent_location_id: int | None = None
    extra_metadata: dict[str, Any] | None = None


class LocationResponse(LocationBase):
    id: int
    novel_id: int
    related_chapters: list[int] | None = None
    parent_name: str | None = None
    children_count: int | None = 0
    created_at: datetime
    updated_at: datetime

    model_config = ConfigDict(from_attributes=True)


class LocationNetworkResponse(BaseModel):
    """地点网络响应（层级结构）"""
    nodes: list[dict[str, Any]]
    edges: list[dict[str, Any]]
    total_nodes: int
    root_locations: list[dict[str, Any]]
