"""
生成API路由 - HTTP兜底（WebSocket不可用时使用）
支持所有LLM生成类型的HTTP异步生成
支持用户自定义提示词和模型选择
"""
import logging
import uuid
from fastapi import APIRouter, BackgroundTasks
from sqlalchemy import select, func
from typing import Any

from app.core.response import ApiResponse
from app.core.database import DBSession, AsyncSessionLocal
from app.core.dependencies import NovelOwner
from app.core.llm_service import llm_service
from app.core.context_builder import ContextBuilder
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

router = APIRouter(prefix="/generation", tags=["generation"])
logger = logging.getLogger(__name__)


def _format_characters_list(characters: Any) -> str:
    """格式化角色列表为字符串"""
    if isinstance(characters, list):
        return ', '.join(str(c) for c in characters)
    return str(characters)


@router.post("/novels/{novel_id}/generate")
async def generate_http(
    novel: NovelOwner,
    db: DBSession,
    background_tasks: BackgroundTasks,
    generation_type: str = "chapter",
    params: dict = None
):
    """
    通用生成接口（HTTP异步 - 兜底方案）
    
    推荐使用WebSocket: ws://host/ws/generation
    
    - generation_type: 生成类型 (chapter|dialogue|description|outline|summary|character_profile)
    - params: 生成参数（根据类型不同）
    
    通用参数:
    - model: LLM模型 (deepseek-chat|deepseek-reasoner)
    - style: 写作风格 (narrative|descriptive|dialogue|poetic|dramatic|natural|vivid)
    - user_prompt: 用户自定义提示词
    
    章节生成参数:
    - chapter_number: 章节号（不传则自动获取下一章）
    - target_length: 目标字数
    - chapter_outline: 章节大纲
    - key_events: 关键事件列表
    - focus_characters: 重点角色列表
    
    对话生成参数:
    - characters: 参与对话的角色列表
    - context: 对话场景背景
    
    描写生成参数:
    - subject: 描写对象
    
    大纲生成参数:
    - premise: 故事前提
    - genre: 类型
    - total_chapters: 总章节数
    
    摘要生成参数:
    - content: 原文内容
    - max_length: 最大长度
    
    角色档案生成参数:
    - name: 角色名
    - role: 角色定位
    - novel_context: 小说背景
    """
    params = params or {}
    task_id = f"http_gen_{novel.id}_{generation_type}_{uuid.uuid4().hex[:8]}"
    
    if generation_type == GenerationType.CHAPTER:
        chapter_number = params.get("chapter_number")
        if chapter_number is None:
            result = await db.execute(
                select(func.max(Chapter.chapter_number)).where(
                    Chapter.novel_id == novel.id
                )
            )
            max_chapter = result.scalar()
            chapter_number = (max_chapter or 0) + 1
            params["chapter_number"] = chapter_number
        
        result = await db.execute(
            select(Chapter).where(
                Chapter.novel_id == novel.id,
                Chapter.chapter_number == chapter_number
            )
        )
        existing = result.scalar_one_or_none()
        
        if existing and existing.status == "generating":
            return ApiResponse.error(
                code="GEN_001",
                message="章节正在生成中",
                status_code=409
            )
        
        if not existing:
            chapter = Chapter(
                novel_id=novel.id,
                chapter_number=chapter_number,
                title=f"第{chapter_number}章",
                content="",
                status="generating"
            )
            db.add(chapter)
            await db.commit()
        else:
            existing.status = "generating"
            await db.commit()
    
    background_tasks.add_task(
        _run_generation_task,
        novel_id=novel.id,
        generation_type=generation_type,
        params=params,
        task_id=task_id
    )
    
    return ApiResponse.success({
        "task_id": task_id,
        "generation_type": generation_type,
        "status": "generating",
        "message": "生成任务已提交（HTTP兜底模式）",
        "note": "推荐使用WebSocket获取实时进度: ws://host/ws/generation",
        "params": params
    })


@router.get("/novels/{novel_id}/tasks/{task_id}")
async def get_generation_status(
    novel: NovelOwner,
    task_id: str
):
    """获取生成任务状态"""
    return ApiResponse.success({
        "task_id": task_id,
        "status": "processing",
        "note": "HTTP模式无法获取实时进度，请使用WebSocket"
    })


@router.get("/novels/{novel_id}/chapters/{chapter_number}/status")
async def get_chapter_generation_status(
    novel: NovelOwner,
    db: DBSession,
    chapter_number: int
):
    """获取章节生成状态"""
    result = await db.execute(
        select(Chapter).where(
            Chapter.novel_id == novel.id,
            Chapter.chapter_number == chapter_number
        )
    )
    chapter = result.scalar_one_or_none()
    
    if not chapter:
        return ApiResponse.success({
            "exists": False,
            "status": "not_created"
        })
    
    return ApiResponse.success({
        "exists": True,
        "chapter_id": chapter.id,
        "chapter_number": chapter.chapter_number,
        "status": chapter.status,
        "word_count": len(chapter.content or ""),
        "has_content": bool(chapter.content)
    })


@router.get("/models")
async def list_models():
    """获取可用的LLM模型列表"""
    return ApiResponse.success({
        "models": get_available_models(),
        "default": "deepseek-chat"
    })


@router.get("/styles")
async def list_styles():
    """获取可用的写作风格列表"""
    return ApiResponse.success({
        "styles": get_available_styles(),
        "default": "narrative"
    })


@router.get("/types")
async def get_generation_types():
    """获取支持的生成类型"""
    return ApiResponse.success({
        "types": [
            {
                "value": "chapter",
                "label": "章节生成",
                "description": "生成小说章节内容",
                "params": [
                    {"key": "chapter_number", "label": "章节号", "type": "number", "required": False},
                    {"key": "target_length", "label": "目标字数", "type": "number", "default": 3000},
                    {"key": "style", "label": "写作风格", "type": "select", "options": "styles"},
                    {"key": "model", "label": "LLM模型", "type": "select", "options": "models"},
                    {"key": "user_prompt", "label": "自定义提示词", "type": "textarea"},
                    {"key": "chapter_outline", "label": "章节大纲", "type": "textarea"},
                    {"key": "key_events", "label": "关键事件", "type": "array"},
                    {"key": "focus_characters", "label": "重点角色", "type": "array"}
                ]
            },
            {
                "value": "dialogue",
                "label": "对话生成",
                "description": "生成角色对话内容",
                "params": [
                    {"key": "characters", "label": "参与角色", "type": "array", "required": True},
                    {"key": "context", "label": "场景背景", "type": "textarea", "required": True},
                    {"key": "style", "label": "对话风格", "type": "select", "options": "styles"},
                    {"key": "model", "label": "LLM模型", "type": "select", "options": "models"},
                    {"key": "user_prompt", "label": "自定义提示词", "type": "textarea"}
                ]
            },
            {
                "value": "description",
                "label": "描写生成",
                "description": "生成场景或人物描写",
                "params": [
                    {"key": "subject", "label": "描写对象", "type": "text", "required": True},
                    {"key": "style", "label": "描写风格", "type": "select", "options": "styles"},
                    {"key": "model", "label": "LLM模型", "type": "select", "options": "models"},
                    {"key": "user_prompt", "label": "自定义提示词", "type": "textarea"}
                ]
            },
            {
                "value": "outline",
                "label": "大纲生成",
                "description": "生成小说大纲",
                "params": [
                    {"key": "premise", "label": "故事前提", "type": "textarea", "required": True},
                    {"key": "genre", "label": "小说类型", "type": "text", "required": True},
                    {"key": "total_chapters", "label": "总章节数", "type": "number", "default": 20},
                    {"key": "model", "label": "LLM模型", "type": "select", "options": "models"},
                    {"key": "user_prompt", "label": "自定义提示词", "type": "textarea"}
                ]
            },
            {
                "value": "summary",
                "label": "摘要生成",
                "description": "生成内容摘要",
                "params": [
                    {"key": "content", "label": "原文内容", "type": "textarea", "required": True},
                    {"key": "max_length", "label": "最大字数", "type": "number", "default": 500},
                    {"key": "model", "label": "LLM模型", "type": "select", "options": "models"},
                    {"key": "user_prompt", "label": "自定义提示词", "type": "textarea"}
                ]
            },
            {
                "value": "character_profile",
                "label": "角色档案生成",
                "description": "生成角色详细档案",
                "params": [
                    {"key": "name", "label": "角色名", "type": "text", "required": True},
                    {"key": "role", "label": "角色定位", "type": "text", "required": True},
                    {"key": "novel_context", "label": "小说背景", "type": "textarea"},
                    {"key": "model", "label": "LLM模型", "type": "select", "options": "models"},
                    {"key": "user_prompt", "label": "自定义提示词", "type": "textarea"}
                ]
            }
        ],
        "note": "推荐使用WebSocket获取实时流式输出: ws://host/ws/generation"
    })


async def _run_generation_task(
    novel_id: int,
    generation_type: str,
    params: dict,
    task_id: str
):
    """后台任务：执行生成"""
    try:
        async with AsyncSessionLocal() as db:
            if generation_type == GenerationType.CHAPTER:
                await _generate_chapter_http(db, novel_id, params, task_id)
            else:
                await _generate_other_http(db, novel_id, generation_type, params, task_id)
                
    except Exception as e:
        logger.error(f"HTTP generation task {task_id} error: {e}")
        if generation_type == GenerationType.CHAPTER:
            try:
                async with AsyncSessionLocal() as db:
                    chapter_number = params.get("chapter_number")
                    if chapter_number:
                        result = await db.execute(
                            select(Chapter).where(
                                Chapter.novel_id == novel_id,
                                Chapter.chapter_number == chapter_number
                            )
                        )
                        chapter = result.scalar_one_or_none()
                        if chapter:
                            chapter.status = "failed"
                            await db.commit()
            except Exception:
                pass


async def _generate_chapter_http(
    db,
    novel_id: int,
    params: dict,
    task_id: str
):
    """HTTP章节生成"""
    chapter_number = params.get("chapter_number")
    target_length = params.get("target_length", 3000)
    style = params.get("style", "narrative")
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    chapter_outline = params.get("chapter_outline")
    key_events = params.get("key_events")
    focus_characters = params.get("focus_characters")
    
    context_builder = ContextBuilder(db, novel_id)
    context_data = await context_builder.build_writing_context(
        chapter_number=chapter_number,
        context_size=params.get("context_size", 3000),
        include_previous_chapters=True,
        include_characters=True,
        include_plot_events=True
    )
    
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
    
    result = await llm_service.generate_text(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    )
    
    db_result = await db.execute(
        select(Chapter).where(
            Chapter.novel_id == novel_id,
            Chapter.chapter_number == chapter_number
        )
    )
    chapter = db_result.scalar_one_or_none()
    
    if chapter:
        chapter.content = result
        chapter.status = "completed"
        chapter.word_count = len(result)
        await db.commit()
    
    logger.info(f"HTTP chapter generation task {task_id} completed: {len(result)} chars")


async def _generate_other_http(
    db,
    novel_id: int,
    generation_type: str,
    params: dict,
    task_id: str
):
    """HTTP其他类型生成"""
    model = params.get("model")
    user_prompt = params.get("user_prompt")
    
    if generation_type == GenerationType.DIALOGUE:
        characters = _format_characters_list(params.get("characters", []))
        context = params.get("context", "")
        style = params.get("style", "natural")
        
        system_prompt = get_system_prompt(GenerationType.DIALOGUE, style)
        user_message = build_dialogue_prompt(
            characters=[characters],
            context=context,
            style=style,
            user_prompt=user_prompt
        )
        
    elif generation_type == GenerationType.DESCRIPTION:
        subject = params.get("subject", "")
        style = params.get("style", "vivid")
        
        system_prompt = get_system_prompt(GenerationType.DESCRIPTION, style)
        user_message = build_description_prompt(
            subject=subject,
            style=style,
            user_prompt=user_prompt
        )
        
    elif generation_type == GenerationType.OUTLINE:
        premise = params.get("premise", "")
        genre = params.get("genre", "")
        total_chapters = params.get("total_chapters", 20)
        
        system_prompt = get_system_prompt(GenerationType.OUTLINE)
        user_message = build_outline_prompt(
            premise=premise,
            genre=genre,
            total_chapters=total_chapters,
            user_prompt=user_prompt
        )
        
    elif generation_type == GenerationType.SUMMARY:
        content = params.get("content", "")
        max_length = params.get("max_length", 500)
        
        system_prompt = get_system_prompt(GenerationType.SUMMARY)
        user_message = build_summary_prompt(
            content=content,
            max_length=max_length,
            user_prompt=user_prompt
        )
        
    elif generation_type == GenerationType.CHARACTER_PROFILE:
        name = params.get("name", "")
        role = params.get("role", "")
        novel_context = params.get("novel_context", "")
        
        system_prompt = get_system_prompt(GenerationType.CHARACTER_PROFILE)
        user_message = build_character_profile_prompt(
            name=name,
            role=role,
            novel_context=novel_context,
            user_prompt=user_prompt
        )
    else:
        logger.error(f"Unknown generation type: {generation_type}")
        return
    
    result = await llm_service.generate_text(
        prompt=user_message,
        system_prompt=system_prompt,
        model=model
    )
    
    logger.info(f"HTTP {generation_type} generation task {task_id} completed: {len(result)} chars")
