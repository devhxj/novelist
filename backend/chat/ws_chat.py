"""
WebSocket路由 - AI IDE风格统一入口
整合所有功能：对话、生成、编辑、工具调用
"""
import logging
import asyncio
import json
from datetime import datetime, timezone
from fastapi import APIRouter, WebSocket, WebSocketDisconnect, Query
from sqlalchemy import select
from typing import Any

from core.websocket import ws_manager
from core.database import AsyncSessionLocal
from core.auth import decode_token
from core.llm_service import llm_service
from chat.session_manager import (
    Session, MessageRole,
    session_manager
)
from sessions.session_storage import session_storage
from context.context_builder import (
    ContextBuilder,
    _format_creative_profile_for_prompt,
    _build_novel_context_snapshot,
    _build_novel_context,
)
from chat.edit_mode import EditMode, EditModeConfig
from chapters.models import Chapter
from novels.models import NovelCreativeProfile
from novels.models import Novel
from editor.service import get_edit_session_manager
from mcp_tools.registry import get_mcp_registry

from chat.ws_utils import (
    _friendly_error_message,
    _sanitize_tool_error,
    _extract_partial_argument_string,
    _sync_tool_display_name,
    _build_tool_call_presentation,
    _TOOL_SYNC_KINDS,
)
from chat.ws_generation import _run_generation_task


router = APIRouter(tags=["websocket"])
logger = logging.getLogger(__name__)

session_manager.set_storage(session_storage)


LONG_TERM_RULE_CUES = (
    "以后都", "之后都", "长期", "一直", "整体风格", "整体基调", "这本书",
    "不要再", "不要出现", "必须保留", "务必保留", "默认", "固定",
    "禁忌", "原则", "设定上", "统一", "长期目标"
)

AUTHORING_INTENT_CUES = (
    "写", "续写", "改", "修改", "润色", "重写", "扩写", "补写", "生成",
    "创建章节", "新建章节", "写一章", "写个", "规划", "大纲", "审阅", "检查",
    "整理", "完善", "补充", "设计", "安排", "帮我写", "帮我改", "开始写",
    "看", "查看", "了解", "分析", "怎么样", "好不好", "角色", "情节",
    "时间线", "伏笔", "关系", "设定", "内容", "章节", "进度",
    "总结", "回顾", "梳理", "评估", "建议", "意见", "想法", "讨论"
)


async def get_user_from_token(token: str) -> int | None:
    try:
        payload = decode_token(token)
        if payload and payload.get("sub"):
            return int(payload["sub"])
    except Exception:
        pass
    return None


def _looks_like_long_term_rule(message: str) -> bool:
    text = (message or "").strip()
    if len(text) < 6:
        return False
    return any(cue in text for cue in LONG_TERM_RULE_CUES)


def _looks_like_authoring_intent(message: str) -> bool:
    text = (message or "").strip()
    if len(text) < 2:
        return False
    return any(cue in text for cue in AUTHORING_INTENT_CUES)




@router.websocket("/ws/chat")
async def websocket_chat(
    websocket: WebSocket,
    token: str = Query(...),
    novel_id: int = Query(...)
):
    """
    AI IDE风格WebSocket - 统一入口
    """
    user_id = await get_user_from_token(token)
    if not user_id:
        await websocket.close(code=4001, reason="Invalid token")
        return
    
    async with AsyncSessionLocal() as db:
        result = await db.execute(
            select(Novel).where(Novel.id == novel_id)
        )
        novel = result.scalar_one_or_none()
        
        if not novel or novel.author_id != user_id:
            await websocket.close(code=4003, reason="No permission")
            return
    
    connected = await ws_manager.connect(websocket, user_id, novel_id)
    if not connected:
        await websocket.close(code=4005, reason="Too many connections")
        return
    
    active_tasks: dict[str, asyncio.Task] = {}
    task_flags: dict[str, bool] = {}
    current_session: Session | None = None
    
    logger.info(f"WebSocket connected: user={user_id}, novel={novel_id}")
    
    try:
        while True:
            data = await websocket.receive_json()
            message_type = data.get("type")

            # 审批消息拦截：工作流等在 _approval_events 上，直接通知
            if data.get("type") == "outline_approval" and current_session.session_id:
                from mcp_tools.workflow_tools import signal_approval
                signal_approval(current_session.session_id, data.get("approved", False), data.get("feedback", ""))
                continue

            logger.debug(f"Received message type: {message_type}")
            try:
                if message_type == "create_session":
                    current_session = await _handle_create_session(
                        websocket, data, user_id, novel_id
                    )
                
                elif message_type == "load_session":
                    current_session = await _handle_load_session(
                        websocket, data, user_id
                    )
                
                elif message_type == "list_sessions":
                    await _handle_list_sessions(websocket, user_id, novel_id, data)
                
                elif message_type == "chat":
                    if not current_session:
                        current_session = session_manager.create_session(
                            user_id=user_id,
                            novel_id=novel_id,
                        )
                        await session_manager.save_session(current_session)
                    
                    task_id = f"chat_{current_session.session_id}_{datetime.now(timezone.utc).strftime('%H%M%S')}"
                    task_flags[task_id] = True
                    
                    await ws_manager.send_personal_message({
                        "type": "chat_started",
                        "task_id": task_id,
                        "session_id": current_session.session_id,
                        "timestamp": datetime.now(timezone.utc).isoformat()
                    }, websocket)
                    
                    task = asyncio.create_task(
                        _run_chat_with_tools(
                            task_id=task_id,
                            session=current_session,
                            user_message=data.get("message", ""),
                            tools_enabled=data.get("tools_enabled", True),
                            novel_id=novel_id,
                            websocket=websocket,
                            task_flags=task_flags
                        )
                    )
                    active_tasks[task_id] = task
                
                elif message_type == "generate":
                    task_id = f"gen_{novel_id}_{data.get('generation_type', 'chapter')}_{datetime.now(timezone.utc).strftime('%H%M%S')}"
                    task_flags[task_id] = True
                    
                    task = asyncio.create_task(
                        _run_generation_task(
                            task_id=task_id,
                            novel_id=novel_id,
                            generation_type=data.get("generation_type", "chapter"),
                            params=data.get("params", {}),
                            websocket=websocket,
                            task_flags=task_flags
                        )
                    )
                    active_tasks[task_id] = task
                
                elif message_type == "cancel":
                    task_id = data.get("task_id")
                    if task_id in task_flags:
                        task_flags[task_id] = False
                        if task_id in active_tasks:
                            active_tasks[task_id].cancel()
                        await ws_manager.send_personal_message({
                            "type": "task_cancelled",
                            "task_id": task_id,
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)
                
                elif message_type == "read_chapter":
                    await _handle_read_chapter(websocket, data.get("chapter_id"), novel_id)
                
                elif message_type == "start_edit":
                    await _handle_start_edit(websocket, data, novel_id, current_session)
                
                elif message_type == "apply_edit":
                    await _handle_apply_edit(websocket, data, novel_id)
                
                elif message_type == "accept_edit":
                    await _handle_accept_edit(websocket, data, novel_id)
                
                elif message_type == "reject_edit":
                    await _handle_reject_edit(websocket, data, novel_id)
                
                elif message_type == "end_session":
                    await _handle_end_session(
                        websocket, current_session, active_tasks, task_flags, user_id, novel_id
                    )
                    current_session = None
            except Exception as e:
                logger.error(f"Message handling error: type={message_type}, error={e}", exc_info=True)
                await ws_manager.send_personal_message({
                    "type": "error",
                    "error": _friendly_error_message(e),
                    "message_type": message_type,
                    "timestamp": datetime.now(timezone.utc).isoformat()
                }, websocket)
    
    except WebSocketDisconnect:
        logger.info(f"Chat WebSocket disconnected: user={user_id}, novel={novel_id}")
        for task_id in task_flags:
            task_flags[task_id] = False
        if current_session and current_session.session_id:
            from mcp_tools.workflow_tools import abort_approval
            abort_approval(current_session.session_id)
        ws_manager.disconnect(websocket, user_id, novel_id)
    except Exception as e:
        logger.error(f"Chat WebSocket error: {e}", exc_info=True)
        for task_id in task_flags:
            task_flags[task_id] = False
        if current_session and current_session.session_id:
            from mcp_tools.workflow_tools import abort_approval
            abort_approval(current_session.session_id)
        ws_manager.disconnect(websocket, user_id, novel_id)


async def _handle_create_session(websocket, data, user_id, novel_id):
    model = data.get("model", "deepseek-v4-flash")
    edit_mode = "agent"
    reasoning_effort = data.get("reasoning_effort")

    async with AsyncSessionLocal() as db:
        novel_context = await _build_novel_context(db, novel_id)

    session = session_manager.create_session(
        user_id=user_id,
        novel_id=novel_id,
        novel_context=novel_context,
        model=model,
        metadata={"reasoning_effort": reasoning_effort} if reasoning_effort else None,
    )
    if not session.title:
        base_title = novel_context.title if novel_context else ""
        session.title = f"{base_title} 对话" if base_title else "新对话"
    session.edit_mode = edit_mode
    await session_manager.save_session(session)

    await ws_manager.send_personal_message({
        "type": "session_created",
        "session_id": session.session_id,
        "display_name": session.get_display_name(),
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "model": model,
        "reasoning_effort": reasoning_effort,
        "stats": session_manager.get_session_stats(session),
        "timestamp": datetime.now(timezone.utc).isoformat()
    }, websocket)

    return session


async def _handle_load_session(websocket, data, user_id):
    session_id = data.get("session_id")
    session = await session_manager.load_session(session_id)
    
    if not session:
        await ws_manager.send_personal_message({
            "type": "error",
            "error": "会话不存在",
            "timestamp": datetime.now(timezone.utc).isoformat()
        }, websocket)
        return None
    
    if session.user_id != user_id:
        await ws_manager.send_personal_message({
            "type": "error",
            "error": "无权访问此会话",
            "timestamp": datetime.now(timezone.utc).isoformat()
        }, websocket)
        return None
    
    await ws_manager.send_personal_message({
        "type": "session_loaded",
        "session_id": session.session_id,
        "display_name": session.get_display_name(),
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "message_count": session.get_message_count(),
        "stats": session_manager.get_session_stats(session),
        "recent_messages": [
            m.to_dict()
            for m in session.messages
            if m.role != MessageRole.TOOL
        ],
        "timestamp": datetime.now(timezone.utc).isoformat()
    }, websocket)
    
    return session


async def _handle_list_sessions(websocket, user_id, novel_id, data):
    sessions = await session_manager.list_user_sessions(
        user_id=user_id,
        novel_id=novel_id,
    )

    await ws_manager.send_personal_message({
        "type": "sessions_list",
        "sessions": [
            {
                "session_id": s.session_id,
                "display_name": s.get_display_name(),
                "title": s.title,
                "subtitle": s.get_subtitle(),
                "message_count": s.get_message_count(),
                "updated_at": s.updated_at.isoformat()
            }
            for s in sessions
        ],
        "timestamp": datetime.now(timezone.utc).isoformat()
    }, websocket)


async def _handle_read_chapter(websocket, chapter_id, novel_id):
    async with AsyncSessionLocal() as db:
        result = await db.execute(
            select(Chapter).where(Chapter.id == chapter_id)
        )
        chapter = result.scalar_one_or_none()
        
        if not chapter:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "章节不存在",
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        
        if chapter.novel_id != novel_id:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "无权访问此章节",
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        
        manager = get_edit_session_manager(db)
        edit_session = await manager.get_edit_session(chapter_id)
        
        await ws_manager.send_personal_message({
            "type": "chapter_content",
            "chapter_id": chapter.id,
            "chapter_number": chapter.chapter_number,
            "title": chapter.title,
            "content": chapter.content or "",
            "word_count": chapter.word_count or 0,
            "status": chapter.status,
            "has_active_edit": edit_session is not None,
            "edit_session_id": edit_session.edit_session_id if edit_session else None,
            "working_content": edit_session.working_content if edit_session else None,
            "change_count": edit_session.change_count if edit_session else 0,
            "timestamp": datetime.now(timezone.utc).isoformat()
        }, websocket)


async def _handle_start_edit(websocket, data, novel_id, session):
    chapter_id = data.get("chapter_id")
    ws_session_id = session.session_id if session else "unknown"
    
    async with AsyncSessionLocal() as db:
        result = await db.execute(
            select(Chapter).where(Chapter.id == chapter_id)
        )
        chapter = result.scalar_one_or_none()
        
        if not chapter:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "章节不存在",
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        
        if chapter.novel_id != novel_id:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "无权编辑此章节",
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        
        manager = get_edit_session_manager(db)
        edit_session = await manager.create_edit_session(chapter_id, ws_session_id)
        
        await ws_manager.send_personal_message({
            "type": "edit_started",
            "edit_session_id": edit_session.edit_session_id,
            "latest_pending_edit_session_id": edit_session.edit_session_id,
            "chapter_id": chapter_id,
            "original_content": edit_session.original_content,
            "working_content": edit_session.working_content,
            "change_count": 0,
            "timestamp": datetime.now(timezone.utc).isoformat()
        }, websocket)


async def _handle_apply_edit(websocket, data, novel_id):
    edit_session_id = data.get("edit_session_id")
    change_type = data.get("change_type", "full_replace")
    new_content = data.get("new_content", "")
    start_line = data.get("start_line")
    end_line = data.get("end_line")
    reason = data.get("reason")
    
    async with AsyncSessionLocal() as db:
        manager = get_edit_session_manager(db)
        edit_session = await manager.get_edit_session_by_id(edit_session_id)
        
        if not edit_session:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "编辑会话不存在",
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        
        await manager.apply_change(
            edit_session=edit_session,
            change_type=change_type,
            new_content=new_content,
            start_line=start_line,
            end_line=end_line,
            reason=reason
        )
        
        diff_data = await manager.get_diff(edit_session_id)
        
        await ws_manager.send_personal_message({
            "type": "edit_applied",
            "edit_session_id": edit_session_id,
            "latest_pending_edit_session_id": edit_session.edit_session_id,
            "chapter_id": edit_session.chapter_id,
            "change_count": edit_session.change_count,
            "working_content": edit_session.working_content,
            "diff": diff_data.get("diff", {}),
            "timestamp": datetime.now(timezone.utc).isoformat()
        }, websocket)


async def _resolve_edit_session_for_action(db, edit_session_id: str | None, chapter_id: int | None):
    manager = get_edit_session_manager(db)
    edit_session = None
    if edit_session_id:
        edit_session = await manager.get_edit_session_by_id(edit_session_id)
    if not edit_session and chapter_id:
        edit_session = await manager.get_edit_session(chapter_id)
    return manager, edit_session


async def _get_latest_pending_edit_session_id(db, chapter_id: int | None) -> str | None:
    if not chapter_id:
        return None
    manager = get_edit_session_manager(db)
    latest = await manager.get_edit_session(chapter_id)
    return latest.edit_session_id if latest else None


async def _handle_accept_edit(websocket, data, novel_id):
    edit_session_id = data.get("edit_session_id")
    chapter_id = data.get("chapter_id")
    async with AsyncSessionLocal() as db:
        manager, edit_session = await _resolve_edit_session_for_action(db, edit_session_id, chapter_id)
        if not edit_session:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "未找到可接受的编辑会话，可能已处理或章节当前没有待确认修改",
                "edit_session_id": edit_session_id,
                "chapter_id": chapter_id,
                "latest_pending_edit_session_id": await _get_latest_pending_edit_session_id(db, chapter_id),
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        try:
            result = await manager.accept_edit_session(edit_session.edit_session_id)
        except ValueError as e:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": str(e),
                "edit_session_id": edit_session.edit_session_id,
                "chapter_id": edit_session.chapter_id,
                "latest_pending_edit_session_id": await _get_latest_pending_edit_session_id(db, edit_session.chapter_id),
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        
        await ws_manager.send_personal_message({
            "type": "edit_accepted",
            "edit_session_id": edit_session.edit_session_id,
            "chapter_id": result["chapter_id"],
            "latest_pending_edit_session_id": await _get_latest_pending_edit_session_id(db, result["chapter_id"]),
            "change_count": result["change_count"],
            "word_count": result["word_count"],
            "already_processed": result.get("already_processed", False),
            "message": "编辑会话此前已被接受" if result.get("already_processed") else f"已接受 {result['change_count']} 处变更",
            "timestamp": datetime.now(timezone.utc).isoformat()
        }, websocket)


async def _handle_reject_edit(websocket, data, novel_id):
    edit_session_id = data.get("edit_session_id")
    chapter_id = data.get("chapter_id")
    async with AsyncSessionLocal() as db:
        manager, edit_session = await _resolve_edit_session_for_action(db, edit_session_id, chapter_id)
        if not edit_session:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "未找到可拒绝的编辑会话，可能已处理或章节当前没有待确认修改",
                "edit_session_id": edit_session_id,
                "chapter_id": chapter_id,
                "latest_pending_edit_session_id": await _get_latest_pending_edit_session_id(db, chapter_id),
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        try:
            result = await manager.reject_edit_session(edit_session.edit_session_id)
        except ValueError as e:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": str(e),
                "edit_session_id": edit_session.edit_session_id,
                "chapter_id": edit_session.chapter_id,
                "latest_pending_edit_session_id": await _get_latest_pending_edit_session_id(db, edit_session.chapter_id),
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
            return
        
        await ws_manager.send_personal_message({
            "type": "edit_rejected",
            "edit_session_id": edit_session.edit_session_id,
            "chapter_id": result["chapter_id"],
            "latest_pending_edit_session_id": await _get_latest_pending_edit_session_id(db, result["chapter_id"]),
            "already_processed": result.get("already_processed", False),
            "message": "编辑会话此前已被拒绝" if result.get("already_processed") else "已拒绝所有变更，回退到原版本",
            "timestamp": datetime.now(timezone.utc).isoformat()
        }, websocket)


async def _handle_end_session(websocket, session, active_tasks, task_flags, user_id, novel_id):
    """终止当前会话，取消所有任务"""
    cancelled_tasks = []
    
    for task_id, task in list(active_tasks.items()):
        task_flags[task_id] = False
        task.cancel()
        cancelled_tasks.append(task_id)
    
    active_tasks.clear()
    task_flags.clear()
    
    if session:
        await session_manager.delete_session(session.session_id)
    
    await ws_manager.send_personal_message({
        "type": "session_ended",
        "session_id": session.session_id if session else None,
        "cancelled_tasks": cancelled_tasks,
        "message": "会话已终止，所有任务已取消",
        "timestamp": datetime.now(timezone.utc).isoformat()
    }, websocket)
    
    logger.info(f"Session ended: user={user_id}, novel={novel_id}, cancelled {len(cancelled_tasks)} tasks")


async def _run_chat_with_tools(
    task_id: str,
    session: Session,
    user_message: str,
    tools_enabled: bool,
    novel_id: int,
    websocket: WebSocket,
    task_flags: dict[str, bool]
):
    """执行支持工具调用的对话 - 优化版：支持前缀缓存"""
    try:
        logger.info(f"Starting chat task {task_id}, mode={session.edit_mode}")
        
        edit_mode = EditMode.AGENT
        
        session_manager.add_message(session, MessageRole.USER, user_message)
        
        async with AsyncSessionLocal() as db:
            registry = get_mcp_registry()
            
            extra_context_for_user = ""
            creative_profile_text = ""
            conditional_reminders = []
            
            try:
                if session_manager.compressor.should_compress(session):
                    if session_manager.config.enable_auto_summary:
                        session = await session_manager.compressor.compress_with_llm(session)
                    else:
                        session = session_manager.compressor.compress(session)
                    # 压缩后清除快照，下次重新生成
                    session.metadata.pop("novel_context_snapshot", None)
                
                context_builder = ContextBuilder(db, novel_id)
                retrieved = await context_builder.search_relevant_context(query=user_message, top_k=5)
                if retrieved:
                    formatted = "\n".join(
                        f"- {item.get('content','')[:200]}"
                        for item in retrieved
                    )
                    extra_context_for_user = f"\n\n【相关记忆检索】\n{formatted}"
                
                profile_result = await db.execute(
                    select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == novel_id)
                )
                creative_profile = profile_result.scalar_one_or_none()
                if creative_profile:
                    formatted_profile = _format_creative_profile_for_prompt(creative_profile)
                    if formatted_profile:
                        creative_profile_text = f"【当前已沉淀的作者长期创作配置】\n{formatted_profile}"
                
                if not _looks_like_authoring_intent(user_message):
                    conditional_reminders.append(
                        "用户这次没有明确的创作或编辑意图，更像是在确认、反馈或简单交流。"
                        "请正常对话回应，不要主动创建章节、生成正文或修改正文，除非用户随后明确提出产出内容的请求。"
                    )
                
                if _looks_like_long_term_rule(user_message):
                    conditional_reminders.append(
                        "用户这次很可能在表达长期创作规则或全局偏好。"
                        "如果这些要求不是只针对当前这一章，而是希望后续持续生效，"
                        "请优先先读取 get_creative_profile，再用 update_creative_profile 做增量沉淀。"
                    )
            except Exception as exc:
                logger.warning(f"Context preparation failed for session {session.session_id}: {exc}")
                extra_context_for_user = ""
                creative_profile_text = ""
                conditional_reminders = []
                await ws_manager.send_personal_message({
                    "type": "system_warning",
                    "task_id": task_id,
                    "message": "记忆检索或创作配置加载暂时不可用，生成质量可能受影响",
                    "timestamp": datetime.now(timezone.utc).isoformat()
                }, websocket)
            
            # --- 构建/获取小说上下文快照（system2）---
            novel_context_snapshot = session.metadata.get("novel_context_snapshot")
            if not novel_context_snapshot:
                try:
                    novel_context_snapshot = await _build_novel_context_snapshot(db, novel_id)
                    if novel_context_snapshot:
                        session.metadata["novel_context_snapshot"] = novel_context_snapshot
                except Exception as exc:
                    logger.warning(f"Failed to build novel context snapshot: {exc}")
                    novel_context_snapshot = ""

            all_tools = registry.get_openai_functions() if tools_enabled else None
            if all_tools:
                selected_tool_names = EditModeConfig.get_llm_tools_for_message(edit_mode, user_message)
                order_map = {name: idx for idx, name in enumerate(selected_tool_names)}
                tools = [
                    tool for tool in all_tools
                    if tool.get("function", {}).get("name") in order_map
                ]
                tools.sort(key=lambda tool: order_map.get(tool.get("function", {}).get("name", ""), 999))
            else:
                tools = None
            
            # --- 构建消息结构 ---
            # system1：基础指令 + 创作偏好（永远不变）
            base_system_prompt = EditModeConfig.get_system_prompt(edit_mode)
            stable_sections = [base_system_prompt]
            if creative_profile_text:
                stable_sections.append(creative_profile_text)
            prefix_messages = [{
                "role": "system",
                "content": "\n\n".join(s for s in stable_sections if s).strip()
            }]
            # system2：小说上下文快照（对话开始时注入，压缩时才更新）
            if novel_context_snapshot:
                prefix_messages.append({
                    "role": "system",
                    "content": novel_context_snapshot,
                })

            # 动态内容（conditional_reminders + RAG）追加到用户消息末尾
            user_parts = [user_message]
            if extra_context_for_user:
                user_parts.append(extra_context_for_user)
            if conditional_reminders:
                reminder_text = "\n".join(
                    f"- {reminder}" for reminder in conditional_reminders
                )
                user_parts.append(f"【本轮额外提醒】\n{reminder_text}")
            enhanced_user_content = "\n\n".join(user_parts)

            history_messages = session_manager.get_messages_for_api(session, include_context=False)

            full_messages = (
                prefix_messages +
                history_messages +
                [{"role": "user", "content": enhanced_user_content}]
            )
            
            full_response = ""
            response_buffer = ""
            thinking_buffer = ""
            is_thinking = False
            loop_count = 0
            
            tool_cache: dict[str, dict[str, Any]] = {}
            disabled_tools: set[str] = set()
            failed_tool_keys: dict[str, int] = {}
            pending_injects: dict[str, list[dict[str, Any]]] = {}
            max_tool_retries = 3
            recent_tool_patterns: list[str] = []
            max_tool_loops = 50
            max_context_tokens = session_manager.config.max_tokens
            READ_ONLY_TOOLS = {
                "search_story_memory", "get_novel_info",
                "get_chapter_list", "get_chapter_content", "get_characters",
                "get_timeline", "run_review",
                "get_locations"
            }
            while loop_count < max_tool_loops:
                tool_outputs: list[dict[str, Any]] = []
                if tools:
                    tools = [t for t in tools if t["function"]["name"] not in disabled_tools]
                async for event in llm_service.chat_stream_with_tools(
                    messages=full_messages,
                    model=session.model,
                    tools=tools,
                    system_prompt=None,
                    reasoning_effort=session.metadata.get("reasoning_effort"),
                ):
                    if not task_flags.get(task_id):
                        logger.info(f"Task {task_id} cancelled")
                        partial = response_buffer.strip() or full_response.strip()
                        if partial:
                            session_manager.add_message(session, MessageRole.ASSISTANT, partial, metadata={"cancelled": True})
                        return
                    
                    if event["type"] == "thinking":
                        thinking_content = event.get("content", "")
                        if not is_thinking and thinking_content:
                            is_thinking = True
                        thinking_buffer += thinking_content
                        await ws_manager.send_personal_message({
                            "type": "thinking_chunk",
                            "task_id": task_id,
                            "chunk": thinking_content,
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)

                    elif event["type"] == "content":
                        if is_thinking:
                            is_thinking = False
                            await ws_manager.send_personal_message({
                                "type": "thinking_done",
                                "task_id": task_id,
                                "timestamp": datetime.now(timezone.utc).isoformat()
                            }, websocket)
                        chunk = event["content"]
                        full_response += chunk
                        response_buffer += chunk
                        
                        await ws_manager.send_personal_message({
                            "type": "content_chunk",
                            "task_id": task_id,
                            "chunk": chunk,
                            "accumulated_length": len(full_response),
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)
                    
                    elif event["type"] == "tool_call_start":
                        if is_thinking:
                            is_thinking = False
                            await ws_manager.send_personal_message({
                                "type": "thinking_done",
                                "task_id": task_id,
                                "timestamp": datetime.now(timezone.utc).isoformat()
                            }, websocket)

                        tool_name = event.get("tool_name", "unknown")
                        tool_id = event.get("tool_id")
                        
                        logger.info(f"Tool call started: {tool_name}")
                        if not tool_name:
                            continue
                        
                        if not EditModeConfig.can_use_tool(edit_mode, tool_name):
                            logger.warning(f"Tool {tool_name} not allowed in mode {edit_mode.value}")
                            await ws_manager.send_personal_message({
                                "type": "tool_call",
                                "task_id": task_id,
                                "tool_name": tool_name,
                                "status": "rejected",
                                "error": f"当前模式({edit_mode.value})不允许使用此工具",
                                "timestamp": datetime.now(timezone.utc).isoformat()
                            }, websocket)
                            continue
                        
                        await ws_manager.send_personal_message({
                            "type": "tool_call",
                            "task_id": task_id,
                            "tool_name": tool_name,
                            "tool_id": tool_id,
                            "status": "executing",
                            "phase": "selected",
                            "display_text": f"正在{_sync_tool_display_name(tool_name)}",
                            "activity_kind": _TOOL_SYNC_KINDS.get(tool_name, "general"),
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)

                    elif event["type"] == "tool_call_arguments":
                        tool_name = event.get("tool_name", "unknown")
                        if tool_name != "edit_chapter":
                            continue
                        arguments_text = event.get("arguments_text", "")
                        partial_content = _extract_partial_argument_string(arguments_text, "new_content")
                        if partial_content is None:
                            continue
                        chapter_id = session.current_chapter_id
                        edit_session_id = _extract_partial_argument_string(arguments_text, "edit_session_id")
                        await ws_manager.send_personal_message({
                            "type": "edit_stream",
                            "task_id": task_id,
                            "tool_name": tool_name,
                            "chapter_id": chapter_id,
                            "edit_session_id": edit_session_id,
                            "working_content": partial_content,
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)
                    
                    elif event["type"] == "tool_call_end":
                        tool_name = event.get("tool_name", "unknown")
                        tool_id = event.get("tool_id")
                        arguments = event.get("arguments", {})
                        
                        logger.info(f"Tool call end: {tool_name}, args: {arguments}")
                        if not tool_name:
                            continue
                        
                        if not EditModeConfig.can_use_tool(edit_mode, tool_name):
                            logger.warning(f"Tool {tool_name} not allowed in mode {edit_mode.value}")
                            result_payload = {"success": False, "error": f"当前模式({edit_mode.value})不允许使用此工具"}
                            presentation = await _build_tool_call_presentation(db, novel_id, tool_name, arguments, result_payload, status="failed")
                            await ws_manager.send_personal_message({
                                "type": "tool_call",
                                "task_id": task_id,
                                "tool_name": tool_name,
                                "tool_id": tool_id,
                                "status": "failed",
                                **presentation,
                                "error": result_payload["error"],
                                "timestamp": datetime.now(timezone.utc).isoformat()
                            }, websocket)
                            tool_outputs.append({
                                "tool": tool_name,
                                "tool_id": tool_id,
                                "arguments": arguments,
                                "result": result_payload,
                                "display_text": presentation.get("display_text"),
                                "activity_kind": presentation.get("activity_kind"),
                            })
                            continue
                        
                        if tools_enabled and tool_name:
                            raw_args = {k: v for k, v in arguments.items() if k not in ('session_id', 'novel_id')}
                            # 归一化：LLM 偶尔传字符串 "true"/"false"
                            clean_args = {
                                k: True if v == "true" else False if v == "false" else v
                                for k, v in raw_args.items()
                            }
                            
                            if session.current_chapter_id and 'chapter_id' not in arguments:
                                clean_args['chapter_id'] = session.current_chapter_id
                            elif tool_name in {"edit_chapter"} and 'chapter_id' not in arguments:
                                result = await db.execute(
                                    select(Chapter)
                                    .where(Chapter.novel_id == novel_id)
                                    .order_by(Chapter.chapter_number.desc())
                                    .limit(1)
                                )
                                chapter = result.scalar_one_or_none()
                                if chapter:
                                    clean_args["chapter_id"] = chapter.id
                            
                            cache_key = f"{tool_name}:{json.dumps(clean_args, ensure_ascii=False, sort_keys=True)}"
                            presentation = await _build_tool_call_presentation(db, novel_id, tool_name, clean_args)
                            await ws_manager.send_personal_message({
                                "type": "tool_call",
                                "task_id": task_id,
                                "tool_name": tool_name,
                                "tool_id": tool_id,
                                "status": "executing",
                                "phase": "executing",
                                "arguments": clean_args,
                                **presentation,
                                "timestamp": datetime.now(timezone.utc).isoformat()
                            }, websocket)
                            cached = tool_cache.get(cache_key)
                            if cached:
                                tool_result_payload = cached
                            else:
                                async with AsyncSessionLocal() as tool_db:
                                    tool_result = await registry.execute(
                                        tool_name,
                                        db=tool_db,
                                        user_id=session.user_id,
                                        session_id=session.session_id,
                                        novel_id=novel_id,
                                        websocket=websocket,
                                        chat_session=session,
                                        tool_id=tool_id,
                                        **clean_args
                                    )
                                    if tool_result.inject:
                                        pending_injects[tool_id] = tool_result.inject
                                    tool_result_payload = tool_result.model_dump(exclude={"inject"})
                                if tool_result_payload.get("success"):
                                    tool_cache[cache_key] = tool_result_payload
                            
                            logger.info(f"Tool result payload: {tool_result_payload}")
                            
                            metadata = tool_result_payload.get("metadata") or {}
                            data_payload = tool_result_payload.get("data") or {}
                            presentation = await _build_tool_call_presentation(
                                db,
                                novel_id,
                                tool_name,
                                clean_args,
                                tool_result_payload,
                                status="completed" if tool_result_payload.get("success") else "failed"
                            )

                            await ws_manager.send_personal_message({
                                "type": "tool_call",
                                "task_id": task_id,
                                "tool_name": tool_name,
                                "status": "completed" if tool_result_payload.get("success") else "failed",
                                "tool_id": tool_id,
                                "phase": "completed" if tool_result_payload.get("success") else "failed",
                                "arguments": clean_args,
                                "result_summary": {
                                    "success": tool_result_payload.get("success"),
                                    "error": _sanitize_tool_error(tool_result_payload.get("error")),
                                    "metadata": metadata,
                                    "data_keys": list(data_payload.keys()) if isinstance(data_payload, dict) else [],
                                },
                                **presentation,
                                "error": _sanitize_tool_error(tool_result_payload.get("error")),
                                "timestamp": datetime.now(timezone.utc).isoformat()
                            }, websocket)
                            
                            tool_outputs.append({
                                "tool": tool_name,
                                "tool_id": tool_id,
                                "arguments": clean_args,
                                "result": tool_result_payload,
                                "display_text": presentation.get("display_text"),
                                "activity_kind": presentation.get("activity_kind"),
                            })
                            
                            if not tool_result_payload.get("success"):
                                failed_tool_keys[cache_key] = failed_tool_keys.get(cache_key, 0) + 1
                                error_message = tool_result_payload.get("error", "未知错误")
                                session_manager.add_message(
                                    session,
                                    MessageRole.SYSTEM,
                                    f"工具 {tool_name} 失败：{error_message}。请修正参数后重试。"
                                )
                                if failed_tool_keys[cache_key] >= max_tool_retries:
                                    disabled_tools.add(tool_name)
                                    session_manager.add_message(
                                        session,
                                        MessageRole.SYSTEM,
                                        f"工具 {tool_name} 连续失败 {max_tool_retries} 次，暂停调用。请改用其他工具或继续正文。"
                                    )
                            else:
                                if cache_key in failed_tool_keys:
                                    failed_tool_keys.pop(cache_key, None)
                                if tool_name == "update_creative_profile":
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith("get_creative_profile:")
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)
                                    summary_parts = []
                                    if data_payload.get("author_intent"):
                                        summary_parts.append(f"作者意图：{data_payload['author_intent']}")
                                    if data_payload.get("must_keep"):
                                        summary_parts.append("已补充长期保留规则")
                                    if data_payload.get("must_avoid"):
                                        summary_parts.append("已补充长期避免规则")
                                    session_manager.add_message(
                                        session,
                                        MessageRole.SYSTEM,
                                        "已更新作者长期创作配置。" + (f" {'；'.join(summary_parts)}。" if summary_parts else "")
                                    )
                                
                                if tool_name in ("add_timeline_entry", "update_timeline_entry"):
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith(("get_timeline:", "run_review:"))
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name in ("create_character", "update_character"):
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith("get_characters:")
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name == "update_character_relationship":
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith("get_characters:")
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name == "create_location":
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith("get_locations:")
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name in ("update_location", "delete_location"):
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith("get_locations:")
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name == "create_new_chapter":
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith(("get_chapter_list:", "get_novel_info:"))
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)
                            
                            if tool_name == "edit_chapter" and tool_result_payload.get("success"):
                                await ws_manager.send_personal_message({
                                    "type": "edit_pending",
                                    "task_id": task_id,
                                    "edit_session_id": metadata.get("edit_session_id"),
                                    "chapter_id": data_payload.get("chapter_id") or clean_args.get("chapter_id"),
                                    "change_count": data_payload.get("change_count", 0),
                                    "timestamp": datetime.now(timezone.utc).isoformat()
                                }, websocket)

                            if tool_result_payload.get("success") and data_payload.get("working_content") is not None:
                                await ws_manager.send_personal_message({
                                    "type": "edit_preview",
                                    "task_id": task_id,
                                    "tool_name": tool_name,
                                    "chapter_id": data_payload.get("chapter_id") or clean_args.get("chapter_id"),
                                    "edit_session_id": data_payload.get("edit_session_id") or metadata.get("edit_session_id"),
                                    "working_content": data_payload.get("working_content"),
                                    "change_count": data_payload.get("change_count", 0),
                                    "diff": data_payload.get("diff", {}),
                                    "timestamp": datetime.now(timezone.utc).isoformat()
                                }, websocket)
                            edit_session_id = metadata.get("edit_session_id")
                            if edit_session_id:
                                await ws_manager.send_personal_message({
                                    "type": "edit_pending",
                                    "task_id": task_id,
                                    "edit_session_id": edit_session_id,
                                    "change_count": (tool_result_payload.get("data") or {}).get("change_count", 0),
                                    "timestamp": datetime.now(timezone.utc).isoformat()
                                }, websocket)
                
                if tool_outputs and tools_enabled:
                    # 合并本轮所有工具调用为一条 assistant 消息（DeepSeek 要求）
                    # content 为模型在工具调用前输出的文本，reasoning_content 为思考内容
                    combined_tool_calls = []
                    for item in tool_outputs:
                        combined_tool_calls.append({
                            "id": item.get("tool_id") or f"call_{item['tool']}",
                            "type": "function",
                            "function": {
                                "name": item["tool"],
                                "arguments": json.dumps(item.get("arguments", {}), ensure_ascii=False)
                            },
                            "display_text": item.get("display_text"),
                            "activity_kind": item.get("activity_kind"),
                        })
                    tool_meta: dict[str, Any] = {"tool_calls": combined_tool_calls}
                    tool_meta["thinking_content"] = thinking_buffer
                    thinking_buffer = ""
                    tool_call_content = response_buffer.strip()
                    response_buffer = ""
                    full_response = ""
                    session_manager.add_message(
                        session,
                        MessageRole.ASSISTANT,
                        tool_call_content,
                        metadata=tool_meta
                    )
                    for item in tool_outputs:
                        session_manager.add_message(
                            session,
                            MessageRole.TOOL,
                            json.dumps(item["result"], ensure_ascii=False),
                            metadata={
                                "tool_call_id": item.get("tool_id") or f"call_{item['tool']}",
                                "tool_name": item["tool"],
                                "display_text": item.get("display_text"),
                                "activity_kind": item.get("activity_kind"),
                            }
                        )
                        # 工具返回了 inject 消息列表，需要注入到 session
                        inject_msgs = pending_injects.pop(item.get("tool_id", ""), None)
                        if inject_msgs and isinstance(inject_msgs, list):
                            for inj in inject_msgs:
                                role = inj.get("role", "user")
                                content = inj.get("content", "")
                                meta = {k: v for k, v in inj.items() if k not in ("role", "content")}
                                session_manager.add_message(
                                    session,
                                    MessageRole(role),
                                    content,
                                    metadata=meta or None,
                                )
                    history_messages = session_manager.get_messages_for_api(session, include_context=False)
                    full_messages = (
                        prefix_messages +
                        history_messages
                    )
                    
                    for i, m in enumerate(full_messages):
                        if m.get("role") == "assistant" and m.get("tool_calls"):
                            has_rc = "reasoning_content" in m
                            rc_len = len(m.get("reasoning_content", "")) if has_rc else -1
                            logger.info(
                                f"Loop {loop_count + 1} msg[{i}]: assistant+tool_calls, "
                                f"reasoning_content={'present(' + str(rc_len) + ' chars)' if has_rc else 'MISSING'}"
                            )
                    
                    estimated_tokens = sum(
                        session_manager.compressor.estimate_tokens(m.get("content", ""))
                        for m in full_messages
                        if m.get("content")
                    )
                    if estimated_tokens > max_context_tokens:
                        logger.warning(
                            f"Token budget exceeded at loop {loop_count + 1}: "
                            f"{estimated_tokens} > {max_context_tokens}, forcing stop"
                        )
                        session_manager.add_message(
                            session,
                            MessageRole.SYSTEM,
                            "上下文长度已接近模型限制，请基于已有信息直接输出结论，不要再调用工具。"
                        )
                        history_messages = session_manager.get_messages_for_api(session, include_context=False)
                        full_messages = prefix_messages + history_messages
                    
                    logger.info(
                        f"Loop {loop_count + 1}: rebuilt full_messages with "
                        f"{len(full_messages)} messages, ~{estimated_tokens} tokens"
                    )
                    
                    current_pattern = "|".join(sorted(f"{item['tool']}:{json.dumps(item.get('arguments', {}), ensure_ascii=False, sort_keys=True)[:100]}" for item in tool_outputs))
                    recent_tool_patterns.append(current_pattern)
                    if len(recent_tool_patterns) > 6:
                        recent_tool_patterns.pop(0)
                    
                    is_read_only_round = all(item["tool"] in READ_ONLY_TOOLS for item in tool_outputs)
                    has_repetitive_pattern = len(recent_tool_patterns) >= 4 and len(set(recent_tool_patterns[-4:])) <= 2
                    is_stuck_in_loop = is_read_only_round and has_repetitive_pattern and loop_count >= 4
                    
                    if is_stuck_in_loop:
                        logger.warning(
                            f"Detected potential infinite loop: {len(recent_tool_patterns)} rounds, "
                            f"read_only={is_read_only_round}, repetitive={has_repetitive_pattern}, "
                            f"pattern={current_pattern[:200]}"
                        )
                        await ws_manager.send_personal_message({
                            "type": "tool_call",
                            "task_id": task_id,
                            "status": "loop_detected",
                            "message": "检测到重复的工具调用模式，已自动停止。请基于已有信息继续创作或提出新的指令。",
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)
                        session_manager.add_message(
                            session,
                            MessageRole.SYSTEM,
                            "系统检测到你可能陷入了重复查询。请基于已获取的信息直接开始写作，或者明确告诉我你需要什么新的操作。"
                        )
                        break
                    
                    loop_count += 1
                    continue
                
                if is_thinking:
                    await ws_manager.send_personal_message({
                        "type": "thinking_done",
                        "task_id": task_id,
                        "timestamp": datetime.now(timezone.utc).isoformat()
                    }, websocket)
                break
            
            if response_buffer.strip():
                final_meta: dict[str, Any] = {}
                if thinking_buffer:
                    final_meta["thinking_content"] = thinking_buffer
                session_manager.add_message(session, MessageRole.ASSISTANT, response_buffer, metadata=final_meta or None)
            elif full_response.strip():
                final_meta = {}
                if thinking_buffer:
                    final_meta["thinking_content"] = thinking_buffer
                session_manager.add_message(session, MessageRole.ASSISTANT, full_response, metadata=final_meta or None)
            
            logger.info(f"Chat task {task_id} completed")
            
            await ws_manager.send_personal_message({
                "type": "chat_completed",
                "task_id": task_id,
                "session_id": session.session_id,
                "message_count": session.get_message_count(),
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
    
    except asyncio.CancelledError:
        logger.info(f"Chat task {task_id} was cancelled")
        partial = response_buffer.strip() or full_response.strip()
        if partial:
            session_manager.add_message(session, MessageRole.ASSISTANT, partial, metadata={"cancelled": True})
    except Exception as e:
        logger.error(f"Chat with tools failed: {e}", exc_info=True)
        await ws_manager.send_personal_message({
            "type": "chat_failed",
            "task_id": task_id,
            "error": _friendly_error_message(e),
            "timestamp": datetime.now(timezone.utc).isoformat()
        }, websocket)
    finally:
        try:
            await session_manager.save_session(session)
        except Exception:
            logger.warning(f"Failed to save session {session.session_id} in finally block")
        task_flags.pop(task_id, None)
