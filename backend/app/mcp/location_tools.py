"""
地点管理MCP工具
"""
from typing import Any, Dict, List, Optional

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory
from app.core.permissions import verify_novel_ownership
from app.mcp.novel_tools import _invalidate_novel_cache


class GetLocationsTool(BaseMCPTool):
    """获取地点信息（列表/详情）"""

    name = "get_locations"
    description = (
        "获取当前小说的地点信息，支持两种模式："
        "\n- list: 地点列表，参数: location_type(类型筛选), search(关键词搜索)"
        "\n- detail: 地点详情（含地理信息、关联角色、子地点等），参数: location_id(必填)"
        "\n无需传novel_id，系统会注入当前小说ID。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "mode": {
                "type": "string",
                "enum": ["list", "detail"],
                "description": "查询模式：list=地点列表，detail=地点详情"
            },
            "location_id": {
                "type": "integer",
                "description": "地点ID（detail模式必填）"
            },
            "location_type": {
                "type": "string",
                "enum": ["city", "town", "forest", "mountain", "building", "room",
                         "sea", "river", "road", "castle", "temple", "village",
                         "dungeon", "palace", "market", "inn", "other"],
                "description": "按类型筛选（list模式可选）"
            },
            "search": {"type": "string", "description": "按名称或描述搜索（list模式可选）"},
        },
        "required": ["mode"]
    }

    async def execute(self, db, novel_id: int, user_id: int, mode: str = "list",
                     location_id: Optional[int] = None, location_type: Optional[str] = None,
                     search: Optional[str] = None, **kwargs) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

            from app.locations.service import LocationService
            svc = LocationService(db, novel_id)

            if mode == "detail":
                if not location_id:
                    return MCPToolResult(success=False, error="detail 模式需要 location_id")
                location = await svc.get_by_id(location_id)
                if not location:
                    return MCPToolResult(success=False, error=f"地点 {location_id} 不存在")

                parent_name = None
                if location.parent_location_id:
                    parent = await svc.get_by_id(location.parent_location_id)
                    if parent:
                        parent_name = parent.name

                child_locs = await svc.get_children(location_id)
                children = [{"id": c.id, "name": c.name, "type": c.location_type}
                           for c in child_locs]

                return MCPToolResult(
                    success=True,
                    data={
                        "id": location.id,
                        "name": location.name,
                        "type": location.location_type,
                        "description": location.description,
                        "geo_info": location.geo_info,
                        "related_characters": location.related_characters,
                        "related_chapters": location.related_chapters,
                        "tags": location.tags,
                        "parent_name": parent_name,
                        "children": children,
                        "children_count": len(children),
                    },
                    metadata={"tool": self.name, "novel_id": novel_id, "location_id": location_id, "mode": "detail"}
                )
            else:
                if search:
                    locations = await svc.search(search)
                elif location_type:
                    locations = await svc.get_by_type(location_type)
                else:
                    locations = await svc.get_all()

                return MCPToolResult(
                    success=True,
                    data={
                        "locations": [
                            {"id": l.id, "name": l.name, "type": l.location_type,
                             "description": l.description[:100] if l.description else None,
                             "tags": l.tags}
                            for l in locations
                        ],
                        "total": len(locations),
                        "types": list(set(l.location_type for l in locations)),
                    },
                    metadata={"tool": self.name, "novel_id": novel_id, "mode": "list"}
                )
        except Exception as e:
            return MCPToolResult(success=False, error=f"获取地点信息失败: {str(e)}")


class CreateLocationTool(BaseMCPTool):
    """创建新地点"""

    name = "create_location"
    description = (
        "为当前小说创建一个新地点。无需传novel_id，系统会注入当前小说ID。"
        "\n适用场景：用户要求添加新地点、AI写作时发现需要新的场景设定、规划世界观时。"
        "\nname 为必填；location_type 建议从 city/town/forest/building 等中选择。"
        "\n创建后可通过 get_locations(mode=\"detail\") 查看详情，通过 update_location 修改设定。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "地点名称（必填），如'迷雾森林''黑铁城堡'"},
            "location_type": {
                "type": "string",
                "enum": ["city", "town", "forest", "mountain", "building", "room",
                         "sea", "river", "road", "castle", "temple", "village",
                         "dungeon", "palace", "market", "inn", "other"],
                "description": "地点类型"
            },
            "description": {"type": "string", "description": "环境氛围、特色等描述"},
            "tags": {
                "type": "array",
                "items": {"type": "string"},
                "description": "标签，如['危险','神秘','主角出生地']"
            },
            "parent_location_id": {"type": "integer", "description": "父级地点ID（用于构建层级关系）"},
        },
        "required": ["name"]
    }

    async def execute(self, db, novel_id: int, user_id: int, name: str="",
                     location_type: Optional[str]=None, description: Optional[str]=None,
                     tags: Optional[List[str]]=None, parent_location_id: Optional[int]=None,
                     **kwargs) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

            from app.locations.schemas import LocationCreate, LocationType
            from app.locations.service import LocationService

            loc_data = LocationCreate(
                name=name,
                location_type=LocationType(location_type) if location_type else LocationType.OTHER,
                description=description,
                tags=tags,
                parent_location_id=parent_location_id,
            )

            svc = LocationService(db, novel_id)
            location = await svc.create(loc_data)

            await _invalidate_novel_cache(novel_id)

            return MCPToolResult(
                success=True,
                data={
                    "id": location.id,
                    "name": location.name,
                    "type": location.location_type,
                    "novel_id": novel_id,
                },
                metadata={"tool": self.name, "novel_id": novel_id, "location_id": location.id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"创建地点失败: {str(e)}")


class UpdateLocationTool(BaseMCPTool):
    """更新地点信息"""

    name = "update_location"
    description = (
        "更新已有地点的设定信息。无需传novel_id，系统会注入当前小说ID。"
        "\n适用场景：修改地点名称、类型、描述、标签、父子关系等。"
        "\n只需传入要修改的字段，未传入的字段保持不变。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "location_id": {"type": "integer", "description": "地点ID（必填）"},
            "name": {"type": "string", "description": "新的名称"},
            "location_type": {
                "type": "string",
                "enum": ["city", "town", "forest", "mountain", "building", "room",
                         "sea", "river", "road", "castle", "temple", "village",
                         "dungeon", "palace", "market", "inn", "other"],
                "description": "新的类型"
            },
            "description": {"type": "string", "description": "新的描述"},
            "tags": {
                "type": "array",
                "items": {"type": "string"},
                "description": "新的标签列表（完全替换旧的）"
            },
            "parent_location_id": {"type": ["integer", "null"], "description": "新的父级地点ID（设null解除层级）"},
        },
        "required": ["location_id"]
    }

    async def execute(self, db, novel_id: int, user_id: int, location_id: int,
                     **kwargs) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
            
            from app.locations.schemas import LocationUpdate, LocationType
            from app.locations.service import LocationService

            svc = LocationService(db, novel_id)
            update_fields = {}
            if kwargs.get("name") is not None:
                update_fields["name"] = kwargs["name"]
            if kwargs.get("location_type") is not None:
                update_fields["location_type"] = LocationType(kwargs["location_type"])
            if kwargs.get("description") is not None:
                update_fields["description"] = kwargs["description"]
            if kwargs.get("tags") is not None:
                update_fields["tags"] = kwargs["tags"]
            if kwargs.get("parent_location_id") is not None:
                update_fields["parent_location_id"] = kwargs["parent_location_id"]

            if not update_fields:
                return MCPToolResult(success=False, error="至少需要提供一个要修改的字段")

            data = LocationUpdate(**update_fields)
            location = await svc.update(location_id, data)
            if not location:
                return MCPToolResult(success=False, error=f"地点 {location_id} 不存在或不属于当前小说")

            await _invalidate_novel_cache(novel_id)

            return MCPToolResult(
                success=True,
                data={
                    "id": location.id,
                    "name": location.name,
                    "type": location.location_type,
                    "description": location.description,
                    "tags": location.tags,
                },
                metadata={"tool": self.name, "novel_id": novel_id, "location_id": location_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"更新地点失败: {str(e)}")


class DeleteLocationTool(BaseMCPTool):
    """删除地点"""

    name = "delete_location"
    description = (
        "删除一个已创建的地点。无需传novel_id，系统会注入当前小说ID。"
        "\n⚠️ 谨慎操作：删除后不可恢复。如果该地点有子地点，子地点不会自动删除但会失去父级引用。"
        "\n适用场景：错误创建了重复地点、某地点不再需要时。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "location_id": {"type": "integer", "description": "要删除的地点ID（必填）"},
        },
        "required": ["location_id"]
    }

    async def execute(self, db, novel_id: int, user_id: int, location_id: int,
                     **kwargs) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
            
            from app.locations.service import LocationService
            svc = LocationService(db, novel_id)
            deleted = await svc.delete(location_id)
            if not deleted:
                return MCPToolResult(success=False, error=f"地点 {location_id} 不存在、不属于当前小说或已删除")
            await _invalidate_novel_cache(novel_id)
            return MCPToolResult(
                success=True,
                data={"deleted": True, "location_id": location_id},
                metadata={"tool": self.name, "novel_id": novel_id, "location_id": location_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"删除地点失败: {str(e)}")


def register_location_tools(registry) -> None:
    registry.register(GetLocationsTool())
    registry.register(CreateLocationTool())
    registry.register(UpdateLocationTool())
    registry.register(DeleteLocationTool())
