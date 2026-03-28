"""
章节生成服务 - 整合RAG、Memory、Agent系统
"""
import logging
from typing import Dict, Any, Optional, List
from datetime import datetime
from sqlalchemy.orm import Session

from app.core.context_builder import ContextBuilder
from app.core.vector_store import vector_store
from app.agents.base import AgentTask, TaskType
from app.agents.coordinator import CoordinatorAgent
from app.novels.models import Novel
from app.chapters.models import Chapter
from app.characters.models import Character
from app.plot_events.models import PlotEvent

logger = logging.getLogger(__name__)


class ChapterGenerationService:
    """章节生成服务"""
    
    def __init__(self, db: Session, novel_id: int):
        self.db = db
        self.novel_id = novel_id
        self.novel = db.query(Novel).filter(Novel.id == novel_id).first()
        self.context_builder = ContextBuilder(db, novel_id)
        self.coordinator = CoordinatorAgent()
        
        from app.agents.writer import WriterAgent
        from app.agents.reviewer import ReviewerAgent
        self.coordinator.register_agent(WriterAgent())
        self.coordinator.register_agent(ReviewerAgent())
    
    async def generate_chapter(
        self,
        chapter_number: int,
        target_length: int = 3000,
        style: str = "narrative",
        additional_context: Optional[Dict[str, Any]] = None
    ) -> Dict[str, Any]:
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
            context = await self._prepare_context(chapter_number, additional_context)
            
            task = AgentTask(
                task_id=f"gen_{self.novel_id}_{chapter_number}_{datetime.now().timestamp()}",
                task_type=TaskType.GENERATE_CHAPTER,
                novel_id=self.novel_id,
                parameters={
                    "chapter_number": chapter_number,
                    "target_length": target_length,
                    "style": style
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
                
                await self._index_chapter(chapter)
                
                logger.info(f"Chapter {chapter_number} generated successfully: {chapter.id}")
                
                return {
                    "success": True,
                    "chapter_id": chapter.id,
                    "chapter_number": chapter_number,
                    "content": chapter.content,
                    "word_count": len(chapter.content),
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
        additional_context: Optional[Dict[str, Any]] = None
    ) -> Dict[str, Any]:
        """准备生成上下文"""
        logger.info(f"Preparing context for chapter {chapter_number}")
        
        context = {}
        
        previous_chapters = self.db.query(Chapter).filter(
            Chapter.novel_id == self.novel_id,
            Chapter.chapter_number < chapter_number
        ).order_by(Chapter.chapter_number.desc()).limit(3).all()
        
        if previous_chapters:
            summaries = []
            for ch in reversed(previous_chapters):
                if ch.summary:
                    summaries.append(f"第{ch.chapter_number}章: {ch.summary}")
                elif ch.content:
                    summaries.append(f"第{ch.chapter_number}章: {ch.content[:200]}...")
            context["previous_summary"] = "\n".join(summaries)
        
        characters = self.db.query(Character).filter(
            Character.novel_id == self.novel_id
        ).all()
        context["characters"] = [
            {
                "id": char.id,
                "name": char.name,
                "personality": char.personality,
                "abilities": char.abilities
            }
            for char in characters
        ]
        
        plot_events = self.db.query(PlotEvent).filter(
            PlotEvent.novel_id == self.novel_id
        ).order_by(PlotEvent.created_at.desc()).limit(10).all()
        context["plot_hints"] = [
            {
                "id": event.id,
                "type": event.event_type,
                "description": event.description
            }
            for event in plot_events
        ]
        
        if additional_context:
            context.update(additional_context)
        
        return context
    
    async def _save_chapter(
        self,
        chapter_number: int,
        content: str,
        style: str
    ) -> Chapter:
        """保存章节"""
        existing = self.db.query(Chapter).filter(
            Chapter.novel_id == self.novel_id,
            Chapter.chapter_number == chapter_number
        ).first()
        
        if existing:
            existing.content = content
            existing.status = "completed"
            existing.updated_at = datetime.now()
            self.db.commit()
            self.db.refresh(existing)
            return existing
        else:
            chapter = Chapter(
                novel_id=self.novel_id,
                chapter_number=chapter_number,
                title=f"第{chapter_number}章",
                content=content,
                status="completed"
            )
            self.db.add(chapter)
            self.db.commit()
            self.db.refresh(chapter)
            return chapter
    
    async def _index_chapter(self, chapter: Chapter):
        """索引章节到向量存储"""
        try:
            if not chapter.content:
                return
            
            chunks = self._split_text(chapter.content)
            
            chunk_data = []
            for i, chunk_content in enumerate(chunks):
                chunk_data.append({
                    "id": f"{chapter.id}_{i}",
                    "content": chunk_content,
                    "chapter_id": chapter.id,
                    "chunk_type": "content",
                    "chunk_index": i,
                    "metadata": {
                        "chapter_number": chapter.chapter_number,
                        "chapter_title": chapter.title
                    }
                })
            
            if chunk_data:
                vector_store.add_chunks(self.novel_id, chunk_data)
                logger.info(f"Indexed {len(chunk_data)} chunks for chapter {chapter.chapter_number}")
                
        except Exception as e:
            logger.error(f"Failed to index chapter: {e}")
    
    def _split_text(self, text: str, chunk_size: int = 500, overlap: int = 50) -> List[str]:
        """分割文本"""
        if not text:
            return []
        
        chunks = []
        start = 0
        while start < len(text):
            end = start + chunk_size
            chunk = text[start:end]
            if chunk.strip():
                chunks.append(chunk.strip())
            start = end - overlap
        
        return chunks
    
    async def regenerate_chapter(
        self,
        chapter_id: int,
        feedback: Optional[str] = None
    ) -> Dict[str, Any]:
        """重新生成章节"""
        chapter = self.db.query(Chapter).filter(Chapter.id == chapter_id).first()
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
    
    async def get_generation_status(self, task_id: str) -> Dict[str, Any]:
        """获取生成状态"""
        status = self.coordinator.get_task_status(task_id)
        return status or {"status": "not_found"}
