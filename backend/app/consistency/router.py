"""
一致性检查API路由
"""
import logging
from fastapi import APIRouter, Depends, Query
from sqlalchemy import select, func
from sqlalchemy.orm import selectinload
from typing import Optional
from datetime import datetime

from app.core.database import DBSession
from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException
from app.core.dependencies import NovelOwner
from app.consistency.service import ConsistencyChecker
from app.foreshadowing.models import Foreshadowing, ForeshadowingStatus
from app.foreshadowing.schemas import (
    ForeshadowingCreate,
    ForeshadowingUpdate,
    ForeshadowingResolve,
    ForeshadowingResponse,
    ConsistencyCheckRequest
)

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
    """
    checker = ConsistencyChecker(db, novel.id)
    result = await checker.check_all(
        chapter_ids=request.chapter_ids,
        check_types=request.check_types
    )
    
    return ApiResponse.success(result)


@router.get("/novels/{novel_id}/foreshadowings")
async def list_foreshadowings(
    novel: NovelOwner,
    db: DBSession,
    status: Optional[str] = None,
    foreshadowing_type: Optional[str] = None,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100)
):
    """
    获取伏笔列表
    
    - status: 状态筛选 (unresolved/resolved/abandoned)
    - foreshadowing_type: 类型筛选 (plot/character/item/mystery/other)
    """
    query = select(Foreshadowing).where(
        Foreshadowing.novel_id == novel.id
    )
    
    if status:
        query = query.where(Foreshadowing.status == status)
    
    if foreshadowing_type:
        query = query.where(Foreshadowing.foreshadowing_type == foreshadowing_type)
    
    count_query = select(func.count()).select_from(query.subquery())
    total_result = await db.execute(count_query)
    total = total_result.scalar()
    
    query = query.order_by(
        Foreshadowing.importance.desc(),
        Foreshadowing.created_at.desc()
    ).offset((page - 1) * page_size).limit(page_size)
    
    result = await db.execute(query)
    items = result.scalars().all()
    
    return ApiResponse.paginated(
        [ForeshadowingResponse.model_validate(item) for item in items],
        total,
        page,
        page_size
    )


@router.post("/novels/{novel_id}/foreshadowings", status_code=201)
async def create_foreshadowing(
    novel: NovelOwner,
    data: ForeshadowingCreate,
    db: DBSession
):
    """
    创建伏笔（挖坑）
    
    - title: 伏笔标题
    - description: 伏笔描述
    - created_chapter_id: 挖坑章节ID
    - foreshadowing_type: 伏笔类型
    - importance: 重要程度 1-5
    """
    foreshadowing = Foreshadowing(
        novel_id=novel.id,
        title=data.title,
        description=data.description,
        created_chapter_id=data.created_chapter_id,
        foreshadowing_type=data.foreshadowing_type.value,
        importance=data.importance,
        extra_metadata=data.metadata
    )
    
    db.add(foreshadowing)
    await db.commit()
    await db.refresh(foreshadowing)
    
    return ApiResponse.success({
        "id": foreshadowing.id,
        "title": foreshadowing.title,
        "status": foreshadowing.status,
        "message": "伏笔创建成功"
    })


@router.get("/novels/{novel_id}/foreshadowings/{foreshadowing_id}")
async def get_foreshadowing(
    novel: NovelOwner,
    foreshadowing_id: int,
    db: DBSession
):
    """
    获取伏笔详情
    """
    result = await db.execute(
        select(Foreshadowing).where(
            Foreshadowing.id == foreshadowing_id,
            Foreshadowing.novel_id == novel.id
        )
    )
    foreshadowing = result.scalar_one_or_none()
    
    if not foreshadowing:
        raise NotFoundException("伏笔")
    
    return ApiResponse.success(ForeshadowingResponse.model_validate(foreshadowing))


@router.put("/novels/{novel_id}/foreshadowings/{foreshadowing_id}")
async def update_foreshadowing(
    novel: NovelOwner,
    foreshadowing_id: int,
    data: ForeshadowingUpdate,
    db: DBSession
):
    """
    更新伏笔信息
    """
    result = await db.execute(
        select(Foreshadowing).where(
            Foreshadowing.id == foreshadowing_id,
            Foreshadowing.novel_id == novel.id
        )
    )
    foreshadowing = result.scalar_one_or_none()
    
    if not foreshadowing:
        raise NotFoundException("伏笔")
    
    if data.title is not None:
        foreshadowing.title = data.title
    if data.description is not None:
        foreshadowing.description = data.description
    if data.foreshadowing_type is not None:
        foreshadowing.foreshadowing_type = data.foreshadowing_type.value
    if data.importance is not None:
        foreshadowing.importance = data.importance
    if data.metadata is not None:
        foreshadowing.extra_metadata = data.metadata
    
    await db.commit()
    await db.refresh(foreshadowing)
    
    return ApiResponse.success({
        "id": foreshadowing.id,
        "title": foreshadowing.title,
        "message": "伏笔更新成功"
    })


@router.post("/novels/{novel_id}/foreshadowings/{foreshadowing_id}/resolve")
async def resolve_foreshadowing(
    novel: NovelOwner,
    foreshadowing_id: int,
    data: ForeshadowingResolve,
    db: DBSession
):
    """
    解决伏笔（填坑）
    
    - resolved_chapter_id: 填坑章节ID
    - resolution_notes: 解决说明
    """
    result = await db.execute(
        select(Foreshadowing).where(
            Foreshadowing.id == foreshadowing_id,
            Foreshadowing.novel_id == novel.id
        )
    )
    foreshadowing = result.scalar_one_or_none()
    
    if not foreshadowing:
        raise NotFoundException("伏笔")
    
    foreshadowing.status = ForeshadowingStatus.RESOLVED.value
    foreshadowing.resolved_chapter_id = data.resolved_chapter_id
    foreshadowing.resolution_notes = data.resolution_notes
    foreshadowing.resolved_at = datetime.now()
    
    await db.commit()
    await db.refresh(foreshadowing)
    
    return ApiResponse.success({
        "id": foreshadowing.id,
        "title": foreshadowing.title,
        "status": foreshadowing.status,
        "message": "伏笔已解决"
    })


@router.post("/novels/{novel_id}/foreshadowings/{foreshadowing_id}/abandon")
async def abandon_foreshadowing(
    novel: NovelOwner,
    foreshadowing_id: int,
    db: DBSession,
    reason: Optional[str] = None
):
    """
    放弃伏笔
    
    - reason: 放弃原因
    """
    result = await db.execute(
        select(Foreshadowing).where(
            Foreshadowing.id == foreshadowing_id,
            Foreshadowing.novel_id == novel.id
        )
    )
    foreshadowing = result.scalar_one_or_none()
    
    if not foreshadowing:
        raise NotFoundException("伏笔")
    
    foreshadowing.status = ForeshadowingStatus.ABANDONED.value
    if reason:
        foreshadowing.resolution_notes = f"放弃原因: {reason}"
    
    await db.commit()
    
    return ApiResponse.success({
        "id": foreshadowing.id,
        "status": foreshadowing.status,
        "message": "伏笔已放弃"
    })


@router.get("/novels/{novel_id}/foreshadowings/unresolved")
async def list_unresolved_foreshadowings(
    novel: NovelOwner,
    db: DBSession
):
    """
    获取未解决的伏笔列表
    """
    result = await db.execute(
        select(Foreshadowing)
        .where(
            Foreshadowing.novel_id == novel.id,
            Foreshadowing.status == ForeshadowingStatus.UNRESOLVED.value
        )
        .order_by(Foreshadowing.importance.desc())
    )
    foreshadowings = result.scalars().all()
    
    return ApiResponse.success({
        "items": [ForeshadowingResponse.model_validate(fs) for fs in foreshadowings],
        "total": len(foreshadowings)
    })


@router.get("/novels/{novel_id}/foreshadowings/statistics")
async def get_foreshadowing_statistics(
    novel: NovelOwner,
    db: DBSession
):
    """
    获取伏笔统计信息
    """
    total_result = await db.execute(
        select(func.count()).where(Foreshadowing.novel_id == novel.id)
    )
    total = total_result.scalar()
    
    unresolved_result = await db.execute(
        select(func.count()).where(
            Foreshadowing.novel_id == novel.id,
            Foreshadowing.status == ForeshadowingStatus.UNRESOLVED.value
        )
    )
    unresolved = unresolved_result.scalar()
    
    resolved_result = await db.execute(
        select(func.count()).where(
            Foreshadowing.novel_id == novel.id,
            Foreshadowing.status == ForeshadowingStatus.RESOLVED.value
        )
    )
    resolved = resolved_result.scalar()
    
    abandoned_result = await db.execute(
        select(func.count()).where(
            Foreshadowing.novel_id == novel.id,
            Foreshadowing.status == ForeshadowingStatus.ABANDONED.value
        )
    )
    abandoned = abandoned_result.scalar()
    
    high_importance_result = await db.execute(
        select(func.count()).where(
            Foreshadowing.novel_id == novel.id,
            Foreshadowing.status == ForeshadowingStatus.UNRESOLVED.value,
            Foreshadowing.importance >= 4
        )
    )
    high_importance_unresolved = high_importance_result.scalar()
    
    return ApiResponse.success({
        "total": total,
        "unresolved": unresolved,
        "resolved": resolved,
        "abandoned": abandoned,
        "high_importance_unresolved": high_importance_unresolved,
        "resolution_rate": round(resolved / total * 100, 1) if total > 0 else 0
    })
