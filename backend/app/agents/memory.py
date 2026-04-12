"""
记忆Agent - 负责向量索引更新和记忆维护
"""
import logging
from typing import Dict, Any, Optional

from sqlalchemy import select

from .base import BaseAgent, AgentTask, AgentResult, AgentRole, TaskType
from app.core.database import AsyncSessionLocal
from app.chapters.models import Chapter

logger = logging.getLogger(__name__)


class MemoryAgent(BaseAgent):
    """记忆Agent - 负责向量索引维护"""

    def __init__(self, agent_id: str = "memory_001"):
        super().__init__(agent_id, AgentRole.MEMORY)
        self.supported_tasks = {
            TaskType.UPDATE_MEMORY
        }

    def can_handle(self, task_type: TaskType) -> bool:
        return task_type in self.supported_tasks

    async def execute(self, task: AgentTask) -> AgentResult:
        self.log_task_start(task)

        try:
            if task.task_type == TaskType.UPDATE_MEMORY:
                result = await self._update_memory(task)
            else:
                result = self.create_result(
                    task=task,
                    success=False,
                    error=f"Unsupported task type: {task.task_type}"
                )

            self.log_task_complete(result)
            return result
        except Exception as e:
            self.logger.error(f"Error in memory task: {e}")
            return self.create_result(
                task=task,
                success=False,
                error=str(e)
            )

    async def _update_memory(self, task: AgentTask) -> AgentResult:
        chapter_id = task.chapter_id or task.parameters.get("chapter_id")
        chapter_number = task.parameters.get("chapter_number")

        async with AsyncSessionLocal() as db:
            from app.core.vector_store import vector_store

            query = select(Chapter).where(Chapter.novel_id == task.novel_id)
            if chapter_id:
                query = query.where(Chapter.id == chapter_id)
            elif chapter_number is not None:
                query = query.where(Chapter.chapter_number == chapter_number)
            else:
                return self.create_result(
                    task=task,
                    success=False,
                    error="缺少 chapter_id 或 chapter_number，无法更新记忆"
                )

            result = await db.execute(query)
            chapter = result.scalar_one_or_none()
            if not chapter:
                return self.create_result(
                    task=task,
                    success=False,
                    error="章节不存在"
                )

            content = chapter.content or ""
            if not content.strip():
                return self.create_result(
                    task=task,
                    success=True,
                    result={
                        "chapter_id": chapter.id,
                        "chunks_created": 0,
                        "message": "章节内容为空，跳过记忆更新"
                    }
                )

            vector_store.delete_chapter_chunks(task.novel_id, chapter.id)
            chunk_data = vector_store.build_chapter_chunks(
                chapter_id=chapter.id,
                chapter_number=chapter.chapter_number,
                chapter_title=chapter.title,
                content=content,
                summary=chapter.summary,
            )

            if chunk_data:
                vector_store.add_chunks(task.novel_id, chunk_data)

            return self.create_result(
                task=task,
                success=True,
                result={
                    "chapter_id": chapter.id,
                    "chapter_number": chapter.chapter_number,
                    "chunks_created": len(chunk_data),
                    "message": "记忆索引已更新"
                }
            )
