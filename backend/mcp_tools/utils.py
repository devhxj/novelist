
async def _invalidate_novel_cache(novel_id: int) -> None:
    try:
        from core.redis_service import redis_service
        from context.context_builder import context_cache
        await redis_service.clear_pattern(f"novel:{novel_id}:*")
        context_cache.invalidate_novel(novel_id)
    except Exception:
        pass


async def _invalidate_character_cache(novel_id: int, character_id: int | None = None) -> None:
    try:
        from core.redis_service import redis_service
        if character_id:
            await redis_service.delete(f"character:{character_id}:detail")
            await redis_service.delete(f"character:{character_id}:memory")
        await redis_service.clear_pattern(f"novel:{novel_id}:characters:*")
    except Exception:
        pass
    await _invalidate_novel_cache(novel_id)


async def _invalidate_chapter_cache(novel_id: int, chapter_id: int | None = None) -> None:
    try:
        from core.redis_service import redis_service
        if chapter_id:
            await redis_service.delete(f"chapter:{chapter_id}:detail")
        await redis_service.clear_pattern(f"novel:{novel_id}:chapters:*")
    except Exception:
        pass
    await _invalidate_novel_cache(novel_id)
