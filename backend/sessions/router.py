"""
会话管理API路由 - AI IDE风格
"""
import logging
from datetime import datetime, timezone
from fastapi import APIRouter, Query, Body

from core.response import ApiResponse
from core.database import DBSession
from core.auth import CurrentUserDep
from sessions.manager import (
    session_manager
)
from sessions.schema import MessageRole
from sessions.storage import session_storage

router = APIRouter(prefix="/sessions", tags=["sessions"])
logger = logging.getLogger(__name__)

session_manager.set_storage(session_storage)


@router.post("/create")
async def create_session(
    user: CurrentUserDep,
    db: DBSession,
    novel_id: int = Body(..., description="小说ID"),
    model: str = Body("deepseek-v4-flash", description="LLM模型"),
    title: str | None = Body(None, description="会话标题"),
    subtitle: str | None = Body(None, description="会话副标题"),
):
    """创建新会话"""
    from novels.models import Novel
    from sqlalchemy import select

    result = await db.execute(select(Novel).where(Novel.id == novel_id))
    novel = result.scalar_one_or_none()

    if not novel:
        return ApiResponse.error(code="NOVEL_NOT_FOUND", message="小说不存在", status_code=404)

    if novel.author_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此小说", status_code=403)

    session = session_manager.create_session(
        user_id=user.id,
        novel_id=novel_id,
        model=model,
    )

    if title:
        session.title = title[:50]
    elif not session.title:
        base_title = novel.title or ""
        session.title = f"{base_title} 对话" if base_title else "新对话"
    if subtitle:
        session.subtitle = subtitle[:50]
        session.extra_metadata["subtitle"] = session.subtitle

    await session_manager.save_session(session)

    return ApiResponse.success({
        "session_id": session.session_id,
        "display_name": session.get_display_name(),
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "novel_id": novel_id,
        "model": model,
        "created_at": session.created_at.isoformat(),
        "message": "会话创建成功",
    })


@router.get("/list")
async def list_sessions(
    user: CurrentUserDep,
    novel_id: int | None = Query(None, description="按小说ID过滤"),
    limit: int = Query(20, ge=1, le=100),
):
    """列出用户会话"""
    sessions = await session_manager.list_user_sessions(
        user_id=user.id,
        novel_id=novel_id,
    )

    return ApiResponse.success({
        "sessions": [
            {
                "session_id": s.session_id,
                "display_name": s.get_display_name(),
                "title": s.title,
                "subtitle": s.get_subtitle(),
                "novel_id": s.novel_id,
                "message_count": s.get_message_count(),
                "model": s.model,
                "pending_changes": len(s.pending_changes),
                "created_at": s.created_at.isoformat(),
                "updated_at": s.updated_at.isoformat(),
                "preview": s.messages[-1].content[:100] if s.messages else "",
            }
            for s in sessions[:limit]
        ],
        "total": len(sessions),
    })


@router.get("/{session_id}")
async def get_session(
    user: CurrentUserDep,
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
        "display_name": session.get_display_name(),
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "novel_id": session.novel_id,
        "messages": [m.model_dump(mode="json") for m in session.messages],
        "summary": session.summary,
        "pending_changes": session.pending_changes,
        "stats": stats,
        "created_at": session.created_at.isoformat(),
        "updated_at": session.updated_at.isoformat()
    })


@router.get("/{session_id}/messages")
async def get_messages(
    user: CurrentUserDep,
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
        "messages": [m.model_dump(mode="json") for m in messages],
        "total": len(session.messages),
        "limit": limit,
        "offset": offset
    })


@router.delete("/{session_id}")
async def delete_session(
    user: CurrentUserDep,
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


@router.put("/{session_id}/title")
async def update_session_title(
    user: CurrentUserDep,
    session_id: str,
    title: str = Body(..., embed=True, description="会话标题"),
    subtitle: str | None = Body(None, embed=True, description="会话副标题")
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
        session.extra_metadata["subtitle"] = session.subtitle
    session.updated_at = datetime.now(timezone.utc)
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "标题已更新",
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "display_name": session.get_display_name()
    })


@router.post("/{session_id}/clear")
async def clear_messages(
    user: CurrentUserDep,
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
    user: CurrentUserDep,
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

