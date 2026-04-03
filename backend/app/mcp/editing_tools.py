"""
编辑类MCP工具 - 支持副本编辑机制
注意：accept_edit和reject_edit是用户操作，不暴露给AI
"""
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from sqlalchemy.orm import selectinload

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.novels.models import Novel
from app.chapters.models import Chapter
from app.editor.service import get_edit_session_manager
from app.editor.models import EditSession, EditSessionStatus, EditChange
from app.core.context_builder import ContextBuilder


def _validate_chapter_access(db: AsyncSession, chapter_id: int, novel_id: int) -> tuple[bool, Optional[Chapter], str]:
    """验证章节访问权限"""
    async def _check():
        result = await db.execute(
            select(Chapter).where(Chapter.id == chapter_id)
        )
        chapter = result.scalar_one_or_none()
        
        if not chapter:
            return False, None, f"章节不存在: {chapter_id}"
        
        if chapter.novel_id != novel_id:
            return False, None, f"无权访问此章节: 章节不属于当前小说"
        
        return True, chapter, ""
    
    return _check()


class StartEditSessionTool(BaseMCPTool):
    """开始编辑会话（创建副本）"""
    
    name = "start_edit_session"
    description = "开始编辑会话，创建一个副本用于AI和用户编辑。原内容保持不变，直到用户接受或拒绝。必须提供chapter_id；若不清楚章节ID，先调用 get_chapter_list 或 read_chapter_for_edit 获取。成功后应继续调用 apply_edit 写入正文。"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "chapter_id": {
                "type": "integer",
                "description": "章节ID（必填）"
            }
        },
        "required": ["chapter_id"]
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self,
        db: AsyncSession,
        chapter_id: Optional[int] = None,
        session_id: str = "",
        novel_id: int = 0,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            if not chapter_id:
                return MCPToolResult(
                    success=False,
                    error="无法确定要编辑的章节，请先选择一个章节或提供chapter_id"
                )
            
            result = await db.execute(
                select(Chapter).where(Chapter.id == chapter_id)
            )
            chapter = result.scalar_one_or_none()
            
            if not chapter:
                return MCPToolResult(success=False, error=f"章节不存在: {chapter_id}")
            
            if chapter.novel_id != novel_id:
                return MCPToolResult(success=False, error="无权编辑此章节：章节不属于当前小说")
            
            if user_id:
                novel_result = await db.execute(select(Novel).where(Novel.id == novel_id))
                novel = novel_result.scalar_one_or_none()
                if not novel:
                    return MCPToolResult(success=False, error=f"小说不存在: {novel_id}")
                if novel.author_id != user_id:
                    return MCPToolResult(success=False, error="无权编辑此章节")
            
            manager = get_edit_session_manager(db)
            existing = await manager.get_edit_session(chapter_id)
            
            if existing:
                return MCPToolResult(
                    success=True,
                    data={
                        "edit_session_id": existing.edit_session_id,
                        "chapter_id": chapter_id,
                        "original_content": existing.original_content,
                        "working_content": existing.working_content,
                        "change_count": existing.change_count,
                        "status": existing.status,
                        "reused_existing": True,
                        "message": "已有活动的编辑会话，可以继续编辑"
                    },
                    metadata={"tool": self.name, "edit_session_id": existing.edit_session_id}
                )
            
            edit_session = await manager.create_edit_session(chapter_id, session_id)
            
            return MCPToolResult(
                success=True,
                data={
                    "edit_session_id": edit_session.edit_session_id,
                    "chapter_id": chapter_id,
                    "original_content": edit_session.original_content,
                    "working_content": edit_session.working_content,
                    "change_count": 0,
                    "status": "pending",
                    "reused_existing": False,
                    "message": "编辑会话已创建，可以开始编辑。编辑完成后用户需要确认接受或拒绝。"
                },
                metadata={"tool": self.name, "edit_session_id": edit_session.edit_session_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))


class ApplyEditTool(BaseMCPTool):
    """应用编辑到副本"""
    
    name = "apply_edit"
    description = "应用编辑到副本内容。必须先用 start_edit_session 获取 edit_session_id。支持全量替换、部分编辑、插入、删除。多次编辑会累积变更计数。编辑只修改副本，需用户确认后才生效。"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "edit_session_id": {
                "type": "string",
                "description": "编辑会话ID"
            },
            "change_type": {
                "type": "string",
                "enum": ["full_replace", "partial_edit", "insert", "delete"],
                "description": "变更类型"
            },
            "new_content": {
                "type": "string",
                "description": "新内容"
            },
            "start_line": {
                "type": "integer",
                "description": "起始行号（partial_edit/insert/delete时必填）"
            },
            "end_line": {
                "type": "integer",
                "description": "结束行号（partial_edit/delete时必填）"
            },
            "reason": {
                "type": "string",
                "description": "修改原因"
            }
        },
        "required": ["edit_session_id", "change_type", "new_content"]
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self,
        db: AsyncSession,
        edit_session_id: str,
        change_type: str,
        new_content: str,
        start_line: Optional[int] = None,
        end_line: Optional[int] = None,
        reason: Optional[str] = None,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            manager = get_edit_session_manager(db)
            edit_session = await manager.get_edit_session_by_id(edit_session_id)
            
            if not edit_session:
                return MCPToolResult(success=False, error="编辑会话不存在")
            
            chapter_result = await db.execute(
                select(Chapter).where(Chapter.id == edit_session.chapter_id)
            )
            chapter = chapter_result.scalar_one_or_none()
            if not chapter:
                return MCPToolResult(success=False, error="章节不存在")
            
            if user_id:
                novel_result = await db.execute(select(Novel).where(Novel.id == chapter.novel_id))
                novel = novel_result.scalar_one_or_none()
                if not novel:
                    return MCPToolResult(success=False, error="小说不存在")
                if novel.author_id != user_id:
                    return MCPToolResult(success=False, error="无权编辑此章节")
            
            await manager.apply_change(
                edit_session=edit_session,
                change_type=change_type,
                new_content=new_content,
                start_line=start_line,
                end_line=end_line,
                reason=reason
            )
            
            diff_data = await manager.get_diff(edit_session_id)
            
            return MCPToolResult(
                success=True,
                data={
                    "edit_session_id": edit_session_id,
                    "change_count": edit_session.change_count,
                    "working_content": edit_session.working_content,
                    "diff": diff_data.get("diff", {}),
                    "message": f"变更已应用到副本，共 {edit_session.change_count} 处改动。等待用户确认。"
                },
                metadata={
                    "tool": self.name, 
                    "change_count": edit_session.change_count,
                    "edit_session_id": edit_session_id,
                    "requires_user_confirmation": True
                }
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))


class EditChapterContentTool(BaseMCPTool):
    """编辑章节内容，创建或复用编辑会话"""
    
    name = "edit_chapter_content"
    description = "编辑章节内容，自动创建或复用副本编辑会话并应用变更。用于前端API调用，不建议LLM直接调用。"
    category = MCPToolCategory.WRITING_ASSISTANT
    expose_to_llm = False
    parameters_schema = {
        "type": "object",
        "properties": {
            "session_id": {
                "type": "string",
                "description": "会话ID"
            },
            "chapter_id": {
                "type": "integer",
                "description": "章节ID"
            },
            "change_type": {
                "type": "string",
                "enum": ["full_replace", "partial_edit", "insert", "delete"],
                "description": "变更类型"
            },
            "new_content": {
                "type": "string",
                "description": "新内容"
            },
            "start_line": {
                "type": "integer",
                "description": "起始行号（partial_edit/insert/delete时可选）"
            },
            "end_line": {
                "type": "integer",
                "description": "结束行号（partial_edit/delete时可选）"
            },
            "reason": {
                "type": "string",
                "description": "修改原因"
            }
        },
        "required": ["session_id", "chapter_id", "change_type", "new_content"]
    }
    
    async def execute(
        self,
        db: AsyncSession,
        session_id: str,
        chapter_id: int,
        change_type: str,
        new_content: str,
        start_line: Optional[int] = None,
        end_line: Optional[int] = None,
        reason: Optional[str] = None,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            chapter_result = await db.execute(
                select(Chapter).where(Chapter.id == chapter_id)
            )
            chapter = chapter_result.scalar_one_or_none()
            if not chapter:
                return MCPToolResult(success=False, error="章节不存在")
            
            if user_id:
                novel_result = await db.execute(select(Novel).where(Novel.id == chapter.novel_id))
                novel = novel_result.scalar_one_or_none()
                if not novel:
                    return MCPToolResult(success=False, error="小说不存在")
                if novel.author_id != user_id:
                    return MCPToolResult(success=False, error="无权编辑此章节")
            
            manager = get_edit_session_manager(db)
            edit_session = await manager.get_edit_session(chapter_id)
            if not edit_session:
                edit_session = await manager.create_edit_session(chapter_id, session_id)
            
            await manager.apply_change(
                edit_session=edit_session,
                change_type=change_type,
                new_content=new_content,
                start_line=start_line,
                end_line=end_line,
                reason=reason
            )
            
            diff_data = await manager.get_diff(edit_session.edit_session_id)
            
            return MCPToolResult(
                success=True,
                data={
                    "edit_session_id": edit_session.edit_session_id,
                    "chapter_id": chapter_id,
                    "change_count": edit_session.change_count,
                    "working_content": edit_session.working_content,
                    "diff": diff_data.get("diff", {}),
                    "message": f"变更已应用到副本，共 {edit_session.change_count} 处改动。等待用户确认。"
                },
                metadata={
                    "tool": self.name,
                    "change_count": edit_session.change_count,
                    "edit_session_id": edit_session.edit_session_id,
                    "requires_user_confirmation": True
                }
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))


class GetEditStatusTool(BaseMCPTool):
    """获取编辑状态"""
    
    name = "get_edit_status"
    description = "获取章节当前的编辑状态，包括是否有活动的编辑会话、副本内容等。必须提供chapter_id，可先用 get_chapter_list 获取。"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "chapter_id": {
                "type": "integer",
                "description": "章节ID"
            },
            "include_line_numbers": {
                "type": "boolean",
                "default": True,
                "description": "是否包含行号"
            }
        },
        "required": ["chapter_id"]
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self,
        db: AsyncSession,
        chapter_id: int,
        include_line_numbers: bool = True,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            manager = get_edit_session_manager(db)
            edit_session = await manager.get_edit_session(chapter_id)
            
            if edit_session:
                if user_id:
                    result = await db.execute(
                        select(Chapter).where(Chapter.id == chapter_id)
                    )
                    chapter = result.scalar_one_or_none()
                    if not chapter:
                        return MCPToolResult(success=False, error="章节不存在")
                    novel_result = await db.execute(select(Novel).where(Novel.id == chapter.novel_id))
                    novel = novel_result.scalar_one_or_none()
                    if not novel:
                        return MCPToolResult(success=False, error="小说不存在")
                    if novel.author_id != user_id:
                        return MCPToolResult(success=False, error="无权访问此章节")
                diff_data = await manager.get_diff(edit_session.edit_session_id)
                changes_result = await db.execute(
                    select(EditChange)
                    .where(EditChange.edit_session_id == edit_session.id)
                    .order_by(EditChange.created_at.desc())
                    .limit(5)
                )
                recent_changes = list(changes_result.scalars().all())
                return MCPToolResult(
                    success=True,
                    data={
                        "has_active_edit": True,
                        "edit_session_id": edit_session.edit_session_id,
                        "latest_pending_edit_session_id": edit_session.edit_session_id,
                        "status": edit_session.status,
                        "change_count": edit_session.change_count,
                        "working_content": edit_session.working_content,
                        "original_content": edit_session.original_content,
                        "diff": diff_data.get("diff", {}),
                        "created_from_ws_session": (edit_session.extra_metadata or {}).get("created_from_ws_session"),
                        "recent_changes": [change.to_dict() for change in recent_changes]
                    }
                )
            
            result = await db.execute(
                select(Chapter).where(Chapter.id == chapter_id)
            )
            chapter = result.scalar_one_or_none()
            if user_id and chapter:
                novel_result = await db.execute(select(Novel).where(Novel.id == chapter.novel_id))
                novel = novel_result.scalar_one_or_none()
                if not novel:
                    return MCPToolResult(success=False, error="小说不存在")
                if novel.author_id != user_id:
                    return MCPToolResult(success=False, error="无权访问此章节")
            
            return MCPToolResult(
                success=True,
                data={
                    "has_active_edit": False,
                    "latest_pending_edit_session_id": None,
                    "chapter_content": chapter.content if chapter else "",
                    "message": "当前没有活动的编辑会话"
                }
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))


class RunAgentTaskTool(BaseMCPTool):
    """执行Agent任务"""
    
    name = "run_agent_task"
    description = "由主Agent调度子Agent执行任务（写作/审核/一致性/规划）。task_type可选：generate_chapter/review_chapter/check_consistency/update_memory/plan_plot/manage_foreshadowing，也支持别名 writing/write/review/consistency/memory/plan/foreshadowing。"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "task_type": {
                "type": "string",
                "description": "任务类型"
            },
            "novel_id": {
                "type": "integer",
                "description": "小说ID"
            },
            "chapter_id": {
                "type": "integer",
                "description": "章节ID（可选）"
            },
            "parameters": {
                "type": "object",
                "description": "任务参数"
            },
            "agent_role": {
                "type": "string",
                "description": "指定Agent角色（可选）"
            },
            "agent_id": {
                "type": "string",
                "description": "指定Agent ID（可选）"
            },
            "model": {
                "type": "string",
                "description": "指定模型（可选）"
            }
        },
        "required": ["task_type", "novel_id"]
    }
    
    async def execute(
        self,
        db: AsyncSession,
        task_type: str,
        novel_id: int,
        chapter_id: Optional[int] = None,
        parameters: Optional[Dict[str, Any]] = None,
        agent_role: Optional[str] = None,
        agent_id: Optional[str] = None,
        model: Optional[str] = None,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            task_type_map = {
                "writing": "generate_chapter",
                "write": "generate_chapter",
                "review": "review_chapter",
                "consistency": "check_consistency",
                "memory": "update_memory",
                "plan": "plan_plot",
                "foreshadowing": "manage_foreshadowing"
            }
            normalized_type = task_type_map.get(task_type, task_type)
            novel_result = await db.execute(select(Novel).where(Novel.id == novel_id))
            novel = novel_result.scalar_one_or_none()
            if not novel:
                return MCPToolResult(success=False, error="小说不存在")
            if user_id and novel.author_id != user_id:
                return MCPToolResult(success=False, error="无权访问此小说")
            
            from app.agents.base import AgentTask, TaskType
            from app.agents.factory import create_default_coordinator
            from app.consistency.service import ConsistencyChecker
            
            coordinator = create_default_coordinator()
            
            task_parameters = parameters or {}
            nested_agent_role = task_parameters.get("agent_role")
            nested_agent_id = task_parameters.get("agent_id")
            nested_model = task_parameters.get("model")
            if agent_role:
                task_parameters["agent_role"] = agent_role
            elif nested_agent_role:
                task_parameters["agent_role"] = nested_agent_role
            if agent_id:
                task_parameters["agent_id"] = agent_id
            elif nested_agent_id:
                task_parameters["agent_id"] = nested_agent_id
            if model:
                task_parameters["model"] = model
            elif nested_model:
                task_parameters["model"] = nested_model
            
            context: Dict[str, Any] = {}
            if chapter_id:
                chapter_result = await db.execute(select(Chapter).where(Chapter.id == chapter_id))
                chapter = chapter_result.scalar_one_or_none()
                if chapter:
                    task_parameters.setdefault("chapter_number", chapter.chapter_number)
                    context_builder = ContextBuilder(db, novel_id)
                    context = await context_builder.build_writing_context(
                        chapter_id=chapter_id,
                        context_size=3000,
                        include_previous_chapters=True,
                        include_characters=True,
                        include_plot_events=True
                    )
            if normalized_type == "check_consistency":
                checker = ConsistencyChecker(db, novel_id)
                context["consistency_result"] = await checker.check_all(
                    chapter_ids=[chapter_id] if chapter_id else None,
                    check_types=task_parameters.get("check_types")
                )
            
            task = AgentTask(
                task_id=f"task_{novel_id}_{task_type}_{chapter_id or 'general'}",
                task_type=TaskType(normalized_type),
                novel_id=novel_id,
                chapter_id=chapter_id,
                parameters=task_parameters,
                context=context
            )
            
            result = await coordinator.execute(task)
            
            return MCPToolResult(
                success=result.success,
                data=result.to_dict(),
                error=result.error
            )
        except ValueError:
            return MCPToolResult(
                success=False,
                error="无效的task_type，可用: generate_chapter, review_chapter, check_consistency, update_memory, plan_plot, manage_foreshadowing"
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))


class GetPendingChangesTool(BaseMCPTool):
    """获取待确认变更列表"""
    
    name = "get_pending_changes"
    description = "获取待确认的副本编辑变更列表，可按章节或会话筛选"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "chapter_id": {
                "type": "integer",
                "description": "章节ID（可选）"
            },
            "session_id": {
                "type": "string",
                "description": "会话ID（可选）"
            },
            "limit": {
                "type": "integer",
                "default": 10,
                "description": "返回数量限制"
            }
        },
        "required": []
    }
    
    async def execute(
        self,
        db: AsyncSession,
        chapter_id: Optional[int] = None,
        session_id: Optional[str] = None,
        limit: int = 10,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            query = select(EditSession).options(selectinload(EditSession.chapter))
            if user_id:
                query = (
                    query.join(Chapter, EditSession.chapter_id == Chapter.id)
                    .join(Novel, Chapter.novel_id == Novel.id)
                    .where(Novel.author_id == user_id)
                )
            
            query = query.where(EditSession.status == EditSessionStatus.PENDING)
            
            if chapter_id:
                query = query.where(EditSession.chapter_id == chapter_id)
            if session_id:
                query = query.where(EditSession.ws_session_id == session_id)
            
            query = query.order_by(EditSession.created_at.desc()).limit(limit)
            result = await db.execute(query)
            sessions = result.scalars().all()
            
            data = [
                {
                    "edit_session_id": s.edit_session_id,
                    "chapter_id": s.chapter_id,
                    "chapter_title": s.chapter.title if s.chapter else None,
                    "ws_session_id": s.ws_session_id,
                    "status": s.status,
                    "change_count": s.change_count,
                    "created_at": s.created_at.isoformat() if s.created_at else None,
                    "content_preview": (s.working_content or "")[:120]
                }
                for s in sessions
            ]
            
            return MCPToolResult(
                success=True,
                data={"items": data, "total": len(data)}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))


class ReadChapterForEditTool(BaseMCPTool):
    """读取章节内容用于编辑"""
    
    name = "read_chapter_for_edit"
    description = "读取章节内容用于编辑，返回完整内容和行号信息"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "chapter_id": {
                "type": "integer",
                "description": "章节ID"
            }
        },
        "required": ["chapter_id"]
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self,
        db: AsyncSession,
        chapter_id: int,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            result = await db.execute(
                select(Chapter).where(Chapter.id == chapter_id)
            )
            chapter = result.scalar_one_or_none()
            
            if not chapter:
                return MCPToolResult(success=False, error="章节不存在")
            
            if user_id:
                novel_result = await db.execute(select(Novel).where(Novel.id == chapter.novel_id))
                novel = novel_result.scalar_one_or_none()
                if not novel:
                    return MCPToolResult(success=False, error="小说不存在")
                if novel.author_id != user_id:
                    return MCPToolResult(success=False, error="无权访问此章节")
            
            content = chapter.content or ""
            lines = content.splitlines()
            lines_payload = [{"line_number": i + 1, "content": line} for i, line in enumerate(lines)]
            
            return MCPToolResult(
                success=True,
                data={
                    "chapter_id": chapter.id,
                    "chapter_number": chapter.chapter_number,
                    "title": chapter.title,
                    "content": content,
                    "line_count": len(lines),
                    "word_count": len(content),
                    "lines": lines_payload
                },
                metadata={"tool": self.name, "chapter_id": chapter_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))


class EditingTools:
    """编辑工具集合 - 只包含AI可调用的工具"""
    
    @staticmethod
    def register_all(registry: MCPToolRegistry) -> None:
        """注册所有编辑工具（不包括accept/reject，那是用户操作）"""
        registry.register(StartEditSessionTool())
        registry.register(ApplyEditTool())
        registry.register(EditChapterContentTool())
        registry.register(GetEditStatusTool())
        registry.register(RunAgentTaskTool())
        registry.register(GetPendingChangesTool())
        registry.register(ReadChapterForEditTool())
