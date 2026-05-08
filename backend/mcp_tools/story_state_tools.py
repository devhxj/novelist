"""
故事状态文档 MCP 工具集
CLAUDE.md 风格的轻量 markdown 状态文档，帮 AI 快速了解故事当前情况
"""
from sqlalchemy import select

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from novels.models import NovelStoryState


class GetStoryStateTool(BaseMCPTool):
    """获取当前故事状态文档"""

    name = "get_story_state"
    description = (
        "获取当前小说的故事状态文档（CLAUDE.md 风格的 markdown 快照）。"
        "包含当前进展、角色动态、开着的悬念等信息，帮 AI 快速了解故事现在是什么情况。"
        "无需传novel_id，系统会注入当前小说ID。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {},
    }

    async def _execute(self, db, novel_id: int, user_id: int, **kwargs) -> MCPToolResult:
        result = await db.execute(
            select(NovelStoryState).where(NovelStoryState.novel_id == novel_id)
        )
        state = result.scalar_one_or_none()
        if not state:
            return MCPToolResult(success=True, data={"content": "", "exists": False})
        return MCPToolResult(success=True, data={"content": state.content, "exists": True})


class UpdateStoryStateTool(BaseMCPTool):
    """更新故事状态文档"""

    name = "update_story_state"
    description = (
        "更新故事状态文档（CLAUDE.md 风格的 markdown）。"
        "在每章写完后调用，顺手更新当前进展、角色动态、开着的悬念等。"
        "传入完整的 markdown 内容，会全量替换旧内容。"
        "无需传novel_id，系统会注入当前小说ID。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "content": {
                "type": "string",
                "description": "完整的故事状态 markdown 内容，会全量替换旧内容"
            },
        },
        "required": ["content"],
    }

    async def _execute(self, db, novel_id: int, user_id: int, content: str = "", **kwargs) -> MCPToolResult:
        result = await db.execute(
            select(NovelStoryState).where(NovelStoryState.novel_id == novel_id)
        )
        state = result.scalar_one_or_none()
        if not state:
            state = NovelStoryState(novel_id=novel_id, content=content)
            db.add(state)
        else:
            state.content = content
        await db.commit()
        return MCPToolResult(success=True, data={"updated": True})


def register_story_state_tools(registry: MCPToolRegistry):
    registry.register(GetStoryStateTool())
    registry.register(UpdateStoryStateTool())
