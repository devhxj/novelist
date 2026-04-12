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
from app.core.llm_service import llm_service, LLMServiceError
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
from app.novels.models import NovelCreativeProfile
from app.novels.models import Novel
from app.editor.service import get_edit_session_manager
from app.mcp.registry import get_mcp_registry
from app.workflows.langgraph_workflow import ChapterWorkflow, LANGGRAPH_AVAILABLE
from app.generation.service import ChapterGenerationService

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
    "整理", "完善", "补充", "设计", "安排", "帮我写", "帮我改", "开始写"
)


async def get_user_from_token(token: str) -> Optional[int]:
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


def _friendly_error_message(exc: Exception) -> str:
    if isinstance(exc, LLMServiceError):
        return exc.message
    return str(exc) or "请求处理失败，请稍后重试。"


def _decode_partial_json_string(raw: str) -> str:
    result: List[str] = []
    i = 0
    while i < len(raw):
        ch = raw[i]
        if ch != "\\":
            result.append(ch)
            i += 1
            continue
        if i + 1 >= len(raw):
            break
        nxt = raw[i + 1]
        mapping = {"n": "\n", "r": "\r", "t": "\t", '"': '"', "\\": "\\", "/": "/"}
        if nxt in mapping:
            result.append(mapping[nxt])
            i += 2
            continue
        if nxt == "u":
            hex_part = raw[i + 2:i + 6]
            if len(hex_part) == 4 and all(c in "0123456789abcdefABCDEF" for c in hex_part):
                result.append(chr(int(hex_part, 16)))
                i += 6
                continue
            break
        result.append(nxt)
        i += 2
    return "".join(result)


def _extract_partial_argument_string(arguments_text: str, key: str) -> Optional[str]:
    marker = f'"{key}"'
    key_idx = arguments_text.find(marker)
    if key_idx < 0:
        return None
    colon_idx = arguments_text.find(":", key_idx + len(marker))
    if colon_idx < 0:
        return None
    quote_idx = arguments_text.find('"', colon_idx + 1)
    if quote_idx < 0:
        return None

    buf: List[str] = []
    escape = False
    i = quote_idx + 1
    while i < len(arguments_text):
        ch = arguments_text[i]
        if escape:
            buf.append("\\" + ch)
            escape = False
            i += 1
            continue
        if ch == "\\":
            escape = True
            i += 1
            continue
        if ch == '"':
            break
        buf.append(ch)
        i += 1
    return _decode_partial_json_string("".join(buf))


async def _lookup_chapter_brief(
    db,
    novel_id: int,
    chapter_id: Optional[int] = None,
    chapter_number: Optional[int] = None
) -> Dict[str, Any]:
    stmt = None
    if chapter_id:
        stmt = select(Chapter).where(Chapter.id == chapter_id)
    elif chapter_number:
        stmt = select(Chapter).where(
            Chapter.novel_id == novel_id,
            Chapter.chapter_number == chapter_number
        )
    if stmt is None:
        return {}

    result = await db.execute(stmt)
    chapter = result.scalar_one_or_none()
    if not chapter:
        return {}
    return {
        "chapter_id": chapter.id,
        "chapter_number": chapter.chapter_number,
        "chapter_title": chapter.title or f"第{chapter.chapter_number}章"
    }


_TOOL_SYNC_NAMES = {
    "get_chapter_list": "查看章节目录",
    "read_chapter_for_edit": "读取待编辑原文",
    "read_chapter": "读取章节正文",
    "get_chapter_content": "读取章节正文",
    "start_edit_session": "开始安全编辑",
    "get_edit_status": "查看编辑状态",
    "edit_chapter_content": "编辑章节内容",
    "create_new_chapter": "创建新章节",
    "generate_chapter_draft": "AI生成新章节",
    "apply_edit": "应用修改内容",
    "get_creative_profile": "查看创作规则",
    "update_creative_profile": "设置创作规则",
    "search_plot_memory": "搜索情节内容",
    "search_story_memory": "搜索故事记忆",
    "prepare_story_brief": "构建写前认知",
    "get_recent_context": "获取写作上下文",
    "get_character_memory": "回顾角色经历",
    "get_timeline": "查看情节时间线",
    "get_story_timeline": "查看故事追踪板",
    "get_timeline_context": "获取AI写作参考",
    "add_timeline_entry": "记录追踪条目",
    "update_timeline_entry": "更新追踪条目",
    "resolve_timeline_entry": "标记条目完成",
    "get_location_list": "查看地点列表",
    "get_location_detail": "查看地点详情",
    "create_location": "创建新地点",
    "update_location": "更新地点设定",
    "delete_location": "删除地点",
    "run_review": "执行审查",
    "get_novel_summary": "查看小说概况",
    "get_novel_progress": "查看写作进度",
    "get_character_list": "查看角色列表",
    "get_character_detail": "查看角色档案",
    "get_writing_characters": "整理角色阵容和关系",
    "create_character": "创建新角色",
    "update_character": "更新角色设定",
    "get_character_network": "查看人物关系图",
    "get_character_relationships": "查看角色关系详情",
    "update_character_relationship": "更新人物关系",
    "run_agent_task": "调度AI子任务",
    "get_pending_changes": "查看待确认修改",
}

def _sync_tool_display_name(tool_name: str) -> str:
    return _TOOL_SYNC_NAMES.get(tool_name, "处理创作任务")

_TOOL_SYNC_KINDS = {
    "get_chapter_list": "browse",
    "read_chapter_for_edit": "view",
    "read_chapter": "view",
    "get_chapter_content": "view",
    "start_edit_session": "edit",
    "get_edit_status": "view",
    "edit_chapter_content": "write",
    "create_new_chapter": "create",
    "generate_chapter_draft": "write",
    "apply_edit": "edit",
    "get_creative_profile": "memory",
    "update_creative_profile": "memory",
    "search_plot_memory": "browse",
    "search_story_memory": "memory",
    "prepare_story_brief": "memory",
    "get_recent_context": "memory",
    "get_character_memory": "memory",
    "get_timeline": "view",
    "get_story_timeline": "view",
    "get_timeline_context": "memory",
    "add_timeline_entry": "write",
    "update_timeline_entry": "edit",
    "resolve_timeline_entry": "edit",
    "get_location_list": "view",
    "get_location_detail": "view",
    "create_location": "create",
    "update_location": "edit",
    "delete_location": "delete",
    "run_review": "review",
    "get_novel_summary": "view",
    "get_novel_progress": "view",
    "get_character_list": "view",
    "get_character_detail": "view",
    "get_writing_characters": "memory",
    "create_character": "create",
    "update_character": "edit",
    "get_character_network": "view",
    "get_character_relationships": "view",
    "update_character_relationship": "edit",
    "run_agent_task": "plan",
    "get_pending_changes": "view",
}


async def _build_tool_call_presentation(
    db,
    novel_id: int,
    tool_name: str,
    arguments: Optional[Dict[str, Any]] = None,
    result_payload: Optional[Any] = None,
    status: str = "executing"
) -> Dict[str, Any]:
    arguments = arguments or {}
    
    if not isinstance(result_payload, dict):
        result_payload = {}
    
    data_payload = result_payload.get("data") or {}
    if not isinstance(data_payload, dict):
        data_payload = {}

    chapter_id = arguments.get("chapter_id") or data_payload.get("chapter_id")
    if not chapter_id and tool_name in {"create_new_chapter", "generate_chapter_draft"}:
        chapter_id = data_payload.get("chapter_id") or data_payload.get("id")
    chapter_number = arguments.get("chapter_number") or data_payload.get("chapter_number")
    chapter_title = arguments.get("title") or data_payload.get("title")

    chapter_brief = {}
    if chapter_id or chapter_number:
        chapter_brief = await _lookup_chapter_brief(
            db,
            novel_id,
            chapter_id=chapter_id,
            chapter_number=chapter_number
        )
        chapter_id = chapter_brief.get("chapter_id", chapter_id)
        chapter_number = chapter_brief.get("chapter_number", chapter_number)
        chapter_title = chapter_brief.get("chapter_title", chapter_title)

    chapter_label = None
    if chapter_number and chapter_title:
        chapter_label = f"第{chapter_number}章 {chapter_title}"
    elif chapter_number:
        chapter_label = f"第{chapter_number}章"
    elif chapter_title:
        chapter_label = str(chapter_title)

    base_text = tool_name
    activity_kind = "general"

    _TOOL_BASE_NAMES = {
        "get_chapter_list": ("查看章节目录", "browse"),
        "read_chapter_for_edit": (f"查看 {chapter_label}" if chapter_label else "读取待编辑原文", "view"),
        "read_chapter": (f"查看 {chapter_label}" if chapter_label else "读取章节正文", "view"),
        "get_chapter_content": (f"查看 {chapter_label}" if chapter_label else "读取章节正文", "view"),
        "start_edit_session": (f"准备修改 {chapter_label}" if chapter_label else "开始安全编辑", "edit"),
        "get_edit_status": (f"查看 {chapter_label} 的修改进度" if chapter_label else "查看编辑状态", "view"),
        "edit_chapter_content": (f"修改 {chapter_label}" if chapter_label else "编辑章节内容", "write"),
        "create_new_chapter": (f"创建 {chapter_label}" if chapter_label else "创建新章节", "create"),
        "generate_chapter_draft": (f"撰写 {chapter_label}" if chapter_label else "AI生成新章节", "write"),
        "apply_edit": (f"修改 {chapter_label}" if chapter_label else "应用修改内容", "edit"),
        "get_creative_profile": ("查看创作规则", "memory"),
        "update_creative_profile": ("设置创作规则", "memory"),
        "search_plot_memory": ("搜索情节内容", "browse"),
        "get_recent_context": ("获取写作上下文", "memory"),
        "get_character_memory": ("回顾角色经历", "memory"),
        "get_timeline": ("查看情节时间线", "view"),
        "get_story_timeline": ("查看故事追踪板", "view"),
        "get_timeline_context": ("获取AI写作参考", "memory"),
        "add_timeline_entry": ("记录追踪条目", "write"),
        "update_timeline_entry": ("更新追踪条目", "edit"),
        "resolve_timeline_entry": ("标记条目完成", "edit"),
        "run_review": ("执行审查", "review"),
        "get_location_list": ("查看地点列表", "view"),
        "get_location_detail": ("查看地点详情", "view"),
        "create_location": ("创建新地点", "create"),
        "update_location": ("更新地点设定", "edit"),
        "delete_location": ("删除地点", "delete"),
        "get_novel_summary": ("查看小说概况", "view"),
        "get_novel_progress": ("查看写作进度", "view"),
        "get_character_list": ("查看角色列表", "view"),
        "get_character_detail": ("查看角色档案", "view"),
        "get_writing_characters": ("整理角色阵容和关系", "memory"),
        "create_character": ("创建新角色", "create"),
        "update_character": ("更新角色设定", "edit"),
        "get_character_network": ("查看人物关系图", "view"),
        "get_character_relationships": ("查看角色关系详情", "view"),
        "update_character_relationship": ("更新人物关系", "edit"),
        "run_agent_task": ("调度AI子任务", "plan"),
        "start_edit_session": ("开始安全编辑", "edit"),
        "read_chapter_for_edit": ("读取待编辑原文", "view"),
        "get_pending_changes": ("查看待确认修改", "view"),
    }

    if tool_name in _TOOL_BASE_NAMES:
        base_text, activity_kind = _TOOL_BASE_NAMES[tool_name]
    elif tool_name == "run_agent_task":
        task_type = arguments.get("task_type")
        if task_type == "check_consistency":
            base_text, activity_kind = "检查剧情一致性", "review"
        elif task_type == "manage_foreshadowing":
            base_text, activity_kind = "整理伏笔线索", "review"

    is_active = status == "executing"
    display_text = f"正在{base_text}" if is_active else base_text

    return {
        "display_text": display_text,
        "activity_kind": activity_kind,
        "chapter_id": chapter_id,
        "chapter_number": chapter_number,
        "chapter_title": chapter_title
    }


async def _ensure_generation_chapter(
    db,
    novel_id: int,
    chapter_number: Optional[int],
    title: Optional[str],
    overwrite_existing: bool = False
) -> Chapter:
    if chapter_number is None:
        result = await db.execute(
            select(func.max(Chapter.chapter_number)).where(Chapter.novel_id == novel_id)
        )
        max_chapter = result.scalar()
        chapter_number = (max_chapter or 0) + 1

    result = await db.execute(
        select(Chapter).where(
            Chapter.novel_id == novel_id,
            Chapter.chapter_number == chapter_number
        )
    )
    chapter = result.scalar_one_or_none()
    if chapter:
        if not overwrite_existing:
            raise ValueError("目标章节已存在。如需重写，请显式设置 overwrite_existing=true。")
        if title:
            chapter.title = title
            chapter.updated_at = datetime.now()
            await db.commit()
            await db.refresh(chapter)
        return chapter

    chapter = Chapter(
        novel_id=novel_id,
        chapter_number=chapter_number,
        title=title or f"第{chapter_number}章",
        content="",
        status="draft",
        word_count=0,
    )
    db.add(chapter)
    await db.commit()
    await db.refresh(chapter)
    return chapter


async def _execute_streaming_chapter_draft(
    db,
    novel_id: int,
    session: Session,
    task_id: str,
    tool_id: Optional[str],
    websocket: WebSocket,
    task_flags: Dict[str, bool],
    arguments: Dict[str, Any]
) -> Dict[str, Any]:
    try:
        chapter = await _ensure_generation_chapter(
            db,
            novel_id=novel_id,
            chapter_number=arguments.get("chapter_number"),
            title=arguments.get("title"),
            overwrite_existing=bool(arguments.get("overwrite_existing", False))
        )
    except ValueError as exc:
        return {"success": False, "data": None, "error": str(exc), "metadata": {"tool": "generate_chapter_draft"}}
    session.current_chapter_id = chapter.id

    presentation = await _build_tool_call_presentation(
        db,
        novel_id,
        "generate_chapter_draft",
        {
            **arguments,
            "chapter_id": chapter.id,
            "chapter_number": chapter.chapter_number,
            "title": chapter.title,
        }
    )
    await ws_manager.send_personal_message({
        "type": "tool_call",
        "task_id": task_id,
        "tool_name": "generate_chapter_draft",
        "tool_id": tool_id,
        "status": "executing",
        **presentation,
        "timestamp": datetime.now().isoformat()
    }, websocket)

    target_length = arguments.get("target_length", 3000)
    style = arguments.get("style", "narrative")
    model = arguments.get("model")
    context_builder = ContextBuilder(db, novel_id)
    context_data = await context_builder.build_writing_context(
        chapter_number=chapter.chapter_number,
        context_size=arguments.get("context_size", 3000),
        include_previous_chapters=True,
        include_characters=True,
        include_plot_events=True
    )
    system_prompt = get_system_prompt(GenerationType.CHAPTER, style)
    user_prompt = build_chapter_prompt(
        chapter_number=chapter.chapter_number,
        target_length=target_length,
        style=style,
        context=context_data.get("context", ""),
        user_prompt=arguments.get("writing_task"),
        author_intent=arguments.get("author_intent"),
        scene_goal=arguments.get("scene_goal"),
        chapter_outline=arguments.get("outline"),
        tone=arguments.get("tone"),
        must_keep=arguments.get("must_keep"),
        must_avoid=arguments.get("must_avoid"),
        key_events=arguments.get("key_events"),
        focus_characters=arguments.get("focus_characters")
    )

    full_content = ""
    try:
        async for chunk in llm_service.generate_stream(
            prompt=user_prompt,
            system_prompt=system_prompt,
            model=model
        ):
            if not task_flags.get(task_id):
                raise asyncio.CancelledError()
            full_content += chunk
            await ws_manager.send_personal_message({
                "type": "chapter_stream",
                "task_id": task_id,
                "tool_name": "generate_chapter_draft",
                "chapter_id": chapter.id,
                "chapter_number": chapter.chapter_number,
                "chapter_title": chapter.title,
                "chunk": chunk,
                "content": full_content,
                "word_count": len(full_content),
                "timestamp": datetime.now().isoformat()
            }, websocket)
    except asyncio.CancelledError:
        raise
    except Exception as exc:
        await db.rollback()
        return {
            "success": False,
            "data": {
                "chapter_id": chapter.id,
                "chapter_number": chapter.chapter_number,
                "title": chapter.title,
            },
            "error": _friendly_error_message(exc),
            "metadata": {"tool": "generate_chapter_draft", "novel_id": novel_id, "chapter_id": chapter.id}
        }

    service = ChapterGenerationService(db, novel_id)
    chapter.content = full_content
    chapter.status = "completed"
    chapter.word_count = len(full_content)
    chapter.updated_at = datetime.now()
    await db.commit()
    await db.refresh(chapter)

    from app.core.chapter_post_processor import ChapterPostProcessor
    try:
        post_processor = ChapterPostProcessor(db, novel_id)
        process_result = await post_processor.process(
            content=chapter.content,
            chapter_number=chapter.chapter_number,
            chapter_id=chapter.id,
            model=model
        )
        if process_result.get("was_truncated"):
            chapter.content = process_result["final_content"]
        else:
            chapter.content = process_result.get("final_content", chapter.content)
        chapter.word_count = len(chapter.content)
        chapter.summary = await service._generate_chapter_summary(chapter.content)
        await db.commit()
        logger.info(
            f"Chapter {chapter.chapter_number} post-process completed: "
            f"truncated={process_result['was_truncated']}, "
            f"ending_completed={process_result['ending_completed']}"
        )
    except Exception as exc:
        logger.warning(f"Chapter post-processing failed (non-fatal): {exc}")
        chapter.summary = await service._generate_chapter_summary(chapter.content)
        await db.commit()

    try:
        await service._update_chapter_memory(chapter.id)
    except Exception as exc:
        logger.warning(f"Failed to update chapter memory after streamed generation: {exc}")

    return {
        "success": True,
        "data": {
            "chapter_id": chapter.id,
            "chapter_number": chapter.chapter_number,
            "title": chapter.title,
            "summary": chapter.summary,
            "status": chapter.status,
            "word_count": chapter.word_count,
            "content": chapter.content,
            "iterations": 1
        },
        "error": None,
        "metadata": {"tool": "generate_chapter_draft", "novel_id": novel_id, "chapter_id": chapter.id}
    }


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
                    "timestamp": datetime.now().isoformat()
                }, websocket)
    
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
    if not session.title:
        base_title = novel_context.title if novel_context else ""
        session.title = f"{base_title} 对话" if base_title else "新对话"
    session.edit_mode = edit_mode
    session.current_chapter_id = current_chapter_id
    await session_manager.save_session(session)
    
    await ws_manager.send_personal_message({
        "type": "session_created",
        "session_id": session.session_id,
        "scope": scope.to_dict(),
        "display_name": scope.get_display_name(),
        "title": session.title,
        "subtitle": session.get_subtitle(),
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
        "title": session.title,
        "subtitle": session.get_subtitle(),
        "message_count": session.get_message_count(),
        "recent_messages": [
            m.to_dict()
            for m in session.messages
            if m.role != MessageRole.TOOL
        ],
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
                "subtitle": s.get_subtitle(),
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
    session.subtitle = new_scope.get_display_name()
    
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
        "title": session.title,
        "subtitle": session.get_subtitle(),
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
            "latest_pending_edit_session_id": edit_session.edit_session_id,
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
            "latest_pending_edit_session_id": edit_session.edit_session_id,
            "chapter_id": edit_session.chapter_id,
            "change_count": edit_session.change_count,
            "working_content": edit_session.working_content,
            "diff": diff_data.get("diff", {}),
            "timestamp": datetime.now().isoformat()
        }, websocket)


async def _resolve_edit_session_for_action(db, edit_session_id: Optional[str], chapter_id: Optional[int]):
    manager = get_edit_session_manager(db)
    edit_session = None
    if edit_session_id:
        edit_session = await manager.get_edit_session_by_id(edit_session_id)
    if not edit_session and chapter_id:
        edit_session = await manager.get_edit_session(chapter_id)
    return manager, edit_session


async def _get_latest_pending_edit_session_id(db, chapter_id: Optional[int]) -> Optional[str]:
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
                "timestamp": datetime.now().isoformat()
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
                "timestamp": datetime.now().isoformat()
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
            "timestamp": datetime.now().isoformat()
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
                "timestamp": datetime.now().isoformat()
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
                "timestamp": datetime.now().isoformat()
            }, websocket)
            return
        
        await ws_manager.send_personal_message({
            "type": "edit_rejected",
            "edit_session_id": edit_session.edit_session_id,
            "chapter_id": result["chapter_id"],
            "latest_pending_edit_session_id": await _get_latest_pending_edit_session_id(db, result["chapter_id"]),
            "already_processed": result.get("already_processed", False),
            "message": "编辑会话此前已被拒绝" if result.get("already_processed") else "已拒绝所有变更，回退到原版本",
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
    """执行支持工具调用的对话 - 优化版：支持前缀缓存"""
    try:
        logger.info(f"Starting chat task {task_id}, mode={session.edit_mode}")
        
        try:
            edit_mode = EditMode(session.edit_mode) if session.edit_mode else EditMode.AGENT
        except ValueError:
            edit_mode = EditMode.AGENT
            logger.warning(f"Invalid edit_mode: {session.edit_mode}, fallback to AGENT")
        
        session_manager.add_message(session, MessageRole.USER, user_message)
        
        async with AsyncSessionLocal() as db:
            registry = get_mcp_registry()
            
            extra_context_for_user = ""
            creative_profile_text = ""
            conditional_reminders = []
            
            try:
                if session_manager.compressor.should_compress(session) and session_manager.config.enable_auto_summary:
                    summary_prompt = session_manager.compressor.build_summary_request_prompt(session.messages)
                    summary = await llm_service.generate_text(
                        prompt=summary_prompt,
                        system_prompt="你是对话摘要助手，请提炼关键信息与设定。"
                    )
                    session_manager.compress_session(session, summary=summary)
                
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
                
                if edit_mode == EditMode.AGENT and not _looks_like_authoring_intent(user_message):
                    conditional_reminders.append(
                        "用户这次更像是在闲聊、反馈、确认或提问，而不是明确要求你开始写正文或修改章节。"
                        "请先正常对话回应，不要主动创建章节、生成正文或修改正文，除非用户随后明确提出创作或编辑请求。"
                    )
                
                if edit_mode == EditMode.AGENT and _looks_like_long_term_rule(user_message):
                    conditional_reminders.append(
                        "用户这次很可能在表达长期创作规则或全局偏好。"
                        "如果这些要求不是只针对当前这一章，而是希望后续持续生效，"
                        "请优先先读取 get_creative_profile，再用 update_creative_profile 做增量沉淀。"
                    )
            except Exception:
                extra_context_for_user = ""
                creative_profile_text = ""
                conditional_reminders = []
            
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
            
            base_system_prompt = EditModeConfig.get_system_prompt(edit_mode)
            
            system_sections = [base_system_prompt]
            if creative_profile_text:
                system_sections.append(creative_profile_text)
            if conditional_reminders:
                reminder_text = "\n".join(
                    f"- {reminder}" for reminder in conditional_reminders
                )
                system_sections.append(f"【本轮额外提醒】\n{reminder_text}")
            prefix_messages = [{
                "role": "system",
                "content": "\n\n".join(section for section in system_sections if section).strip()
            }]
            
            history_messages = session_manager.get_messages_for_api(session, include_context=False)
            
            enhanced_user_content = user_message + (extra_context_for_user or "")
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
            
            tool_cache: Dict[str, Dict[str, Any]] = {}
            disabled_tools: set[str] = set()
            failed_tool_keys: Dict[str, int] = {}
            max_tool_retries = 3
            recent_tool_patterns: List[str] = []
            READ_ONLY_TOOLS = {
                "search_story_memory", "prepare_story_brief", "get_novel_summary",
                "get_chapter_list", "get_chapter_content", "get_character_list",
                "get_character_detail", "get_writing_characters",
                "get_timeline_context", "get_story_timeline", "run_review",
                "get_location_list", "get_location_detail"
            }
            while loop_count < 50:
                tool_outputs: List[Dict[str, Any]] = []
                if tools:
                    tools = [t for t in tools if t["function"]["name"] not in disabled_tools]
                async for event in llm_service.chat_stream_with_tools(
                    messages=full_messages,
                    model=session.model,
                    tools=tools,
                    system_prompt=None
                ):
                    if not task_flags.get(task_id):
                        logger.info(f"Task {task_id} cancelled")
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
                            "timestamp": datetime.now().isoformat()
                        }, websocket)

                    elif event["type"] == "content":
                        if is_thinking:
                            is_thinking = False
                            await ws_manager.send_personal_message({
                                "type": "thinking_done",
                                "task_id": task_id,
                                "timestamp": datetime.now().isoformat()
                            }, websocket)
                        chunk = event["content"]
                        full_response += chunk
                        response_buffer += chunk
                        
                        await ws_manager.send_personal_message({
                            "type": "content_chunk",
                            "task_id": task_id,
                            "chunk": chunk,
                            "accumulated_length": len(full_response),
                            "timestamp": datetime.now().isoformat()
                        }, websocket)
                    
                    elif event["type"] == "tool_call_start":
                        if is_thinking:
                            is_thinking = False
                            await ws_manager.send_personal_message({
                                "type": "thinking_done",
                                "task_id": task_id,
                                "timestamp": datetime.now().isoformat()
                            }, websocket)
                        if response_buffer.strip():
                            msg_meta = {}
                            if thinking_buffer.strip():
                                msg_meta["thinking_content"] = thinking_buffer
                                thinking_buffer = ""
                            session_manager.add_message(session, MessageRole.ASSISTANT, response_buffer, metadata=msg_meta or None)
                            response_buffer = ""

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
                                "timestamp": datetime.now().isoformat()
                            }, websocket)
                            continue
                        
                        await ws_manager.send_personal_message({
                            "type": "tool_call",
                            "task_id": task_id,
                            "tool_name": tool_name,
                            "tool_id": tool_id,
                            "status": "executing",
                            "display_text": f"正在{_sync_tool_display_name(tool_name)}",
                            "activity_kind": _TOOL_SYNC_KINDS.get(tool_name, "general"),
                            "timestamp": datetime.now().isoformat()
                        }, websocket)

                    elif event["type"] == "tool_call_arguments":
                        tool_name = event.get("tool_name", "unknown")
                        if tool_name != "apply_edit":
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
                            "timestamp": datetime.now().isoformat()
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
                                "timestamp": datetime.now().isoformat()
                            }, websocket)
                            tool_outputs.append({
                                "tool": tool_name,
                                "tool_id": tool_id,
                                "arguments": arguments,
                                "result": result_payload
                            })
                            continue
                        
                        if tools_enabled and tool_name:
                            clean_args = {k: v for k, v in arguments.items() if k not in ('session_id', 'novel_id')}
                            
                            if session.current_chapter_id and 'chapter_id' not in arguments:
                                clean_args['chapter_id'] = session.current_chapter_id
                            elif tool_name in {"start_edit_session", "read_chapter_for_edit", "get_edit_status", "edit_chapter_content"} and 'chapter_id' not in arguments:
                                chapter_id = None
                                if session.scope.type == ScopeType.CHAPTER and session.scope.chapter_start:
                                    result = await db.execute(
                                        select(Chapter).where(
                                            Chapter.novel_id == novel_id,
                                            Chapter.chapter_number == session.scope.chapter_start
                                        )
                                    )
                                    chapter = result.scalar_one_or_none()
                                    if chapter:
                                        chapter_id = chapter.id
                                if not chapter_id:
                                    result = await db.execute(
                                        select(Chapter)
                                        .where(Chapter.novel_id == novel_id)
                                        .order_by(Chapter.chapter_number.desc())
                                        .limit(1)
                                    )
                                    chapter = result.scalar_one_or_none()
                                    if chapter:
                                        chapter_id = chapter.id
                                if chapter_id:
                                    clean_args["chapter_id"] = chapter_id
                            
                            cache_key = f"{tool_name}:{json.dumps(clean_args, ensure_ascii=False, sort_keys=True)}"
                            presentation = await _build_tool_call_presentation(db, novel_id, tool_name, clean_args)
                            await ws_manager.send_personal_message({
                                "type": "tool_call",
                                "task_id": task_id,
                                "tool_name": tool_name,
                                "tool_id": tool_id,
                                "status": "executing",
                                **presentation,
                                "timestamp": datetime.now().isoformat()
                            }, websocket)
                            cached = tool_cache.get(cache_key)
                            if cached:
                                tool_result_payload = cached
                            else:
                                if tool_name == "generate_chapter_draft":
                                    tool_result_payload = await _execute_streaming_chapter_draft(
                                        db=db,
                                        novel_id=novel_id,
                                        session=session,
                                        task_id=task_id,
                                        tool_id=tool_id,
                                        websocket=websocket,
                                        task_flags=task_flags,
                                        arguments=clean_args
                                    )
                                else:
                                    tool_result = await registry.execute(
                                        tool_name,
                                        db=db,
                                        user_id=session.user_id,
                                        session_id=session.session_id,
                                        novel_id=novel_id,
                                        **clean_args
                                    )
                                    tool_result_payload = tool_result.model_dump()
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
                                **presentation,
                                "error": tool_result_payload.get("error"),
                                "timestamp": datetime.now().isoformat()
                            }, websocket)
                            
                            tool_outputs.append({
                                "tool": tool_name,
                                "tool_id": tool_id,
                                "arguments": clean_args,
                                "result": tool_result_payload
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
                                
                                if tool_name in ("add_timeline_entry", "update_timeline_entry", "resolve_timeline_entry"):
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith(("get_story_timeline:", "get_timeline_context:", "run_review:", "prepare_story_brief:"))
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name in ("create_character", "update_character"):
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith(("get_character_list:", "get_character_detail:", "get_writing_characters:", "get_character_memory:", "get_character_network:", "get_character_relationships:"))
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name == "update_character_relationship":
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith(("get_character_network:", "get_character_relationships:", "get_writing_characters:"))
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name == "create_location":
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith(("get_location_list:", "get_location_detail:"))
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name in ("update_location", "delete_location"):
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith(("get_location_list:", "get_location_detail:"))
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)

                                if tool_name == "create_new_chapter":
                                    stale_keys = [
                                        key for key in list(tool_cache.keys())
                                        if key.startswith(("get_chapter_list:", "get_novel_progress:"))
                                    ]
                                    for stale_key in stale_keys:
                                        tool_cache.pop(stale_key, None)
                            
                            if tool_name == "start_edit_session" and tool_result_payload.get("success") and not session.metadata.get("edit_session_hint_sent"):
                                await ws_manager.send_personal_message({
                                    "type": "edit_started",
                                    "task_id": task_id,
                                    "tool_name": tool_name,
                                    "chapter_id": data_payload.get("chapter_id") or clean_args.get("chapter_id"),
                                    "edit_session_id": data_payload.get("edit_session_id") or metadata.get("edit_session_id"),
                                    "working_content": data_payload.get("working_content", ""),
                                    "original_content": data_payload.get("original_content", ""),
                                    "change_count": data_payload.get("change_count", 0),
                                    "timestamp": datetime.now().isoformat()
                                }, websocket)
                                session_manager.add_message(
                                    session,
                                    MessageRole.SYSTEM,
                                    "已创建编辑会话。请直接调用 apply_edit 写入正文内容，避免重复 start_edit_session。"
                                )
                                session.metadata["edit_session_hint_sent"] = True
                            
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
                                    "timestamp": datetime.now().isoformat()
                                }, websocket)
                            if metadata.get("requires_user_confirmation"):
                                edit_session_id = metadata.get("edit_session_id")
                                if edit_session_id:
                                    await ws_manager.send_personal_message({
                                        "type": "edit_pending",
                                        "task_id": task_id,
                                        "edit_session_id": edit_session_id,
                                        "change_count": (tool_result_payload.get("data") or {}).get("change_count", 0),
                                        "timestamp": datetime.now().isoformat()
                                    }, websocket)
                
                if tool_outputs and tools_enabled:
                    if full_response:
                        session_manager.add_message(session, MessageRole.ASSISTANT, full_response)
                        full_response = ""
                    for item in tool_outputs:
                        tool_call_payload = [{
                            "id": item.get("tool_id") or f"call_{item['tool']}",
                            "type": "function",
                            "function": {
                                "name": item["tool"],
                                "arguments": json.dumps(item.get("arguments", {}), ensure_ascii=False)
                            }
                        }]
                        session_manager.add_message(
                            session,
                            MessageRole.ASSISTANT,
                            "",
                            metadata={"tool_calls": tool_call_payload}
                        )
                        session_manager.add_message(
                            session,
                            MessageRole.TOOL,
                            json.dumps(item["result"], ensure_ascii=False),
                            metadata={
                                "tool_call_id": item.get("tool_id") or f"call_{item['tool']}",
                                "tool_name": item["tool"]
                            }
                        )
                    history_messages = session_manager.get_messages_for_api(session, include_context=False)
                    full_messages = (
                        prefix_messages +
                        history_messages
                    )
                    logger.info(
                        f"Loop {loop_count + 1}: rebuilt full_messages with "
                        f"{len(full_messages)} messages (last 3 roles: "
                        f"{[m.get('role', '?') for m in full_messages[-3:]]})"
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
                            "timestamp": datetime.now().isoformat()
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
                        "timestamp": datetime.now().isoformat()
                    }, websocket)
                break
            
            if response_buffer.strip():
                final_meta = {}
                if thinking_buffer.strip():
                    final_meta["thinking_content"] = thinking_buffer
                session_manager.add_message(session, MessageRole.ASSISTANT, response_buffer, metadata=final_meta or None)
            elif full_response.strip():
                final_meta = {}
                if thinking_buffer.strip():
                    final_meta["thinking_content"] = thinking_buffer
                session_manager.add_message(session, MessageRole.ASSISTANT, full_response, metadata=final_meta or None)
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
            "error": _friendly_error_message(e),
            "timestamp": datetime.now().isoformat()
        }, websocket)
    finally:
        task_flags.pop(task_id, None)


def _format_creative_profile_for_prompt(profile: NovelCreativeProfile) -> str:
    llm_brief = (profile.extra_metadata or {}).get("llm_brief")
    if llm_brief:
        return str(llm_brief).strip()
    parts: List[str] = []
    if profile.author_intent:
        parts.append(f"- 长期作者意图：{profile.author_intent}")
    if profile.preferred_tone:
        parts.append(f"- 默认语气：{profile.preferred_tone}")
    if profile.scene_planning_notes:
        parts.append(f"- 规划备注：{profile.scene_planning_notes}")
    for item in (profile.long_term_goals or [])[:5]:
        parts.append(f"- 长线目标：{item}")
    for item in (profile.must_keep or [])[:8]:
        parts.append(f"- 必须长期保留：{item}")
    for item in (profile.must_avoid or [])[:8]:
        parts.append(f"- 必须长期避免：{item}")
    return "\n".join(parts)


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
            GenerationProgress.failed(task_id, _friendly_error_message(e)),
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
    context_size = params.get("context_size", 3000)
    
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
        context_size=context_size,
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
    
    use_langgraph = params.get("use_langgraph")
    if use_langgraph:
        if not LANGGRAPH_AVAILABLE:
            await ws_manager.send_personal_message(
                GenerationProgress.failed(task_id, "LangGraph不可用"),
                websocket
            )
            return
        workflow = ChapterWorkflow()
        workflow_result = await workflow.run(
            task_id=task_id,
            novel_id=novel_id,
            chapter_number=chapter_number,
            target_length=target_length,
            style=style,
            context=context_data,
            model=model,
            agent_role=params.get("agent_role"),
            context_size=context_size,
            extra_parameters={
                "writing_task": user_prompt,
                "outline": params.get("chapter_outline"),
                "tone": params.get("tone")
            }
        )
        if not workflow_result.get("success"):
            await ws_manager.send_personal_message(
                GenerationProgress.failed(task_id, workflow_result.get("error", "工作流失败")),
                websocket
            )
            return
        generated = workflow_result.get("generated_content", "")
        review_result = workflow_result.get("review_result") or {}
        consistency_result = workflow_result.get("consistency_result") or {}
        await ws_manager.send_personal_message(
            GenerationProgress.review_result(
                task_id,
                review_result.get("approved", True),
                review_result.get("score", 0),
                review_result.get("issues", [])
            ),
            websocket
        )
        await ws_manager.send_personal_message(
            GenerationProgress.consistency_check(
                task_id,
                consistency_result.get("passed", True),
                consistency_result.get("issues", [])
            ),
            websocket
        )
        chapter_result = await db.execute(
            select(Chapter).where(
                Chapter.novel_id == novel_id,
                Chapter.chapter_number == chapter_number
            )
        )
        saved = chapter_result.scalar_one_or_none()
        await ws_manager.send_personal_message(
            GenerationProgress.completed(
                task_id,
                saved.id if saved else None,
                chapter_number,
                generated,
                len(generated)
            ),
            websocket
        )
        return

    if use_langgraph is None and LANGGRAPH_AVAILABLE:
        service = ChapterGenerationService(db, novel_id)
        workflow_result = await service.generate_chapter(
            chapter_number=chapter_number,
            target_length=target_length,
            style=style,
            additional_context={
                "user_prompt": user_prompt,
                "author_intent": params.get("author_intent"),
                "scene_goal": params.get("scene_goal"),
                "chapter_outline": params.get("chapter_outline"),
                "must_keep": params.get("must_keep"),
                "must_avoid": params.get("must_avoid"),
                "key_events": params.get("key_events"),
                "focus_characters": params.get("focus_characters")
            },
            agent_role=params.get("agent_role"),
            model=model,
            use_workflow=True,
            context_size=context_size
        )
        if not workflow_result.get("success"):
            await ws_manager.send_personal_message(
                GenerationProgress.failed(task_id, workflow_result.get("error", "章节生成失败")),
                websocket
            )
            return
        review_result = workflow_result.get("review_result") or {}
        consistency_result = workflow_result.get("consistency_result") or {}
        generated = workflow_result.get("content", "")
        await ws_manager.send_personal_message(
            GenerationProgress.review_result(
                task_id,
                review_result.get("approved", True),
                review_result.get("score", 0),
                review_result.get("issues", [])
            ),
            websocket
        )
        await ws_manager.send_personal_message(
            GenerationProgress.consistency_check(
                task_id,
                consistency_result.get("passed", True),
                consistency_result.get("issues", [])
            ),
            websocket
        )
        await ws_manager.send_personal_message(
            GenerationProgress.completed(
                task_id,
                workflow_result.get("chapter_id"),
                chapter_number,
                generated,
                len(generated)
            ),
            websocket
        )
        return
    
    system_prompt = get_system_prompt(GenerationType.CHAPTER, style)
    user_message = build_chapter_prompt(
        chapter_number=chapter_number,
        target_length=target_length,
        style=style,
        context=context_data.get("context", ""),
        user_prompt=user_prompt,
        author_intent=params.get("author_intent"),
        scene_goal=params.get("scene_goal"),
        chapter_outline=params.get("chapter_outline"),
        must_keep=params.get("must_keep"),
        must_avoid=params.get("must_avoid"),
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
    try:
        service = ChapterGenerationService(db, novel_id)
        await service._update_chapter_memory(chapter.id)
    except Exception as e:
        logger.warning(f"Failed to update chapter memory after WS fallback generation: {e}")
    
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
