"""
叙事弧线MCP工具集
供AI调用的工具：查询/添加/更新叙事弧线
"""
from typing import Any

from pydantic import BaseModel, Field
from typing import Literal

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from story_arcs.models import StoryArc
from story_arcs.schemas import StoryArcCreate, StoryArcUpdate
from story_arcs.service import StoryArcService


class GetStoryArcsArgs(BaseModel):
    arc_type: Literal["main", "sub", "character", "background"] | None = Field(default=None, description="按弧线类型筛选（可选）")
    status: Literal["active", "paused", "completed", "abandoned"] | None = Field(default=None, description="按状态筛选（可选，默认返回所有）")


class GetStoryArcsTool(BaseMCPTool):
    """获取小说的叙事弧线"""

    name = "get_story_arcs"
    description = (
        "获取小说的叙事弧线列表。叙事弧线是跨越多章节的故事线（如主线、支线、角色线），"
        "每条弧线包含名称、类型、章节范围和状态。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    args_schema = GetStoryArcsArgs

    async def _execute(
        self,
        args: GetStoryArcsArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        try:
            service = StoryArcService(db, novel_id)
            arcs = await service.list_arcs(arc_type=args.arc_type, status=args.status)
            return MCPToolResult(
                success=True,
                data=[_arc_to_dict(a) for a in arcs],
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"获取叙事弧线失败: {str(e)}")


class AddStoryArcArgs(BaseModel):
    name: str = Field(description="弧线名称（必填），如'复仇之路'")
    description: str | None = Field(default=None, description="弧线描述")
    arc_type: Literal["main", "sub", "character", "background"] = Field(default="sub", description="弧线类型")
    start_chapter: int | None = Field(default=None, description="起始章节号（可选）")
    end_chapter: int | None = Field(default=None, description="结束章节号（可选）")
    importance: int = Field(default=1, description="重要程度1-5")


class AddStoryArcTool(BaseMCPTool):
    """创建叙事弧线"""

    name = "add_story_arc"
    description = (
        "创建一条新的叙事弧线。叙事弧线是跨越多章节的故事线，用于组织情节节点的宏观结构。"
        "\n弧线类型说明："
        "- main: 主线（核心故事线）"
        "- sub: 支线（辅助故事线）"
        "- character: 角色线（某角色的发展线）"
        "- background: 背景线（世界观/背景设定推进线）"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    args_schema = AddStoryArcArgs

    async def _execute(
        self,
        args: AddStoryArcArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        try:
            from story_arcs.schemas import StoryArcType as SchemaArcType
            service = StoryArcService(db, novel_id)
            data = StoryArcCreate(
                name=args.name,
                description=args.description,
                arc_type=SchemaArcType(args.arc_type),
                start_chapter=args.start_chapter,
                end_chapter=args.end_chapter,
                importance=args.importance,
            )
            arc = await service.create_arc(data)
            return MCPToolResult(
                success=True,
                data=_arc_to_dict(arc),
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"创建叙事弧线失败: {str(e)}")


class UpdateStoryArcArgs(BaseModel):
    arc_id: int = Field(description="弧线ID（必填）")
    name: str | None = Field(default=None, description="新的弧线名称")
    description: str | None = Field(default=None, description="新的描述")
    arc_type: Literal["main", "sub", "character", "background"] | None = Field(default=None, description="新的弧线类型")
    start_chapter: int | None = Field(default=None, description="新的起始章节号")
    end_chapter: int | None = Field(default=None, description="新的结束章节号")
    importance: int | None = Field(default=None, description="新的重要程度(1-5)")
    status: Literal["active", "paused", "completed", "abandoned"] | None = Field(default=None, description="新状态")


class UpdateStoryArcTool(BaseMCPTool):
    """更新叙事弧线"""

    name = "update_story_arc"
    description = (
        "更新已有的叙事弧线。可用于修改弧线状态（如暂停/完成）、调整章节范围、更新描述等。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    args_schema = UpdateStoryArcArgs

    async def _execute(
        self,
        args: UpdateStoryArcArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        try:
            update_fields = args.model_dump(exclude_unset=True)
            update_fields.pop("arc_id", None)

            if not update_fields:
                return MCPToolResult(success=False, error="没有提供更新字段")

            data = StoryArcUpdate(**update_fields)
            service = StoryArcService(db, novel_id)
            arc = await service.update_arc(args.arc_id, data)
            if not arc:
                return MCPToolResult(success=False, error=f"弧线 {args.arc_id} 不存在")
            return MCPToolResult(
                success=True,
                data=_arc_to_dict(arc),
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"更新叙事弧线失败: {str(e)}")


def _arc_to_dict(arc: StoryArc) -> dict[str, Any]:
    return {
        "id": arc.id,
        "name": arc.name,
        "description": arc.description,
        "arc_type": arc.arc_type,
        "start_chapter": arc.start_chapter,
        "end_chapter": arc.end_chapter,
        "importance": arc.importance,
        "status": arc.status,
        "created_at": arc.created_at.isoformat() if arc.created_at else None,
        "updated_at": arc.updated_at.isoformat() if arc.updated_at else None,
    }


def register_story_arc_tools(registry: MCPToolRegistry):
    registry.register(GetStoryArcsTool())
    registry.register(AddStoryArcTool())
    registry.register(UpdateStoryArcTool())
