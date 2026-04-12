"""
角色管理模块 - 数据库模型
"""
from sqlalchemy import String, Text, Integer, JSON, ForeignKey, Index, func
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, List, Dict, Any

from app.core.database import Base


class Character(Base):
    """角色模型 - 存储小说角色信息"""
    __tablename__ = "characters"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    name: Mapped[str] = mapped_column(String(100), nullable=False, index=True)
    personality: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    relationships: Mapped[Optional[Dict[str, List[int]]]] = mapped_column(JSON)
    abilities: Mapped[Optional[List[str]]] = mapped_column(JSON)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())

    novel: Mapped["Novel"] = relationship(back_populates="characters")

    __table_args__ = (
        Index('idx_character_novel_name', 'novel_id', 'name'),
    )


class CharacterRelation(Base):
    """人物关系 - 有向图边结构，支持关系演变追踪"""
    __tablename__ = "character_relations"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)

    source_character_id: Mapped[int] = mapped_column(ForeignKey("characters.id", ondelete="CASCADE"), nullable=False, index=True)
    target_character_id: Mapped[int] = mapped_column(ForeignKey("characters.id", ondelete="CASCADE"), nullable=False, index=True)

    relationship_type: Mapped[str] = mapped_column(String(50), nullable=False, index=True)
    description: Mapped[Optional[str]] = mapped_column(Text)
    intensity: Mapped[int] = mapped_column(Integer, default=3)
    status: Mapped[str] = mapped_column(String(30), default="active")

    established_chapter_id: Mapped[Optional[int]] = mapped_column(ForeignKey("chapters.id", ondelete="SET NULL"))
    evolved_from_id: Mapped[Optional[int]] = mapped_column(ForeignKey("character_relations.id", ondelete="SET NULL"))

    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(server_default=func.now(), onupdate=func.now())

    source: Mapped["Character"] = relationship(foreign_keys=[source_character_id], backref="outgoing_relations")
    target: Mapped["Character"] = relationship(foreign_keys=[target_character_id], backref="incoming_relations")
    evolved_from: Mapped["CharacterRelation"] = relationship(remote_side=[id], backref="evolutions")

    __table_args__ = (
        Index('idx_relation_pair', 'source_character_id', 'target_character_id'),
        Index('idx_relation_novel_type', 'novel_id', 'relationship_type'),
        Index('idx_relation_target', 'target_character_id'),
    )
