"""
文本编辑服务 - 副本编辑机制
"""
import asyncio
import logging
import uuid
from datetime import datetime, timezone
from typing import Optional, List, Dict, Any
from sqlalchemy import select, and_
from sqlalchemy.ext.asyncio import AsyncSession

from app.editor.models import EditSession, EditSessionStatus, EditChange, ChangeSource
from app.core.diff_engine import diff_engine, DiffChangeType
from app.chapters.models import Chapter
from app.core.vector_store import vector_store
from app.core.chapter_summary import generate_chapter_summary
from app.core.chapter_post_processor import ChapterPostProcessor

logger = logging.getLogger(__name__)


class EditSessionManager:
    """编辑会话管理器 - 管理副本编辑"""
    
    def __init__(self, db: AsyncSession):
        self.db = db
    
    async def create_edit_session(
        self,
        chapter_id: int,
        ws_session_id: str
    ) -> EditSession:
        """创建编辑会话（副本）"""
        existing = await self.get_edit_session(chapter_id)
        if existing:
            return existing

        result = await self.db.execute(
            select(Chapter).where(Chapter.id == chapter_id)
        )
        chapter = result.scalar_one_or_none()
        
        if not chapter:
            raise ValueError(f"Chapter {chapter_id} not found")
        
        edit_session_id = f"edit_{uuid.uuid4().hex[:12]}"
        
        edit_session = EditSession(
            edit_session_id=edit_session_id,
            chapter_id=chapter_id,
            ws_session_id=ws_session_id,
            original_content=chapter.content or "",
            working_content=chapter.content or "",
            status=EditSessionStatus.PENDING,
            change_count=0,
            extra_metadata={
                "source_chapter_updated_at": chapter.updated_at.isoformat() if chapter.updated_at else None,
                "source_word_count": chapter.word_count or 0,
                "created_from_ws_session": ws_session_id
            }
        )
        
        self.db.add(edit_session)
        await self.db.commit()
        await self.db.refresh(edit_session)
        
        logger.info(f"Created edit session {edit_session_id} for chapter {chapter_id}")
        return edit_session
    
    async def get_edit_session(
        self,
        chapter_id: int
    ) -> Optional[EditSession]:
        """获取章节的活动编辑会话"""
        result = await self.db.execute(
            select(EditSession).where(
                EditSession.chapter_id == chapter_id,
                EditSession.status == EditSessionStatus.PENDING
            ).order_by(EditSession.created_at.desc()).limit(1)
        )
        return result.scalar_one_or_none()
    
    async def get_edit_session_by_id(
        self,
        edit_session_id: str
    ) -> Optional[EditSession]:
        """通过ID获取编辑会话"""
        result = await self.db.execute(
            select(EditSession).where(
                EditSession.edit_session_id == edit_session_id
            )
        )
        return result.scalar_one_or_none()
    
    async def apply_change(
        self,
        edit_session: EditSession,
        change_type: str,
        new_content: str,
        start_line: Optional[int] = None,
        end_line: Optional[int] = None,
        source: str = ChangeSource.AI,
        reason: Optional[str] = None
    ) -> EditChange:
        """应用变更到副本（事务性操作）"""
        old_working_content = edit_session.working_content or ""
        edit_session_db_id = edit_session.id
        edit_session_public_id = edit_session.edit_session_id
        
        try:
            if edit_session.status != EditSessionStatus.PENDING:
                raise ValueError("编辑会话已结束，不能继续修改")

            if change_type == "full_replace":
                edit_session.working_content = new_content
            elif change_type == "partial_edit":
                if start_line is not None and end_line is not None:
                    edit_session.working_content = diff_engine.apply_partial_edit(
                        edit_session.working_content or "",
                        start_line,
                        end_line,
                        new_content.splitlines()
                    )
            elif change_type == "insert":
                lines = (edit_session.working_content or "").splitlines(keepends=True)
                insert_lines = new_content.splitlines()
                insert_with_newlines = [line if line.endswith('\n') else line + '\n' for line in insert_lines]
                insert_idx = max(0, (start_line or len(lines)) - 1) if start_line is not None else len(lines)
                lines = lines[:insert_idx] + insert_with_newlines + lines[insert_idx:]
                edit_session.working_content = ''.join(lines)
            elif change_type == "delete":
                if start_line is not None and end_line is not None:
                    lines = (edit_session.working_content or "").splitlines(keepends=True)
                    start_idx = max(0, start_line - 1)
                    end_idx = min(len(lines), end_line)
                    lines = lines[:start_idx] + lines[end_idx:]
                    edit_session.working_content = ''.join(lines)
            
            diff_result = diff_engine.compute_diff(
                old_working_content,
                edit_session.working_content or "",
                DiffChangeType(change_type)
            )
            
            hunks_count = len(diff_result.hunks)
            edit_session.change_count += hunks_count
            
            change = EditChange(
                edit_session_id=edit_session_db_id,
                change_type=change_type,
                source=source,
                old_content=old_working_content,
                new_content=edit_session.working_content,
                start_line=start_line,
                end_line=end_line,
                diff_data=diff_result.to_dict(),
                reason=reason
            )
            
            self.db.add(change)
            await self.db.commit()
            
            logger.info(f"Applied change to edit session {edit_session_public_id}, added {hunks_count} hunks")
            return change
            
        except Exception as e:
            await self.db.rollback()
            logger.error(f"Failed to apply change, rolled back: {e}")
            raise
    
    async def accept_edit_session(
        self,
        edit_session_id: str
    ) -> Dict[str, Any]:
        """接受所有变更"""
        result = await self.db.execute(
            select(EditSession).where(
                EditSession.edit_session_id == edit_session_id
            )
        )
        edit_session = result.scalar_one_or_none()
        
        if not edit_session:
            raise ValueError(f"Edit session {edit_session_id} not found")

        result = await self.db.execute(
            select(Chapter).where(Chapter.id == edit_session.chapter_id)
        )
        chapter = result.scalar_one_or_none()
        
        if not chapter:
            raise ValueError(f"Chapter {edit_session.chapter_id} not found")

        if edit_session.status == EditSessionStatus.ACCEPTED:
            return {
                "edit_session_id": edit_session_id,
                "chapter_id": edit_session.chapter_id,
                "status": "accepted",
                "change_count": edit_session.change_count,
                "final_content": chapter.content,
                "word_count": chapter.word_count or 0,
                "summary": chapter.summary,
                "already_processed": True
            }

        if edit_session.status == EditSessionStatus.REJECTED:
            raise ValueError("编辑会话已被拒绝，不能再接受")

        source_updated_at = (edit_session.extra_metadata or {}).get("source_chapter_updated_at")
        if source_updated_at and chapter.updated_at:
            from datetime import datetime as dt, timezone
            try:
                source_dt = dt.fromisoformat(source_updated_at)
                if chapter.updated_at > source_dt:
                    chapter_word_count = chapter.word_count or 0
                    edit_word_count = len(edit_session.working_content or "")
                    if abs(chapter_word_count - edit_word_count) > chapter_word_count * 0.3:
                        raise ValueError(
                            f"章节在编辑期间已被外部修改（原字数={chapter_word_count}，"
                            f"编辑后字数={edit_word_count}），请先确认是否要覆盖。"
                        )
            except ValueError:
                raise
            except Exception:
                pass

        chapter.content = edit_session.working_content
        chapter.word_count = len(edit_session.working_content) if edit_session.working_content else 0
        chapter.status = "completed" if (edit_session.working_content or "").strip() else chapter.status

        post_processor = ChapterPostProcessor(self.db, chapter.novel_id)
        try:
            process_result = await post_processor.process(
                content=chapter.content or "",
                chapter_number=chapter.chapter_number,
                chapter_id=chapter.id,
            )
            chapter.content = process_result.get("final_content", chapter.content)
            chapter.word_count = len(chapter.content or "")
        except Exception as exc:
            logger.warning(f"Failed to post-process accepted edit session {edit_session_id}: {exc}")

        chapter.summary = await self._generate_chapter_summary(chapter.content or "")

        edit_session.status = EditSessionStatus.ACCEPTED
        edit_session.accepted_at = datetime.now(timezone.utc)
        
        await self.db.commit()
        chapter_snapshot = {
            "id": chapter.id,
            "novel_id": chapter.novel_id,
            "chapter_number": chapter.chapter_number,
            "title": chapter.title,
            "content": chapter.content or "",
            "summary": chapter.summary,
        }
        asyncio.create_task(self._post_accept_refresh(chapter_snapshot))
        
        logger.info(f"Accepted edit session {edit_session_id}, {edit_session.change_count} changes")
        
        return {
            "edit_session_id": edit_session_id,
            "chapter_id": edit_session.chapter_id,
            "status": "accepted",
            "change_count": edit_session.change_count,
            "final_content": edit_session.working_content,
            "word_count": chapter.word_count,
            "summary": chapter.summary,
            "already_processed": False
        }
    
    async def reject_edit_session(
        self,
        edit_session_id: str
    ) -> Dict[str, Any]:
        """拒绝所有变更，回退到原版本"""
        result = await self.db.execute(
            select(EditSession).where(
                EditSession.edit_session_id == edit_session_id
            )
        )
        edit_session = result.scalar_one_or_none()
        
        if not edit_session:
            raise ValueError(f"Edit session {edit_session_id} not found")

        if edit_session.status == EditSessionStatus.REJECTED:
            return {
                "edit_session_id": edit_session_id,
                "chapter_id": edit_session.chapter_id,
                "status": "rejected",
                "change_count": edit_session.change_count,
                "original_content": edit_session.original_content,
                "already_processed": True
            }

        if edit_session.status == EditSessionStatus.ACCEPTED:
            raise ValueError("编辑会话已被接受，不能再拒绝")
        
        edit_session.status = EditSessionStatus.REJECTED
        edit_session.rejected_at = datetime.now(timezone.utc)
        
        await self.db.commit()
        
        logger.info(f"Rejected edit session {edit_session_id}, reverted to original")
        
        return {
            "edit_session_id": edit_session_id,
            "chapter_id": edit_session.chapter_id,
            "status": "rejected",
            "change_count": edit_session.change_count,
            "original_content": edit_session.original_content,
            "already_processed": False
        }
    
    async def get_diff(
        self,
        edit_session_id: str
    ) -> Dict[str, Any]:
        """获取副本与原版的diff"""
        result = await self.db.execute(
            select(EditSession).where(
                EditSession.edit_session_id == edit_session_id
            )
        )
        edit_session = result.scalar_one_or_none()
        
        if not edit_session:
            raise ValueError(f"Edit session {edit_session_id} not found")
        
        diff_result = diff_engine.compute_diff(
            edit_session.original_content or "",
            edit_session.working_content or ""
        )
        
        return {
            "edit_session_id": edit_session_id,
            "original_content": edit_session.original_content,
            "working_content": edit_session.working_content,
            "change_count": edit_session.change_count,
            "diff": diff_result.to_dict()
        }

    async def _generate_chapter_summary(self, content: str) -> Optional[str]:
        return await generate_chapter_summary(content)

    async def _post_accept_refresh(self, chapter_snapshot: Dict[str, Any]) -> None:
        try:
            await self._refresh_chapter_memory(chapter_snapshot)
        except Exception as exc:
            logger.warning("Failed to refresh chapter memory after accept edit: %s", exc)
        try:
            await self._invalidate_cache(chapter_snapshot)
        except Exception as exc:
            logger.warning("Failed to invalidate cache after accept edit: %s", exc)

    async def _refresh_chapter_memory(self, chapter: Dict[str, Any]) -> None:
        vector_store.delete_chapter_chunks(chapter["novel_id"], chapter["id"])
        content = chapter["content"]
        if not content.strip():
            return
        chunk_data = vector_store.build_chapter_chunks(
            chapter_id=chapter["id"],
            chapter_number=chapter["chapter_number"],
            chapter_title=chapter.get("title"),
            content=content,
            summary=chapter.get("summary"),
        )
        if chunk_data:
            vector_store.add_chunks(chapter["novel_id"], chunk_data)

    async def _invalidate_cache(self, chapter: Dict[str, Any]) -> None:
        from app.core.redis_service import redis_service
        await redis_service.delete(f"chapter:{chapter['id']}:detail")
        await redis_service.clear_pattern(f"novel:{chapter['novel_id']}:chapters:*")
        await redis_service.delete(f"novel:{chapter['novel_id']}:detail")


def get_edit_session_manager(db: AsyncSession) -> EditSessionManager:
    """获取编辑会话管理器实例"""
    return EditSessionManager(db)
