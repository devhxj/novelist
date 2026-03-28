"""
角色管理模块 - Pydantic Schemas
"""
from pydantic import BaseModel
from typing import Optional, List, Dict, Any
from datetime import datetime


class CharacterBase(BaseModel):
    name: str
    personality: Optional[Dict[str, Any]] = None
    relationships: Optional[Dict[str, str]] = None
    abilities: Optional[List[str]] = None


class CharacterCreate(CharacterBase):
    novel_id: int


class CharacterUpdate(BaseModel):
    name: Optional[str] = None
    personality: Optional[Dict[str, Any]] = None
    relationships: Optional[Dict[str, str]] = None
    abilities: Optional[List[str]] = None


class CharacterResponse(CharacterBase):
    id: int
    novel_id: int
    created_at: datetime
    
    class Config:
        from_attributes = True
