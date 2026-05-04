"""
叙事弧线API路由
"""
import logging
from fastapi import APIRouter, Depends, HTTPException

from core.database import get_db, DBSession
from core.response import ApiResponse
from core.dependencies import NovelOwner
from story_arcs.models import StoryArc, StoryArcType, StoryArcStatus
from story_arcs.schemas import (
    StoryArcCreate,
    StoryArcUpdate,
    StoryArcResponse,
)
from story_arcs.service import StoryArcService

router = APIRouter(prefix="/story-arcs", tags=["story-arcs"])
logger = logging.getLogger(__name__)


async def check_arc_ownership(
    arc_id: int,
    novel: NovelOwner,
    db: DBSession,
) -> StoryArc:
    service = StoryArcService(db, novel.id)
    arc = await service.get_arc(arc_id)
    if not arc:
        raise HTTPException(status_code=404, detail="叙事弧线不存在")
    return arc


@router.get("/novels/{novel_id}/arcs")
async def list_story_arcs(
    novel: NovelOwner,
    db: DBSession,
    arc_type: str | None = None,
    status: str | None = None,
):
    """获取小说的叙事弧线列表"""
    service = StoryArcService(db, novel.id)
    arcs = await service.list_arcs(arc_type=arc_type, status=status)
    return ApiResponse.success([StoryArcResponse.model_validate(a) for a in arcs])


@router.post("/novels/{novel_id}/arcs")
async def create_story_arc(
    novel: NovelOwner,
    data: StoryArcCreate,
    db: DBSession,
):
    """创建叙事弧线"""
    service = StoryArcService(db, novel.id)
    arc = await service.create_arc(data)
    return ApiResponse.success(StoryArcResponse.model_validate(arc))


@router.get("/arcs/{arc_id}")
async def get_story_arc(
    arc_id: int,
    novel: NovelOwner,
    db: DBSession,
):
    """获取叙事弧线详情"""
    service = StoryArcService(db, novel.id)
    arc = await service.get_arc(arc_id)
    if not arc:
        return ApiResponse.error(code="ARC_NOT_FOUND", message="叙事弧线不存在", status_code=404)
    return ApiResponse.success(StoryArcResponse.model_validate(arc))


@router.put("/arcs/{arc_id}")
async def update_story_arc(
    arc_id: int,
    novel: NovelOwner,
    data: StoryArcUpdate,
    db: DBSession,
):
    """更新叙事弧线"""
    service = StoryArcService(db, novel.id)
    arc = await service.update_arc(arc_id, data)
    if not arc:
        return ApiResponse.error(code="ARC_NOT_FOUND", message="叙事弧线不存在", status_code=404)
    return ApiResponse.success(StoryArcResponse.model_validate(arc))


@router.delete("/arcs/{arc_id}")
async def delete_story_arc(
    arc_id: int,
    novel: NovelOwner,
    db: DBSession,
):
    """删除叙事弧线"""
    service = StoryArcService(db, novel.id)
    success = await service.delete_arc(arc_id)
    if not success:
        return ApiResponse.error(code="ARC_NOT_FOUND", message="叙事弧线不存在", status_code=404)
    return ApiResponse.success({"message": "叙事弧线已删除"})
