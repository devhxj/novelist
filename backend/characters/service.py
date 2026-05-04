"""
角色管理模块 - 服务层
提供角色和人物关系的业务逻辑
"""
from typing import Any, Dict, List, Optional, Tuple

from sqlalchemy import select, and_, or_
from sqlalchemy.ext.asyncio import AsyncSession

from datetime import datetime, timezone

from characters.models import Character, CharacterRelation
from core.exceptions import BadRequestException
from characters.schemas import (
    CharacterRelationCreate,
    CharacterRelationUpdate,
    CharacterRelationEvolve,
    RelationStatus,
)


class CharacterService:
    """角色与人物关系服务"""

    def __init__(self, db: AsyncSession, novel_id: int):
        self.db = db
        self.novel_id = novel_id

    async def get_all_characters(self) -> List[Character]:
        """获取小说所有角色"""
        result = await self.db.execute(
            select(Character).where(Character.novel_id == self.novel_id)
        )
        return list(result.scalars().all())

    async def get_network(self) -> Dict[str, Any]:
        """
        获取人物关系图结构 {nodes:[], edges:[]}
        nodes: [{id, name, role_hint}]
        edges: [{source, source_name, target, target_name, type, intensity, status}]
        """
        characters = await self.get_all_characters()
        char_map = {c.id: c for c in characters}

        nodes = []
        for c in characters:
            role_hint = ""
            if c.personality:
                role_hint = (
                    c.personality.get("role", "")
                    or c.personality.get("role_hint", "")
                    or ""
                )
            nodes.append(
                {
                    "id": c.id,
                    "name": c.name,
                    "role_hint": role_hint,
                }
            )

        edges = []
        result = await self.db.execute(
            select(CharacterRelation).where(
                CharacterRelation.novel_id == self.novel_id,
                CharacterRelation.status == RelationStatus.ACTIVE.value,
            )
        )
        relations = result.scalars().all()

        for r in relations:
            source_char = char_map.get(r.source_character_id)
            target_char = char_map.get(r.target_character_id)
            if source_char and target_char:
                edges.append(
                    {
                        "source": r.source_character_id,
                        "source_name": source_char.name,
                        "target": r.target_character_id,
                        "target_name": target_char.name,
                        "type": r.relationship_type,
                        "intensity": r.intensity,
                        "status": r.status,
                    }
                )

        return {
            "nodes": nodes,
            "edges": edges,
            "total_nodes": len(nodes),
            "total_edges": len(edges),
        }

    async def get_character_relationships(
        self, character_id: int, include_inactive: bool = False
    ) -> List[Dict[str, Any]]:
        """
        获取某角色的所有关系（作为source或target）
        返回 enriched 数据（含对方名字等）
        """
        query = select(CharacterRelation).where(
            CharacterRelation.novel_id == self.novel_id,
            or_(
                CharacterRelation.source_character_id == character_id,
                CharacterRelation.target_character_id == character_id,
            ),
        )
        if not include_inactive:
            query = query.where(CharacterRelation.status == RelationStatus.ACTIVE.value)

        query = query.order_by(CharacterRelation.intensity.desc(), CharacterRelation.created_at)
        result = await self.db.execute(query)
        relations = result.scalars().all()

        # 获取所有相关角色名
        char_ids = set()
        for r in relations:
            char_ids.add(r.source_character_id)
            char_ids.add(r.target_character_id)

        char_result = await self.db.execute(
            select(Character).where(Character.id.in_(char_ids))
        )
        char_map = {c.id: c.name for c in char_result.scalars().all()}

        result_list = []
        for r in relations:
            evolved_from_type = None
            if r.evolved_from_id:
                evolved_result = await self.db.execute(
                    select(CharacterRelation).where(
                        CharacterRelation.id == r.evolved_from_id
                    )
                )
                evolved = evolved_result.scalar_one_or_none()
                if evolved:
                    evolved_from_type = evolved.relationship_type

            result_list.append(
                {
                    "id": r.id,
                    "source_character_id": r.source_character_id,
                    "source_name": char_map.get(r.source_character_id, "未知"),
                    "target_character_id": r.target_character_id,
                    "target_name": char_map.get(r.target_character_id, "未知"),
                    "relationship_type": r.relationship_type,
                    "description": r.description,
                    "intensity": r.intensity,
                    "status": r.status,
                    "established_chapter_id": r.established_chapter_id,
                    "evolved_from_id": r.evolved_from_id,
                    "evolved_from_type": evolved_from_type,
                    "created_at": r.created_at.isoformat() if r.created_at else None,
                }
            )

        return result_list

    async def add_relation(self, data: CharacterRelationCreate) -> CharacterRelation:
        """创建新关系"""
        # 校验两个角色属于同一小说
        source = await self.db.get(Character, data.source_character_id)
        target = await self.db.get(Character, data.target_character_id)
        if not source or not target:
            raise BadRequestException(f"源角色或目标角色不存在 (source_id={data.source_character_id}, target_id={data.target_character_id})")
        if source.novel_id != self.novel_id or target.novel_id != self.novel_id:
            raise BadRequestException("角色不属于当前小说")
        if data.source_character_id == data.target_character_id:
            raise BadRequestException("不能创建自身到自身的关系")

        relation = CharacterRelation(
            novel_id=self.novel_id,
            **data.model_dump(),
        )
        self.db.add(relation)
        await self.db.commit()
        await self.db.refresh(relation)
        return relation

    async def update_relation(
        self, relation_id: int, data: CharacterRelationUpdate
    ) -> Optional[CharacterRelation]:
        """更新关系"""
        relation = await self.db.get(CharacterRelation, relation_id)
        if not relation or relation.novel_id != self.novel_id:
            return None

        update_data = data.model_dump(exclude_unset=True)
        for key, value in update_data.items():
            setattr(relation, key, value)

        await self.db.commit()
        await self.db.refresh(relation)
        return relation

    async def evolve_relation(
        self, relation_id: int, data: CharacterRelationEvolve
    ) -> Tuple[CharacterRelation, CharacterRelation]:
        """
        关系演变：
        - 将旧关系标记为 dormant
        - 创建新关系记录，evolved_from_id 指向旧记录
        - 返回 (旧关系, 新关系)
        """
        old_relation = await self.db.get(CharacterRelation, relation_id)
        if not old_relation or old_relation.novel_id != self.novel_id:
            raise BadRequestException(f"原关系不存在或不属于当前小说 (relation_id={relation_id})")

        # 标记旧关系为 dormant
        old_relation.status = RelationStatus.DORMANT.value

        # 创建新关系
        new_relation = CharacterRelation(
            novel_id=self.novel_id,
            source_character_id=old_relation.source_character_id,
            target_character_id=old_relation.target_character_id,
            relationship_type=data.relationship_type,
            description=data.description,
            intensity=data.intensity,
            status=data.status.value if isinstance(data.status, RelationStatus) else data.status,
            established_chapter_id=data.established_chapter_id,
            evolved_from_id=relation_id,
            extra_metadata={
                **(old_relation.extra_metadata or {}),
                "evolution_notes": data.evolution_notes,
                "evolved_at": datetime.now(timezone.utc).isoformat(),
                **(data.extra_metadata or {}),
            },
        )
        self.db.add(new_relation)
        await self.db.commit()
        await self.db.refresh(old_relation)
        await self.db.refresh(new_relation)

        await self._create_timeline_entry_for_evolution(old_relation, new_relation, data.evolution_notes)

        return (old_relation, new_relation)

    async def _create_timeline_entry_for_evolution(
        self,
        old_rel: CharacterRelation,
        new_rel: CharacterRelation,
        notes: Optional[str] = None,
    ):
        try:
            from timeline.models import (
                TimelineEntry,
                TimelineEntryCategory,
                TimelineEntryStatus,
            )
            from timeline.schemas import TimelineEntryCreate
            from timeline.service import TimelineService

            source_name = f"角色#{old_rel.source_character_id}"
            target_name = f"角色#{old_rel.target_character_id}"

            description_parts = [
                f"「{source_name}」与「{target_name}」之间的关系从 {old_rel.relationship_type} 演变为 {new_rel.relationship_type}。",
                f"新关系强度：{new_rel.intensity}",
            ]
            if new_rel.description:
                description_parts.append(f"描述：{new_rel.description}")
            if notes:
                description_parts.append(f"演变备注：{notes}")

            entry_data = TimelineEntryCreate(
                category=TimelineEntryCategory.PLOT_NODE,
                title=f"关系演变：{old_rel.relationship_type} → {new_rel.relationship_type}",
                description="\n".join(description_parts),
                detail_json={
                    "source_character_id": old_rel.source_character_id,
                    "target_character_id": old_rel.target_character_id,
                    "old_type": old_rel.relationship_type,
                    "new_type": new_rel.relationship_type,
                    "old_intensity": old_rel.intensity,
                    "new_intensity": new_rel.intensity,
                    "relation_id": new_rel.id,
                    "evolved_from_id": old_rel.id,
                    "evolution_notes": notes,
                },
                target_chapter=new_rel.established_chapter_id,
                importance=4,
                source="ai_generated",
            )
            timeline_svc = TimelineService(self.db, self.novel_id)
            await timeline_svc.add_entry(entry_data)
        except Exception:
            pass

    async def get_relation_history(
        self, char_a_id: int, char_b_id: int
    ) -> List[Dict[str, Any]]:
        """
        获取两个角色之间的完整关系演变历史
        按时间正序排列，形成链条
        """
        result = await self.db.execute(
            select(CharacterRelation).where(
                CharacterRelation.novel_id == self.novel_id,
                or_(
                    and_(
                        CharacterRelation.source_character_id == char_a_id,
                        CharacterRelation.target_character_id == char_b_id,
                    ),
                    and_(
                        CharacterRelation.source_character_id == char_b_id,
                        CharacterRelation.target_character_id == char_a_id,
                    ),
                ),
            ).order_by(CharacterRelation.created_at)
        )
        relations = result.scalars().all()

        # 获取角色名
        chars = await self.db.execute(
            select(Character).where(Character.id.in_([char_a_id, char_b_id]))
        )
        char_map = {c.id: c.name for c in chars.scalars().all()}

        history = []
        for r in relations:
            history.append(
                {
                    "id": r.id,
                    "type": r.relationship_type,
                    "direction": f"{char_map.get(r.source_character_id, '?')} → {char_map.get(r.target_character_id, '?')}",
                    "intensity": r.intensity,
                    "status": r.status,
                    "description": r.description,
                    "chapter_id": r.established_chapter_id,
                    "created_at": r.created_at.isoformat() if r.created_at else None,
                    "is_evolution": r.evolved_from_id is not None,
                }
            )

        return history

    async def migrate_from_json(self) -> Dict[str, Any]:
        """
        从旧的 Character.relationships JSON 字段迁移数据到 CharacterRelation 表
        迁移策略：遍历所有角色，解析 relationships JSON，创建 CharacterRelation 记录
        跳过已存在的关系（避免重复）
        """
        characters = await self.get_all_characters()
        char_map = {c.id: c for c in characters}

        migrated_count = 0
        skipped_count = 0
        errors = []

        for char in characters:
            if not char.relationships or not isinstance(char.relationships, dict):
                continue

            for rel_type, targets in char.relationships.items():
                if isinstance(targets, list):
                    for target_id in targets:
                        try:
                            target_id_int = int(target_id)
                            if target_id_int not in char_map:
                                skipped_count += 1
                                continue

                            # 检查是否已存在
                            existing = await self.db.execute(
                                select(CharacterRelation).where(
                                    CharacterRelation.novel_id == self.novel_id,
                                    CharacterRelation.source_character_id == char.id,
                                    CharacterRelation.target_character_id == target_id_int,
                                    CharacterRelation.relationship_type == rel_type,
                                )
                            )
                            if existing.scalar_one_or_none():
                                skipped_count += 1
                                continue

                            relation = CharacterRelation(
                                novel_id=self.novel_id,
                                source_character_id=char.id,
                                target_character_id=target_id_int,
                                relationship_type=rel_type,
                                intensity=3,
                                status="active",
                                extra_metadata={"migrated_from_json": True},
                            )
                            self.db.add(relation)
                            migrated_count += 1
                        except (ValueError, TypeError) as e:
                            errors.append(f"Char {char.id} rel {rel_type}->{target_id}: {e}")
                            skipped_count += 1

        if migrated_count > 0:
            await self.db.commit()

        return {
            "migrated": migrated_count,
            "skipped": skipped_count,
            "errors": errors[:10],
        }
