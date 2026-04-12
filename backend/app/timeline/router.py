"""
故事时间线API路由
统一管理伏笔/情节规划/章节安排/用户指令的RESTful接口
"""
import logging
from fastapi import APIRouter, Depends, Query
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from app.core.database import get_db, DBSession
from app.core.response import ApiResponse
from app.core.auth import CurrentUserDep
from app.core.dependencies import NovelOwner
from app.timeline.models import TimelineEntry
from app.timeline.schemas import (
    TimelineEntryCreate,
    TimelineEntryUpdate,
    TimelineEntryResolve,
    TimelineEntryResponse,
    TimelineListResponse,
    TimelineContextRequest,
    TimelineContextResponse,
)
from app.timeline.service import TimelineService
from app.novels.models import Novel

router = APIRouter(prefix="/timeline", tags=["timeline"])
logger = logging.getLogger(__name__)


@router.get("/novels/{novel_id}")
async def get_timeline(
    novel: NovelOwner,
    db: DBSession,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    category: str = Query(None),
    status: str = Query(None),
    time_horizon: str = Query(None),
    search: str = Query(None),
    sort_by: str = Query("created_at"),
    sort_order: str = Query("desc"),
):
    """获取故事时间线列表"""
    service = TimelineService(db, novel.id)
    items, total = await service.get_timeline(
        page=page,
        page_size=page_size,
        category=category,
        status=status,
        time_horizon=time_horizon,
        search=search,
        sort_by=sort_by,
        sort_order=sort_order,
    )
    return ApiResponse.success(TimelineListResponse(
        items=[TimelineEntryResponse.model_validate(e) for e in items],
        total=total,
        page=page,
        page_size=page_size,
    ))


@router.post("/novels/{novel_id}/entries")
async def add_timeline_entry(
    novel: NovelOwner,
    data: TimelineEntryCreate,
    db: DBSession,
):
    """添加时间线条目"""
    service = TimelineService(db, novel.id)
    entry = await service.add_entry(data)
    return ApiResponse.success(TimelineEntryResponse.model_validate(entry))


@router.get("/novels/{novel_id}/entries/{entry_id}")
async def get_timeline_entry(
    novel: NovelOwner,
    entry_id: int,
    db: DBSession,
):
    """获取单条时间线条目详情"""
    service = TimelineService(db, novel.id)
    entry = await service.get_entry(entry_id)
    if not entry:
        return ApiResponse.error(code="ENTRY_NOT_FOUND", message=f"条目 {entry_id} 不存在", status_code=404)
    return ApiResponse.success(TimelineEntryResponse.model_validate(entry))


@router.put("/novels/{novel_id}/entries/{entry_id}")
async def update_timeline_entry(
    novel: NovelOwner,
    entry_id: int,
    data: TimelineEntryUpdate,
    db: DBSession,
):
    """更新时间线条目（用户修改AI输出或手动编辑）"""
    service = TimelineService(db, novel.id)
    entry = await service.update_entry(entry_id, data, editor="user")
    if not entry:
        return ApiResponse.error(code="ENTRY_NOT_FOUND", message=f"条目 {entry_id} 不存在", status_code=404)
    return ApiResponse.success(TimelineEntryResponse.model_validate(entry))


@router.patch("/novels/{novel_id}/entries/{entry_id}/status")
async def update_entry_status(
    novel: NovelOwner,
    entry_id: int,
    data: TimelineEntryResolve,
    db: DBSession,
):
    """更新条目状态（解决/完成/放弃等）"""
    service = TimelineService(db, novel.id)
    entry = await service.resolve_entry(entry_id, data)
    if not entry:
        return ApiResponse.error(code="ENTRY_NOT_FOUND", message=f"条目 {entry_id} 不存在", status_code=404)
    return ApiResponse.success(TimelineEntryResponse.model_validate(entry))


@router.delete("/novels/{novel_id}/entries/{entry_id}")
async def delete_timeline_entry(
    novel: NovelOwner,
    entry_id: int,
    db: DBSession,
):
    """删除时间线条目"""
    service = TimelineService(db, novel.id)
    success = await service.delete_entry(entry_id)
    if not success:
        return ApiResponse.error(code="ENTRY_NOT_FOUND", message=f"条目 {entry_id} 不存在", status_code=404)
    return ApiResponse.success({"message": "条目已删除"})


@router.get("/novels/{novel_id}/context")
async def get_timeline_context(
    novel: NovelOwner,
    db: DBSession,
    current_chapter: int = Query(..., description="当前章节号"),
    max_entries: int = Query(15, ge=1, le=50),
):
    """获取AI生成时注入的精简时间线上下文"""
    service = TimelineService(db, novel.id)
    entries, summary_text = await service.get_context_for_generation(current_chapter, max_entries)
    total_available = len(entries)
    truncated = False
    if entries and hasattr(service, '_last_total'):
        truncated = total_available < getattr(service, '_last_total', total_available)  # type: ignore[attr-defined]
    return ApiResponse.success(TimelineContextResponse(
        entries=[TimelineEntryResponse.model_validate(e) for e in entries],
        total_available=total_available,
        truncated=truncated,
        summary=summary_text,
    ))


@router.post("/novels/{novel_id}/auto-extract")
async def trigger_auto_extract(
    novel: NovelOwner,
     db: DBSession,
    chapter_id: int = Query(..., description="目标章节ID"),
   
):
    """手动触发从章节内容自动提取时间线条目"""
    from app.chapters.models import Chapter
    from app.core.chapter_post_processor import ChapterPostProcessor

    result = await db.execute(
        select(Chapter).where(Chapter.id == chapter_id, Chapter.novel_id == novel.id)
    )
    chapter = result.scalar_one_or_none()
    if not chapter:
        return ApiResponse.error(code="CHAPTER_NOT_FOUND", message="章节不存在", status_code=404)

    post_processor = ChapterPostProcessor(db, novel.id)
    structured_info = post_processor._extract_structured_info(chapter.content or "")

    if not structured_info:
        return ApiResponse.success({
            "message": "未检测到结构化信息",
            "entries_created": 0,
        })

    service = TimelineService(db, novel.id)
    entries = await service.auto_extract_from_chapter(
        chapter_content=chapter.content or "",
        chapter_number=chapter.chapter_number,
        chapter_id=chapter.id,
        structured_info=structured_info,
    )
    return ApiResponse.success({
        "message": f"成功提取 {len(entries)} 条时间线条目",
        "entries_created": len(entries),
        "entry_ids": [e.id for e in entries],
    })


@router.get("/novels/{novel_id}/stats")
async def get_timeline_stats(novel: NovelOwner, db: DBSession):
    """获取时间线统计信息（各分类的未完成数量）"""
    service = TimelineService(db, novel.id)
    counts = await service.get_unresolved_count()
    return ApiResponse.success(counts)
