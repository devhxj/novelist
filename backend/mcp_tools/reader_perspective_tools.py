"""
读者认知 MCP 工具集
记录读者已知信息、活跃悬念、读者误知，帮助控制信息揭露节奏
"""
from sqlalchemy import select, or_

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from novels.models import ReaderPerspective


class GetReaderPerspectiveTool(BaseMCPTool):
    """获取当前读者认知状态"""

    name = "get_reader_perspective"
    description = (
        "获取当前小说的读者认知状态，包括已知信息、活跃悬念、读者误知。"
        "帮 AI 了解读者视角，控制信息揭露节奏。"
        "无需传novel_id，系统会注入当前小说ID。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {},
    }

    async def _execute(self, db, novel_id: int, user_id: int, **kwargs) -> MCPToolResult:
        # known 类型全部返回；suspense/misconception 只返回未回收的（revealed_chapter IS NULL）
        result = await db.execute(
            select(ReaderPerspective)
            .where(
                ReaderPerspective.novel_id == novel_id,
                or_(
                    ReaderPerspective.type == "known",
                    ReaderPerspective.revealed_chapter.is_(None),
                ),
            )
            .order_by(ReaderPerspective.type, ReaderPerspective.planted_chapter)
        )
        entries = result.scalars().all()

        known = [e for e in entries if e.type == "known"]
        suspenses = [e for e in entries if e.type == "suspense"]
        misconceptions = [e for e in entries if e.type == "misconception"]

        def _format_known():
            if not known:
                return ""
            lines = ["### 已知信息"]
            for e in known:
                ref = f" [第{e.planted_chapter}章起]"
                lines.append(f"- {e.content}{ref}")
            return "\n".join(lines)

        def _format_suspenses():
            if not suspenses:
                return ""
            lines = ["### 活跃悬念"]
            for e in suspenses:
                ref = f"（第{e.planted_chapter}章种下"
                if e.last_mentioned_chapter:
                    ref += f"，最近提及：第{e.last_mentioned_chapter}章"
                ref += "）"
                lines.append(f"- {e.content}{ref}")
            return "\n".join(lines)

        def _format_misconceptions():
            if not misconceptions:
                return ""
            lines = ["### 读者误知"]
            for e in misconceptions:
                truth = f" → 实际：{e.related_truth}" if e.related_truth else ""
                lines.append(f"- {e.content}{truth}")
            return "\n".join(lines)

        sections = [s for s in [_format_known(), _format_suspenses(), _format_misconceptions()] if s]
        formatted = "\n\n".join(sections) if sections else "暂无读者认知数据。"

        return MCPToolResult(
            success=True,
            data={
                "content": formatted,
                "counts": {
                    "known": len(known),
                    "suspense": len(suspenses),
                    "misconception": len(misconceptions),
                },
            },
        )


class AddReaderPerspectiveEntryTool(BaseMCPTool):
    """添加读者认知条目"""

    name = "add_reader_perspective_entry"
    description = (
        "添加一条读者认知条目。三种类型：\n"
        "- known：读者在某章之后知道了什么\n"
        "- suspense：读者当前在等待解答的悬念\n"
        "- misconception：读者以为的情况（用于未来反转）\n"
        "每章写完后如有新揭露的信息或新种下的悬念，应主动添加。"
        "无需传novel_id，系统会注入当前小说ID。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "type": {
                "type": "string",
                "enum": ["known", "suspense", "misconception"],
                "description": "条目类型"
            },
            "content": {
                "type": "string",
                "description": "内容描述"
            },
            "planted_chapter": {
                "type": "integer",
                "description": "种下的章节号"
            },
            "related_truth": {
                "type": "string",
                "description": "仅 misconception 类型：真实情况是什么"
            },
            "planned_reveal_chapter": {
                "type": "integer",
                "description": "仅 suspense/misconception：计划在哪章揭露/回收"
            },
        },
        "required": ["type", "content", "planted_chapter"],
    }

    async def _execute(
        self, db, novel_id: int, user_id: int,
        type: str = "", content: str = "", planted_chapter: int = 0,
        related_truth: str | None = None, planned_reveal_chapter: int | None = None,
        **kwargs
    ) -> MCPToolResult:
        entry = ReaderPerspective(
            novel_id=novel_id,
            type=type,
            content=content,
            planted_chapter=planted_chapter,
            related_truth=related_truth if type == "misconception" else None,
            revealed_chapter=planned_reveal_chapter if type in ("suspense", "misconception") else None,
        )
        db.add(entry)
        await db.commit()
        return MCPToolResult(success=True, data={"id": entry.id, "type": type})


class UpdateReaderPerspectiveEntryTool(BaseMCPTool):
    """更新读者认知条目"""

    name = "update_reader_perspective_entry"
    description = (
        "更新一条读者认知条目。常见用途：\n"
        "- 回收悬念：设置 revealed_chapter\n"
        "- 更新提及频率：设置 last_mentioned_chapter\n"
        "- 揭露误知：设置 revealed_chapter\n"
        "无需传novel_id，系统会注入当前小说ID。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "entry_id": {
                "type": "integer",
                "description": "要更新的条目 ID"
            },
            "last_mentioned_chapter": {
                "type": "integer",
                "description": "最近提及的章节号"
            },
            "revealed_chapter": {
                "type": "integer",
                "description": "实际揭露/回收的章节号（设置后该条目不再出现在活跃列表中）"
            },
        },
        "required": ["entry_id"],
    }

    async def _execute(
        self, db, novel_id: int, user_id: int,
        entry_id: int = 0,
        last_mentioned_chapter: int | None = None,
        revealed_chapter: int | None = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(
            select(ReaderPerspective).where(
                ReaderPerspective.id == entry_id,
                ReaderPerspective.novel_id == novel_id,
            )
        )
        entry = result.scalar_one_or_none()
        if not entry:
            return MCPToolResult(success=False, error=f"条目 {entry_id} 不存在")

        if last_mentioned_chapter is not None:
            entry.last_mentioned_chapter = last_mentioned_chapter
        if revealed_chapter is not None:
            entry.revealed_chapter = revealed_chapter
        await db.commit()
        return MCPToolResult(success=True, data={"id": entry.id, "revealed_chapter": entry.revealed_chapter})


def register_reader_perspective_tools(registry: MCPToolRegistry):
    registry.register(GetReaderPerspectiveTool())
    registry.register(AddReaderPerspectiveEntryTool())
    registry.register(UpdateReaderPerspectiveEntryTool())
