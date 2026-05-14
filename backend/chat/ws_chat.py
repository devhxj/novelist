"""
WebSocket路由 - AI IDE风格统一入口
整合所有功能：对话、生成、编辑、工具调用
"""
import logging
import asyncio
from datetime import datetime, timezone
from fastapi import APIRouter, WebSocket, WebSocketDisconnect, Query
from sqlalchemy import select
from typing import Any

from core.websocket import ws_manager
from core.database import AsyncSessionLocal
from core.agent_loop import (
    run_agent_loop,
    AgentLoopResult,
)
from mcp_tools.base import MCPToolResult
from core.auth import decode_token
from sessions.session_manager import (
    session_manager
)
from sessions.schema import MessageRole, Session
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
    _extract_partial_argument_string,
    _build_tool_call_presentation,
)


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
            if data.get("type") == "outline_approval" and current_session and current_session.session_id:
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
                        session_id = data.get("session_id")
                        if session_id:
                            current_session = await session_manager.load_session(session_id)
                            if current_session and current_session.user_id != user_id:
                                current_session = None
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
            m.model_dump(mode="json")
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

            main_agent_tools = EditModeConfig.get_main_agent_tools()
            tools = registry.get_openai_functions(allowed_names=list(main_agent_tools)) if tools_enabled else None
            
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
            

            # --- 设置工具调用回调和取消机制 ---

            cache_db_ref = db
            registry_for_handler = registry


            # 取消事件：同步 task_flags → asyncio.Event
            cancel_event = asyncio.Event()

            async def _sync_cancel():
                while not cancel_event.is_set():
                    if not task_flags.get(task_id):
                        cancel_event.set()
                        return
                    await asyncio.sleep(0.2)

            cancel_sync_task = asyncio.create_task(_sync_cancel())

            loop_result: AgentLoopResult | None = None

            try:
                # --- display_handler ---
                async def _display(
                    tool_name: str, arguments: dict[str, Any], status: str = "executing"
                ) -> tuple[str | None, str | None, dict[str, Any] | None]:
                    try:
                        async with AsyncSessionLocal() as disp_db:
                            pres = await _build_tool_call_presentation(
                                disp_db, novel_id, tool_name, arguments, status=status
                            )
                            return pres.get("display_text"), pres.get("activity_kind"), pres.get("metadata")
                    except Exception:
                        logger.warning(f"display_handler failed for {tool_name}", exc_info=True)
                        return None, None, None

                # --- 消息即时持久化回调 ---
                async def _on_message(msg: dict[str, Any]) -> None:
                    role = msg.get("role", "")
                    content = msg.get("content", "")
                    # 公共元数据：从消息里提取 source/parent_task_id 和 inject 额外字段
                    common_meta: dict[str, Any] = {
                        k: v for k, v in msg.items()
                        if k not in ("role", "content", "tool_calls", "reasoning_content", "tool_call_id")
                    }

                    if role == "assistant":
                        if msg.get("tool_calls"):
                            common_meta["tool_calls"] = msg["tool_calls"]
                        if msg.get("reasoning_content"):
                            common_meta["thinking_content"] = msg["reasoning_content"]
                        session_manager.add_message(
                            session, MessageRole.ASSISTANT, content, metadata=common_meta or None
                        )
                    elif role == "tool":
                        common_meta["tool_call_id"] = msg.get("tool_call_id", "")
                        session_manager.add_message(
                            session, MessageRole.TOOL, content,
                            metadata=common_meta
                        )
                    elif role == "user":
                        session_manager.add_message(
                            session, MessageRole.USER, content,
                            metadata=common_meta or None
                        )
                    elif role == "system":
                        session_manager.add_message(
                            session, MessageRole.SYSTEM, content,
                            metadata=common_meta or None
                        )

                # --- tool_call_handler ---
                async def _handle_tool(
                    tool_name: str, tool_id: str, arguments: dict[str, Any]
                ) -> MCPToolResult:

                    # 参数清洗
                    raw_args = {k: v for k, v in arguments.items() if k not in ('session_id', 'novel_id')}
                    clean_args: dict[str, Any] = {
                        k: True if v == "true" else False if v == "false" else v
                        for k, v in raw_args.items()
                    }

                    # 自动补 chapter_id
                    if session.current_chapter_id and 'chapter_id' not in arguments:
                        clean_args['chapter_id'] = session.current_chapter_id
                    elif tool_name in {"edit_chapter"} and 'chapter_id' not in arguments:
                        ch_result = await cache_db_ref.execute(
                            select(Chapter)
                            .where(Chapter.novel_id == novel_id)
                            .order_by(Chapter.chapter_number.desc())
                            .limit(1)
                        )
                        chapter = ch_result.scalar_one_or_none()
                        if chapter:
                            clean_args["chapter_id"] = chapter.id

                    async with AsyncSessionLocal() as tool_db:
                        tool_result = await registry_for_handler.execute(
                            tool_name,
                            db=tool_db,
                            user_id=session.user_id,
                            session_id=session.session_id,
                            novel_id=novel_id,
                            allowed_tools=main_agent_tools,
                            websocket=websocket,
                            chat_session=session,
                            tool_id=tool_id,
                            on_message=_on_message,
                            display=_display,
                            parent_task_id=task_id,
                            cancel_event=cancel_event,
                            **clean_args
                        )

                    # edit_chapter 成功后推送 edit_pending 和 edit_preview
                    if tool_name == "edit_chapter" and tool_result.success:
                        result_data = (tool_result.data or {}) if isinstance(tool_result.data, dict) else {}
                        edit_session_id = result_data.get("edit_session_id")
                        if edit_session_id:
                            await ws_manager.send_personal_message({
                                "type": "edit_pending",
                                "task_id": task_id,
                                "tool_id": tool_id,
                                "tool_name": tool_name,
                                "edit_session_id": edit_session_id,
                                "chapter_id": clean_args.get("chapter_id"),
                                "timestamp": datetime.now(timezone.utc).isoformat()
                            }, websocket)
                            new_content = clean_args.get("new_content") or result_data.get("new_content")
                            if new_content:
                                await ws_manager.send_personal_message({
                                    "type": "edit_preview",
                                    "task_id": task_id,
                                    "tool_id": tool_id,
                                    "tool_name": tool_name,
                                    "edit_session_id": edit_session_id,
                                    "chapter_id": clean_args.get("chapter_id"),
                                    "new_content": new_content,
                                    "timestamp": datetime.now(timezone.utc).isoformat()
                                }, websocket)

                    return tool_result

                # --- on_args_stream（edit_chapter 实时预览） ---
                async def _on_args(tool_name: str, tool_id: str, arguments_text: str):
                    if tool_name != "edit_chapter":
                        return
                    partial_content = _extract_partial_argument_string(arguments_text, "new_content")
                    if partial_content is None:
                        return
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

                # --- usage 持久化回调 ---
                async def _on_usage(usage: dict[str, Any], detail: dict[str, int]) -> None:
                    session.last_usage = {**usage, "detail": detail}
                    await session_storage.save(session)

                # --- 执行 Agent 循环 ---
                loop_result = await run_agent_loop(
                    messages=full_messages,
                    tools=tools or [],
                    websocket=websocket,
                    task_id=task_id,
                    parent_task_id=None,
                    cancel_event=cancel_event,
                    tool_call_handler=_handle_tool,
                    display_handler=_display,
                    on_args_stream=_on_args,
                    on_message=_on_message,
                    on_usage=_on_usage,
                    model=session.model,
                    reasoning_effort=session.metadata.get("reasoning_effort"),
                    max_turns=50,
                    max_context_tokens=session_manager.config.max_tokens,
                )

            finally:
                cancel_sync_task.cancel()
                try:
                    await cancel_sync_task
                except asyncio.CancelledError:
                    pass

            # --- 持久化最终回复 ---
            if loop_result and loop_result.final_text:
                session_manager.add_message(
                    session, MessageRole.ASSISTANT, loop_result.final_text
                )

            # --- 推送完成事件 ---
            await ws_manager.send_personal_message({
                "type": "chat_completed",
                "task_id": task_id,
                "session_id": session.session_id,
                "message_count": session.get_message_count(),
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)

    except asyncio.CancelledError:
        logger.info(f"Chat task {task_id} was cancelled")
        final_text = loop_result.final_text if loop_result else ""
        if final_text:
            session_manager.add_message(
                session, MessageRole.ASSISTANT, final_text, metadata={"cancelled": True}
            )
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
