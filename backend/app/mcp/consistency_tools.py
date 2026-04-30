"""
审查类MCP工具（整合版）

架构说明（重要）：
本系统有4层故事数据架构，工具按层归属：

  Layer 1: PlotOutline     → 整体大纲（premise/三幕结构）
  Layer 2: PlotLine+Node   → 情节线规划（main/sub/character/background线+节点）
  Layer 3: PlotEvent       → 已发生的历史事件（通过 get_timeline 查询）
  Layer 4: TimelineEntry   → 规划追踪（替代旧的独立 Foreshadowing 模块）
           └─ category=foreshadowing: 伏笔/钩子（待回收）
           └─ category=plot_node:      情节里程碑
           └─ category=chapter_plan:  章节写作计划
           └─ category=user_directive: 用户指令

关键区分：
- "plots" 指 Layer 2 的 PlotLine/PlotNode 系统（独立情节规划结构）
- "foreshadowing" 指 Layer 4 TimelineEntry 的 foreshadowing 分类（伏笔管理）
- 两者是完全独立的系统，不要混淆！

工具映射：
- check_character_consistency → run_review(scope='character')
- check_plot_consistency      → run_review(scope='plot')
- list_unresolved_plots        → run_review(scope='foreshadowing') [查询TimelineEntry的foreshadowing分类]
- get_foreshadowing_status    → run_review(scope='foreshadowing_status') [统计信息]
- run_full_consistency_check   → run_review(scope='full')
"""
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select, func, case
from datetime import datetime

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.novels.models import Novel
from app.chapters.models import Chapter
from app.characters.models import Character
from app.plot_events.models import PlotEvent
from app.timeline.models import TimelineEntry, TimelineEntryCategory, TimelineEntryStatus
from app.consistency.service import ConsistencyChecker
from app.core.permissions import verify_novel_ownership


class RunReviewTool(BaseMCPTool):
    """统一审查工具"""

    name = "run_review"
    description = (
        "执行小说审查，支持多种检查类型。无需传novel_id，系统会注入当前小说ID。"
        "\n适用场景："
        "\n- 写完几章后检查角色/情节是否前后一致 → scope='character' 或 'plot'"
        "\n- 查看还有哪些伏笔(foreshadowing)没有回收 → scope='foreshadowing'"
        "\n- 查看伏笔的整体统计数据（解决率/各状态分布/优先级分布）→ scope='foreshadowing_status'"
        "\n- 全面体检发现潜在问题 → scope='full'"
        "\n注意：'伏笔'(foreshadowing)属于时间线追踪系统(Layer4 TimelineEntry)，"
        "与'情节规划'(Layer2 PlotLine/PlotNode)是不同的系统。"
    )
    category = MCPToolCategory.CONSISTENCY_CHECK
    parameters_schema = {
        "type": "object",
        "properties": {
            "scope": {
                "type": "string",
                "enum": ["character", "plot", "foreshadowing", "foreshadowing_status", "full"],
                "description": (
                    "检查范围："
                    "\ncharacter=角色一致性检查;"
                    "\nplot=情节逻辑检查;"
                    "\nforeshadowing=未回收伏笔列表(查的是TimelineEntry中category=foreshadowing的条目);"
                    "\nforeshadowing_status=伏笔统计(总数/解决率/状态分布/优先级分布);"
                    "\nfull=全面检查(character+plot+timeline+foreshadowing)"
                )
            },
            "chapter_ids": {
                "type": "array",
                "items": {"type": "integer"},
                "description": "只检查指定章节（可选，不传则检查全部已完成章节）"
            },
            "min_importance": {
                "type": "integer",
                "description": "仅scope=foreshadowing时有效，筛选最低重要程度（1-5），默认不筛选"
            },
        },
        "required": ["scope"]
    }

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        scope: str,
        chapter_ids: Optional[List[int]] = None,
        min_importance: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

        try:
            if scope == "foreshadowing":
                return await self._query_foreshadowing_list(db, novel_id, min_importance)

            if scope == "foreshadowing_status":
                return await self._query_foreshadowing_stats(db, novel_id)

            checker = ConsistencyChecker(db, novel_id)

            query = select(Chapter).where(Chapter.novel_id == novel_id, Chapter.status == "completed")
            if chapter_ids:
                query = query.where(Chapter.id.in_(chapter_ids))
            query = query.order_by(Chapter.chapter_number)
            result = await db.execute(query)
            chapters = result.scalars().all()

            if scope == "character":
                issues = await checker.check_character_consistency(chapters)
                return MCPToolResult(
                    success=True,
                    data={
                        "review_type": "character",
                        "issues": [issue.model_dump() for issue in issues],
                        "total_issues": len(issues),
                        "checked_chapters": len(chapters),
                        "summary": f"角色一致性检查完成，共{len(issues)}个问题" if issues else "角色一致性检查通过，未发现问题",
                    },
                    metadata={"tool": self.name, "novel_id": novel_id}
                )

            elif scope == "plot":
                issues = await checker.check_plot_consistency(chapters)
                return MCPToolResult(
                    success=True,
                    data={
                        "review_type": "plot",
                        "issues": [issue.model_dump() for issue in issues],
                        "total_issues": len(issues),
                        "checked_chapters": len(chapters),
                        "summary": f"情节逻辑检查完成，共{len(issues)}个问题" if issues else "情节逻辑检查通过，未发现问题",
                    },
                    metadata={"tool": self.name, "novel_id": novel_id}
                )

            elif scope == "full":
                check_result = await checker.check_all(
                    chapter_ids=chapter_ids,
                    check_types=["character", "plot", "timeline", "foreshadowing"]
                )
                fs_data = await self._query_foreshadowing_list(db, novel_id, min_importance, return_raw=True)
                check_result["unresolved_plots"] = fs_data.get("unresolved_plots", [])
                stats_data = await self._query_foreshadowing_stats(db, novel_id, return_raw=True)
                check_result["foreshadowing_statistics"] = stats_data
                check_result["review_type"] = "full"
                return MCPToolResult(success=True, data=check_result, metadata={"tool": self.name, "novel_id": novel_id})

            else:
                return MCPToolResult(success=False, error=f"不支持的scope: {scope}")

        except Exception as e:
            return MCPToolResult(success=False, error=f"审查失败: {str(e)}")

    async def _query_foreshadowing_list(self, db, novel_id, min_importance=None, return_raw=False):
        query = select(TimelineEntry).where(
            TimelineEntry.novel_id == novel_id,
            TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value,
            TimelineEntry.status.in_([TimelineEntryStatus.PENDING.value, TimelineEntryStatus.ACTIVE.value])
        )
        if min_importance:
            query = query.where(TimelineEntry.importance >= min_importance)
        query = query.order_by(TimelineEntry.importance.desc(), TimelineEntry.created_at.desc())
        result = await db.execute(query)
        entries = result.scalars().all()

        result_list = []
        for entry in entries:
            pending_days = (datetime.now() - entry.created_at).days if entry.created_at else 0
            source_chapter = None
            if entry.source_chapter_id:
                ch_result = await db.execute(select(Chapter).where(Chapter.id == entry.source_chapter_id))
                chapter = ch_result.scalar_one_or_none()
                if chapter:
                    source_chapter = {"id": chapter.id, "chapter_number": chapter.chapter_number, "title": chapter.title}
            result_list.append({
                "id": entry.id, "title": entry.title, "description": entry.description,
                "category": entry.category, "importance": entry.importance, "status": entry.status,
                "source_chapter": source_chapter, "created_at": entry.created_at.isoformat() if entry.created_at else None,
                "days_pending": pending_days, "detail_json": entry.detail_json
            })

        data = {"review_type": "foreshadowing", "unresolved_plots": result_list, "total": len(result_list), "filters": {"min_importance": min_importance}}
        if return_raw:
            return data
        return MCPToolResult(success=True, data=data, metadata={"tool": self.name, "novel_id": novel_id})

    async def _query_foreshadowing_stats(self, db, novel_id, return_raw=False):
        total_result = await db.execute(
            select(func.count()).select_from(TimelineEntry).where(
                TimelineEntry.novel_id == novel_id,
                TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value
            )
        )
        total = total_result.scalar() or 0

        status_counts = {}
        for status_val in [s.value for s in TimelineEntryStatus]:
            cnt_result = await db.execute(
                select(func.count()).select_from(TimelineEntry).where(
                    TimelineEntry.novel_id == novel_id,
                    TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value,
                    TimelineEntry.status == status_val
                )
            )
            status_counts[status_val] = cnt_result.scalar() or 0

        unresolved = status_counts.get(TimelineEntryStatus.PENDING.value, 0) + status_counts.get(TimelineEntryStatus.ACTIVE.value, 0)
        resolved = status_counts.get(TimelineEntryStatus.RESOLVED.value, 0) + status_counts.get(TimelineEntryStatus.COMPLETED.value, 0)
        abandoned = status_counts.get(TimelineEntryStatus.ABANDONED.value, 0)

        importance_dist = {}
        for i in range(1, 6):
            imp_result = await db.execute(
                select(func.count()).select_from(TimelineEntry).where(
                    TimelineEntry.novel_id == novel_id,
                    TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value,
                    TimelineEntry.importance == i
                )
            )
            importance_dist[str(i)] = imp_result.scalar() or 0

        high_priority_query = select(TimelineEntry).where(
            TimelineEntry.novel_id == novel_id,
            TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value,
            TimelineEntry.status.in_([TimelineEntryStatus.PENDING.value, TimelineEntryStatus.ACTIVE.value]),
            TimelineEntry.importance >= 4
        ).order_by(TimelineEntry.importance.desc(), TimelineEntry.created_at.asc()).limit(5)
        hp_result = await db.execute(high_priority_query)
        high_priority_items = [
            {"id": e.id, "title": e.title, "importance": e.importance,
             "days_pending": (datetime.now() - e.created_at).days if e.created_at else 0}
            for e in hp_result.scalars().all()
        ]

        stats = {
            "review_type": "foreshadowing_status",
            "statistics": {
                "total": total,
                "unresolved": unresolved,
                "resolved": resolved,
                "abandoned": abandoned,
                "resolution_rate": round(resolved / total * 100, 1) if total > 0 else 0,
                "by_status": status_counts,
                "by_importance": importance_dist,
            },
            "high_priority_unresolved": high_priority_items,
        }
        if return_raw:
            return stats
        return MCPToolResult(success=True, data=stats, metadata={"tool": self.name, "novel_id": novel_id})


class ConsistencyCheckTools:
    """审查工具集合 — 只注册 RunReviewTool"""

    @staticmethod
    def register_all(registry: MCPToolRegistry) -> None:
        registry.register(RunReviewTool())
