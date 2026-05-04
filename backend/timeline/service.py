"""
故事时间线服务层
统一管理伏笔/情节规划/章节安排/用户指令的CRUD与智能查询
"""
import logging
from enum import Enum
from typing import Dict, Any, List, Optional
from datetime import datetime, timezone

from sqlalchemy import select, func, or_, desc, asc, case
from sqlalchemy.ext.asyncio import AsyncSession

from timeline.models import (
    TimelineEntry,
    TimelineEntryCategory,
    TimelineEntryStatus,
    TimeHorizon,
)
from timeline.schemas import (
    TimelineEntryCreate,
    TimelineEntryUpdate,
    TimelineEntryCategory as SchemaTimelineEntryCategory,
    TimeHorizon as SchemaTimeHorizon,
)

logger = logging.getLogger(__name__)


class TimelineService:
    def __init__(self, db: AsyncSession, novel_id: int):
        self.db = db
        self.novel_id = novel_id

    async def get_timeline(
        self,
        page: int = 1,
        page_size: int = 20,
        category: Optional[str] = None,
        status: Optional[str] = None,
        time_horizon: Optional[str] = None,
        search: Optional[str] = None,
        sort_by: str = "created_at",
        sort_order: str = "desc",
    ) -> tuple[List[TimelineEntry], int]:
        query = select(TimelineEntry).where(TimelineEntry.novel_id == self.novel_id)
        count_query = select(func.count()).select_from(TimelineEntry).where(TimelineEntry.novel_id == self.novel_id)

        if category:
            query = query.where(TimelineEntry.category == category)
            count_query = count_query.where(TimelineEntry.category == category)
        if status:
            query = query.where(TimelineEntry.status == status)
            count_query = count_query.where(TimelineEntry.status == status)
        if time_horizon:
            query = query.where(TimelineEntry.time_horizon == time_horizon)
            count_query = count_query.where(TimelineEntry.time_horizon == time_horizon)
        if search:
            search_pattern = f"%{search}%"
            query = query.where(
                or_(
                    TimelineEntry.title.ilike(search_pattern),
                    TimelineEntry.description.ilike(search_pattern),
                )
            )
            count_query = count_query.where(
                or_(
                    TimelineEntry.title.ilike(search_pattern),
                    TimelineEntry.description.ilike(search_pattern),
                )
            )

        total_result = await self.db.execute(count_query)
        total = total_result.scalar() or 0

        order_col = getattr(TimelineEntry, sort_by, TimelineEntry.created_at)
        if sort_order == "desc":
            query = query.order_by(desc(order_col))
        else:
            query = query.order_by(asc(order_col))

        query = query.offset((page - 1) * page_size).limit(page_size)
        result = await self.db.execute(query)
        items = list(result.scalars().all())

        return items, total

    async def get_entry(self, entry_id: int) -> Optional[TimelineEntry]:
        result = await self.db.execute(
            select(TimelineEntry).where(
                TimelineEntry.id == entry_id,
                TimelineEntry.novel_id == self.novel_id,
            )
        )
        return result.scalar_one_or_none()

    async def add_entry(
        self,
        data: TimelineEntryCreate,
        *,
        auto_commit: bool = True
    ) -> TimelineEntry:
        entry = TimelineEntry(
            novel_id=self.novel_id,
            category=data.category.value,
            title=data.title,
            description=data.description,
            detail_json=data.detail_json,
            target_chapter=data.target_chapter,
            time_horizon=data.time_horizon.value if data.time_horizon else None,
            importance=data.importance,
            source=data.source,
            source_chapter_id=data.source_chapter_id,
            related_entry_ids=data.related_entry_ids,
            tags=data.tags,
            arc_id=data.arc_id,
            sequence=data.sequence,
            version=1,
            last_editor=data.source,
        )
        self.db.add(entry)
        await self.db.flush()
        await self.db.refresh(entry)
        if auto_commit:
            await self.db.commit()
        logger.info(f"Timeline entry created: id={entry.id}, category={entry.category}, title={entry.title}")
        return entry

    async def update_entry(
        self, entry_id: int, data: TimelineEntryUpdate, editor: str = "user"
    ) -> Optional[TimelineEntry]:
        entry = await self.get_entry(entry_id)
        if not entry:
            return None

        current_snapshot = {
            "title": entry.title,
            "description": entry.description,
            "detail_json": entry.detail_json,
            "status": entry.status,
            "target_chapter": entry.target_chapter,
            "time_horizon": entry.time_horizon,
            "importance": entry.importance,
        }

        update_data = data.model_dump(exclude_unset=True)
        # 抽离 resolve 相关字段，单独处理
        resolved_chapter_id = update_data.pop("resolved_chapter_id", None)
        resolution_notes = update_data.pop("resolution_notes", None)

        for field, value in update_data.items():
            if value is not None and hasattr(entry, field):
                if isinstance(value, Enum):
                    setattr(entry, field, value.value)
                else:
                    setattr(entry, field, value)

        # 当状态变更为 resolved/completed 时，自动处理伏笔回收逻辑
        new_status = update_data.get("status")
        if new_status is not None:
            status_val = new_status.value if isinstance(new_status, Enum) else new_status
            if status_val == "resolved" and entry.category != TimelineEntryCategory.FORESHADOWING.value:
                # 非伏笔条目不能标记为 resolved，改用 completed
                entry.status = TimelineEntryStatus.COMPLETED.value
            if status_val in ("resolved", "completed"):
                if resolved_chapter_id is not None:
                    entry.resolved_chapter_id = resolved_chapter_id
                entry.resolved_at = datetime.now(timezone.utc)
                if resolution_notes:
                    if entry.detail_json is None:
                        entry.detail_json = {}
                    entry.detail_json["resolution_notes"] = resolution_notes

        if entry.original_ai_output is None and entry.source == "ai":
            entry.original_ai_output = current_snapshot

        entry.version += 1
        entry.last_editor = editor

        await self.db.commit()
        await self.db.refresh(entry)
        logger.info(f"Timeline entry updated: id={entry_id}, version={entry.version}, editor={editor}")
        return entry

    async def delete_entry(self, entry_id: int) -> bool:
        entry = await self.get_entry(entry_id)
        if not entry:
            return False
        await self.db.delete(entry)
        await self.db.commit()
        logger.info(f"Timeline entry deleted: id={entry_id}")
        return True

    async def get_context_for_generation(
        self, current_chapter: int, max_entries: int = 15
    ) -> tuple[List[TimelineEntry], str]:
        entries = []
        seen_ids = set()

        active_pending = await self.db.execute(
            select(TimelineEntry)
            .where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.status.in_([
                    TimelineEntryStatus.PENDING.value,
                    TimelineEntryStatus.ACTIVE.value,
                ]),
                or_(
                    TimelineEntry.target_chapter.is_(None),
                    TimelineEntry.target_chapter <= current_chapter + 3,
                ),
            )
            .order_by(desc(TimelineEntry.importance), asc(TimelineEntry.target_chapter))
            .limit(max_entries)
        )
        for e in active_pending.scalars().all():
            if e.id not in seen_ids:
                entries.append(e)
                seen_ids.add(e.id)

        unresolved_foreshadowing = await self.db.execute(
            select(TimelineEntry)
            .where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value,
                TimelineEntry.status == TimelineEntryStatus.PENDING.value,
            )
            .order_by(desc(TimelineEntry.importance))
            .limit(8)
        )
        for e in unresolved_foreshadowing.scalars().all():
            if e.id not in seen_ids:
                entries.append(e)
                seen_ids.add(e.id)

        user_directives = await self.db.execute(
            select(TimelineEntry)
            .where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.category == TimelineEntryCategory.USER_DIRECTIVE.value,
            )
            .order_by(desc(TimelineEntry.created_at))
            .limit(3)
        )
        for e in user_directives.scalars().all():
            if e.id not in seen_ids:
                entries.append(e)
                seen_ids.add(e.id)

        next_chapter_plans = await self.db.execute(
            select(TimelineEntry)
            .where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.category == TimelineEntryCategory.CHAPTER_PLAN.value,
                TimelineEntry.time_horizon == TimeHorizon.NEXT.value,
                TimelineEntry.status.in_([
                    TimelineEntryStatus.PENDING.value,
                    TimelineEntryStatus.ACTIVE.value,
                ]),
            )
            .order_by(asc(TimelineEntry.target_chapter))
            .limit(3)
        )
        for e in next_chapter_plans.scalars().all():
            if e.id not in seen_ids:
                entries.append(e)
                seen_ids.add(e.id)

        entries.sort(key=lambda e: (e.target_chapter or 9999, -e.importance))
        entries = entries[:max_entries]

        summary_text = self._build_context_summary(entries, current_chapter)

        summary_text = await self._append_relation_changes(summary_text, current_chapter)

        return entries, summary_text

    def _build_context_summary(self, entries: List[TimelineEntry], current_chapter: int) -> str:
        if not entries:
            return ""

        sections: Dict[str, List[str]] = {}
        for entry in entries:
            cat = entry.category
            if cat not in sections:
                sections[cat] = []
            line = f"- [{entry.title}] (状态:{entry.status}"
            if entry.target_chapter:
                line += f", 目标章节:{entry.target_chapter}"
            line += f", 重要度:{entry.importance})"
            if entry.description:
                line += f" {entry.description[:100]}"
            sections[cat].append(line)

        summary_parts = [f"【故事时间线 - 第{current_chapter}章相关】"]
        category_labels = {
            TimelineEntryCategory.FORESHADOWING.value: "未回收伏笔",
            TimelineEntryCategory.CHAPTER_PLAN.value: "章节规划",
            TimelineEntryCategory.USER_DIRECTIVE.value: "用户指令",
            TimelineEntryCategory.PLOT_NODE.value: "情节节点",
        }
        for cat, lines in sections.items():
            label = category_labels.get(cat, cat)
            summary_parts.append(f"\n【{label}】({len(lines)}条)")
            summary_parts.extend(lines)

        return "\n".join(summary_parts)

    async def _append_relation_changes(
        self, summary_text: str, current_chapter: int
    ) -> str:
        try:
            from characters.models import CharacterRelation, Character
            from characters.schemas import RelationStatus

            threshold = max(1, current_chapter - 3)
            result = await self.db.execute(
                select(CharacterRelation).where(
                    CharacterRelation.novel_id == self.novel_id,
                    CharacterRelation.status == RelationStatus.ACTIVE.value,
                    CharacterRelation.established_chapter_id >= threshold,
                ).order_by(
                    CharacterRelation.established_chapter_id.desc(),
                    CharacterRelation.intensity.desc()
                ).limit(10)
            )
            relations = result.scalars().all()

            if not relations:
                return summary_text

            char_ids = set()
            for r in relations:
                char_ids.add(r.source_character_id)
                char_ids.add(r.target_character_id)

            char_result = await self.db.execute(
                select(Character).where(Character.id.in_(char_ids))
            )
            char_map = {c.id: c.name for c in char_result.scalars().all()}

            relation_lines = []
            for r in relations:
                src_name = char_map.get(r.source_character_id, f"角色#{r.source_character_id}")
                tgt_name = char_map.get(r.target_character_id, f"角色#{r.target_character_id}")
                chapter_info = f"(第{r.established_chapter_id}章)" if r.established_chapter_id else ""
                relation_lines.append(
                    f"- {src_name} → {tgt_name} [{r.relationship_type}] 强度{r.intensity} {chapter_info}"
                )

            relation_section = (
                "\n\n【近期人物关系变化】\n"
                + "\n".join(relation_lines[:10])
            )
            return summary_text + relation_section
        except Exception:
            return summary_text

    async def auto_extract_from_chapter(
        self,
        chapter_content: str,
        chapter_number: int,
        chapter_id: int,
        structured_info: Optional[Dict[str, Any]] = None,
    ) -> List[TimelineEntry]:
        created_entries = []
        if not structured_info:
            return created_entries

        foreshadowing_items = structured_info.get("foreshadowing_items", [])
        for item_text in foreshadowing_items:
            parsed = self._parse_foreshadowing_text(item_text)
            entry = await self._upsert_auto_entry(
                category=TimelineEntryCategory.FORESHADOWING,
                title=parsed.get("title") or item_text[:50],  # type: ignore[assignment]
                description=parsed.get("description", item_text),  # type: ignore[assignment]
                detail_json={
                    "foreshadowing_type": parsed.get("type", "plot"),
                    "hint_text": item_text,
                    "expected_resolution": parsed.get("expected_resolution", ""),
                },
                target_chapter=None,
                time_horizon=TimeHorizon.UNDEFINED,
                importance=3,
                chapter_id=chapter_id,
            )
            created_entries.append(entry)

        next_plan = structured_info.get("next_chapter_plan")
        if next_plan:
            detail = {"plan_type": "next_chapter", "raw_plan": next_plan}
            entry = await self._upsert_auto_entry(
                category=TimelineEntryCategory.CHAPTER_PLAN,
                title=f"第{chapter_number + 1}章安排",
                description=next_plan,
                detail_json=detail,
                target_chapter=chapter_number + 1,
                time_horizon=TimeHorizon.NEXT,
                importance=4,
                chapter_id=chapter_id,
            )
            created_entries.append(entry)

        near_term_plans = structured_info.get("near_term_plans", [])
        for i, plan_text in enumerate(near_term_plans):
            target = chapter_number + 2 + i
            entry = await self._upsert_auto_entry(
                category=TimelineEntryCategory.CHAPTER_PLAN,
                title=f"近期规划-{target}章方向",
                description=plan_text,
                detail_json={"plan_type": "near_term", "raw_plan": plan_text},
                target_chapter=target,
                time_horizon=TimeHorizon.NEAR_TERM,
                importance=3,
                chapter_id=chapter_id,
            )
            created_entries.append(entry)

        long_term_direction = structured_info.get("long_term_direction")
        if long_term_direction:
            entry = await self._upsert_auto_entry(
                category=TimelineEntryCategory.CHAPTER_PLAN,
                title=f"远期方向（源自第{chapter_number}章）",
                description=long_term_direction,
                detail_json={"plan_type": "long_term", "raw_plan": long_term_direction},
                target_chapter=None,
                time_horizon=TimeHorizon.LONG_TERM,
                importance=2,
                chapter_id=chapter_id,
            )
            created_entries.append(entry)

        logger.info(
            f"Auto-extracted {len(created_entries)} timeline entries from chapter {chapter_number}"
        )
        return created_entries

    async def _upsert_auto_entry(
        self,
        *,
        category: TimelineEntryCategory,
        title: str,
        description: str,
        detail_json: Optional[Dict[str, Any]],
        target_chapter: Optional[int],
        time_horizon: TimeHorizon,
        importance: int,
        chapter_id: int,
    ) -> TimelineEntry:
        existing = await self._find_similar_active_entry(
            category=category.value,
            title=title,
            target_chapter=target_chapter
        )
        if existing:
            updated = await self.update_entry(
                existing.id,
                TimelineEntryUpdate(
                    title=title,
                    description=description,
                    detail_json=detail_json,
                    target_chapter=target_chapter,
                    time_horizon=SchemaTimeHorizon(time_horizon.value) if time_horizon else None,
                    importance=max(existing.importance, importance),
                ),
                editor="ai"
            )
            return updated or existing

        return await self.add_entry(TimelineEntryCreate(
            category=SchemaTimelineEntryCategory(category.value),
            title=title,
            description=description,
            detail_json=detail_json,
            target_chapter=target_chapter,
            time_horizon=SchemaTimeHorizon(time_horizon.value) if time_horizon else None,
            importance=importance,
            source="ai_generated",
            source_chapter_id=chapter_id,
        ))

    async def _find_similar_active_entry(
        self,
        *,
        category: str,
        title: str,
        target_chapter: Optional[int]
    ) -> Optional[TimelineEntry]:
        result = await self.db.execute(
            select(TimelineEntry)
            .where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.category == category,
                TimelineEntry.title == title,
                TimelineEntry.target_chapter == target_chapter,
                TimelineEntry.status.in_([
                    TimelineEntryStatus.PENDING.value,
                    TimelineEntryStatus.ACTIVE.value,
                    TimelineEntryStatus.DEFERRED.value,
                ])
            )
            .order_by(case((TimelineEntry.updated_at.is_(None), 0), else_=1), TimelineEntry.updated_at.desc(), TimelineEntry.id.desc())
            .limit(1)
        )
        return result.scalar_one_or_none()

    def _parse_foreshadowing_text(self, text: str) -> Dict[str, str]:
        result = {"title": "", "description": text, "type": "plot", "expected_resolution": ""}
        if "|" in text:
            parts = text.split("|")
            result["title"] = parts[0].strip().lstrip("- ").strip()
            for part in parts[1:]:
                part_lower = part.strip().lower()
                if "类型：" in part or "type:" in part_lower:
                    type_str = part.split("：")[-1].split(":")[-1].strip() if "：" in part else part.split(":")[-1].strip()
                    result["type"] = type_str
                elif "预期回收" in part or "expected" in part_lower:
                    result["expected_resolution"] = part.split("：")[-1].split(":")[-1].strip() if "：" in part else part.split(":")[-1].strip()
        else:
            result["title"] = text.strip().lstrip("- ").strip()[:50]
        return result

    async def get_unresolved_count(self) -> Dict[str, int]:
        counts = {}
        categories = [
            TimelineEntryCategory.FORESHADOWING.value,
            TimelineEntryCategory.CHAPTER_PLAN.value,
            TimelineEntryCategory.USER_DIRECTIVE.value,
        ]
        for cat in categories:
            result = await self.db.execute(
                select(func.count()).select_from(TimelineEntry).where(
                    TimelineEntry.novel_id == self.novel_id,
                    TimelineEntry.category == cat,
                    TimelineEntry.status.in_([
                        TimelineEntryStatus.PENDING.value,
                        TimelineEntryStatus.ACTIVE.value,
                    ]),
                )
            )
            counts[cat] = result.scalar() or 0
        return counts
