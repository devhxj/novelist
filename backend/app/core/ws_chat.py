"""
WebSocket路由 - AI IDE风格统一入口
整合所有功能：对话、生成、编辑、工具调用
"""
import logging
import asyncio
import json
from datetime import datetime
from fastapi import APIRouter, WebSocket, WebSocketDisconnect, Query
from sqlalchemy import select, func
from typing import Optional, Dict, Any, List

from app.core.websocket import ws_manager, GenerationProgress
from app.core.database import AsyncSessionLocal
from app.core.auth import decode_token
from app.core.llm_service import llm_service
from app.core.session_manager import (
    Session, SessionManager, SessionConfig, MessageRole,
    SessionScope, ScopeType, NovelContext, ChapterContext,
    session_manager
)
from app.core.session_storage import session_storage
from app.core.context_builder import ContextBuilder
from app.core.edit_mode import EditMode, EditModeConfig
from app.core.prompt_templates import (
    get_system_prompt,
    build_chapter_prompt,
    build_dialogue_prompt,
    build_description_prompt,
    build_outline_prompt,
    build_summary_prompt,
    build_character_profile_prompt,
    GenerationType
)
from app.chapters.models import Chapter
from app.novels.models import Novel
from app.editor.service import get_edit_session_manager

router = APIRouter(tags=["websocket"])
logger = logging.getLogger(__name__)

session_manager.set_storage(session_storage)


async def get_user_from_token(token: str) -> Optional[int]:
    try:
        payload = decode_token(token)
        if payload and payload.get("sub"):
            return int(payload["sub"])
    except Exception:
        pass
    return None


def get_mcp_registry(db):
    from app.mcp.base import MCPToolRegistry
    from app.mcp.novel_tools import NovelManagementTools
    from app.mcp.memory_tools import MemoryRetrievalTools
    from app.mcp.consistency_tools import ConsistencyCheckTools
    from app.mcp.editing_tools import EditingTools
    
    registry = MCPToolRegistry()
    NovelManagementTools.register_all(db, registry)
    MemoryRetrievalTools.register_all(db, registry)
    ConsistencyCheckTools.register_all(db, registry)
    EditingTools.register_all(db, registry)
    return registry


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
    
    active_tasks: Dict[str, asyncio.Task] = {}
    task_flags: Dict[str, bool] = {}
    current_session: Optional[Session] = None
    
    logger.info(f"WebSocket connected: user={user_id}, novel={novel_id}")
    
    try:
        while True:
            data = await websocket.receive_json()
            message_type = data.get("type")
            
            logger.debug(f"Received message type: {message_type}")
            
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
            
            elif message_type == "change_scope":
                if current_session:
                    await _handle_change_scope(websocket, current_session, data, novel_id)
            
            elif message_type == "chat":
                if not current_session:
                    current_session = session_manager.create_session(
                        user_id=user_id,
                        novel_id=novel_id,
                        scope=SessionScope(type=ScopeType.NOVEL)
                    )
                    await session_manager.save_session(current_session)
                
                task_id = f"chat_{current_session.session_id}_{datetime.now().strftime('%H%M%S')}"
                task_flags[task_id] = True
                
                await ws_manager.send_personal_message({
                    "type": "chat_started",
                    "task_id": task_id,
                    "session_id": current_session.session_id,
                    "timestamp": datetime.now().isoformat()
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
                task_id = f"gen_{novel_id}_{data.get('generation_type', 'chapter')}_{datetime.now().strftime('%H%M%S')}"
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
                        "timestamp": datetime.now().isoformat()
                    }, websocket)
            
            elif message_type == "read_chapter":
                await _handle_read_chapter(websocket, data.get("chapter_id"), novel_id)
            
            elif message_type == "start_edit":
                await _handle_start_edit(websocket, data, novel_id, current_session)
            
            elif message_type == "apply_edit":
                await _handle_apply_edit(websocket, data, novel_id)
            
            elif message_type == "accept_edit":
                await _handle_accept_edit(websocket, data.get("edit_session_id"), novel_id)
            
            elif message_type == "reject_edit":
                await _handle_reject_edit(websocket, data.get("edit_session_id"), novel_id)
            
            elif message_type == "end_session":
                await _handle_end_session(
                    websocket, current_session, active_tasks, task_flags, user_id, novel_id
                )
                current_session = None
    
    except WebSocketDisconnect:
        logger.info(f"Chat WebSocket disconnected: user={user_id}, novel={novel_id}")
        for task_id in task_flags:
            task_flags[task_id] = False
        ws_manager.disconnect(websocket, user_id, novel_id)
    except Exception as e:
        logger.error(f"Chat WebSocket error: {e}", exc_info=True)
        for task_id in task_flags:
            task_flags[task_id] = False
        ws_manager.disconnect(websocket, user_id, novel_id)


async def _handle_create_session(websocket, data, user_id, novel_id):
    scope_data = data.get("scope", {})
    scope = SessionScope(
        type=ScopeType(scope_data.get("type", "novel")),
        chapter_start=scope_data.get("chapter_start"),
        chapter_end=scope_data.get("chapter_end")
    )
    model = data.get("model", "deepseek-chat")
    edit_mode = data.get("edit_mode", "agent")
    
    async with AsyncSessionLocal() as db:
        novel_context = await _build_novel_context(db, novel_id)
        chapter_context = None
        current_chapter_id = None
        if scope.type == ScopeType.CHAPTER and scope.chapter_start:
            chapter_context = await _build_chapter_context(db, novel_id, scope.chapter_start)
            result = await db.execute(
                select(Chapter).where(
                    Chapter.novel_id == novel_id,
                    Chapter.chapter_number == scope.chapter_start
                )
            )
            chapter = result.scalar_one_or_none()
            if chapter:
                current_chapter_id = chapter.id
    
    session = session_manager.create_session(
        user_id=user_id,
        novel_id=novel_id,
        scope=scope,
        novel_context=novel_context,
        chapter_context=chapter_context,
        model=model
    )
    session.edit_mode = edit_mode
    session.current_chapter_id = current_chapter_id
    await session_manager.save_session(session)
    
    await ws_manager.send_personal_message({
        "type": "session_created",
        "session_id": session.session_id,
        "scope": scope.to_dict(),
        "display_name": scope.get_display_name(),
        "model": model,
        "edit_mode": edit_mode,
        "current_chapter_id": current_chapter_id,
        "timestamp": datetime.now().isoformat()
    }, websocket)
    
    return session


async def _handle_load_session(websocket, data, user_id):
    session_id = data.get("session_id")
    session = await session_manager.load_session(session_id)
    
    if not session:
        await ws_manager.send_personal_message({
            "type": "error",
            "error": "会话不存在",
            "timestamp": datetime.now().isoformat()
        }, websocket)
        return None
    
    if session.user_id != user_id:
        await ws_manager.send_personal_message({
            "type": "error",
            "error": "无权访问此会话",
            "timestamp": datetime.now().isoformat()
        }, websocket)
        return None
    
    await ws_manager.send_personal_message({
        "type": "session_loaded",
        "session_id": session.session_id,
        "scope": session.scope.to_dict(),
        "display_name": session.get_display_name(),
        "message_count": session.get_message_count(),
        "recent_messages": [m.to_dict() for m in session.messages[-10:]],
        "timestamp": datetime.now().isoformat()
    }, websocket)
    
    return session


async def _handle_list_sessions(websocket, user_id, novel_id, data):
    scope_type = data.get("scope_type")
    scope_enum = ScopeType(scope_type) if scope_type else None
    
    sessions = await session_manager.list_user_sessions(
        user_id=user_id,
        novel_id=novel_id,
        scope_type=scope_enum
    )
    
    await ws_manager.send_personal_message({
        "type": "sessions_list",
        "sessions": [
            {
                "session_id": s.session_id,
                "scope": s.scope.to_dict(),
                "display_name": s.get_display_name(),
                "title": s.title,
                "message_count": s.get_message_count(),
                "updated_at": s.updated_at.isoformat()
            }
            for s in sessions
        ],
        "timestamp": datetime.now().isoformat()
    }, websocket)


async def _handle_change_scope(websocket, session, data, novel_id):
    scope_data = data.get("scope", {})
    new_scope = SessionScope(
        type=ScopeType(scope_data.get("type", "novel")),
        chapter_start=scope_data.get("chapter_start"),
        chapter_end=scope_data.get("chapter_end")
    )
    
    session.scope = new_scope
    
    async with AsyncSessionLocal() as db:
        if new_scope.type == ScopeType.CHAPTER and new_scope.chapter_start:
            session.chapter_context = await _build_chapter_context(
                db, novel_id, new_scope.chapter_start
            )
        else:
            session.chapter_context = None
    
    await session_manager.save_session(session)
    
    await ws_manager.send_personal_message({
        "type": "scope_changed",
        "session_id": session.session_id,
        "scope": new_scope.to_dict(),
        "display_name": new_scope.get_display_name(),
        "timestamp": datetime.now().isoformat()
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
                "timestamp": datetime.now().isoformat()
            }, websocket)
            return
        
        if chapter.novel_id != novel_id:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "无权访问此章节",
                "timestamp": datetime.now().isoformat()
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
            "timestamp": datetime.now().isoformat()
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
                "timestamp": datetime.now().isoformat()
            }, websocket)
            return
        
        if chapter.novel_id != novel_id:
            await ws_manager.send_personal_message({
                "type": "error",
                "error": "无权编辑此章节",
                "timestamp": datetime.now().isoformat()
            }, websocket)
            return
        
        manager = get_edit_session_manager(db)
        edit_session = await manager.create_edit_session(chapter_id, ws_session_id)
        
        await ws_manager.send_personal_message({
            "type": "edit_started",
            "edit_session_id": edit_session.edit_session_id,
            "chapter_id": chapter_id,
            "original_content": edit_session.original_content,
            "working_content": edit_session.working_content,
            "change_count": 0,
            "timestamp": datetime.now().isoformat()
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
                "timestamp": datetime.now().isoformat()
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
            "change_count": edit_session.change_count,
            "working_content": edit_session.working_content,
            "diff": diff_data.get("diff", {}),
            "timestamp": datetime.now().isoformat()
        }, websocket)


async def _handle_accept_edit(websocket, edit_session_id, novel_id):
    async with AsyncSessionLocal() as db:
        manager = get_edit_session_manager(db)
        result = await manager.accept_edit_session(edit_session_id)
        
        await ws_manager.send_personal_message({
            "type": "edit_accepted",
            "edit_session_id": edit_session_id,
            "chapter_id": result["chapter_id"],
            "change_count": result["change_count"],
            "word_count": result["word_count"],
            "message": f"已接受 {result['change_count']} 处变更",
            "timestamp": datetime.now().isoformat()
        }, websocket)


async def _handle_reject_edit(websocket, edit_session_id, novel_id):
    async with AsyncSessionLocal() as db:
        manager = get_edit_session_manager(db)
        result = await manager.reject_edit_session(edit_session_id)
        
        await ws_manager.send_personal_message({
            "type": "edit_rejected",
            "edit_session_id": edit_session_id,
            "chapter_id": result["chapter_id"],
            "message": "已拒绝所有变更，回退到原版本",
            "timestamp": datetime.now().isoformat()
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
        "timestamp": datetime.now().isoformat()
    }, websocket)
    
    logger.info(f"Session ended: user={user_id}, novel={novel_id}, cancelled {len(cancelled_tasks)} tasks")


async def _run_chat_with_tools(
    task_id: str,
    session: Session,
    user_message: str,
    tools_enabled: bool,
    novel_id: int,
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """执行支持工具调用的对话"""
    try:
        logger.info(f"Starting chat task {task_id}, mode={session.edit_mode}")
        
        try:
            edit_mode = EditMode(session.edit_mode) if session.edit_mode else EditMode.AGENT
        except ValueError:
            edit_mode = EditMode.AGENT
            logger.warning(f"Invalid edit_mode: {session.edit_mode}, fallback to AGENT")
        
        session_manager.add_message(session, MessageRole.USER, user_message)
        
        async with AsyncSessionLocal() as db:
            registry = get_mcp_registry(db)
            all_tools = registry.get_openai_functions() if tools_enabled else None
            
            if all_tools and edit_mode != EditMode.AGENT:
                allowed_tool_names = EditModeConfig.filter_tools(edit_mode, [t["function"]["name"] for t in all_tools])
                tools = [t for t in all_tools if t["function"]["name"] in allowed_tool_names]
                logger.info(f"Mode {edit_mode.value}: filtered {len(all_tools)} tools to {len(tools)}")
            else:
                tools = all_tools
            
            logger.debug(f"Tools enabled: {tools_enabled}, tools count: {len(tools) if tools else 0}")
            
            system_prompt = EditModeConfig.get_system_prompt(edit_mode)
            
            full_response = ""
            
            async for event in llm_service.chat_stream_with_tools(
                messages=session_manager.get_messages_for_api(session),
                model=session.model,
                tools=tools,
                system_prompt=system_prompt
            ):
                if not task_flags.get(task_id):
                    logger.info(f"Task {task_id} cancelled")
                    return
                
                if event["type"] == "content":
                    chunk = event["content"]
                    full_response += chunk
                    
                    await ws_manager.send_personal_message({
                        "type": "content_chunk",
                        "task_id": task_id,
                        "chunk": chunk,
                        "accumulated_length": len(full_response),
                        "timestamp": datetime.now().isoformat()
                    }, websocket)
                
                elif event["type"] == "tool_call_start":
                    tool_name = event.get("tool_name", "unknown")
                    
                    logger.info(f"Tool call started: {tool_name}")
                    
                    if not EditModeConfig.can_use_tool(edit_mode, tool_name):
                        logger.warning(f"Tool {tool_name} not allowed in mode {edit_mode.value}")
                        await ws_manager.send_personal_message({
                            "type": "tool_call",
                            "task_id": task_id,
                            "tool_name": tool_name,
                            "status": "rejected",
                            "error": f"当前模式({edit_mode.value})不允许使用此工具",
                            "timestamp": datetime.now().isoformat()
                        }, websocket)
                        continue
                    
                    await ws_manager.send_personal_message({
                        "type": "tool_call",
                        "task_id": task_id,
                        "tool_name": tool_name,
                        "status": "executing",
                        "timestamp": datetime.now().isoformat()
                    }, websocket)
                
                elif event["type"] == "tool_call_end":
                    tool_name = event.get("tool_name", "unknown")
                    arguments = event.get("arguments", {})
                    
                    logger.info(f"Tool call end: {tool_name}, args: {arguments}")
                    
                    if not EditModeConfig.can_use_tool(edit_mode, tool_name):
                        logger.warning(f"Tool {tool_name} not allowed in mode {edit_mode.value}")
                        continue
                    
                    if tools_enabled and tool_name:
                        clean_args = {k: v for k, v in arguments.items() if k not in ('session_id', 'novel_id', 'chapter_id')}
                        
                        if session.current_chapter_id and 'chapter_id' not in arguments:
                            clean_args['chapter_id'] = session.current_chapter_id
                        
                        tool_result = await registry.execute(
                            tool_name,
                            session_id=session.session_id,
                            novel_id=novel_id,
                            **clean_args
                        )
                        
                        logger.info(f"Tool result: success={tool_result.success}")
                        
                        await ws_manager.send_personal_message({
                            "type": "tool_result",
                            "task_id": task_id,
                            "tool_name": tool_name,
                            "result": tool_result.model_dump(),
                            "timestamp": datetime.now().isoformat()
                        }, websocket)
                        
                        if tool_result.metadata and tool_result.metadata.get("requires_user_confirmation"):
                            edit_session_id = tool_result.metadata.get("edit_session_id")
                            if edit_session_id:
                                await ws_manager.send_personal_message({
                                    "type": "edit_pending",
                                    "task_id": task_id,
                                    "edit_session_id": edit_session_id,
                                    "change_count": tool_result.data.get("change_count", 0),
                                    "timestamp": datetime.now().isoformat()
                                }, websocket)
            
            session_manager.add_message(session, MessageRole.ASSISTANT, full_response)
            await session_manager.save_session(session)
            
            logger.info(f"Chat task {task_id} completed")
            
            await ws_manager.send_personal_message({
                "type": "chat_completed",
                "task_id": task_id,
                "session_id": session.session_id,
                "message_count": session.get_message_count(),
                "timestamp": datetime.now().isoformat()
            }, websocket)
    
    except asyncio.CancelledError:
        logger.info(f"Chat task {task_id} was cancelled")
    except Exception as e:
        logger.error(f"Chat with tools failed: {e}", exc_info=True)
        await ws_manager.send_personal_message({
            "type": "chat_failed",
            "task_id": task_id,
            "error": str(e),
            "timestamp": datetime.now().isoformat()
        }, websocket)
    finally:
        task_flags.pop(task_id, None)


async def _run_generation_task(
    task_id: str,
    novel_id: int,
    generation_type: str,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """执行生成任务"""
    try:
        async with AsyncSessionLocal() as db:
            await ws_manager.send_personal_message(
                GenerationProgress.started(task_id, generation_type, novel_id),
                websocket
            )
            
            if not task_flags.get(task_id):
                return
            
            context_builder = ContextBuilder(db, novel_id)
            
            if generation_type == GenerationType.CHAPTER:
                await _generate_chapter_ws(
                    task_id, novel_id, params, websocket, 
                    task_flags, db, context_builder
                )
            elif generation_type == GenerationType.DIALOGUE:
                await _generate_dialogue_ws(
                    task_id, novel_id, params, websocket, task_flags
                )
            elif generation_type == GenerationType.DESCRIPTION:
                await _generate_description_ws(
                    task_id, novel_id, params, websocket, task_flags
                )
            elif generation_type == GenerationType.OUTLINE:
                await _generate_outline_ws(
                    task_id, novel_id, params, websocket, task_flags
                )
            elif generation_type == GenerationType.SUMMARY:
                await _generate_summary_ws(
                    task_id, novel_id, params, websocket, task_flags
                )
            elif generation_type == GenerationType.CHARACTER_PROFILE:
                await _generate_character_profile_ws(
                    task_id, novel_id, params, websocket, task_flags
                )
            else:
                await ws_manager.send_personal_message(
                    GenerationProgress.failed(task_id, f"不支持的生成类型: {generation_type}"),
                    websocket
                )
                
    except asyncio.CancelledError:
        logger.info(f"Generation task {task_id} was cancelled")
    except Exception as e:
        logger.error(f"Generation task failed: {e}", exc_info=True)
        await ws_manager.send_personal_message(
            GenerationProgress.failed(task_id, str(e)),
            websocket
        )
    finally:
        task_flags.pop(task_id, None)


async def _generate_chapter_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool],
    db,
    context_builder
):
    chapter_number = params.get("chapter_number")
    target_length = params.get("target_length", 3000)
    style = params.get("style", "narrative")
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    
    if chapter_number is None:
        result = await db.execute(
            select(func.max(Chapter.chapter_number)).where(
                Chapter.novel_id == novel_id
            )
        )
        max_chapter = result.scalar()
        chapter_number = (max_chapter or 0) + 1
    
    context_data = await context_builder.build_writing_context(
        chapter_number=chapter_number,
        context_size=5,
        include_previous_chapters=True,
        include_characters=True,
        include_plot_events=True
    )
    
    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "generating", 20, "开始生成章节"),
        websocket
    )
    
    if not task_flags.get(task_id):
        return
    
    system_prompt = get_system_prompt(GenerationType.CHAPTER, style)
    user_message = build_chapter_prompt(
        chapter_number=chapter_number,
        target_length=target_length,
        style=style,
        context=context_data.get("context", ""),
        user_prompt=user_prompt,
        chapter_outline=params.get("chapter_outline"),
        key_events=params.get("key_events"),
        focus_characters=params.get("focus_characters")
    )
    
    full_content = ""
    accumulated_length = 0
    
    async for chunk in llm_service.generate_stream(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    ):
        if not task_flags.get(task_id):
            return
        
        full_content += chunk
        accumulated_length += len(chunk)
        
        await ws_manager.send_personal_message(
            GenerationProgress.content_chunk(task_id, chunk, accumulated_length),
            websocket
        )
        
        progress = 20 + int((accumulated_length / target_length) * 60)
        if progress > 80:
            progress = 80
        
        await ws_manager.send_personal_message(
            GenerationProgress.progress(task_id, "generating", progress, f"已生成 {accumulated_length} 字"),
            websocket
        )
    
    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "saving", 90, "保存章节"),
        websocket
    )
    
    if not task_flags.get(task_id):
        return
    
    result = await db.execute(
        select(Chapter).where(
            Chapter.novel_id == novel_id,
            Chapter.chapter_number == chapter_number
        )
    )
    chapter = result.scalar_one_or_none()
    
    if chapter:
        chapter.content = full_content
        chapter.status = "completed"
        chapter.word_count = len(full_content)
    else:
        chapter = Chapter(
            novel_id=novel_id,
            chapter_number=chapter_number,
            title=f"第{chapter_number}章",
            content=full_content,
            status="completed",
            word_count=len(full_content)
        )
        db.add(chapter)
    
    await db.commit()
    await db.refresh(chapter)
    
    await ws_manager.send_personal_message(
        GenerationProgress.completed(
            task_id=task_id,
            chapter_id=chapter.id,
            chapter_number=chapter_number,
            content=full_content,
            word_count=len(full_content)
        ),
        websocket
    )


async def _generate_dialogue_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    characters = params.get("characters", [])
    context = params.get("context", "")
    style = params.get("style", "natural")
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    
    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "generating", 30, "生成对话中"),
        websocket
    )
    
    if not task_flags.get(task_id):
        return
    
    system_prompt = get_system_prompt(GenerationType.DIALOGUE, style)
    user_message = build_dialogue_prompt(
        characters=[str(c) for c in characters],
        context=context,
        style=style,
        user_prompt=user_prompt
    )
    
    full_content = ""
    accumulated_length = 0
    
    async for chunk in llm_service.generate_stream(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    ):
        if not task_flags.get(task_id):
            return
        
        full_content += chunk
        accumulated_length += len(chunk)
        
        await ws_manager.send_personal_message(
            GenerationProgress.content_chunk(task_id, chunk, accumulated_length),
            websocket
        )
    
    await ws_manager.send_personal_message(
        GenerationProgress.completed(
            task_id=task_id,
            chapter_id=None,
            chapter_number=None,
            content=full_content,
            word_count=len(full_content)
        ),
        websocket
    )


async def _generate_description_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    subject = params.get("subject", "")
    style = params.get("style", "vivid")
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    
    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "generating", 30, "生成描写中"),
        websocket
    )
    
    if not task_flags.get(task_id):
        return
    
    system_prompt = get_system_prompt(GenerationType.DESCRIPTION, style)
    user_message = build_description_prompt(
        subject=subject,
        style=style,
        user_prompt=user_prompt
    )
    
    full_content = ""
    accumulated_length = 0
    
    async for chunk in llm_service.generate_stream(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    ):
        if not task_flags.get(task_id):
            return
        
        full_content += chunk
        accumulated_length += len(chunk)
        
        await ws_manager.send_personal_message(
            GenerationProgress.content_chunk(task_id, chunk, accumulated_length),
            websocket
        )
    
    await ws_manager.send_personal_message(
        GenerationProgress.completed(
            task_id=task_id,
            chapter_id=None,
            chapter_number=None,
            content=full_content,
            word_count=len(full_content)
        ),
        websocket
    )


async def _generate_outline_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    premise = params.get("premise", "")
    genre = params.get("genre", "")
    total_chapters = params.get("total_chapters", 20)
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    
    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "generating", 30, "生成大纲中"),
        websocket
    )
    
    if not task_flags.get(task_id):
        return
    
    system_prompt = get_system_prompt(GenerationType.OUTLINE)
    user_message = build_outline_prompt(
        premise=premise,
        genre=genre,
        total_chapters=total_chapters,
        user_prompt=user_prompt
    )
    
    full_content = ""
    accumulated_length = 0
    
    async for chunk in llm_service.generate_stream(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    ):
        if not task_flags.get(task_id):
            return
        
        full_content += chunk
        accumulated_length += len(chunk)
        
        await ws_manager.send_personal_message(
            GenerationProgress.content_chunk(task_id, chunk, accumulated_length),
            websocket
        )
    
    await ws_manager.send_personal_message(
        GenerationProgress.completed(
            task_id=task_id,
            chapter_id=None,
            chapter_number=None,
            content=full_content,
            word_count=len(full_content)
        ),
        websocket
    )


async def _generate_summary_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    content = params.get("content", "")
    max_length = params.get("max_length", 500)
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    
    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "generating", 30, "生成摘要中"),
        websocket
    )
    
    if not task_flags.get(task_id):
        return
    
    system_prompt = get_system_prompt(GenerationType.SUMMARY)
    user_message = build_summary_prompt(
        content=content,
        max_length=max_length,
        user_prompt=user_prompt
    )
    
    full_content = ""
    accumulated_length = 0
    
    async for chunk in llm_service.generate_stream(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    ):
        if not task_flags.get(task_id):
            return
        
        full_content += chunk
        accumulated_length += len(chunk)
        
        await ws_manager.send_personal_message(
            GenerationProgress.content_chunk(task_id, chunk, accumulated_length),
            websocket
        )
    
    await ws_manager.send_personal_message(
        GenerationProgress.completed(
            task_id=task_id,
            chapter_id=None,
            chapter_number=None,
            content=full_content,
            word_count=len(full_content)
        ),
        websocket
    )


async def _generate_character_profile_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    name = params.get("name", "")
    role = params.get("role", "")
    novel_context = params.get("novel_context", "")
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    
    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "generating", 30, "生成角色档案中"),
        websocket
    )
    
    if not task_flags.get(task_id):
        return
    
    system_prompt = get_system_prompt(GenerationType.CHARACTER_PROFILE)
    user_message = build_character_profile_prompt(
        name=name,
        role=role,
        novel_context=novel_context,
        user_prompt=user_prompt
    )
    
    full_content = ""
    accumulated_length = 0
    
    async for chunk in llm_service.generate_stream(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    ):
        if not task_flags.get(task_id):
            return
        
        full_content += chunk
        accumulated_length += len(chunk)
        
        await ws_manager.send_personal_message(
            GenerationProgress.content_chunk(task_id, chunk, accumulated_length),
            websocket
        )
    
    await ws_manager.send_personal_message(
        GenerationProgress.completed(
            task_id=task_id,
            chapter_id=None,
            chapter_number=None,
            content=full_content,
            word_count=len(full_content)
        ),
        websocket
    )


async def _build_novel_context(db, novel_id: int) -> NovelContext:
    result = await db.execute(select(Novel).where(Novel.id == novel_id))
    novel = result.scalar_one_or_none()
    
    if not novel:
        return NovelContext()
    
    return NovelContext(
        title=novel.title or "",
        description=novel.description or "",
        genre=novel.genre or ""
    )


async def _build_chapter_context(db, novel_id: int, chapter_number: int) -> Optional[ChapterContext]:
    result = await db.execute(
        select(Chapter).where(
            Chapter.novel_id == novel_id,
            Chapter.chapter_number == chapter_number
        )
    )
    chapter = result.scalar_one_or_none()
    
    if not chapter:
        return None
    
    return ChapterContext(
        chapter_number=chapter.chapter_number,
        chapter_title=chapter.title or "",
        previous_summary=chapter.summary or ""
    )
