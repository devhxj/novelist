"""
一致性检查API路由
"""
import logging
from fastapi import APIRouter, Depends, Query
from sqlalchemy import select
from typing import Optional

from core.database import DBSession
from core.response import ApiResponse
from core.dependencies import NovelOwner
from consistency.service import ConsistencyChecker
from consistency.schemas import ConsistencyCheckRequest

router = APIRouter(prefix="/consistency", tags=["consistency"])
logger = logging.getLogger(__name__)


@router.post("/novels/{novel_id}/check")
async def check_consistency(
    novel: NovelOwner,
    request: ConsistencyCheckRequest,
    db: DBSession
):
    """
    执行一致性检查
    
    - chapter_ids: 指定检查的章节ID列表（可选）
    - check_types: 检查类型列表 [character, plot, timeline, foreshadowing]
    
    注意：foreshadowing 类型的一致性检查现在通过时间线系统执行，
    伏笔管理已迁移到 /api/v1/timeline/ 端点。
    """
    checker = ConsistencyChecker(db, novel.id)
    result = await checker.check_all(
        chapter_ids=request.chapter_ids,
        check_types=request.check_types
    )
    
    return ApiResponse.success(result)


@router.get("/novels/{novel_id}/foreshadowings")
async def list_foreshadowings_redirect(novel: NovelOwner):
    """
    [已迁移] 伏笔列表查询已迁移到时间线系统。
    请使用 GET /api/v1/timeline/novels/{novel_id}?category=foreshadowing 替代。
    """
    return ApiResponse.success({
        "message": "此端点已迁移到时间线系统",
        "new_endpoint": f"/api/v1/timeline/novels/{novel.id}?category=foreshadowing",
        "deprecated": True,
    })


@router.get("/novels/{novel_id}/foreshadowings/statistics")
async def get_foreshadowing_statistics_redirect(novel: NovelOwner):
    """
    [已迁移] 伏笔统计已迁移到时间线系统。
    请使用 GET /api/v1/timeline/novels/{novel_id}/stats 替代。
    """
    from timeline.service import TimelineService
    from core.database import get_db_session
    async for session in get_db_session():
        service = TimelineService(session, novel.id)
        counts = await service.get_unresolved_count()
        break
    return ApiResponse.success({
        **counts,
        "message": "统计已迁移到时间线系统",
        "new_endpoint": f"/api/v1/timeline/novels/{novel.id}/stats",
        "deprecated": True,
    })
