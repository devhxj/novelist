"""
人物关系MCP工具集
供AI调用的3个核心工具：获取关系图/获取角色关系/创建更新演变关系
"""
from typing import Any, Dict, List, Optional

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.characters.schemas import (
    CharacterRelationCreate,
    CharacterRelationUpdate,
    CharacterRelationEvolve,
    RelationStatus,
)
from app.characters.service import CharacterService
from app.core.permissions import verify_novel_ownership
from app.mcp.novel_tools import _invalidate_character_cache


class GetCharacterNetworkTool(BaseMCPTool):
    """获取人物关系网络图"""

    name = "get_character_network"
    description = (
        "获取当前小说的人物关系图（网络结构）。无需传novel_id，系统会注入当前小说ID。"
        "\n返回图结构数据：nodes(角色节点列表) + edges(关系边列表)。"
        "每个边包含：源角色→目标角色、关系类型(ally/enemy/lover等)、强度(1-5)、状态。"
        "\n适用场景：需要整体了解人物关系格局、发现隐藏的关系链、确认角色间定位时调用。"
        "\n这是规划复杂情节或处理多角色戏份时的关键参考工具。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {},
        "required": []
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
            
            service = CharacterService(db, novel_id)
            network_data = await service.get_network()
            return MCPToolResult(
                success=True,
                data=network_data,
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"获取人物关系图失败: {str(e)}")


class GetCharacterRelationshipsTool(BaseMCPTool):
    """获取指定角色的详细关系网"""

    name = "get_character_relationships"
    description = (
        "获取指定角色的详细关系网。无需传novel_id，系统会注入当前小说ID。"
        "\n返回该角色作为'发起方'或'接受方'的所有关系，包含对方角色名、关系类型、强度、状态、演变来源等。"
        "\n适用场景：写某个角色的戏份前了解他/她与他人的关系定位、检查关系一致性时调用。"
        "\n如果需要整本书的关系全景图，用 get_character_network 更直观。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "character_id": {"type": "integer", "description": "角色ID（必填）"},
            "include_inactive": {"type": "boolean", "default": False, "description": "是否包含已失效/休眠的关系"},
        },
        "required": ["character_id"]
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        character_id: int,
        include_inactive: bool = False,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
            
            service = CharacterService(db, novel_id)
            relationships = await service.get_character_relationships(
                character_id=character_id,
                include_inactive=include_inactive
            )
            return MCPToolResult(
                success=True,
                data={
                    "relationships": relationships,
                    "total": len(relationships),
                    "character_id": character_id,
                },
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"获取角色关系失败: {str(e)}")


class UpdateCharacterRelationTool(BaseMCPTool):
    """创建或更新人物间的关系记录"""

    name = "update_character_relationship"
    description = (
        "创建或更新人物间的关系记录。无需传novel_id，系统会注入当前小说ID。"
        "\n支持三种操作模式："
        "\n1. 创建新关系 — 提供 source_character_id + target_character_id + relationship_type"
        "\n2. 更新现有关系 — 提供 relation_id + 要修改的字段"
        "\n3. 演变关系 — 提供 relation_id + evolve=true + 新的 relationship_type（旧关系自动标记为dormant，新关系链接到旧记录）"
        "\n适用场景：章节生成后发现角色关系发生变化时主动调用（如敌变友、建立新联盟、解除婚约等）。"
        "\n关系类型包括：ally(盟友), enemy(敌人), lover(恋人), family(家人), mentor(导师), rival(对手) 等18种。"
        "\n注意：这是有向关系——A对B的'mentor'不等于B对A的关系，请根据实际方向设定source/target。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "source_character_id": {"type": "integer", "description": "源角色ID（创建新关系时必填）"},
            "target_character_id": {"type": "integer", "description": "目标角色ID（创建新关系时必填）"},
            "relation_id": {"type": "integer", "description": "已有关系ID（更新或演变时使用）"},
            "relationship_type": {
                "type": "string",
                "enum": ["ally","enemy","lover","family","mentor","student","rival","acquaintance",
                         "stranger","colleague","subordinate","superior","parent","child",
                         "sibling","spouse","ex_lover","other"],
                "description": "关系类型（创建或演变时必填）"
            },
            "description": {"type": "string", "description": "关系描述"},
            "intensity": {"type": "integer", "default": 3, "description": "关系强度1-5"},
            "status": {"type": "string", "enum": ["active","dormant","resolved","severed"], "default": "active", "description": "关系状态"},
            "evolve": {"type": "boolean", "default": False, "description": "是否为关系演变（true则保留旧记录并创建新的）"},
            "evolution_notes": {"type": "string", "description": "演变原因说明（evolve=true时推荐填写）"},
            "established_chapter_id": {"type": "integer", "description": "关系确立/变化的章节ID"},
        },
        "required": []
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        source_character_id: Optional[int] = None,
        target_character_id: Optional[int] = None,
        relation_id: Optional[int] = None,
        relationship_type: Optional[str] = None,
        description: Optional[str] = None,
        intensity: int = 3,
        status: str = "active",
        evolve: bool = False,
        evolution_notes: Optional[str] = None,
        established_chapter_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
            
            service = CharacterService(db, novel_id)

            if relation_id and evolve:
                if not relationship_type:
                    return MCPToolResult(success=False, error="演变关系时 relationship_type 为必填")
                evolve_data = CharacterRelationEvolve(
                    relationship_type=relationship_type,
                    description=description,
                    intensity=intensity,
                    status=RelationStatus(status),
                    evolution_notes=evolution_notes,
                    established_chapter_id=established_chapter_id,
                )
                old_rel, new_rel = await service.evolve_relation(relation_id, evolve_data)
                await _invalidate_character_cache(novel_id)
                return MCPToolResult(
                    success=True,
                    data={
                        "old_relation": {
                            "id": old_rel.id,
                            "status": old_rel.status,
                        },
                        "new_relation": {
                            "id": new_rel.id,
                            "source_character_id": new_rel.source_character_id,
                            "target_character_id": new_rel.target_character_id,
                            "relationship_type": new_rel.relationship_type,
                            "intensity": new_rel.intensity,
                            "status": new_rel.status,
                            "evolved_from_id": new_rel.evolved_from_id,
                        },
                    },
                    metadata={"tool": self.name, "novel_id": novel_id, "action": "evolve"}
                )

            if relation_id and not evolve:
                update_fields = {}
                if relationship_type is not None:
                    update_fields["relationship_type"] = relationship_type
                if description is not None:
                    update_fields["description"] = description
                if intensity is not None:
                    update_fields["intensity"] = intensity
                if status is not None:
                    update_fields["status"] = RelationStatus(status)
                if established_chapter_id is not None:
                    update_fields["established_chapter_id"] = established_chapter_id

                if not update_fields:
                    return MCPToolResult(success=False, error="更新关系时至少需要一个要修改的字段")
                update_data = CharacterRelationUpdate(**update_fields)
                relation = await service.update_relation(relation_id, update_data)
                if not relation:
                    return MCPToolResult(success=False, error=f"关系 {relation_id} 不存在或不属于当前小说")
                await _invalidate_character_cache(novel_id)
                return MCPToolResult(
                    success=True,
                    data={
                        "id": relation.id,
                        "source_character_id": relation.source_character_id,
                        "target_character_id": relation.target_character_id,
                        "relationship_type": relation.relationship_type,
                        "intensity": relation.intensity,
                        "status": relation.status,
                    },
                    metadata={"tool": self.name, "novel_id": novel_id, "action": "update"}
                )

            if source_character_id and target_character_id:
                if not relationship_type:
                    return MCPToolResult(success=False, error="创建新关系时 relationship_type 为必填")
                create_data = CharacterRelationCreate(
                    source_character_id=source_character_id,
                    target_character_id=target_character_id,
                    relationship_type=relationship_type,
                    description=description,
                    intensity=intensity,
                    status=RelationStatus(status),
                    established_chapter_id=established_chapter_id,
                )
                relation = await service.add_relation(create_data)
                await _invalidate_character_cache(novel_id)
                return MCPToolResult(
                    success=True,
                    data={
                        "id": relation.id,
                        "source_character_id": relation.source_character_id,
                        "target_character_id": relation.target_character_id,
                        "relationship_type": relation.relationship_type,
                        "intensity": relation.intensity,
                        "status": relation.status,
                    },
                    metadata={"tool": self.name, "novel_id": novel_id, "action": "create"}
                )

            return MCPToolResult(
                success=False,
                error="参数不足：创建新关系需 source_character_id + target_character_id + relationship_type；"
                      "更新需 relation_id + 至少一个字段；演变需 relation_id + evolve=true + relationship_type"
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"操作人物关系失败: {str(e)}")


def register_character_tools(registry: MCPToolRegistry):
    registry.register(GetCharacterNetworkTool())
    registry.register(GetCharacterRelationshipsTool())
    registry.register(UpdateCharacterRelationTool())
