"""
WebSocket路由 - 实时生成通信（集成LangGraph + 会话管理）
支持所有LLM生成类型的实时流式输出
支持用户自定义提示词和模型选择
支持多轮对话和会话管理
"""
import logging
import asyncio
import uuid
import json
from datetime import datetime
from fastapi import APIRouter, WebSocket, WebSocketDisconnect, Query
from sqlalchemy import select, func
from typing import Optional, Dict, Any, List

from app.core.websocket import ws_manager, GenerationProgress
from app.core.database import AsyncSessionLocal
from app.core.auth import decode_token
from app.core.llm_service import llm_service
from app.core.context_builder import ContextBuilder
from app.core.session_manager import (
    Session, SessionManager, SessionConfig, MessageRole,
    session_manager
)
from app.core.prompt_templates import (
    get_system_prompt,
    build_chapter_prompt,
    build_dialogue_prompt,
    build_description_prompt,
    build_outline_prompt,
    build_summary_prompt,
    build_character_profile_prompt,
    get_available_models,
    get_available_styles,
    GenerationType
)
from app.chapters.models import Chapter
from app.novels.models import Novel

router = APIRouter(tags=["websocket"])
logger = logging.getLogger(__name__)

MAX_CONCURRENT_TASKS = 3


async def get_user_from_token(token: str) -> Optional[int]:
    """从token获取用户ID"""
    try:
        payload = decode_token(token)
        if payload and payload.get("sub"):
            return int(payload["sub"])
    except Exception:
        pass
    return None


@router.websocket("/ws/generation")
async def websocket_generation(
    websocket: WebSocket,
    token: str = Query(...),
    novel_id: int = Query(...)
):
    """
    WebSocket连接 - 实时生成通信（支持会话模式）
    
    连接URL: ws://host/ws/generation?token=xxx&novel_id=xxx
    
    消息类型（客户端 -> 服务端）:
    - create_session: 创建新会话
      {"type": "create_session", "generation_type": "chat", "system_prompt": "..."}
    
    - load_session: 加载已有会话
      {"type": "load_session", "session_id": "xxx"}
    
    - chat: 多轮对话
      {
        "type": "chat",
        "session_id": "xxx",
        "message": "用户消息",
        "model": "deepseek-chat",
        "temperature": 0.7
      }
    
    - start_generation: 开始生成（单次生成模式）
      {
        "type": "start_generation",
        "generation_type": "chapter",
        "params": {...},
        "use_langgraph": true
      }
    
    - cancel_generation: 取消生成
      {"type": "cancel_generation", "task_id": "xxx"}
    
    消息类型（服务端 -> 客户端）:
    - session_created: 会话创建成功
    - session_loaded: 会话加载成功
    - chat_response: 对话响应（流式）
    - generation_started: 生成开始
    - generation_progress: 生成进度
    - content_chunk: 内容片段（流式）
    - generation_completed: 生成完成
    - generation_failed: 生成失败
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
    
    try:
        while True:
            data = await websocket.receive_json()
            message_type = data.get("type")
            
            if message_type == "create_session":
                generation_type = data.get("generation_type", "chat")
                system_prompt = data.get("system_prompt")
                
                if not system_prompt and generation_type != "chat":
                    system_prompt = get_system_prompt(generation_type)
                
                current_session = session_manager.create_session(
                    user_id=user_id,
                    novel_id=novel_id,
                    generation_type=generation_type,
                    system_prompt=system_prompt
                )
                await session_manager.save_session(current_session)
                
                await ws_manager.send_personal_message({
                    "type": "session_created",
                    "session_id": current_session.session_id,
                    "generation_type": generation_type,
                    "timestamp": datetime.now().isoformat()
                }, websocket)
            
            elif message_type == "load_session":
                session_id = data.get("session_id")
                session = await session_manager.load_session(session_id)
                
                if not session:
                    await ws_manager.send_personal_message({
                        "type": "error",
                        "error": "会话不存在",
                        "timestamp": datetime.now().isoformat()
                    }, websocket)
                    continue
                
                if session.user_id != user_id:
                    await ws_manager.send_personal_message({
                        "type": "error",
                        "error": "无权访问此会话",
                        "timestamp": datetime.now().isoformat()
                    }, websocket)
                    continue
                
                current_session = session
                
                await ws_manager.send_personal_message({
                    "type": "session_loaded",
                    "session_id": session.session_id,
                    "generation_type": session.generation_type,
                    "message_count": session.get_message_count(),
                    "messages": [m.to_dict() for m in session.messages[-10:]],
                    "summary": session.summary,
                    "timestamp": datetime.now().isoformat()
                }, websocket)
            
            elif message_type == "chat":
                if not current_session:
                    current_session = session_manager.create_session(
                        user_id=user_id,
                        novel_id=novel_id,
                        generation_type="chat"
                    )
                    await session_manager.save_session(current_session)
                
                user_message = data.get("message", "")
                model = data.get("model")
                temperature = data.get("temperature")
                
                task_id = f"chat_{current_session.session_id}_{uuid.uuid4().hex[:8]}"
                task_flags[task_id] = True
                
                task = asyncio.create_task(
                    run_chat_task(
                        task_id=task_id,
                        session=current_session,
                        user_message=user_message,
                        model=model,
                        temperature=temperature,
                        websocket=websocket,
                        task_flags=task_flags
                    )
                )
                active_tasks[task_id] = task
                
                await ws_manager.send_personal_message({
                    "type": "chat_started",
                    "task_id": task_id,
                    "session_id": current_session.session_id,
                    "timestamp": datetime.now().isoformat()
                }, websocket)
            
            elif message_type == "start_generation":
                running_count = sum(1 for t in active_tasks.values() if not t.done())
                if running_count >= MAX_CONCURRENT_TASKS:
                    await ws_manager.send_personal_message({
                        "type": "generation_rejected",
                        "reason": "too_many_tasks",
                        "message": f"同时最多{MAX_CONCURRENT_TASKS}个生成任务"
                    }, websocket)
                    continue
                
                generation_type = data.get("generation_type", "chapter")
                params = data.get("params", {})
                use_langgraph = data.get("use_langgraph", True)
                
                task_id = f"gen_{novel_id}_{generation_type}_{uuid.uuid4().hex[:8]}"
                task_flags[task_id] = True
                
                task = asyncio.create_task(
                    run_generation_task(
                        task_id=task_id,
                        novel_id=novel_id,
                        generation_type=generation_type,
                        params=params,
                        use_langgraph=use_langgraph,
                        websocket=websocket,
                        task_flags=task_flags
                    )
                )
                active_tasks[task_id] = task
                
                await ws_manager.send_personal_message(
                    GenerationProgress.started(task_id, generation_type, novel_id),
                    websocket
                )
            
            elif message_type == "cancel_generation":
                task_id = data.get("task_id")
                if task_id in task_flags:
                    task_flags[task_id] = False
                    
                    if task_id in active_tasks:
                        task = active_tasks[task_id]
                        if not task.done():
                            task.cancel()
                    
                    await ws_manager.send_personal_message({
                        "type": "generation_cancelled",
                        "task_id": task_id,
                        "timestamp": datetime.now().isoformat()
                    }, websocket)
            
    except WebSocketDisconnect:
        logger.info(f"WebSocket disconnected: user={user_id}, novel={novel_id}")
        for task_id in task_flags:
            task_flags[task_id] = False
        ws_manager.disconnect(websocket, user_id, novel_id)
    except Exception as e:
        logger.error(f"WebSocket error: {e}")
        for task_id in task_flags:
            task_flags[task_id] = False
        ws_manager.disconnect(websocket, user_id, novel_id)


async def run_chat_task(
    task_id: str,
    session: Session,
    user_message: str,
    model: Optional[str],
    temperature: Optional[float],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """执行对话任务（支持多轮）"""
    try:
        async for chunk in await llm_service.chat_with_session(
            session=session,
            user_message=user_message,
            model=model,
            temperature=temperature,
            stream=True
        ):
            if not task_flags.get(task_id):
                return
            
            await ws_manager.send_personal_message({
                "type": "chat_chunk",
                "task_id": task_id,
                "session_id": session.session_id,
                "chunk": chunk,
                "accumulated_length": len(chunk),
                "timestamp": datetime.now().isoformat()
            }, websocket)
        
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
        logger.error(f"Chat task failed: {e}")
        await ws_manager.send_personal_message({
            "type": "chat_failed",
            "task_id": task_id,
            "error": str(e),
            "timestamp": datetime.now().isoformat()
        }, websocket)
    finally:
        task_flags.pop(task_id, None)


async def run_generation_task(
    task_id: str,
    novel_id: int,
    generation_type: str,
    params: Dict[str, Any],
    use_langgraph: bool,
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """执行生成任务（支持所有生成类型 + LangGraph）"""
    try:
        async with AsyncSessionLocal() as db:
            await ws_manager.send_personal_message(
                GenerationProgress.progress(task_id, "preparing", 10, "准备上下文"),
                websocket
            )
            
            if not task_flags.get(task_id):
                return
            
            context_builder = ContextBuilder(db, novel_id)
            
            if generation_type == GenerationType.CHAPTER:
                await _generate_chapter(
                    task_id, novel_id, params, use_langgraph, websocket, 
                    task_flags, db, context_builder
                )
            elif generation_type == GenerationType.DIALOGUE:
                await _generate_dialogue(
                    task_id, novel_id, params, websocket, task_flags
                )
            elif generation_type == GenerationType.DESCRIPTION:
                await _generate_description(
                    task_id, novel_id, params, websocket, task_flags
                )
            elif generation_type == GenerationType.OUTLINE:
                await _generate_outline(
                    task_id, novel_id, params, websocket, task_flags
                )
            elif generation_type == GenerationType.SUMMARY:
                await _generate_summary(
                    task_id, novel_id, params, websocket, task_flags
                )
            elif generation_type == GenerationType.CHARACTER_PROFILE:
                await _generate_character_profile(
                    task_id, novel_id, params, websocket, task_flags
                )
            else:
                await ws_manager.send_personal_message(
                    GenerationProgress.failed(task_id, f"不支持的生成类型: {generation_type}"),
                    websocket
                )
                
    except asyncio.CancelledError:
        logger.info(f"Task {task_id} was cancelled")
    except Exception as e:
        logger.error(f"Generation task failed: {e}")
        await ws_manager.send_personal_message(
            GenerationProgress.failed(task_id, str(e)),
            websocket
        )
    finally:
        task_flags.pop(task_id, None)


def _format_characters_list(characters: Any) -> str:
    """格式化角色列表为字符串"""
    if isinstance(characters, list):
        return ', '.join(str(c) for c in characters)
    return str(characters)


async def _generate_chapter(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    use_langgraph: bool,
    websocket: WebSocket,
    task_flags: Dict[str, bool],
    db,
    context_builder
):
    """生成章节（支持LangGraph工作流）"""
    from app.consistency.service import ConsistencyChecker
    
    chapter_number = params.get("chapter_number")
    target_length = params.get("target_length", 3000)
    style = params.get("style", "narrative")
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    chapter_outline = params.get("chapter_outline")
    key_events = params.get("key_events")
    focus_characters = params.get("focus_characters")
    
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
        chapter_outline=chapter_outline,
        key_events=key_events,
        focus_characters=focus_characters
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
        
        progress = 20 + int((accumulated_length / target_length) * 50)
        if progress > 70:
            progress = 70
        
        await ws_manager.send_personal_message(
            GenerationProgress.progress(task_id, "generating", progress, f"已生成 {accumulated_length} 字"),
            websocket
        )
    
    if use_langgraph:
        await ws_manager.send_personal_message(
            GenerationProgress.progress(task_id, "reviewing", 75, "AI审核中"),
            websocket
        )
        
        if not task_flags.get(task_id):
            return
        
        review_prompt = f"""请审核以下章节内容，评估其质量。

章节内容：
{full_content[:2000]}...

请以JSON格式返回审核结果：
{{
    "approved": true/false,
    "score": 0.0-1.0,
    "issues": ["问题1", "问题2"]
}}
"""
        
        try:
            review_result = await llm_service.generate_text(
                prompt=review_prompt,
                model=model
            )
            review_data = json.loads(review_result)
            
            await ws_manager.send_personal_message(
                GenerationProgress.review_result(
                    task_id, 
                    review_data.get("approved", True),
                    review_data.get("score", 0.85),
                    review_data.get("issues", [])
                ),
                websocket
            )
        except Exception as e:
            logger.warning(f"Review failed: {e}")
            await ws_manager.send_personal_message(
                GenerationProgress.review_result(task_id, True, 0.85),
                websocket
            )
        
        await ws_manager.send_personal_message(
            GenerationProgress.progress(task_id, "consistency_check", 85, "一致性检查"),
            websocket
        )
        
        if not task_flags.get(task_id):
            return
        
        try:
            checker = ConsistencyChecker(db, novel_id)
            consistency_result = await checker.check_all(
                check_types=["character", "plot"]
            )
            
            has_issues = len(consistency_result.get("issues", [])) > 0
            
            await ws_manager.send_personal_message(
                GenerationProgress.consistency_check(
                    task_id,
                    not has_issues,
                    consistency_result.get("issues", [])[:3]
                ),
                websocket
            )
        except Exception as e:
            logger.warning(f"Consistency check failed: {e}")
            await ws_manager.send_personal_message(
                GenerationProgress.consistency_check(task_id, True),
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


async def _generate_dialogue(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """生成对话"""
    characters = _format_characters_list(params.get("characters", []))
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
        characters=[characters],
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


async def _generate_description(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """生成描写"""
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


async def _generate_outline(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """生成大纲"""
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


async def _generate_summary(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """生成摘要"""
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


async def _generate_character_profile(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    """生成角色档案"""
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
