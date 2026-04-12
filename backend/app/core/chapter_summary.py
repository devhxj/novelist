"""
章节摘要工具
统一章节摘要生成逻辑，避免不同链路出现分叉。
"""
from typing import Optional
import logging

from app.core.llm_service import llm_service

logger = logging.getLogger(__name__)


async def generate_chapter_summary(content: str) -> Optional[str]:
    if not content or len(content.strip()) < 200:
        return content[:200] if content else None

    prompt = (
        "请为以下小说章节生成一段120字以内的剧情摘要，"
        "只保留关键情节推进、人物变化和伏笔，不要评价。\n\n"
        f"{content[:4000]}"
    )
    try:
        summary = await llm_service.generate_text(
            prompt=prompt,
            system_prompt="你是长篇小说章节摘要助手。",
            max_tokens=200
        )
        return summary.strip()
    except Exception as e:
        logger.warning(f"Failed to generate chapter summary: {e}")
        return content[:200]
