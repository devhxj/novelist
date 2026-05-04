"""
SubAgent上下文自动注入

根据SubAgentSpec声明的上下文需求，自动从数据库构建上下文。
主Agent无需手动传递章节内容、角色列表等，后端自动交付。
"""
import logging
from typing import Any, Callable, Awaitable

from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from chapters.models import Chapter
from characters.models import Character
from agents.base import SubAgentSpec

logger = logging.getLogger(__name__)

ContextBuilderFunc = Callable[[AsyncSession, int, int | None], Awaitable[Any]]


_CONTEXT_BUILDERS: dict[str, ContextBuilderFunc] = {}


def register_context_builder(key: str):
    def decorator(func: ContextBuilderFunc):
        _CONTEXT_BUILDERS[key] = func
        return func
    return decorator


@register_context_builder("chapter_content")
async def _get_chapter_content(db: AsyncSession, novel_id: int, chapter_id: int | None) -> str | None:
    if not chapter_id:
        return None
    result = await db.execute(select(Chapter).where(Chapter.id == chapter_id))
    chapter = result.scalar_one_or_none()
    return chapter.content if chapter else None


@register_context_builder("chapter_info")
async def _get_chapter_info(db: AsyncSession, novel_id: int, chapter_id: int | None) -> dict[str, Any] | None:
    if not chapter_id:
        return None
    result = await db.execute(select(Chapter).where(Chapter.id == chapter_id))
    chapter = result.scalar_one_or_none()
    if not chapter:
        return None
    return {
        "chapter_id": chapter.id,
        "chapter_number": chapter.chapter_number,
        "title": chapter.title,
        "summary": chapter.summary,
        "word_count": chapter.word_count,
        "status": chapter.status,
    }


@register_context_builder("characters")
async def _get_characters(db: AsyncSession, novel_id: int, chapter_id: int | None) -> list[dict[str, Any]]:
    result = await db.execute(
        select(Character).where(Character.novel_id == novel_id)
    )
    characters = result.scalars().all()
    return [
        {
            "id": c.id,
            "name": c.name,
            "role": c.role,
            "description": c.description,
            "personality": c.personality,
        }
        for c in characters
    ]




@register_context_builder("previous_summary")
async def _get_previous_summary(db: AsyncSession, novel_id: int, chapter_id: int | None) -> str | None:
    if not chapter_id:
        return None
    result = await db.execute(select(Chapter).where(Chapter.id == chapter_id))
    chapter = result.scalar_one_or_none()
    if not chapter or chapter.chapter_number <= 1:
        return ""
    prev_result = await db.execute(
        select(Chapter)
        .where(Chapter.novel_id == novel_id, Chapter.chapter_number == chapter.chapter_number - 1)
    )
    prev_chapter = prev_result.scalar_one_or_none()
    return prev_chapter.summary if prev_chapter else ""


@register_context_builder("layered_context")
async def _get_layered_context(db: AsyncSession, novel_id: int, chapter_id: int | None) -> dict[str, Any]:
    try:
        from core.context_builder import ContextBuilder
        builder = ContextBuilder(db, novel_id)
        chapter_number = 1
        if chapter_id:
            ch_result = await db.execute(select(Chapter).where(Chapter.id == chapter_id))
            ch = ch_result.scalar_one_or_none()
            if ch:
                chapter_number = ch.chapter_number
        ctx = await builder.build_writing_context(
            chapter_number=chapter_number,
            context_size=4000,
        )
        return ctx
    except Exception as e:
        logger.warning(f"Failed to build layered context: {e}")
        return {}


@register_context_builder("active_story_arcs")
async def _get_active_story_arcs(db: AsyncSession, novel_id: int, chapter_id: int | None) -> list[dict[str, Any]]:
    try:
        from story_arcs.service import StoryArcService
        service = StoryArcService(db, novel_id)
        return await service.get_active_arcs(chapter_id or 1)
    except Exception as e:
        logger.warning(f"Failed to load active story arcs: {e}")
        return []


@register_context_builder("unresolved_foreshadowings")
async def _get_unresolved_foreshadowings(
    db: AsyncSession,
    novel_id: int,
    chapter_id: int | None,
) -> list[dict[str, Any]]:
    try:
        from timeline.models import TimelineEntry, TimelineEntryCategory, TimelineEntryStatus
        from sqlalchemy import select
        result = await db.execute(
            select(TimelineEntry)
            .where(
                TimelineEntry.novel_id == novel_id,
                TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value,
                TimelineEntry.status.in_([TimelineEntryStatus.PENDING.value, TimelineEntryStatus.ACTIVE.value]),
            )
            .order_by(TimelineEntry.importance.desc())
            .limit(10)
        )
        return [
            {"id": e.id, "title": e.title, "description": e.description, "importance": e.importance, "status": e.status}
            for e in result.scalars().all()
        ]
    except Exception as e:
        logger.warning(f"Failed to load unresolved foreshadowings: {e}")
        return []


@register_context_builder("consistency_result")
async def _get_consistency_result(db: AsyncSession, novel_id: int, chapter_id: int | None) -> dict[str, Any] | None:
    try:
        from consistency.service import ConsistencyChecker
        checker = ConsistencyChecker(db, novel_id)
        return await checker.check_all(
            chapter_ids=[chapter_id] if chapter_id else None,
        )
    except Exception as e:
        logger.warning(f"Failed to check consistency: {e}")
        return None


async def build_subagent_context(
    db: AsyncSession,
    novel_id: int,
    spec: SubAgentSpec,
    chapter_id: int | None = None,
    instruction: str | None = None,
    extra_parameters: dict[str, Any] | None = None,
) -> dict[str, Any]:
    context: dict[str, Any] = {}

    for key in spec.required_context_keys:
        builder = _CONTEXT_BUILDERS.get(key)
        if builder:
            try:
                context[key] = await builder(db, novel_id, chapter_id)
            except Exception as e:
                logger.error(f"Failed to build required context '{key}': {e}")
                context[key] = None
        else:
            logger.warning(f"No context builder registered for key '{key}'")

    for key in spec.optional_context_keys:
        builder = _CONTEXT_BUILDERS.get(key)
        if builder:
            try:
                result = await builder(db, novel_id, chapter_id)
                if result is not None:
                    context[key] = result
            except Exception:
                pass

    if instruction:
        context["instruction"] = instruction

    if extra_parameters:
        context["extra_parameters"] = extra_parameters

    return context
