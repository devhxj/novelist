"""
叙事弧线MCP工具集
供AI调用的工具：查询/添加/更新叙事弧线
"""
from typing import Any, Dict, List, Optional

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from story_arcs.models import StoryArc, StoryArcType, StoryArcStatus
from story_arcs.schemas import StoryArcCreate, StoryArcUpdate
from story_arcs.service import StoryArcService
from core.permissions import verify_novel_ownership


class GetStoryArcsTool(BaseMCPTool):
    """获取小说的叙事弧线"""

    name = "get_story_arcs"
    description = (
        "获取小说的叙事弧线列表。叙事弧线是跨越多章节的故事线（如主线、支线、角色线），"
        "每条弧线包含名称、类型、章节范围和状态。"
        "无需传novel_id，系统会注入当前小说ID。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "arc_type": {
                "type": "string",
                "enum": ["main", "sub", "character", "background"],
                "description": "按弧线类型筛选（可选）"
            },
            "status": {
                "type": "string",
                "enum": ["active", "paused", "completed", "abandoned"],
                "description": "按状态筛选（可选，默认返回所有）"
            },
        },
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        arc_type: Optional[str] = None,
        status: Optional[str] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

            service = StoryArcService(db, novel_id)
            arcs = await service.list_arcs(arc_type=arc_type, status=status)
            return MCPToolResult(
                success=True,
                data=[_arc_to_dict(a) for a in arcs],
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"获取叙事弧线失败: {str(e)}")


class AddStoryArcTool(BaseMCPTool):
    """创建叙事弧线"""

    name = "add_story_arc"
    description = (
        "创建一条新的叙事弧线。叙事弧线是跨越多章节的故事线，用于组织情节节点的宏观结构。"
        "无需传novel_id，系统会注入当前小说ID。"
        "\n弧线类型说明："
        "- main: 主线（核心故事线）"
        "- sub: 支线（辅助故事线）"
        "- character: 角色线（某角色的发展线）"
        "- background: 背景线（世界观/背景设定推进线）"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "弧线名称（必填），如'复仇之路'"},
            "description": {"type": "string", "description": "弧线描述"},
            "arc_type": {
                "type": "string",
                "enum": ["main", "sub", "character", "background"],
                "default": "sub",
                "description": "弧线类型"
            },
            "start_chapter": {"type": ["integer", "null"], "description": "起始章节号（可选）"},
            "end_chapter": {"type": ["integer", "null"], "description": "结束章节号（可选）"},
            "importance": {"type": "integer", "default": 1, "description": "重要程度1-5"},
        },
        "required": ["name"],
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        name: str,
        description: Optional[str] = None,
        arc_type: str = "sub",
        start_chapter: Optional[int] = None,
        end_chapter: Optional[int] = None,
        importance: int = 1,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

            from story_arcs.schemas import StoryArcType as SchemaArcType
            service = StoryArcService(db, novel_id)
            data = StoryArcCreate(
                name=name,
                description=description,
                arc_type=SchemaArcType(arc_type),
                start_chapter=start_chapter,
                end_chapter=end_chapter,
                importance=importance,
            )
            arc = await service.create_arc(data)
            return MCPToolResult(
                success=True,
                data=_arc_to_dict(arc),
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"创建叙事弧线失败: {str(e)}")


class UpdateStoryArcTool(BaseMCPTool):
    """更新叙事弧线"""

    name = "update_story_arc"
    description = (
        "更新已有的叙事弧线。可用于修改弧线状态（如暂停/完成）、调整章节范围、更新描述等。"
        "无需传novel_id，系统会注入当前小说ID。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "arc_id": {"type": "integer", "description": "弧线ID（必填）"},
            "name": {"type": "string", "description": "新的弧线名称"},
            "description": {"type": "string", "description": "新的描述"},
            "arc_type": {
                "type": "string",
                "enum": ["main", "sub", "character", "background"],
                "description": "新的弧线类型"
            },
            "start_chapter": {"type": ["integer", "null"], "description": "新的起始章节号"},
            "end_chapter": {"type": ["integer", "null"], "description": "新的结束章节号"},
            "importance": {"type": "integer", "description": "新的重要程度(1-5)"},
            "status": {
                "type": "string",
                "enum": ["active", "paused", "completed", "abandoned"],
                "description": "新状态。active=进行中，paused=暂停，completed=已完成，abandoned=已废弃"
            },
        },
        "required": ["arc_id"],
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        arc_id: int,
        name: Optional[str] = None,
        description: Optional[str] = None,
        arc_type: Optional[str] = None,
        start_chapter: Optional[int] = None,
        end_chapter: Optional[int] = None,
        importance: Optional[int] = None,
        status: Optional[str] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

            update_data: Dict[str, Any] = {}
            if name is not None:
                update_data["name"] = name
            if description is not None:
                update_data["description"] = description
            if arc_type is not None:
                update_data["arc_type"] = arc_type
            if start_chapter is not None:
                update_data["start_chapter"] = start_chapter
            if end_chapter is not None:
                update_data["end_chapter"] = end_chapter
            if importance is not None:
                update_data["importance"] = importance
            if status is not None:
                update_data["status"] = status

            if not update_data:
                return MCPToolResult(success=False, error="没有提供更新字段")

            data = StoryArcUpdate(**update_data)
            service = StoryArcService(db, novel_id)
            arc = await service.update_arc(arc_id, data)
            if not arc:
                return MCPToolResult(success=False, error=f"弧线 {arc_id} 不存在")
            return MCPToolResult(
                success=True,
                data=_arc_to_dict(arc),
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"更新叙事弧线失败: {str(e)}")


def _arc_to_dict(arc: StoryArc) -> Dict[str, Any]:
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
