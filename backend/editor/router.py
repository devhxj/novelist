"""
文本编辑API路由 - 副本编辑机制
"""
from fastapi import APIRouter, Query

from core.response import ApiResponse
from core.database import DBSession
from core.auth import CurrentUserDep
from editor.service import get_edit_session_manager
from chapters.models import Chapter
from novels.models import Novel
from sqlalchemy import select

router = APIRouter(prefix="/editor", tags=["editor"])


@router.post("/session/{edit_session_id}/accept")
async def accept_edit_session(
    user: CurrentUserDep,
    db: DBSession,
    edit_session_id: str,
    chapter_id: int | None = Query(None, description="章节ID（edit_session_id 找不到时的回退查找）"),
):
    """接受所有变更，将副本内容应用到原章节"""
    manager = get_edit_session_manager(db)
    edit_session = await manager.get_edit_session_by_id(edit_session_id)
    if not edit_session and chapter_id:
        edit_session = await manager.get_edit_session(chapter_id)

    if not edit_session:
        current = await manager.get_edit_session(chapter_id) if chapter_id else None
        return ApiResponse.error(
            code="SESSION_NOT_FOUND", message="编辑会话不存在",
            status_code=404,
            details={"pending_edit_session_id": current.edit_session_id if current else None}
        )

    result = await db.execute(
        select(Chapter).where(Chapter.id == edit_session.chapter_id)
    )
    chapter = result.scalar_one_or_none()

    if not chapter:
        return ApiResponse.error(code="CHAPTER_NOT_FOUND", message="章节不存在", status_code=404)

    result = await db.execute(
        select(Novel).where(Novel.id == chapter.novel_id)
    )
    novel = result.scalar_one_or_none()

    if not novel or novel.author_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权操作此章节", status_code=403)

    try:
        result = await manager.accept_edit_session(edit_session.edit_session_id)
    except ValueError as e:
        current = await manager.get_edit_session(edit_session.chapter_id)
        return ApiResponse.error(
            code="EDIT_INVALID", message=str(e), status_code=400,
            details={"pending_edit_session_id": current.edit_session_id if current else None}
        )

    return ApiResponse.success({
        "edit_session_id": edit_session.edit_session_id,
        "chapter_id": result["chapter_id"],
        "change_count": result["change_count"],
        "word_count": result["word_count"],
        "summary": result.get("summary"),
        "already_processed": result.get("already_processed", False),
        "message": "编辑会话此前已被接受" if result.get("already_processed") else f"已接受 {result['change_count']} 处变更"
    })


@router.post("/session/{edit_session_id}/reject")
async def reject_edit_session(
    user: CurrentUserDep,
    db: DBSession,
    edit_session_id: str,
    chapter_id: int | None = Query(None, description="章节ID（edit_session_id 找不到时的回退查找）"),
):
    """拒绝所有变更，回退到原版本，副本内容被丢弃"""
    manager = get_edit_session_manager(db)
    edit_session = await manager.get_edit_session_by_id(edit_session_id)
    if not edit_session and chapter_id:
        edit_session = await manager.get_edit_session(chapter_id)

    if not edit_session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="编辑会话不存在", status_code=404)

    result = await db.execute(
        select(Chapter).where(Chapter.id == edit_session.chapter_id)
    )
    chapter = result.scalar_one_or_none()

    if not chapter:
        return ApiResponse.error(code="CHAPTER_NOT_FOUND", message="章节不存在", status_code=404)

    result = await db.execute(
        select(Novel).where(Novel.id == chapter.novel_id)
    )
    novel = result.scalar_one_or_none()

    if not novel or novel.author_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权操作此章节", status_code=403)

    try:
        result = await manager.reject_edit_session(edit_session.edit_session_id)
    except ValueError as e:
        return ApiResponse.error(code="EDIT_INVALID", message=str(e), status_code=400)

    return ApiResponse.success({
        "edit_session_id": edit_session.edit_session_id,
        "chapter_id": result["chapter_id"],
        "already_processed": result.get("already_processed", False),
        "message": "编辑会话此前已被拒绝" if result.get("already_processed") else "已拒绝所有变更，回退到原版本"
    })


@router.get("/session/{edit_session_id}")
async def get_edit_session_status(
    user: CurrentUserDep,
    db: DBSession,
    edit_session_id: str
):
    """获取编辑会话状态"""
    manager = get_edit_session_manager(db)
    edit_session = await manager.get_edit_session_by_id(edit_session_id)
    
    if not edit_session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="编辑会话不存在", status_code=404)

    result = await db.execute(
        select(Chapter).where(Chapter.id == edit_session.chapter_id)
    )
    chapter = result.scalar_one_or_none()
    if not chapter:
        return ApiResponse.error(code="CHAPTER_NOT_FOUND", message="章节不存在", status_code=404)

    result = await db.execute(
        select(Novel).where(Novel.id == chapter.novel_id)
    )
    novel = result.scalar_one_or_none()
    if not novel or novel.author_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此编辑会话", status_code=403)
    
    diff_data = await manager.get_diff(edit_session_id)
    
    return ApiResponse.success({
        "edit_session_id": edit_session.edit_session_id,
        "chapter_id": edit_session.chapter_id,
        "status": edit_session.status,
        "change_count": edit_session.change_count,
        "original_content": edit_session.original_content,
        "working_content": edit_session.working_content,
        "diff": diff_data.get("diff", {}),
        "created_at": edit_session.created_at.isoformat() if edit_session.created_at else None
    })


@router.get("/chapter/{chapter_id}/status")
async def get_chapter_edit_status(
    user: CurrentUserDep,
    db: DBSession,
    chapter_id: int
):
    """获取章节的编辑状态"""
    result = await db.execute(
        select(Chapter).where(Chapter.id == chapter_id)
    )
    chapter = result.scalar_one_or_none()
    
    if not chapter:
        return ApiResponse.error(code="CHAPTER_NOT_FOUND", message="章节不存在", status_code=404)
    
    result = await db.execute(
        select(Novel).where(Novel.id == chapter.novel_id)
    )
    novel = result.scalar_one_or_none()
    
    if not novel or novel.author_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此章节", status_code=403)
    
    manager = get_edit_session_manager(db)
    edit_session = await manager.get_edit_session(chapter_id)
    
    if edit_session:
        diff_data = await manager.get_diff(edit_session.edit_session_id)
        return ApiResponse.success({
            "has_active_edit": True,
            "edit_session_id": edit_session.edit_session_id,
            "latest_pending_edit_session_id": edit_session.edit_session_id,
            "status": edit_session.status,
            "change_count": edit_session.change_count,
            "working_content": edit_session.working_content,
            "original_content": edit_session.original_content,
            "diff": diff_data.get("diff", {}),
            "created_from_ws_session": (edit_session.extra_metadata or {}).get("created_from_ws_session")
        })
    
    return ApiResponse.success({
        "has_active_edit": False,
        "latest_pending_edit_session_id": None,
        "chapter_content": chapter.content,
        "message": "当前没有活动的编辑会话"
    })


@router.get("/chapter/{chapter_id}")
async def get_chapter_for_editor(
    user: CurrentUserDep,
    db: DBSession,
    chapter_id: int
):
    """获取章节内容（供编辑器显示）"""
    result = await db.execute(
        select(Chapter).where(Chapter.id == chapter_id)
    )
    chapter = result.scalar_one_or_none()
    
    if not chapter:
        return ApiResponse.error(code="CHAPTER_NOT_FOUND", message="章节不存在", status_code=404)
    
    result = await db.execute(
        select(Novel).where(Novel.id == chapter.novel_id)
    )
    novel = result.scalar_one_or_none()
    
    if not novel or novel.author_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此章节", status_code=403)
    
    manager = get_edit_session_manager(db)
    edit_session = await manager.get_edit_session(chapter_id)
    
    return ApiResponse.success({
        "chapter_id": chapter.id,
        "chapter_number": chapter.chapter_number,
        "title": chapter.title,
        "content": chapter.content or "",
        "word_count": chapter.word_count or 0,
        "status": chapter.status,
        "has_active_edit": edit_session is not None,
        "edit_session_id": edit_session.edit_session_id if edit_session else None,
        "latest_pending_edit_session_id": edit_session.edit_session_id if edit_session else None,
        "working_content": edit_session.working_content if edit_session else None,
        "change_count": edit_session.change_count if edit_session else 0
    })
