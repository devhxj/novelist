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
    description = "开始编辑会话，创建一个副本用于AI和用户编辑。原内容保持不变，直到用户接受或拒绝。如果不提供chapter_id，将使用当前作用域的章节。"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "chapter_id": {
                "type": "integer",
                "description": "章节ID（可选，不提供则使用当前作用域章节）"
            }
        },
        "required": []
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self, 
        db: AsyncSession,
        chapter_id: Optional[int] = None,
        session_id: str = "",
        novel_id: int = 0,
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
                    "message": "编辑会话已创建，可以开始编辑。编辑完成后用户需要确认接受或拒绝。"
                },
                metadata={"tool": self.name, "edit_session_id": edit_session.edit_session_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))


class ApplyEditTool(BaseMCPTool):
    """应用编辑到副本"""
    
    name = "apply_edit"
    description = "应用编辑到副本内容。支持全量替换、部分编辑、插入等操作。多次编辑会累积变更计数。注意：编辑只修改副本，需要用户确认后才生效。"
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
                "description": "起始行号（partial_edit时必填）"
            },
            "end_line": {
                "type": "integer",
                "description": "结束行号（partial_edit时必填）"
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
        **kwargs
    ) -> MCPToolResult:
        try:
            manager = get_edit_session_manager(db)
            edit_session = await manager.get_edit_session_by_id(edit_session_id)
            
            if not edit_session:
                return MCPToolResult(success=False, error="编辑会话不存在")
            
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


class GetEditStatusTool(BaseMCPTool):
    """获取编辑状态"""
    
    name = "get_edit_status"
    description = "获取章节当前的编辑状态，包括是否有活动的编辑会话、副本内容等"
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
        **kwargs
    ) -> MCPToolResult:
        try:
            manager = get_edit_session_manager(db)
            edit_session = await manager.get_edit_session(chapter_id)
            
            if edit_session:
                diff_data = await manager.get_diff(edit_session.edit_session_id)
                return MCPToolResult(
                    success=True,
                    data={
                        "has_active_edit": True,
                        "edit_session_id": edit_session.edit_session_id,
                        "status": edit_session.status,
                        "change_count": edit_session.change_count,
                        "working_content": edit_session.working_content,
                        "original_content": edit_session.original_content,
                        "diff": diff_data.get("diff", {})
                    }
                )
            
            result = await db.execute(
                select(Chapter).where(Chapter.id == chapter_id)
            )
            chapter = result.scalar_one_or_none()
            
            return MCPToolResult(
                success=True,
                data={
                    "has_active_edit": False,
                    "chapter_content": chapter.content if chapter else "",
                    "message": "当前没有活动的编辑会话"
                }
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
        **kwargs
    ) -> MCPToolResult:
        try:
            result = await db.execute(
                select(Chapter).where(Chapter.id == chapter_id)
            )
            chapter = result.scalar_one_or_none()
            
            if not chapter:
                return MCPToolResult(success=False, error="章节不存在")
            
            content = chapter.content or ""
            lines = content.splitlines()
            
            return MCPToolResult(
                success=True,
                data={
                    "chapter_id": chapter.id,
                    "chapter_number": chapter.chapter_number,
                    "title": chapter.title,
                    "content": content,
                    "line_count": len(lines),
                    "word_count": len(content),
                    "lines": [{"line_number": i + 1, "content": line} for i, line in enumerate(lines)]
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
        registry.register(GetEditStatusTool())
        registry.register(ReadChapterForEditTool())
