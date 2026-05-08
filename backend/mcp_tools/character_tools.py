"""
人物关系MCP工具集
供AI调用的核心工具：创建更新演变关系
"""

from pydantic import BaseModel, Field
from typing import Literal

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from characters.schemas import (
    CharacterRelationCreate,
    CharacterRelationUpdate,
    CharacterRelationEvolve,
    RelationStatus,
)
from characters.service import CharacterService
from mcp_tools.novel_tools import _invalidate_character_cache

RelationTypeEnum = Literal[
    "ally", "enemy", "lover", "family", "mentor", "student", "rival", "acquaintance",
    "stranger", "colleague", "subordinate", "superior", "parent", "child",
    "sibling", "spouse", "ex_lover", "other",
]

RelationStatusEnum = Literal["active", "dormant", "resolved", "severed"]


class UpdateCharacterRelationArgs(BaseModel):
    source_character_id: int | None = Field(default=None, description="源角色ID（创建新关系时必填）")
    target_character_id: int | None = Field(default=None, description="目标角色ID（创建新关系时必填）")
    relation_id: int | None = Field(default=None, description="已有关系ID（更新或演变时使用）")
    relationship_type: RelationTypeEnum | None = Field(default=None, description="关系类型（创建或演变时必填）")
    description: str | None = Field(default=None, description="关系描述")
    intensity: int = Field(default=3, description="关系强度1-5")
    status: RelationStatusEnum = Field(default="active", description="关系状态")
    evolve: bool = Field(default=False, description="是否为关系演变（true则保留旧记录并创建新的）")
    evolution_notes: str | None = Field(default=None, description="演变原因说明（evolve=true时推荐填写）")
    established_chapter_id: int | None = Field(default=None, description="关系确立/变化的章节ID")


class UpdateCharacterRelationTool(BaseMCPTool):
    """创建或更新人物间的关系记录"""

    name = "update_character_relationship"
    description = (
        "创建或更新人物间的关系记录。"
        "\n支持三种操作模式："
        "\n1. 创建新关系 — 提供 source_character_id + target_character_id + relationship_type"
        "\n2. 更新现有关系 — 提供 relation_id + 要修改的字段"
        "\n3. 演变关系 — 提供 relation_id + evolve=true + 新的 relationship_type（旧关系自动标记为dormant，新关系链接到旧记录）"
        "\n适用场景：章节生成后发现角色关系发生变化时主动调用（如敌变友、建立新联盟、解除婚约等）。"
        "\n关系类型包括：ally(盟友), enemy(敌人), lover(恋人), family(家人), mentor(导师), rival(对手) 等18种。"
        "\n注意：这是有向关系——A对B的'mentor'不等于B对A的关系，请根据实际方向设定source/target。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    args_schema = UpdateCharacterRelationArgs

    async def _execute(
        self,
        args: UpdateCharacterRelationArgs,
        *,
        db,
        user_id: int,
        novel_id: int,
    ) -> MCPToolResult:
        try:
            service = CharacterService(db, novel_id)

            if args.relation_id and args.evolve:
                if not args.relationship_type:
                    return MCPToolResult(success=False, error="演变关系时 relationship_type 为必填")
                evolve_data = CharacterRelationEvolve(
                    relationship_type=args.relationship_type,
                    description=args.description,
                    intensity=args.intensity,
                    status=RelationStatus(args.status),
                    evolution_notes=args.evolution_notes,
                    established_chapter_id=args.established_chapter_id,
                )
                old_rel, new_rel = await service.evolve_relation(args.relation_id, evolve_data)
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

            if args.relation_id and not args.evolve:
                update_fields = args.model_dump(exclude_unset=True)
                update_fields.pop("relation_id", None)
                update_fields.pop("source_character_id", None)
                update_fields.pop("target_character_id", None)
                update_fields.pop("evolve", None)
                update_fields.pop("evolution_notes", None)
                if "status" in update_fields:
                    update_fields["status"] = RelationStatus(update_fields["status"])

                if not update_fields:
                    return MCPToolResult(success=False, error="更新关系时至少需要一个要修改的字段")
                update_data = CharacterRelationUpdate(**update_fields)
                relation = await service.update_relation(args.relation_id, update_data)
                if not relation:
                    return MCPToolResult(success=False, error=f"关系 {args.relation_id} 不存在或不属于当前小说")
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

            if args.source_character_id and args.target_character_id:
                if not args.relationship_type:
                    return MCPToolResult(success=False, error="创建新关系时 relationship_type 为必填")
                create_data = CharacterRelationCreate(
                    source_character_id=args.source_character_id,
                    target_character_id=args.target_character_id,
                    relationship_type=args.relationship_type,
                    description=args.description,
                    intensity=args.intensity,
                    status=RelationStatus(args.status),
                    established_chapter_id=args.established_chapter_id,
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
    registry.register(UpdateCharacterRelationTool())
