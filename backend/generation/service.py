"""
章节生成服务 - 整合RAG、Memory、Agent系统
"""
import logging
from typing import Any
from datetime import datetime, timezone
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from context.context_builder import ContextBuilder
from chapters.post_processor import ChapterPostProcessor
from text.utils import count_words
from agents.base import AgentTask, TaskType
from agents.factory import create_default_coordinator
from novels.models import Novel
from chapters.models import Chapter
from chapters.summary import generate_chapter_summary

logger = logging.getLogger(__name__)


class ChapterGenerationService:
    """章节生成服务"""
    
    def __init__(self, db: AsyncSession, novel_id: int):
        self.db = db
        self.novel_id = novel_id
        self.context_builder = ContextBuilder(db, novel_id)
        self.coordinator = create_default_coordinator()
    
    async def _get_novel(self) -> Novel | None:
        """获取小说"""
        result = await self.db.execute(
            select(Novel).where(Novel.id == self.novel_id)
        )
        return result.scalar_one_or_none()
    
    async def generate_chapter(
        self,
        chapter_number: int,
        target_length: int = 3000,
        style: str = "narrative",
        additional_context: dict[str, Any] | None = None,
        agent_role: str | None = None,
        model: str | None = None,
        context_size: int = 3000
    ) -> dict[str, Any]:
        """
        生成章节完整流程
        
        Args:
            chapter_number: 章节号
            target_length: 目标字数
            style: 写作风格
            additional_context: 额外上下文
            
        Returns:
            生成结果
        """
        logger.info(f"Starting chapter {chapter_number} generation for novel {self.novel_id}")
        
        try:
            context = await self._prepare_context(
                chapter_number,
                additional_context,
                context_size=context_size
            )
            extra_parameters = self._build_generation_parameters(additional_context)

            task = AgentTask(
                task_id=f"gen_{self.novel_id}_{chapter_number}_{datetime.now(timezone.utc).timestamp()}",
                task_type=TaskType.GENERATE_CHAPTER,
                novel_id=self.novel_id,
                parameters={
                    "chapter_number": chapter_number,
                    "target_length": target_length,
                    "style": style,
                    "agent_role": agent_role,
                    "model": model,
                    **extra_parameters
                },
                context=context
            )
            
            result = await self.coordinator.execute(task)
            
            if result.success:
                chapter = await self._save_chapter(
                    chapter_number=chapter_number,
                    content=result.result.get("content", ""),
                    style=style
                )
                
                await self._update_chapter_memory(chapter.id)
                
                logger.info(f"Chapter {chapter_number} generated successfully: {chapter.id}")
                
                return {
                    "success": True,
                    "chapter_id": chapter.id,
                    "chapter_number": chapter_number,
                    "content": chapter.content,
                    "word_count": count_words(chapter.content or ""),
                    "suggestions": result.suggestions
                }
            else:
                logger.error(f"Chapter generation failed: {result.error}")
                return {
                    "success": False,
                    "error": result.error
                }
                
        except Exception as e:
            logger.error(f"Chapter generation error: {e}")
            return {
                "success": False,
                "error": str(e)
            }
    
    async def _prepare_context(
        self,
        chapter_number: int,
        additional_context: dict[str, Any] | None = None,
        context_size: int = 3600
    ) -> dict[str, Any]:
        """准备生成上下文"""
        logger.info(f"Preparing context for chapter {chapter_number}")
        layered_context = await self.context_builder.build_writing_context(
            chapter_number=chapter_number,
            context_size=context_size,
            include_previous_chapters=True,
            include_characters=True,
        )

        context: dict[str, Any] = {
            "previous_summary": layered_context.get("previous_summary"),
            "characters": layered_context.get("characters", []),
            "plot_hints": layered_context.get("plot_hints", []),
            "author_preferences": {},
        }

        if additional_context:
            key_events = additional_context.get("key_events")
            if key_events:
                context.setdefault("plot_hints", [])
                context["plot_hints"].extend(
                    {"id": None, "type": "planned_event", "description": str(item)}
                    for item in key_events
                )
            context.update(additional_context)

        return context

    def _build_generation_parameters(self, additional_context: dict[str, Any] | None = None) -> dict[str, Any]:
        additional_context = additional_context or {}
        parameters: dict[str, Any] = {}
        if additional_context.get("user_prompt"):
            parameters["writing_task"] = additional_context["user_prompt"]
        if additional_context.get("chapter_outline"):
            parameters["outline"] = additional_context["chapter_outline"]
        if additional_context.get("tone"):
            parameters["tone"] = additional_context["tone"]
        if additional_context.get("author_intent"):
            parameters["author_intent"] = additional_context["author_intent"]
        if additional_context.get("scene_goal"):
            parameters["scene_goal"] = additional_context["scene_goal"]
        if additional_context.get("must_keep"):
            parameters["must_keep"] = additional_context["must_keep"]
        if additional_context.get("must_avoid"):
            parameters["must_avoid"] = additional_context["must_avoid"]
        return parameters
    
    async def _save_chapter(
        self,
        chapter_number: int,
        content: str,
        style: str
    ) -> Chapter:
        """保存章节"""
        result = await self.db.execute(
            select(Chapter).where(
                Chapter.novel_id == self.novel_id,
                Chapter.chapter_number == chapter_number
            )
        )
        existing = result.scalar_one_or_none()
        
        if existing:
            existing.content = content
            existing.status = "completed"
            existing.word_count = count_words(content)
            await self.db.commit()
            await self.db.refresh(existing)
            chapter = existing
        else:
            chapter = Chapter(
                novel_id=self.novel_id,
                chapter_number=chapter_number,
                title=f"第{chapter_number}章",
                content=content,
                summary=None,
                status="completed",
                word_count=count_words(content)
            )
            self.db.add(chapter)
            await self.db.commit()
            await self.db.refresh(chapter)

        post_processor = ChapterPostProcessor(self.db, self.novel_id)
        try:
            process_result = await post_processor.process(
                content=chapter.content or "",
                chapter_number=chapter.chapter_number,
                chapter_id=chapter.id
            )
            chapter.content = process_result.get("final_content", chapter.content)
            chapter.word_count = count_words(chapter.content or "")
            chapter.summary = await self._generate_chapter_summary(chapter.content or "")
            await self.db.commit()
            await self.db.refresh(chapter)
        except Exception as exc:
            logger.warning(f"Chapter post-processing failed for chapter {chapter.id}: {exc}")
            chapter.summary = await self._generate_chapter_summary(chapter.content or "")
            await self.db.commit()
            await self.db.refresh(chapter)

        return chapter
    
    async def _update_chapter_memory(self, chapter_id: int) -> None:
        from rag.memory_updater import schedule_memory_update
        schedule_memory_update(self.novel_id, chapter_id)

    async def _get_chapter(self, chapter_number: int) -> Chapter | None:
        result = await self.db.execute(
            select(Chapter).where(
                Chapter.novel_id == self.novel_id,
                Chapter.chapter_number == chapter_number
            )
        )
        return result.scalar_one_or_none()

    async def _generate_chapter_summary(self, content: str) -> str | None:
        return await generate_chapter_summary(content)
    
    async def regenerate_chapter(
        self,
        chapter_id: int,
        feedback: str | None = None
    ) -> dict[str, Any]:
        """重新生成章节"""
        result = await self.db.execute(
            select(Chapter).where(Chapter.id == chapter_id)
        )
        chapter = result.scalar_one_or_none()
        if not chapter:
            return {"success": False, "error": "章节不存在"}
        
        additional_context = {}
        if feedback:
            additional_context["feedback"] = feedback
            additional_context["previous_content"] = chapter.content
        
        return await self.generate_chapter(
            chapter_number=chapter.chapter_number,
            additional_context=additional_context
        )
    
    async def get_generation_status(self, task_id: str) -> dict[str, Any]:
        """获取生成状态"""
        status = self.coordinator.get_task_status(task_id)
        return status or {"status": "not_found"}
