"""
人物关系MCP工具集
供AI调用的核心工具：创建更新演变关系
"""
import logging

from pydantic import BaseModel, Field
from typing import Literal

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry

logger = logging.getLogger(__name__)

from characters.schemas import (
    CharacterRelationCreate,
    CharacterRelationUpdate,
    CharacterRelationEvolve,
    RelationStatus,
)
from characters.service import CharacterService
from .utils import _invalidate_character_cache
from sqlalchemy import select                                                                                                                                      
from sqlalchemy.orm import selectinload                                                                                                                            
from rag.vector_store import vector_store, VectorStoreError
from sqlalchemy.ext.asyncio import AsyncSession
from typing import Any
from characters.models import Character
class GetCharactersArgs(BaseModel):
    mode: Literal["list", "detail", "network"] = Field(default="list", description="查询模式：list=角色列表概览，detail=单角色详细档案，network=关系网络图")
    character_id: int | None = Field(default=None, description="角色ID（detail模式必填，network模式可选）")
    search: str | None = Field(default=None, description="角色名搜索（list模式可选）")
    include_relations: bool = Field(default=True, description="是否包含人物关系网络（list模式）")
    include_recent_events: bool = Field(default=True, description="是否包含各角色的最近动态（list模式）")
    include_memory: bool = Field(default=False, description="是否包含语义检索的相关内容片段（detail模式）")
    include_inactive: bool = Field(default=False, description="是否包含已失效/休眠的关系（network模式）")

class GetCharactersTool(BaseMCPTool):
    """获取角色信息（列表/详情/关系网络）"""

    name = "get_characters"
    description = (
        "获取当前小说的角色信息，支持三种模式："
        "\n- list: 角色列表概览（含性格标签、关系概要、最近动态），参数: search, include_relations, include_recent_events"
        "\n- detail: 单角色详细档案，参数: character_id(必填), include_memory(语义检索)"
        "\n- network: 关系网络图，参数: character_id(可选,有=单角色,无=全局), include_inactive"
        "\n适用场景：写作前了解角色阵容、深入了解某个角色、查看人物关系网络。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = GetCharactersArgs

    @staticmethod
    def _extract_personality_summary(personality: dict[str, Any] | None) -> str:
        if not personality or not isinstance(personality, dict):
            return ""
        parts: list[str] = []
        for key, value in personality.items():
            text = str(value).strip()
            if text and len(text) < 200:
                parts.append(f"{key}：{text}")
        return "；".join(parts[:5])

    async def _execute(
        self,
        args: GetCharactersArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        if args.mode == "detail":
            return await self._execute_detail(db, novel_id, args.character_id, args.include_memory)
        elif args.mode == "network":
            return await self._execute_network(db, novel_id, args.character_id, args.include_inactive)
        else:
            return await self._execute_list(db, novel_id, args.search, args.include_relations, args.include_recent_events)

    async def _execute_list(self, db, novel_id, search, include_relations, include_recent_events):
        query = select(Character).where(Character.novel_id == novel_id)
        if search:
            query = query.filter(Character.name.contains(search))
        result = await db.execute(query)
        characters = result.scalars().all()
        char_id_map = {c.id: c for c in characters}

        characters_data = [
            {
                "id": c.id,
                "name": c.name,
                "personality_summary": self._extract_personality_summary(c.personality),
                "abilities": c.abilities or [],
            }
            for c in characters
        ]

        relations_data = []
        if include_relations and characters:
            try:
                from characters.models import CharacterRelation
                rel_result = await db.execute(
                    select(CharacterRelation).where(
                        CharacterRelation.novel_id == novel_id,
                        CharacterRelation.status == "active"
                    )
                )
                all_relations = rel_result.scalars().all()
                for rel in all_relations:
                    source = char_id_map.get(rel.source_character_id)
                    target = char_id_map.get(rel.target_character_id)
                    if source and target:
                        relations_data.append({
                            "source_name": source.name,
                            "target_name": target.name,
                            "type": rel.relationship_type,
                            "intensity": rel.intensity,
                            "status": rel.status,
                        })
            except Exception:
                logger.warning("Failed to load character relations", exc_info=True)

        recent_events_summary = ""
        if include_recent_events and characters:
            try:
                from timeline.models import TimelineEntry
                entry_result = await db.execute(
                    select(TimelineEntry)
                    .where(TimelineEntry.novel_id == novel_id)
                    .order_by(TimelineEntry.updated_at.desc())
                    .limit(10)
                )
                recent_entries = entry_result.scalars().all()
                if recent_entries:
                    event_lines = []
                    for entry in recent_entries:
                        status_label = {"pending": "待办", "active": "进行中", "completed": "已完成", "resolved": "已回收"}.get(entry.status, entry.status)
                        line = f"[{entry.category}/{status_label}] {entry.title}"
                        if entry.description:
                            line += f" — {entry.description[:50]}"
                        event_lines.append(line)
                    recent_events_summary = "\n".join(event_lines)
                else:
                    recent_events_summary = "暂无追踪记录。"
            except Exception:
                logger.warning("Failed to load recent timeline events", exc_info=True)
                recent_events_summary = ""

        return MCPToolResult(
            success=True,
            data={
                "characters": characters_data,
                "relations": relations_data,
                "recent_events_summary": recent_events_summary,
                "total_characters": len(characters),
            },
            metadata={"tool": self.name, "novel_id": novel_id, "mode": "list"}
        )

    async def _execute_detail(self, db, novel_id, character_id, include_memory):
        if not character_id:
            return MCPToolResult(success=False, error="detail 模式需要 character_id")

        result = await db.execute(
            select(Character)
            .options(selectinload(Character.novel))
            .where(Character.id == character_id)
        )
        character = result.scalar_one_or_none()
        if not character:
            return MCPToolResult(success=False, error=f"角色不存在: {character_id}")
        if character.novel_id != novel_id:
            return MCPToolResult(success=False, error=f"角色 {character_id} 不属于当前小说")

        data = {
            "id": character.id,
            "novel_id": character.novel_id,
            "name": character.name,
            "personality": character.personality,
            "abilities": character.abilities,
            "relationships": character.relationships,
            "created_at": character.created_at.isoformat() if character.created_at else None,
            "novel": {"id": character.novel.id, "title": character.novel.title} if character.novel else None,
        }

        if include_memory:
            try:
                search_results = await vector_store.search(novel_id=novel_id, query=character.name, top_k=5)
                data["relevant_content"] = [
                    {"content": r["content"][:200] + "..." if len(r["content"]) > 200 else r["content"],
                     "chapter_id": r["metadata"].get("chapter_id")}
                    for r in search_results
                ]
            except VectorStoreError:
                data["relevant_content"] = []

        return MCPToolResult(
            success=True,
            data=data,
            metadata={"tool": self.name, "novel_id": novel_id, "character_id": character_id, "mode": "detail"}
        )

    async def _execute_network(self, db, novel_id, character_id, include_inactive):
        from characters.service import CharacterService
        service = CharacterService(db, novel_id)

        if character_id:
            relationships = await service.get_character_relationships(
                character_id=character_id, include_inactive=include_inactive
            )
            return MCPToolResult(
                success=True,
                data={"relationships": relationships, "total": len(relationships), "character_id": character_id},
                metadata={"tool": self.name, "novel_id": novel_id, "mode": "network"}
            )
        else:
            network_data = await service.get_network()
            return MCPToolResult(
                success=True,
                data=network_data,
                metadata={"tool": self.name, "novel_id": novel_id, "mode": "network"}
            )


class CreateCharacterArgs(BaseModel):
    name: str = Field(description="角色名称（必填）")
    personality: dict | None = Field(default=None, description="角色性格/设定字典，建议包含: role(定位), traits(性格), background(背景), motivation(动机), appearance(外貌)")
    abilities: list[str] | None = Field(default=None, description="角色能力/技能列表")

class CreateCharacterTool(BaseMCPTool):
    """创建新角色"""

    name = "create_character"
    description = (
        "为当前小说创建一个新角色。"
        "\n适用场景：用户要求添加新角色、AI写作时发现需要新角色、规划角色阵容时。"
        "\n创建后可通过 get_characters(mode=\"detail\") 查看详情，通过 update_character 修改设定。"
        "\n注意：name 为必填；personality 建议包含 role(角色定位)、traits(性格特点)、background(背景) 等字段。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = CreateCharacterArgs

    async def _execute(
        self,
        args: CreateCharacterArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        from characters.models import Character
        character = Character(
            novel_id=novel_id,
            name=args.name,
            personality=args.personality or {},
            abilities=args.abilities or [],
        )
        db.add(character)
        await db.commit()
        await db.refresh(character)

        await _invalidate_character_cache(novel_id)

        return MCPToolResult(
            success=True,
            data={
                "id": character.id,
                "name": character.name,
                "novel_id": character.novel_id,
                "personality": character.personality,
                "abilities": character.abilities,
            },
            metadata={"tool": self.name, "novel_id": novel_id, "character_id": character.id}
        )


class UpdateCharacterArgs(BaseModel):
    character_id: int = Field(description="角色ID（必填）")
    name: str | None = Field(default=None, description="新的名称")
    personality: dict | None = Field(default=None, description="新的性格/设定字典（完全替换旧的）")
    abilities: list[str] | None = Field(default=None, description="新的能力列表（完全替换旧的）")

class UpdateCharacterTool(BaseMCPTool):
    """更新角色信息"""

    name = "update_character"
    description = (
        "更新已有角色的设定信息。"
        "\n适用场景：用户要求修改角色设定、AI写作中发现需要调整角色属性时。"
        "\n只需传入要修改的字段，未传入的字段保持不变。"
        "\n修改后相关缓存会自动失效，下次查询获取最新数据。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = UpdateCharacterArgs

    async def _execute(
        self,
        args: UpdateCharacterArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        from characters.models import Character
        result = await db.execute(
            select(Character).where(Character.id == args.character_id)
        )
        character = result.scalar_one_or_none()
        if not character:
            return MCPToolResult(success=False, error=f"角色 {args.character_id} 不存在")
        if character.novel_id != novel_id:
            return MCPToolResult(success=False, error=f"角色不属于当前小说")

        if args.name is not None:
            character.name = args.name
        if args.personality is not None:
            character.personality = args.personality
        if args.abilities is not None:
            character.abilities = args.abilities

        await db.commit()
        await db.refresh(character)

        await _invalidate_character_cache(novel_id, args.character_id)

        return MCPToolResult(
            success=True,
            data={
                "id": character.id,
                "name": character.name,
                "novel_id": character.novel_id,
                "personality": character.personality,
                "abilities": character.abilities,
            },
            metadata={"tool": self.name, "novel_id": novel_id, "character_id": args.character_id}
        )

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
        **extra,
    ) -> MCPToolResult:
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
       


def register_character_tools(registry: MCPToolRegistry):
    registry.register(GetCharactersTool())
    registry.register(CreateCharacterTool())
    registry.register(UpdateCharacterTool())
    registry.register(UpdateCharacterRelationTool())
