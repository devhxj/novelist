"""
章节管理模块 - API路由
"""
from fastapi import APIRouter, Query
from sqlalchemy import select, func
from sqlalchemy.orm import selectinload

from core.response import ApiResponse
from core.database import DBSession
from core.auth import CurrentUserDep
from core.dependencies import NovelOwner
from core.exceptions import NotFoundException, UnauthorizedException, BadRequestException
from core.redis_service import redis_service
from utils import count_words
from editor.service import get_edit_session_manager
from chapters.utils import _format_outline
from editor.models import ChangeSource
from novels.models import Novel
from .models import Chapter
from .schemas import ChapterCreate, ChapterUpdate, NextChapterNumberResponse

router = APIRouter(prefix="/chapters", tags=["chapters"])


@router.get("/novel/{novel_id}/next-number", response_model=NextChapterNumberResponse)
async def get_next_chapter_number(
    novel: NovelOwner,
    db: DBSession
):
    """
    获取下一个可用的章节号
    
    返回当前最大章节号 + 1，如果没有章节则返回 1
    """
    result = await db.execute(
        select(func.max(Chapter.chapter_number)).where(
            Chapter.novel_id == novel.id
        )
    )
    max_chapter = result.scalar()
    
    next_number = (max_chapter or 0) + 1
    
    return NextChapterNumberResponse(
        next_chapter_number=next_number,
        message=f"下一个可用章节号为第{next_number}章"
    )


@router.get("/novel/{novel_id}")
async def get_chapters_by_novel(
    novel: NovelOwner,
    db: DBSession,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    status: str | None = None,
    order: str = Query("asc", pattern="^(asc|desc)$")
):
    """
    获取小说章节列表
    
    - novel_id: 小说ID
    - page: 页码
    - page_size: 每页数量
    - status: 状态筛选 (draft/completed)
    - order: 排序 (asc/desc)
    """
    cache_key = f"novel:{novel.id}:chapters:{page}:{page_size}:{status}:{order}"
    cached = await redis_service.get(cache_key)
    if cached:
        return ApiResponse.paginated(cached["items"], cached["total"], page, page_size)
    
    query = select(Chapter).where(Chapter.novel_id == novel.id)
    
    if status:
        query = query.where(Chapter.status == status)
    
    if order == "desc":
        query = query.order_by(Chapter.chapter_number.desc())
    else:
        query = query.order_by(Chapter.chapter_number.asc())
    
    count_query = select(func.count()).select_from(query.subquery())
    total_result = await db.execute(count_query)
    total = total_result.scalar()
    
    query = query.offset((page - 1) * page_size).limit(page_size)
    result = await db.execute(query)
    chapters = result.scalars().all()
    
    items = [
        {
            "id": ch.id,
            "novel_id": ch.novel_id,
            "chapter_number": ch.chapter_number,
            "title": ch.title,
            "word_count": ch.word_count or count_words(ch.content or ""),
            "status": ch.status,
            "summary": ch.summary,
            "created_at": ch.created_at,
            "updated_at": ch.updated_at
        }
        for ch in chapters
    ]
    
    await redis_service.set(cache_key, {"items": items, "total": total}, ttl=120)
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.post("", status_code=201)
async def create_chapter(
    chapter: ChapterCreate, 
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    创建章节
    
    - chapter_number: 可选，不传则自动获取下一个章节号
    - 会检查章节号是否已存在，防止重复
    """
    result = await db.execute(
        select(Novel).where(Novel.id == chapter.novel_id)
    )
    novel = result.scalar_one_or_none()
    
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此小说")
    
    if chapter.chapter_number is None:
        max_result = await db.execute(
            select(func.max(Chapter.chapter_number)).where(
                Chapter.novel_id == chapter.novel_id
            )
        )
        max_chapter = max_result.scalar()
        chapter_number = (max_chapter or 0) + 1
    else:
        chapter_number = chapter.chapter_number
    
    existing = await db.execute(
        select(Chapter).where(
            Chapter.novel_id == chapter.novel_id,
            Chapter.chapter_number == chapter_number
        )
    )
    if existing.scalar_one_or_none():
        raise BadRequestException(f"第{chapter_number}章已存在，请选择其他章节号")
    
    db_chapter = Chapter(
        novel_id=chapter.novel_id,
        chapter_number=chapter_number,
        title=chapter.title or f"第{chapter_number}章",
        content=chapter.content,
        summary=chapter.summary,
        word_count=count_words(chapter.content) if chapter.content else 0
    )
    db.add(db_chapter)
    await db.commit()
    await db.refresh(db_chapter)
    
    await redis_service.clear_pattern(f"novel:{chapter.novel_id}:chapters:*")
    
    from context.context_builder import context_cache
    context_cache.invalidate_novel(chapter.novel_id)
    
    return ApiResponse.success(
        {
            "id": db_chapter.id,
            "novel_id": db_chapter.novel_id,
            "chapter_number": db_chapter.chapter_number,
            "title": db_chapter.title,
            "content": db_chapter.content,
            "summary": db_chapter.summary,
            "status": db_chapter.status,
            "word_count": db_chapter.word_count,
            "created_at": db_chapter.created_at
        },
        message=f"第{chapter_number}章创建成功"
    )


@router.get("/{chapter_id}")
async def get_chapter(
    chapter_id: int, 
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    获取章节详情
    """
    cache_key = f"chapter:{chapter_id}:detail"
    cached = await redis_service.get(cache_key)
    if cached:
        return ApiResponse.success(cached)
    
    result = await db.execute(
        select(Chapter)
        .options(
            selectinload(Chapter.novel),
        )
        .where(Chapter.id == chapter_id)
    )
    chapter = result.scalar_one_or_none()
    
    if chapter is None:
        raise NotFoundException("章节")
    
    if chapter.novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此章节")
    
    data = {
        "id": chapter.id,
        "novel_id": chapter.novel_id,
        "chapter_number": chapter.chapter_number,
        "title": chapter.title,
        "content": chapter.content,
        "summary": chapter.summary,
        "status": chapter.status,
        "word_count": chapter.word_count or count_words(chapter.content or ""),
        "outline_json": chapter.outline_json,
        "outline_text": _format_outline(chapter.outline_json) if chapter.outline_json else None,
        "created_at": chapter.created_at,
        "updated_at": chapter.updated_at,
        "novel": {
            "id": chapter.novel.id,
            "title": chapter.novel.title
        },
    }
    
    await redis_service.set(cache_key, data, ttl=300)
    
    return ApiResponse.success(data)


@router.put("/{chapter_id}")
async def update_chapter(
    chapter_id: int, 
    chapter: ChapterUpdate, 
    db: DBSession,
    current_user: CurrentUserDep,
    collaborative: bool = Query(False, description="是否与AI协作编辑副本"),
    session_id: str | None = Query(None, description="协作编辑会话ID")
):
    """
    更新章节
    """
    result = await db.execute(
        select(Chapter)
        .options(selectinload(Chapter.novel))
        .where(Chapter.id == chapter_id)
    )
    db_chapter = result.scalar_one_or_none()
    
    if db_chapter is None:
        raise NotFoundException("章节")
    
    if db_chapter.novel.author_id != current_user.id:
        raise UnauthorizedException("无权修改此章节")
    
    update_data = chapter.model_dump(exclude_unset=True)
    manager = get_edit_session_manager(db)
    edit_session = await manager.get_edit_session(chapter_id)
    
    if edit_session or collaborative:
        if "content" not in update_data or chapter.content is None:
            raise BadRequestException("协作编辑需要提供content")
        if not edit_session:
            if not session_id:
                raise BadRequestException("协作编辑需要session_id")
            edit_session = await manager.create_edit_session(chapter_id, session_id)
        await manager.apply_change(
            edit_session=edit_session,
            change_type="full_replace",
            new_content=chapter.content,
            source=ChangeSource.USER
        )
        diff_data = await manager.get_diff(edit_session.edit_session_id)
        return ApiResponse.success(
            {
                "edit_session_id": edit_session.edit_session_id,
                "chapter_id": chapter_id,
                "change_count": edit_session.change_count,
                "working_content": edit_session.working_content,
                "diff": diff_data.get("diff", {}),
                "message": "已在副本中应用修改，等待确认"
            },
            message="副本更新成功"
        )
    
    for key, value in update_data.items():
        setattr(db_chapter, key, value)
    
    if chapter.content is not None:
        db_chapter.word_count = count_words(chapter.content)
    
    await db.commit()
    await db.refresh(db_chapter)
    
    await redis_service.delete(f"chapter:{chapter_id}:detail")
    await redis_service.clear_pattern(f"novel:{db_chapter.novel_id}:chapters:*")

    from context.context_builder import context_cache
    context_cache.invalidate_novel(db_chapter.novel_id)

    if chapter.content is not None:
        from rag.memory_updater import schedule_memory_update
        schedule_memory_update(db_chapter.novel_id, chapter_id)
    
    return ApiResponse.success(
        {
            "id": db_chapter.id,
            "chapter_number": db_chapter.chapter_number,
            "title": db_chapter.title,
            "content": db_chapter.content,
            "summary": db_chapter.summary,
            "status": db_chapter.status,
            "word_count": db_chapter.word_count,
            "updated_at": db_chapter.updated_at
        },
        message="章节更新成功"
    )


@router.delete("/{chapter_id}")
async def delete_chapter(
    chapter_id: int, 
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    删除章节
    """
    result = await db.execute(
        select(Chapter)
        .options(selectinload(Chapter.novel))
        .where(Chapter.id == chapter_id)
    )
    db_chapter = result.scalar_one_or_none()
    
    if db_chapter is None:
        raise NotFoundException("章节")
    
    if db_chapter.novel.author_id != current_user.id:
        raise UnauthorizedException("无权删除此章节")
    
    novel_id = db_chapter.novel_id
    chapter_number = db_chapter.chapter_number
    
    await db.delete(db_chapter)
    await db.commit()
    
    await redis_service.delete(f"chapter:{chapter_id}:detail")
    await redis_service.clear_pattern(f"novel:{novel_id}:chapters:*")
    
    from context.context_builder import context_cache
    context_cache.invalidate_novel(novel_id)
    
    return ApiResponse.success(
        {"chapter_number": chapter_number},
        message=f"第{chapter_number}章已删除"
    )
