"""
故事状态文档 MCP 工具集
CLAUDE.md 风格的轻量 markdown 状态文档，帮 AI 快速了解故事当前情况
"""
from pydantic import BaseModel, Field
from sqlalchemy import select

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from novels.models import NovelStoryState


class GetStoryStateArgs(BaseModel):
    pass


class GetStoryStateTool(BaseMCPTool):
    """获取当前故事状态文档"""

    name = "get_story_state"
    description = (
        "获取当前小说的故事状态文档（CLAUDE.md 风格的 markdown 快照）。"
        "包含当前进展、角色动态、开着的悬念等信息，帮 AI 快速了解故事现在是什么情况。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    args_schema = GetStoryStateArgs

    async def _execute(
        self,
        args: GetStoryStateArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
    ) -> MCPToolResult:
        result = await db.execute(
            select(NovelStoryState).where(NovelStoryState.novel_id == novel_id)
        )
        state = result.scalar_one_or_none()
        if not state:
            return MCPToolResult(success=True, data={"content": "", "exists": False})
        return MCPToolResult(success=True, data={"content": state.content, "exists": True})


class UpdateStoryStateArgs(BaseModel):
    content: str = Field(description="完整的故事状态 markdown 内容，会全量替换旧内容")


class UpdateStoryStateTool(BaseMCPTool):
    """更新故事状态文档"""

    name = "update_story_state"
    description = (
        "更新故事状态文档（CLAUDE.md 风格的 markdown）。"
        "在每章写完后调用，顺手更新当前进展、角色动态、开着的悬念等。"
        "传入完整的 markdown 内容，会全量替换旧内容。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    args_schema = UpdateStoryStateArgs

    async def _execute(
        self,
        args: UpdateStoryStateArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
    ) -> MCPToolResult:
        result = await db.execute(
            select(NovelStoryState).where(NovelStoryState.novel_id == novel_id)
        )
        state = result.scalar_one_or_none()
        if not state:
            state = NovelStoryState(novel_id=novel_id, content=args.content)
            db.add(state)
        else:
            state.content = args.content
        await db.commit()
        return MCPToolResult(success=True, data={"updated": True})


def register_story_state_tools(registry: MCPToolRegistry):
    registry.register(GetStoryStateTool())
    registry.register(UpdateStoryStateTool())
