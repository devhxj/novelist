"""
地点管理模块 - 数据库模型
"""
from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Index, func
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, Dict, Any, List

from core.database import Base


class Location(Base):
    """地点模型 - 管理小说中的场景地点"""
    __tablename__ = "locations"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)

    name: Mapped[str] = mapped_column(String(200), nullable=False, index=True)
    location_type: Mapped[str] = mapped_column(String(50), nullable=False, index=True)
    description: Mapped[Optional[str]] = mapped_column(Text)

    geo_info: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    related_characters: Mapped[Optional[List[int]]] = mapped_column(JSON)
    related_chapters: Mapped[Optional[List[int]]] = mapped_column(JSON)

    parent_location_id: Mapped[Optional[int]] = mapped_column(ForeignKey("locations.id", ondelete="SET NULL"))

    tags: Mapped[Optional[List[str]]] = mapped_column(JSON)

    first_appearance_chapter_id: Mapped[Optional[int]] = mapped_column(ForeignKey("chapters.id", ondelete="SET NULL"))
    extra_metadata: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)

    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(server_default=func.now(), onupdate=func.now())

    novel: Mapped["Novel"] = relationship(back_populates="locations")
    parent: Mapped["Location"] = relationship(remote_side=[id], backref="children")
    first_appearance_chapter: Mapped["Chapter"] = relationship()

    __table_args__ = (
        Index('idx_location_novel', 'novel_id'),
        Index('idx_location_novel_type', 'novel_id', 'location_type'),
        Index('idx_location_parent', 'parent_location_id'),
    )
