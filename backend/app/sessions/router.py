"""
会话管理API路由 - AI IDE风格
支持会话作用域：整本小说/章节范围/单章节
"""
import logging
from datetime import datetime
from fastapi import APIRouter, Query, Body
from typing import Optional, List

from app.core.response import ApiResponse
from app.core.database import DBSession
from app.core.dependencies import CurrentUser
from app.core.session_manager import (
    Session, SessionManager, SessionConfig, MessageRole,
    SessionScope, ScopeType, NovelContext, ChapterContext,
    session_manager
)
from app.core.session_storage import session_storage
from app.core.llm_service import llm_service

router = APIRouter(prefix="/sessions", tags=["sessions"])
logger = logging.getLogger(__name__)

session_manager.set_storage(session_storage)


@router.post("/create")
async def create_session(
    user: CurrentUser,
    db: DBSession,
    novel_id: int = Body(..., description="小说ID"),
    scope_type: str = Body("novel", description="作用域类型: novel/chapters/chapter"),
    chapter_start: Optional[int] = Body(None, description="起始章节号"),
    chapter_end: Optional[int] = Body(None, description="结束章节号"),
    model: str = Body("deepseek-chat", description="LLM模型"),
    title: Optional[str] = Body(None, description="会话标题"),
    subtitle: Optional[str] = Body(None, description="会话副标题")
):
    """
    创建新会话
    
    作用域类型：
    - novel: 整本小说
    - chapters: 章节范围（需要chapter_start和chapter_end）
    - chapter: 单章节（需要chapter_start）
    """
    try:
        scope_type_enum = ScopeType(scope_type)
    except ValueError:
        return ApiResponse.error(
            code="INVALID_SCOPE_TYPE", 
            message=f"无效的作用域类型: {scope_type}，可选值: novel, chapters, chapter", 
            status_code=400
        )
    
    if scope_type_enum == ScopeType.CHAPTER and not chapter_start:
        return ApiResponse.error(
            code="MISSING_CHAPTER", 
            message="单章节作用域需要指定chapter_start", 
            status_code=400
        )
    
    if scope_type_enum == ScopeType.CHAPTERS and (not chapter_start or not chapter_end):
        return ApiResponse.error(
            code="MISSING_CHAPTER_RANGE", 
            message="章节范围作用域需要指定chapter_start和chapter_end", 
            status_code=400
        )
    
    scope = SessionScope(
        type=scope_type_enum,
        chapter_start=chapter_start,
        chapter_end=chapter_end if scope_type_enum == ScopeType.CHAPTERS else chapter_start
    )
    
    from app.novels.models import Novel
    from app.chapters.models import Chapter
    from sqlalchemy import select
    
    novel_context = None
    chapter_context = None
    
    result = await db.execute(select(Novel).where(Novel.id == novel_id))
    novel = result.scalar_one_or_none()
    
    if not novel:
        return ApiResponse.error(code="NOVEL_NOT_FOUND", message="小说不存在", status_code=404)
    
    if novel.author_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此小说", status_code=403)
    
    novel_context = NovelContext(
        title=novel.title or "",
        description=novel.description or "",
        genre=novel.genre or ""
    )
    
    if scope_type_enum in [ScopeType.CHAPTER, ScopeType.CHAPTERS] and chapter_start:
        chapter_result = await db.execute(
            select(Chapter).where(
                Chapter.novel_id == novel_id,
                Chapter.chapter_number == chapter_start
            )
        )
        chapter = chapter_result.scalar_one_or_none()
        
        if chapter:
            chapter_context = ChapterContext(
                chapter_number=chapter.chapter_number,
                chapter_title=chapter.title or "",
                previous_summary=chapter.summary or ""
            )
    
    session = session_manager.create_session(
        user_id=user.id,
        novel_id=novel_id,
        scope=scope,
        novel_context=novel_context,
        chapter_context=chapter_context,
        model=model
    )
    
    if title:
        session.title = title[:50]
    elif not session.title:
        base_title = novel.title or ""
        session.title = f"{base_title} 对话" if base_title else "新对话"
    if subtitle:
        session.subtitle = subtitle[:50]
        session.metadata["subtitle"] = session.subtitle
    
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "session_id": session.session_id,
        "scope": scope.to_dict(),
        "display_name": scope.get_display_name(),
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "novel_id": novel_id,
        "model": model,
        "created_at": session.created_at.isoformat(),
        "message": "会话创建成功"
    })


@router.get("/list")
async def list_sessions(
    user: CurrentUser,
    novel_id: Optional[int] = Query(None, description="按小说ID过滤"),
    scope_type: Optional[str] = Query(None, description="按作用域类型过滤"),
    limit: int = Query(20, ge=1, le=100)
):
    """
    列出用户会话
    
    - novel_id: 按小说ID过滤
    - scope_type: 按作用域类型过滤 (novel/chapters/chapter)
    """
    scope_enum = None
    if scope_type:
        try:
            scope_enum = ScopeType(scope_type)
        except ValueError:
            return ApiResponse.error(
                code="INVALID_SCOPE_TYPE", 
                message=f"无效的作用域类型: {scope_type}", 
                status_code=400
            )
    
    sessions = await session_manager.list_user_sessions(
        user_id=user.id,
        novel_id=novel_id,
        scope_type=scope_enum
    )
    
    return ApiResponse.success({
        "sessions": [
            {
                "session_id": s.session_id,
                "scope": s.scope.to_dict(),
                "display_name": s.get_display_name(),
                "title": s.title,
                "subtitle": s.get_subtitle(),
                "novel_id": s.novel_id,
                "message_count": s.get_message_count(),
                "model": s.model,
                "pending_changes": len(s.pending_changes),
                "created_at": s.created_at.isoformat(),
                "updated_at": s.updated_at.isoformat(),
                "preview": s.messages[-1].content[:100] if s.messages else ""
            }
            for s in sessions[:limit]
        ],
        "total": len(sessions)
    })


@router.get("/{session_id}")
async def get_session(
    user: CurrentUser,
    session_id: str
):
    """获取会话详情"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此会话", status_code=403)
    
    stats = session_manager.get_session_stats(session)
    
    return ApiResponse.success({
        "session_id": session.session_id,
        "user_id": session.user_id,
        "scope": session.scope.to_dict(),
        "display_name": session.get_display_name(),
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "novel_id": session.novel_id,
        "messages": [m.to_dict() for m in session.messages],
        "summary": session.summary,
        "novel_context": session.novel_context.to_dict() if session.novel_context else None,
        "chapter_context": session.chapter_context.to_dict() if session.chapter_context else None,
        "pending_changes": session.pending_changes,
        "stats": stats,
        "created_at": session.created_at.isoformat(),
        "updated_at": session.updated_at.isoformat()
    })


@router.get("/{session_id}/messages")
async def get_messages(
    user: CurrentUser,
    session_id: str,
    limit: int = Query(50, ge=1, le=200),
    offset: int = Query(0, ge=0)
):
    """获取会话消息列表"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此会话", status_code=403)
    
    messages = session.messages[offset:offset + limit]
    
    return ApiResponse.success({
        "session_id": session.session_id,
        "scope": session.scope.to_dict(),
        "messages": [m.to_dict() for m in messages],
        "total": len(session.messages),
        "limit": limit,
        "offset": offset
    })


@router.delete("/{session_id}")
async def delete_session(
    user: CurrentUser,
    session_id: str
):
    """删除会话"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权删除此会话", status_code=403)
    
    await session_manager.delete_session(session_id)
    
    return ApiResponse.success({"message": "会话已删除"})


@router.put("/{session_id}/scope")
async def update_session_scope(
    user: CurrentUser,
    db: DBSession,
    session_id: str,
    scope_type: str = Body(..., description="作用域类型"),
    chapter_start: Optional[int] = Body(None, description="起始章节号"),
    chapter_end: Optional[int] = Body(None, description="结束章节号")
):
    """更新会话作用域"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权操作此会话", status_code=403)
    
    try:
        scope_enum = ScopeType(scope_type)
    except ValueError:
        return ApiResponse.error(code="INVALID_SCOPE_TYPE", message=f"无效的作用域类型: {scope_type}", status_code=400)
    
    new_scope = SessionScope(
        type=scope_enum,
        chapter_start=chapter_start,
        chapter_end=chapter_end if scope_enum == ScopeType.CHAPTERS else chapter_start
    )
    
    session.scope = new_scope
    session.subtitle = new_scope.get_display_name()
    
    from app.chapters.models import Chapter
    from sqlalchemy import select
    
    if scope_enum in [ScopeType.CHAPTER, ScopeType.CHAPTERS] and chapter_start and session.novel_id:
        chapter_result = await db.execute(
            select(Chapter).where(
                Chapter.novel_id == session.novel_id,
                Chapter.chapter_number == chapter_start
            )
        )
        chapter = chapter_result.scalar_one_or_none()
        
        if chapter:
            session.chapter_context = ChapterContext(
                chapter_number=chapter.chapter_number,
                chapter_title=chapter.title or "",
                previous_summary=chapter.summary or ""
            )
    else:
        session.chapter_context = None
    
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "session_id": session.session_id,
        "scope": new_scope.to_dict(),
        "display_name": new_scope.get_display_name(),
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "message": "作用域已更新"
    })


@router.put("/{session_id}/title")
async def update_session_title(
    user: CurrentUser,
    session_id: str,
    title: str = Body(..., embed=True, description="会话标题"),
    subtitle: Optional[str] = Body(None, embed=True, description="会话副标题")
):
    """更新会话标题"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权操作此会话", status_code=403)
    
    session.title = title[:50]
    if subtitle is not None:
        session.subtitle = subtitle[:50]
        session.metadata["subtitle"] = session.subtitle
    session.updated_at = datetime.now()
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "标题已更新",
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "display_name": session.get_display_name()
    })


@router.post("/{session_id}/clear")
async def clear_messages(
    user: CurrentUser,
    session_id: str,
    keep_system: bool = Body(True, embed=True, description="是否保留系统消息")
):
    """清空会话消息"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权操作此会话", status_code=403)
    
    if keep_system:
        session.messages = [
            m for m in session.messages
            if m.role == MessageRole.SYSTEM
        ]
    else:
        session.messages = []
    
    session.summary = None
    session.pending_changes = []
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "消息已清空",
        "remaining_messages": len(session.messages)
    })


@router.get("/{session_id}/stats")
async def get_session_stats(
    user: CurrentUser,
    session_id: str
):
    """获取会话统计信息"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此会话", status_code=403)
    
    stats = session_manager.get_session_stats(session)
    return ApiResponse.success(stats)


@router.put("/{session_id}/context/novel")
async def update_novel_context(
    user: CurrentUser,
    session_id: str,
    title: str = Body(""),
    description: str = Body(""),
    genre: str = Body(""),
    outline: str = Body(""),
    world_setting: str = Body(""),
    characters_summary: str = Body(""),
    main_plot: str = Body("")
):
    """更新小说级上下文"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权操作此会话", status_code=403)
    
    novel_context = NovelContext(
        title=title,
        description=description,
        genre=genre,
        outline=outline,
        world_setting=world_setting,
        characters_summary=characters_summary,
        main_plot=main_plot
    )
    
    session_manager.update_novel_context(session, novel_context)
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "小说上下文已更新",
        "novel_context": novel_context.to_dict()
    })


@router.put("/{session_id}/context/chapter")
async def update_chapter_context(
    user: CurrentUser,
    session_id: str,
    chapter_number: int = Body(...),
    chapter_title: str = Body(""),
    previous_summary: str = Body(""),
    current_outline: str = Body(""),
    key_events: List[str] = Body(default_factory=list),
    focus_characters: List[str] = Body(default_factory=list)
):
    """更新章节级上下文"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权操作此会话", status_code=403)
    
    chapter_context = ChapterContext(
        chapter_number=chapter_number,
        chapter_title=chapter_title,
        previous_summary=previous_summary,
        current_outline=current_outline,
        key_events=key_events,
        focus_characters=focus_characters
    )
    
    session_manager.update_chapter_context(session, chapter_context)
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "章节上下文已更新",
        "chapter_context": chapter_context.to_dict()
    })
