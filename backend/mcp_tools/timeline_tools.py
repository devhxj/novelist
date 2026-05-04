"""
故事时间线MCP工具集
供AI调用的工具：查询/添加/更新时间线条目
"""
from typing import Any, Dict, List, Optional

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from timeline.models import TimelineEntry
from timeline.schemas import (
    TimelineEntryCreate,
    TimelineEntryUpdate,
)
from mcp_tools.novel_tools import _invalidate_novel_cache
from timeline.service import TimelineService
from core.permissions import verify_novel_ownership


class GetTimelineTool(BaseMCPTool):
    """获取故事时间线（支持两种模式）"""

    name = "get_timeline"
    description = (
        "获取故事时间线。支持两种模式：\n"
        "- context（默认）：智能筛选与当前章节最相关的未完成条目（伏笔、规划、用户指令），适合写作前调用\n"
        "- full：全文查询，支持按分类/状态筛选和分页，适合全面查阅\n"
        "无需传novel_id，系统会注入当前小说ID。\n"
        "\n使用建议：写作前默认调用 context 模式即可，如需查看已完成的历史记录或搜索特定内容再使用 full 模式。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "mode": {
                "type": "string",
                "enum": ["context", "full"],
                "default": "context",
                "description": "查询模式：context（智能精简，写作前调用）/ full（全文查询，全面查阅用）"
            },
            "current_chapter": {
                "type": "integer",
                "description": "当前章节号（context 模式必填，full 模式忽略）"
            },
            "max_entries": {
                "type": "integer",
                "default": 15,
                "description": "最大返回条数 1-50（仅 context 模式生效）"
            },
            "category": {
                "type": "string",
                "enum": ["foreshadowing", "plot_node", "chapter_plan", "user_directive"],
                "description": "按分类筛选（仅 full 模式生效）"
            },
            "status": {
                "type": "string",
                "enum": ["pending", "active", "completed", "resolved", "abandoned", "deferred"],
                "description": "按状态筛选（仅 full 模式生效）"
            },
            "time_horizon": {
                "type": "string",
                "enum": ["next", "near_term", "long_term", "undefined"],
                "description": "按时间范围筛选（仅 full 模式生效）"
            },
            "search": {"type": "string", "description": "搜索关键词，匹配标题和描述（仅 full 模式生效）"},
            "page": {"type": "integer", "default": 1, "description": "页码（仅 full 模式生效）"},
            "page_size": {"type": "integer", "default": 20, "description": "每页数量，最大100（仅 full 模式生效）"},
        },
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        mode: str = "context",
        current_chapter: Optional[int] = None,
        max_entries: int = 15,
        category: Optional[str] = None,
        status: Optional[str] = None,
        time_horizon: Optional[str] = None,
        search: Optional[str] = None,
        page: int = 1,
        page_size: int = 20,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

            service = TimelineService(db, novel_id)

            if mode == "context":
                if current_chapter is None:
                    return MCPToolResult(success=False, error="context 模式需要提供 current_chapter")
                entries, summary_text = await service.get_context_for_generation(current_chapter, max_entries)
                return MCPToolResult(
                    success=True,
                    data={
                        "entries": [_entry_to_dict(e) for e in entries],
                        "total_count": len(entries),
                        "summary_text": summary_text,
                        "current_chapter": current_chapter,
                    },
                    metadata={"tool": self.name, "novel_id": novel_id, "mode": "context"}
                )
            else:
                items, total = await service.get_timeline(
                    page=page, page_size=page_size, category=category,
                    status=status, time_horizon=time_horizon, search=search,
                )
                return MCPToolResult(
                    success=True,
                    data={
                        "items": [_entry_to_dict(e) for e in items],
                        "total": total,
                        "page": page,
                        "page_size": page_size,
                    },
                    metadata={"tool": self.name, "novel_id": novel_id, "mode": "full"}
                )
        except Exception as e:
            return MCPToolResult(success=False, error=f"获取时间线失败: {str(e)}")


class AddTimelineEntryTool(BaseMCPTool):
    """添加时间线条目（单事务批量写入）"""

    name = "add_timeline_entry"
    description = (
        "向故事时间线中添加一条或多条新条目。可以添加伏笔/钩子、章节规划、用户指令等。"
        "无需传novel_id，系统会注入当前小说ID。所有条目会在同一事务内顺序写入，保证一致性。"
        "\n适用场景：章节生成后自动提取伏笔和规划、用户通过对话要求记录某个想法或安排时调用。"
        "\n分类说明："
        "- foreshadowing: 本章埋下的伏笔/钩子（待后续章节回收）"
        "- chapter_plan: 章节安排（下章/近期/远期的写作计划）"
        "- user_directive: 用户主动告知的创作规则或方向性指令"
        "- plot_node: 情节节点（关键事件里程碑）"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "entries": {
                "type": "array",
                "description": "要添加的条目列表（1-6个），系统会顺序执行并一次性提交",
                "items": {
                    "type": "object",
                    "properties": {
                        "category": {
                            "type": "string",
                            "enum": ["foreshadowing", "plot_node", "chapter_plan", "user_directive"],
                            "description": "条目分类（必填）"
                        },
                        "title": {"type": "string", "description": "标题（必填）"},
                        "description": {"type": "string", "description": "详细描述"},
                        "target_chapter": {"type": ["integer", "null"], "description": "目标章节号（可选，不确定时传null）"},
                        "time_horizon": {
                            "type": "string",
                            "enum": ["next", "near_term", "long_term", "undefined"],
                            "description": "时间范围"
                        },
                        "importance": {"type": "integer", "default": 3, "description": "重要程度1-5"},
                        "arc_id": {"type": ["integer", "null"], "description": "所属叙事弧线ID（可选，plot_node类条目可关联到StoryArc）"},
                        "sequence": {"type": "integer", "default": 0, "description": "同章节内排序序号"},
                    },
                    "required": ["category", "title"]
                }
            }
        },
        "required": ["entries"],
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        entries: List[Dict[str, Any]],
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

            if not entries or len(entries) == 0:
                return MCPToolResult(success=False, error="entries不能为空")
            if len(entries) > 6:
                return MCPToolResult(success=False, error="最多支持6个条目")

            service = TimelineService(db, novel_id)

            async def _add_single(op: Dict) -> Dict:
                try:
                    data = TimelineEntryCreate(
                        category=op["category"],
                        title=op["title"],
                        description=op.get("description"),
                        detail_json=op.get("detail_json"),
                        target_chapter=op.get("target_chapter"),
                        time_horizon=op.get("time_horizon"),
                        importance=op.get("importance", 3),
                        source="ai_generated",
                        source_chapter_id=op.get("source_chapter_id"),
                        related_entry_ids=op.get("related_entry_ids"),
                        tags=op.get("tags"),
                        arc_id=op.get("arc_id"),
                        sequence=op.get("sequence", 0),
                    )
                    entry = await service.add_entry(data, auto_commit=False)
                    return {"success": True, "data": _entry_to_dict(entry)}
                except Exception as e:
                    return {"success": False, "error": str(e)}

            results = []
            for op in entries:
                results.append(await _add_single(op))
            success_count = sum(1 for r in results if r.get("success"))
            if success_count > 0:
                await db.commit()
            else:
                await db.rollback()
            await _invalidate_novel_cache(novel_id)
            return MCPToolResult(
                success=success_count > 0,
                data={
                    "total": len(entries),
                    "successful": success_count,
                    "results": results
                },
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            await db.rollback()
            return MCPToolResult(success=False, error=f"添加时间线条目失败: {str(e)}")


class UpdateTimelineEntryTool(BaseMCPTool):
    """更新时间线条目"""

    name = "update_timeline_entry"
    description = (
        "更新已有的时间线条目内容。适用于AI根据新信息修正规划、或应用户要求修改条目时使用。"
        "每次更新会递增版本号并保留原始AI输出（如果之前是AI创建的），方便追踪变更历史。"
        "无需传novel_id，系统会注入当前小说ID。"
        "\n支持状态变更：设置 status=resolved 可标记伏笔回收，status=completed 可标记规划完成，status=abandoned 可废弃条目。"
        "状态变更时可选传 resolved_chapter_id 和 resolution_notes。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "entry_id": {"type": "integer", "description": "条目ID（必填）"},
            "title": {"type": "string", "description": "新的标题"},
            "description": {"type": "string", "description": "新的描述"},
            "detail_json": {"type": "object", "description": "新的结构化详情"},
            "target_chapter": {"type": ["integer", "null"], "description": "新的目标章节号（可选，不确定时传null）"},
            "time_horizon": {
                "type": "string",
                "enum": ["next", "near_term", "long_term", "undefined"],
                "description": "新的时间范围"
            },
            "status": {
                "type": "string",
                "enum": ["pending", "active", "completed", "resolved", "abandoned", "deferred"],
                "description": "新状态。resolved=伏笔回收（仅foreshadowing类），completed=规划完成，abandoned=废弃"
            },
            "importance": {"type": "integer", "description": "新的重要程度(1-5)"},
            "tags": {"type": "array", "items": {"type": "string"}, "description": "新的标签列表"},
            "arc_id": {"type": ["integer", "null"], "description": "所属叙事弧线ID"},
            "sequence": {"type": "integer", "description": "同章节内排序序号"},
            "resolved_chapter_id": {"type": ["integer", "null"], "description": "解决时关联的章节ID（仅状态变更时可选）"},
            "resolution_notes": {"type": "string", "description": "解决说明（仅状态变更为resolved/completed时可选）"},
        },
        "required": ["entry_id"],
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        entry_id: int,
        title: Optional[str] = None,
        description: Optional[str] = None,
        detail_json: Optional[Dict] = None,
        target_chapter: Optional[int] = None,
        time_horizon: Optional[str] = None,
        status: Optional[str] = None,
        importance: Optional[int] = None,
        tags: Optional[List[str]] = None,
        arc_id: Optional[int] = None,
        sequence: Optional[int] = None,
        resolved_chapter_id: Optional[int] = None,
        resolution_notes: Optional[str] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
            
            update_data = {}
            if title is not None:
                update_data["title"] = title
            if description is not None:
                update_data["description"] = description
            if detail_json is not None:
                update_data["detail_json"] = detail_json
            if target_chapter is not None:
                update_data["target_chapter"] = target_chapter
            if time_horizon is not None:
                update_data["time_horizon"] = time_horizon
            if status is not None:
                update_data["status"] = status
            if importance is not None:
                update_data["importance"] = importance
            if tags is not None:
                update_data["tags"] = tags
            if arc_id is not None:
                update_data["arc_id"] = arc_id
            if sequence is not None:
                update_data["sequence"] = sequence
            if resolved_chapter_id is not None:
                update_data["resolved_chapter_id"] = resolved_chapter_id
            if resolution_notes is not None:
                update_data["resolution_notes"] = resolution_notes

            if not update_data:
                return MCPToolResult(success=False, error="没有提供更新字段")

            data = TimelineEntryUpdate(**update_data)
            service = TimelineService(db, novel_id)
            entry = await service.update_entry(entry_id, data, editor="ai")
            if not entry:
                return MCPToolResult(success=False, error=f"条目 {entry_id} 不存在")
            await _invalidate_novel_cache(novel_id)
            return MCPToolResult(
                success=True,
                data=_entry_to_dict(entry),
                metadata={"tool": self.name, "novel_id": novel_id, "version": entry.version}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"更新时间线条目失败: {str(e)}")


def _entry_to_dict(entry: TimelineEntry) -> Dict[str, Any]:
    return {
        "id": entry.id,
        "category": entry.category,
        "status": entry.status,
        "title": entry.title,
        "description": entry.description,
        "detail_json": entry.detail_json,
        "target_chapter": entry.target_chapter,
        "time_horizon": entry.time_horizon,
        "importance": entry.importance,
        "source": entry.source,
        "source_chapter_id": entry.source_chapter_id,
        "arc_id": entry.arc_id,
        "sequence": entry.sequence,
        "version": entry.version,
        "last_editor": entry.last_editor,
        "tags": entry.tags,
        "created_at": entry.created_at.isoformat() if entry.created_at else None,
        "updated_at": entry.updated_at.isoformat() if entry.updated_at else None,
    }



def register_timeline_tools(registry: MCPToolRegistry):
    registry.register(GetTimelineTool())
    registry.register(AddTimelineEntryTool())
    registry.register(UpdateTimelineEntryTool())
