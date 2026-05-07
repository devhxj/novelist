"""
向量记忆自动更新器

章节内容变更后自动重建向量索引，含防抖机制。
"""
import asyncio
import logging

from sqlalchemy import select

from core.database import AsyncSessionLocal
from chapters.models import Chapter
from rag.vector_store import vector_store

logger = logging.getLogger(__name__)

# 防抖：每个 chapter_id 对应一个待执行任务
_pending: dict[int, asyncio.Task] = {}
DEBOUNCE_SECONDS = 10


async def _do_update(novel_id: int, chapter_id: int):
    """实际执行向量索引重建"""
    async with AsyncSessionLocal() as db:
        result = await db.execute(
            select(Chapter).where(
                Chapter.novel_id == novel_id,
                Chapter.id == chapter_id,
            )
        )
        chapter = result.scalar_one_or_none()
        if not chapter or not chapter.content:
            logger.debug(f"Memory update skipped: chapter {chapter_id} has no content")
            return

        vector_store.delete_chapter_chunks(novel_id, chapter_id)
        chunk_data = vector_store.build_chapter_chunks(
            chapter_id=chapter.id,
            chapter_number=chapter.chapter_number,
            chapter_title=chapter.title,
            content=chapter.content,
            summary=chapter.summary,
        )
        if chunk_data:
            vector_store.add_chunks(novel_id, chunk_data)
            logger.info(f"Memory updated: ch{chapter.chapter_number} ({len(chunk_data)} chunks)")


def schedule_memory_update(novel_id: int, chapter_id: int):
    """安排向量索引更新（防抖：多次调用只执行最后一次）"""
    if chapter_id in _pending:
        _pending[chapter_id].cancel()

    async def _debounced():
        try:
            await asyncio.sleep(DEBOUNCE_SECONDS)
            await _do_update(novel_id, chapter_id)
        except asyncio.CancelledError:
            pass
        finally:
            _pending.pop(chapter_id, None)

    _pending[chapter_id] = asyncio.create_task(_debounced())
