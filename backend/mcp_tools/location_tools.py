"""
地点管理MCP工具
"""

from pydantic import BaseModel, Field
from typing import Literal

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory
from mcp_tools.novel_tools import _invalidate_novel_cache

LocationTypeEnum = Literal[
    "city", "town", "forest", "mountain", "building", "room",
    "sea", "river", "road", "castle", "temple", "village",
    "dungeon", "palace", "market", "inn", "other",
]


class GetLocationsArgs(BaseModel):
    mode: Literal["list", "detail"] = Field(default="list", description="查询模式：list=地点列表，detail=地点详情")
    location_id: int | None = Field(default=None, description="地点ID（detail模式必填）")
    location_type: LocationTypeEnum | None = Field(default=None, description="按类型筛选（list模式可选）")
    search: str | None = Field(default=None, description="按名称或描述搜索（list模式可选）")


class GetLocationsTool(BaseMCPTool):
    """获取地点信息（列表/详情）"""

    name = "get_locations"
    description = (
        "获取当前小说的地点信息，支持两种模式："
        "\n- list: 地点列表，参数: location_type(类型筛选), search(关键词搜索)"
        "\n- detail: 地点详情（含地理信息、关联角色、子地点等），参数: location_id(必填)"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = GetLocationsArgs

    async def _execute(
        self,
        args: GetLocationsArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
    ) -> MCPToolResult:
        try:
            from locations.service import LocationService
            svc = LocationService(db, novel_id)

            if args.mode == "detail":
                if not args.location_id:
                    return MCPToolResult(success=False, error="detail 模式需要 location_id")
                location = await svc.get_by_id(args.location_id)
                if not location:
                    return MCPToolResult(success=False, error=f"地点 {args.location_id} 不存在")

                parent_name = None
                if location.parent_location_id:
                    parent = await svc.get_by_id(location.parent_location_id)
                    if parent:
                        parent_name = parent.name

                child_locs = await svc.get_children(args.location_id)
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
                    metadata={"tool": self.name, "novel_id": novel_id, "location_id": args.location_id, "mode": "detail"}
                )
            else:
                if args.search:
                    locations = await svc.search(args.search)
                elif args.location_type:
                    locations = await svc.get_by_type(args.location_type)
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


class CreateLocationArgs(BaseModel):
    name: str = Field(default="", description="地点名称（必填），如'迷雾森林''黑铁城堡'")
    location_type: LocationTypeEnum | None = Field(default=None, description="地点类型")
    description: str | None = Field(default=None, description="环境氛围、特色等描述")
    tags: list[str] | None = Field(default=None, description="标签，如['危险','神秘','主角出生地']")
    parent_location_id: int | None = Field(default=None, description="父级地点ID（用于构建层级关系）")


class CreateLocationTool(BaseMCPTool):
    """创建新地点"""

    name = "create_location"
    description = (
        "为当前小说创建一个新地点。"
        "\n适用场景：用户要求添加新地点、AI写作时发现需要新的场景设定、规划世界观时。"
        "\nname 为必填；location_type 建议从 city/town/forest/building 等中选择。"
        "\n创建后可通过 get_locations(mode=\"detail\") 查看详情，通过 update_location 修改设定。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = CreateLocationArgs

    async def _execute(
        self,
        args: CreateLocationArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
    ) -> MCPToolResult:
        try:
            from locations.schemas import LocationCreate, LocationType
            from locations.service import LocationService

            loc_data = LocationCreate(
                name=args.name,
                location_type=LocationType(args.location_type) if args.location_type else LocationType.OTHER,
                description=args.description,
                tags=args.tags,
                parent_location_id=args.parent_location_id,
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


class UpdateLocationArgs(BaseModel):
    location_id: int = Field(description="地点ID（必填）")
    name: str | None = Field(default=None, description="新的名称")
    location_type: LocationTypeEnum | None = Field(default=None, description="新的类型")
    description: str | None = Field(default=None, description="新的描述")
    tags: list[str] | None = Field(default=None, description="新的标签列表（完全替换旧的）")
    parent_location_id: int | None = Field(default=None, description="新的父级地点ID（设null解除层级）")


class UpdateLocationTool(BaseMCPTool):
    """更新地点信息"""

    name = "update_location"
    description = (
        "更新已有地点的设定信息。"
        "\n适用场景：修改地点名称、类型、描述、标签、父子关系等。"
        "\n只需传入要修改的字段，未传入的字段保持不变。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = UpdateLocationArgs

    async def _execute(
        self,
        args: UpdateLocationArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
    ) -> MCPToolResult:
        try:
            from locations.schemas import LocationUpdate, LocationType
            from locations.service import LocationService

            svc = LocationService(db, novel_id)
            update_fields = args.model_dump(exclude_unset=True)
            update_fields.pop("location_id", None)
            if "location_type" in update_fields:
                update_fields["location_type"] = LocationType(update_fields["location_type"])

            if not update_fields:
                return MCPToolResult(success=False, error="至少需要提供一个要修改的字段")

            data = LocationUpdate(**update_fields)
            location = await svc.update(args.location_id, data)
            if not location:
                return MCPToolResult(success=False, error=f"地点 {args.location_id} 不存在或不属于当前小说")

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
                metadata={"tool": self.name, "novel_id": novel_id, "location_id": args.location_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"更新地点失败: {str(e)}")


class DeleteLocationArgs(BaseModel):
    location_id: int = Field(description="要删除的地点ID（必填）")


class DeleteLocationTool(BaseMCPTool):
    """删除地点"""

    name = "delete_location"
    description = (
        "删除一个已创建的地点。"
        "\n⚠️ 谨慎操作：删除后不可恢复。如果该地点有子地点，子地点不会自动删除但会失去父级引用。"
        "\n适用场景：错误创建了重复地点、某地点不再需要时。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = DeleteLocationArgs

    async def _execute(
        self,
        args: DeleteLocationArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
    ) -> MCPToolResult:
        try:
            from locations.service import LocationService
            svc = LocationService(db, novel_id)
            deleted = await svc.delete(args.location_id)
            if not deleted:
                return MCPToolResult(success=False, error=f"地点 {args.location_id} 不存在、不属于当前小说或已删除")
            await _invalidate_novel_cache(novel_id)
            return MCPToolResult(
                success=True,
                data={"deleted": True, "location_id": args.location_id},
                metadata={"tool": self.name, "novel_id": novel_id, "location_id": args.location_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"删除地点失败: {str(e)}")


def register_location_tools(registry) -> None:
    registry.register(GetLocationsTool())
    registry.register(CreateLocationTool())
    registry.register(UpdateLocationTool())
    registry.register(DeleteLocationTool())
