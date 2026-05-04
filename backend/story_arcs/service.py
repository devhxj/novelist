"""
叙事弧线服务层
"""
import logging
from typing import Any, Optional

from sqlalchemy import select, func
from sqlalchemy.ext.asyncio import AsyncSession

from story_arcs.models import StoryArc, StoryArcType, StoryArcStatus
from story_arcs.schemas import StoryArcCreate, StoryArcUpdate

logger = logging.getLogger(__name__)


class StoryArcService:
    def __init__(self, db: AsyncSession, novel_id: int):
        self.db = db
        self.novel_id = novel_id

    async def create_arc(self, data: StoryArcCreate) -> StoryArc:
        arc = StoryArc(
            novel_id=self.novel_id,
            name=data.name,
            description=data.description,
            arc_type=data.arc_type.value,
            start_chapter=data.start_chapter,
            end_chapter=data.end_chapter,
            importance=data.importance,
            status=StoryArcStatus.ACTIVE.value,
            extra_metadata=data.extra_metadata,
        )
        self.db.add(arc)
        await self.db.commit()
        await self.db.refresh(arc)
        logger.info(f"StoryArc created: id={arc.id}, name={arc.name}")
        return arc

    async def get_arc(self, arc_id: int) -> Optional[StoryArc]:
        result = await self.db.execute(
            select(StoryArc).where(
                StoryArc.id == arc_id,
                StoryArc.novel_id == self.novel_id,
            )
        )
        return result.scalar_one_or_none()

    async def list_arcs(
        self,
        arc_type: Optional[str] = None,
        status: Optional[str] = None,
    ) -> list[StoryArc]:
        query = select(StoryArc).where(
            StoryArc.novel_id == self.novel_id
        )
        if arc_type:
            query = query.where(StoryArc.arc_type == arc_type)
        if status:
            query = query.where(StoryArc.status == status)
        query = query.order_by(StoryArc.importance.desc(), StoryArc.created_at)
        result = await self.db.execute(query)
        return list(result.scalars().all())

    async def update_arc(self, arc_id: int, data: StoryArcUpdate) -> Optional[StoryArc]:
        arc = await self.get_arc(arc_id)
        if not arc:
            return None
        update_data = data.model_dump(exclude_unset=True)
        for field, value in update_data.items():
            if value is not None and hasattr(arc, field):
                if isinstance(value, StoryArcType):
                    setattr(arc, field, value.value)
                elif isinstance(value, StoryArcStatus):
                    setattr(arc, field, value.value)
                else:
                    setattr(arc, field, value)
        await self.db.commit()
        await self.db.refresh(arc)
        logger.info(f"StoryArc updated: id={arc_id}")
        return arc

    async def delete_arc(self, arc_id: int) -> bool:
        arc = await self.get_arc(arc_id)
        if not arc:
            return False
        await self.db.delete(arc)
        await self.db.commit()
        logger.info(f"StoryArc deleted: id={arc_id}")
        return True

    async def get_active_arcs(self, chapter_number: int) -> list[dict[str, Any]]:
        result = await self.db.execute(
            select(StoryArc)
            .where(StoryArc.novel_id == self.novel_id, StoryArc.status == StoryArcStatus.ACTIVE.value)
            .order_by(StoryArc.importance.desc(), StoryArc.updated_at.desc())
            .limit(6)
        )
        arcs = list(result.scalars().all())
        return [
            {
                "id": arc.id,
                "name": arc.name,
                "description": arc.description,
                "arc_type": arc.arc_type,
                "importance": arc.importance,
                "start_chapter": arc.start_chapter,
                "end_chapter": arc.end_chapter,
                "is_current_window": (
                    (arc.start_chapter is None or arc.start_chapter <= chapter_number)
                    and (arc.end_chapter is None or arc.end_chapter >= chapter_number)
                ),
            }
            for arc in arcs
        ]
