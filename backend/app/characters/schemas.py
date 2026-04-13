"""
角色管理模块 - Pydantic Schemas
"""
from enum import Enum
from pydantic import BaseModel, Field, ConfigDict
from typing import Any
from datetime import datetime


class CharacterBase(BaseModel):
    name: str
    personality: dict[str, Any] | None = None
    relationships: dict[str, str] | None = None
    abilities: list[str] | None = None


class CharacterCreate(CharacterBase):
    novel_id: int


class CharacterUpdate(BaseModel):
    name: str | None = None
    personality: dict[str, Any] | None = None
    relationships: dict[str, str] | None = None
    abilities: list[str] | None = None


class CharacterResponse(CharacterBase):
    id: int
    novel_id: int
    created_at: datetime
    
    model_config = ConfigDict(from_attributes=True)


class RelationStatus(str, Enum):
    ACTIVE = "active"
    DORMANT = "dormant"
    RESOLVED = "resolved"
    SEVERED = "severed"


class RelationType(str, Enum):
    ALLY = "ally"
    ENEMY = "enemy"
    LOVER = "lover"
    FAMILY = "family"
    MENTOR = "mentor"
    STUDENT = "student"
    RIVAL = "rival"
    ACQUAINTANCE = "acquaintance"
    STRANGER = "stranger"
    COLLEAGUE = "colleague"
    SUBORDINATE = "subordinate"
    SUPERIOR = "superior"
    PARENT = "parent"
    CHILD = "child"
    SIBLING = "sibling"
    SPOUSE = "spouse"
    EX_LOVER = "ex_lover"
    OTHER = "other"


class CharacterRelationBase(BaseModel):
    relationship_type: RelationType
    description: str | None = None
    intensity: int = Field(default=3, ge=1, le=5)
    status: RelationStatus = RelationStatus.ACTIVE


class CharacterRelationCreate(CharacterRelationBase):
    source_character_id: int
    target_character_id: int
    established_chapter_id: int | None = None
    extra_metadata: dict[str, Any] | None = None


class CharacterRelationUpdate(BaseModel):
    relationship_type: RelationType | None = None
    description: str | None = None
    intensity: int | None = Field(default=None, ge=1, le=5)
    status: RelationStatus | None = None
    established_chapter_id: int | None = None
    extra_metadata: dict[str, Any] | None = None


class CharacterRelationEvolve(BaseModel):
    """关系演变请求 - 创建新的关系记录并链接到旧记录"""
    relationship_type: RelationType
    description: str | None = None
    intensity: int = Field(default=3, ge=1, le=5)
    status: RelationStatus = RelationStatus.ACTIVE
    evolution_notes: str | None = Field(default=None, description="演变原因说明")
    established_chapter_id: int | None = None
    extra_metadata: dict[str, Any] | None = None


class CharacterRelationResponse(CharacterRelationBase):
    id: int
    novel_id: int
    source_character_id: int
    target_character_id: int
    established_chapter_id: int | None = None
    evolved_from_id: int | None = None
    extra_metadata: dict[str, Any] | None = None
    created_at: datetime
    updated_at: datetime

    source_name: str | None = None
    target_name: str | None = None
    evolved_from_type: str | None = None

    model_config = ConfigDict(from_attributes=True)


class CharacterNetworkResponse(BaseModel):
    """人物关系图响应"""
    nodes: list[dict[str, Any]]
    edges: list[dict[str, Any]]
    total_nodes: int
    total_edges: int
