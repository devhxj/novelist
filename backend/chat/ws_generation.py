"""
WebSocket 生成任务 - 从 ws_chat.py 提取
章节生成、对话生成、描写生成等流式生成任务
"""
import asyncio
import logging
from typing import Dict, Any

from fastapi import WebSocket
from sqlalchemy import select, func

from core.websocket import ws_manager, GenerationProgress
from core.database import AsyncSessionLocal
from core.llm_service import llm_service
from context.context_builder import ContextBuilder
from context.prompt_templates import (
    get_system_prompt,
    build_chapter_prompt,
    build_dialogue_prompt,
    build_description_prompt,
    build_outline_prompt,
    build_summary_prompt,
    build_character_profile_prompt,
    GenerationType
)
from chapters.models import Chapter
from text.utils import count_words, compute_text_stats
from generation.service import ChapterGenerationService
from chat.ws_utils import _friendly_error_message

logger = logging.getLogger(__name__)


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
    )

    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "generating", 20, "开始生成章节"),
        websocket
    )

    if not task_flags.get(task_id):
        return

    use_langgraph = params.get("use_langgraph")
    if use_langgraph:
        logger.warning(f"[DEPRECATED] LangGraph workflow is deprecated, ignoring use_langgraph=True for task {task_id}")

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
    stats_interval = 0

    async for chunk in llm_service.generate_stream(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    ):
        if not task_flags.get(task_id):
            return

        full_content += chunk
        accumulated_length += len(chunk)
        stats_interval += 1

        text_stats = None
        if stats_interval % 10 == 0:
            text_stats = compute_text_stats(full_content).to_dict()

        await ws_manager.send_personal_message(
            GenerationProgress.content_chunk(task_id, chunk, accumulated_length, text_stats=text_stats),
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
        chapter.word_count = count_words(full_content)
    else:
        chapter = Chapter(
            novel_id=novel_id,
            chapter_number=chapter_number,
            title=f"第{chapter_number}章",
            content=full_content,
            status="completed",
            word_count=count_words(full_content)
        )
        db.add(chapter)

    await db.commit()
    await db.refresh(chapter)

    from chapters.post_processor import ChapterPostProcessor
    try:
        post_processor = ChapterPostProcessor(db, novel_id)
        process_result = await post_processor.process(
            content=chapter.content or "",
            chapter_number=chapter.chapter_number,
            chapter_id=chapter.id,
        )
        if process_result.get("was_truncated"):
            chapter.content = process_result["final_content"]
        else:
            chapter.content = process_result.get("final_content", chapter.content)
        chapter.word_count = count_words(chapter.content or "")
        await db.commit()
    except Exception as exc:
        logger.warning(f"Chapter post-processing failed (non-fatal) in _generate_chapter_ws: {exc}")

    try:
        service = ChapterGenerationService(db, novel_id)
        chapter.summary = await service._generate_chapter_summary(chapter.content or "")
        await db.commit()
    except Exception as e:
        logger.warning(f"Failed to generate chapter summary in _generate_chapter_ws: {e}")

    try:
        service = ChapterGenerationService(db, novel_id)
        await service._update_chapter_memory(chapter.id)
    except Exception as e:
        logger.warning(f"Failed to update chapter memory after WS fallback generation: {e}")
        from memory.retry import schedule_memory_retry
        await schedule_memory_retry(novel_id, chapter.id)

    final_stats = compute_text_stats(full_content).to_dict()
    await ws_manager.send_personal_message(
        GenerationProgress.completed(
            task_id=task_id,
            chapter_id=chapter.id,
            chapter_number=chapter_number,
            content=full_content,
            word_count=count_words(full_content),
            text_stats=final_stats
        ),
        websocket
    )


async def _generate_streaming_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool],
    gen_type: GenerationType,
    progress_label: str,
    prompt_builder,
    style: str | None = None,
):
    await ws_manager.send_personal_message(
        GenerationProgress.progress(task_id, "generating", 30, progress_label),
        websocket
    )

    if not task_flags.get(task_id):
        return

    system_prompt = get_system_prompt(gen_type, style)
    user_message = prompt_builder()

    full_content = ""
    accumulated_length = 0
    stats_interval = 0

    async for chunk in llm_service.generate_stream(
        prompt=user_message,
        system_prompt=system_prompt,
        model=params.get("model")
    ):
        if not task_flags.get(task_id):
            return

        full_content += chunk
        accumulated_length += len(chunk)
        stats_interval += 1

        text_stats = None
        if stats_interval % 10 == 0:
            text_stats = compute_text_stats(full_content).to_dict()

        await ws_manager.send_personal_message(
            GenerationProgress.content_chunk(task_id, chunk, accumulated_length, text_stats=text_stats),
            websocket
        )

    final_stats = compute_text_stats(full_content).to_dict()
    await ws_manager.send_personal_message(
        GenerationProgress.completed(
            task_id=task_id,
            chapter_id=None,
            chapter_number=None,
            content=full_content,
            word_count=count_words(full_content),
            text_stats=final_stats
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
    await _generate_streaming_ws(
        task_id, novel_id, params, websocket, task_flags,
        gen_type=GenerationType.DIALOGUE,
        progress_label="生成对话中",
        style=params.get("style", "natural"),
        prompt_builder=lambda: build_dialogue_prompt(
            characters=[str(c) for c in params.get("characters", [])],
            context=params.get("context", ""),
            style=params.get("style", "natural"),
            user_prompt=params.get("user_prompt")
        ),
    )


async def _generate_description_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    await _generate_streaming_ws(
        task_id, novel_id, params, websocket, task_flags,
        gen_type=GenerationType.DESCRIPTION,
        progress_label="生成描写中",
        style=params.get("style", "vivid"),
        prompt_builder=lambda: build_description_prompt(
            subject=params.get("subject", ""),
            style=params.get("style", "vivid"),
            user_prompt=params.get("user_prompt")
        ),
    )


async def _generate_outline_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    await _generate_streaming_ws(
        task_id, novel_id, params, websocket, task_flags,
        gen_type=GenerationType.OUTLINE,
        progress_label="生成大纲中",
        prompt_builder=lambda: build_outline_prompt(
            premise=params.get("premise", ""),
            genre=params.get("genre", ""),
            total_chapters=params.get("total_chapters", 20),
            user_prompt=params.get("user_prompt")
        ),
    )


async def _generate_summary_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    await _generate_streaming_ws(
        task_id, novel_id, params, websocket, task_flags,
        gen_type=GenerationType.SUMMARY,
        progress_label="生成摘要中",
        prompt_builder=lambda: build_summary_prompt(
            content=params.get("content", ""),
            max_length=params.get("max_length", 500),
            user_prompt=params.get("user_prompt")
        ),
    )


async def _generate_character_profile_ws(
    task_id: str,
    novel_id: int,
    params: Dict[str, Any],
    websocket: WebSocket,
    task_flags: Dict[str, bool]
):
    await _generate_streaming_ws(
        task_id, novel_id, params, websocket, task_flags,
        gen_type=GenerationType.CHARACTER_PROFILE,
        progress_label="生成角色档案中",
        prompt_builder=lambda: build_character_profile_prompt(
            name=params.get("name", ""),
            role=params.get("role", ""),
            novel_context=params.get("novel_context", ""),
            user_prompt=params.get("user_prompt")
        ),
    )
