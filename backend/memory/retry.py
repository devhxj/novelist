"""
Memory update retry mechanism.

When chapter memory (vector store) update fails after chapter content is saved,
the update is queued for retry to ensure eventual consistency between
the database and the vector store.

Uses app.core.redis_service for persistence so retries survive server restarts.
Falls back to in-memory dict if Redis is unavailable.
"""
import logging
import asyncio
import json
from datetime import datetime, timezone
from typing import Any

logger = logging.getLogger(__name__)

REDIS_KEY = "memory_retry_queue"
MAX_RETRY_ATTEMPTS = 3
RETRY_INTERVAL_SECONDS = 60

_pending_retries: dict[str, dict[str, Any]] = {}


async def schedule_memory_retry(novel_id: int, chapter_id: int) -> None:
    key = f"{novel_id}:{chapter_id}"
    entry = {
        "novel_id": novel_id,
        "chapter_id": chapter_id,
        "attempts": 0,
        "scheduled_at": datetime.now(timezone.utc).isoformat(),
    }

    _pending_retries[key] = entry
    logger.info(f"Scheduled memory retry for novel={novel_id}, chapter={chapter_id}")

    try:
        from core.redis_service import redis_service
        await redis_service.client.hset(REDIS_KEY, key, json.dumps(entry))
    except Exception as e:
        logger.warning(f"Redis schedule failed: {e}")


async def _load_all_pending() -> dict[str, dict[str, Any]]:
    merged = dict(_pending_retries)

    try:
        from core.redis_service import redis_service
        raw: Any = await redis_service.client.hgetall(REDIS_KEY)
        for key_bytes, val_bytes in raw.items():
            key = key_bytes if isinstance(key_bytes, str) else key_bytes.decode()
            val = val_bytes if isinstance(val_bytes, str) else val_bytes.decode()
            try:
                merged[key] = json.loads(val)
            except json.JSONDecodeError:
                pass
    except Exception as e:
        logger.warning(f"Failed to load retries from Redis: {e}")

    return merged


async def _remove_pending(key: str) -> None:
    _pending_retries.pop(key, None)

    try:
        from core.redis_service import redis_service
        await redis_service.client.hdel(REDIS_KEY, key)
    except Exception:
        pass


async def _update_pending(key: str, entry: dict[str, Any]) -> None:
    _pending_retries[key] = entry

    try:
        from core.redis_service import redis_service
        await redis_service.client.hset(REDIS_KEY, key, json.dumps(entry))
    except Exception:
        pass


async def execute_pending_retries() -> int:
    all_pending = await _load_all_pending()
    if not all_pending:
        return 0

    completed = 0
    done_keys: list[str] = []

    for key, info in list(all_pending.items()):
        novel_id = info["novel_id"]
        chapter_id = info["chapter_id"]
        info["attempts"] += 1

        try:
            from core.database import AsyncSessionLocal
            from rag.vector_store import vector_store
            from chapters.models import Chapter
            from sqlalchemy import select

            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(Chapter).where(Chapter.id == chapter_id)
                )
                chapter = result.scalar_one_or_none()
                if not chapter or not chapter.content:
                    logger.warning(f"Chapter {chapter_id} not found or empty, skipping retry")
                    done_keys.append(key)
                    continue

                vector_store.delete_chapter_chunks(novel_id, chapter.id)
                chunk_data = vector_store.build_chapter_chunks(
                    chapter_id=chapter.id,
                    chapter_number=chapter.chapter_number,
                    chapter_title=chapter.title,
                    content=chapter.content,
                    summary=chapter.summary,
                )
                if chunk_data:
                    vector_store.add_chunks(novel_id, chunk_data)

                logger.info(f"Memory retry succeeded for chapter {chapter_id}")
                completed += 1
                done_keys.append(key)

        except Exception as exc:
            if info["attempts"] >= MAX_RETRY_ATTEMPTS:
                logger.error(
                    f"Memory retry exhausted for chapter {chapter_id} "
                    f"after {info['attempts']} attempts: {exc}"
                )
                done_keys.append(key)
            else:
                logger.warning(
                    f"Memory retry attempt {info['attempts']} failed for chapter {chapter_id}: {exc}"
                )
                await _update_pending(key, info)

    for key in done_keys:
        await _remove_pending(key)

    return completed


async def _retry_loop() -> None:
    while True:
        try:
            await asyncio.sleep(RETRY_INTERVAL_SECONDS)
            count = await execute_pending_retries()
            if count > 0:
                logger.info(f"Memory retry loop completed {count} retries")
        except asyncio.CancelledError:
            logger.info("Memory retry loop cancelled")
            break
        except Exception as e:
            logger.error(f"Memory retry loop error: {e}")


_retry_task: asyncio.Task | None = None


def start_retry_background_task() -> None:
    global _retry_task
    if _retry_task is not None and not _retry_task.done():
        return
    _retry_task = asyncio.create_task(_retry_loop())
    logger.info("Memory retry background task started")


def stop_retry_background_task() -> None:
    global _retry_task
    if _retry_task and not _retry_task.done():
        _retry_task.cancel()
        _retry_task = None
        logger.info("Memory retry background task stopped")


async def get_pending_retry_count() -> int:
    return len(await _load_all_pending())


async def get_pending_retries_info() -> list[dict[str, Any]]:
    return [
        {
            "key": key,
            "novel_id": info["novel_id"],
            "chapter_id": info["chapter_id"],
            "attempts": info["attempts"],
            "scheduled_at": info["scheduled_at"],
        }
        for key, info in (await _load_all_pending()).items()
    ]
