"""
会话管理API路由 - AI IDE风格
"""
import logging
from datetime import datetime, timezone
from fastapi import APIRouter, Query, Body

from core.response import ApiResponse
from core.auth import CurrentUserDep
from core.pagination import PageResponse, PaginationDep
from sessions.storage import session_storage

router = APIRouter(prefix="/sessions", tags=["sessions"])
logger = logging.getLogger(__name__)


@router.get("/list")
async def list_sessions(
    user: CurrentUserDep,
    pagination: PaginationDep,
    novel_id: int | None = Query(None, description="按小说ID过滤"),
):
    """列出用户会话（分页）"""
    sessions = await session_storage.list_by_user(
        user_id=user.id,
        novel_id=novel_id,
        limit=pagination.limit,
        offset=pagination.offset,
    )
    total = await session_storage.get_session_count(user.id, novel_id)

    return ApiResponse.success(PageResponse(
        items=[{
            "session_id": s.session_id,
            "title": s.title,
            "novel_id": s.novel_id,
            "model": s.model,
            "pending_changes": len(s.pending_changes),
            "created_at": s.created_at.isoformat(),
            "updated_at": s.updated_at.isoformat(),
        } for s in sessions],
        total=total,
        page=pagination.page,
        size=pagination.size,
    ).model_dump())


@router.get("/{session_id}")
async def get_session(
    user: CurrentUserDep,
    session_id: str
):
    """获取会话详情"""
    session = await session_storage.load(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此会话", status_code=403)
    
    
    return ApiResponse.success({
        **session.model_dump(mode="json"),
        "messages": [m.model_dump(mode="json") for m in session.messages],
        
    })


@router.get("/{session_id}/messages")
async def get_messages(
    user: CurrentUserDep,
    session_id: str,
    limit: int = Query(50, ge=1, le=200),
    offset: int = Query(0, ge=0)
):
    """获取会话消息列表"""
    session = await session_storage.load(session_id)
    
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
    session = await session_storage.load(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权删除此会话", status_code=403)
    
    await session_storage.delete(session_id)
    
    return ApiResponse.success({"message": "会话已删除"})


@router.put("/{session_id}/title")
async def update_session_title(
    user: CurrentUserDep,
    session_id: str,
    title: str = Body(..., embed=True, description="会话标题"),
):
    """更新会话标题"""
    session = await session_storage.load(session_id)

    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)

    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权操作此会话", status_code=403)

    session.title = title[:50]
    session.updated_at = datetime.now(timezone.utc)
    await session_storage.save(session)
    
    return ApiResponse.success({
        "message": "标题已更新",
        "title": session.title,
    })



@router.get("/{session_id}/usage")
async def get_session_stats(
    user: CurrentUserDep,
    session_id: str
):
    """获取会话统计信息"""
    session = await session_storage.load(session_id)
    
    if not session:
        return ApiResponse.error(code="SESSION_NOT_FOUND", message="会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error(code="FORBIDDEN", message="无权访问此会话", status_code=403)
    
   
    return ApiResponse.success(session.usage)

