"""
章节创作 MCP 工具

LLM 搜集上下文 + 生成大纲后调用本工具。
工具负责：大纲审批，通过后构建 Layer3 精准上下文，注入创作指令到 session。
"""
from __future__ import annotations

import asyncio
import logging

from pydantic import BaseModel, Field
from sqlalchemy.ext.asyncio import AsyncSession

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry

logger = logging.getLogger(__name__)

# 审批机制：ws_chat 主循环收到审批消息后，通过 event 通知工具
# key 为 session_id，多用户隔离
_approval_events: dict[str, asyncio.Event] = {}
_approval_results: dict[str, dict] = {}


def _get_approval(session_id: str) -> tuple[asyncio.Event, dict]:
    """获取或创建审批等待对象"""
    if session_id not in _approval_events:
        _approval_events[session_id] = asyncio.Event()
        _approval_results[session_id] = {}
    return _approval_events[session_id], _approval_results[session_id]


def signal_approval(session_id: str, approved: bool, feedback: str = ""):
    """外部通知审批结果"""
    if session_id in _approval_events:
        event = _approval_events[session_id]
        _approval_results[session_id].update({"approved": approved, "feedback": feedback})
        event.set()


def abort_approval(session_id: str):
    """终止审批（断连等场景）"""
    signal_approval(session_id, False, "会话已断开")


def cleanup_approval(session_id: str):
    """清理审批等待对象"""
    _approval_events.pop(session_id, None)
    _approval_results.pop(session_id, None)


class CreateOutlineArgs(BaseModel):
    chapter_numbers: list[int] = Field(description="章节号列表，单章如[15]，多章如[15,16,17]")
    outline: dict = Field(description="结构化大纲 JSON，统一数组格式：{'chapters': [{...}, {...}]}")
    model: str | None = Field(default=None)


class CreateOutlineTool(BaseMCPTool):
    """大纲审批工具 — 提交大纲供用户审核，通过后注入创作指令"""

    name = "create_outline"
    description = (
        "提交章节大纲进行审批。审批通过后系统会自动构建精准上下文并注入创作指令到对话中。"
        "审批未通过时请根据用户反馈修改大纲后重新调用。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    args_schema = CreateOutlineArgs

    async def _execute(
        self,
        args: CreateOutlineArgs,
        *,
        db: AsyncSession | None = None,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        websocket = extra.get("websocket")
        chat_session = extra.get("chat_session")

        if not websocket or not chat_session:
            return MCPToolResult(success=False, error="缺少 ws 或 session")

        try:
            from chapters.utils import _format_outline

            # === 解析大纲 ===
            if "chapters" in args.outline and isinstance(args.outline["chapters"], list):
                outlines: list[dict] = args.outline["chapters"]
            else:
                outlines = [args.outline]

            if not outlines:
                return MCPToolResult(success=False, error="大纲为空")

            # 补 chapter_number
            for i, ol in enumerate(outlines):
                if not ol.get("chapter_number") and i < len(args.chapter_numbers):
                    ol["chapter_number"] = args.chapter_numbers[i]

            outline_texts: list[str] = [_format_outline(ol) for ol in outlines]
            combined_text = "\n\n---\n\n".join(outline_texts)

            # === 发送大纲给前端审批 ===
            await websocket.send_json({
                "type": "outline_generated",
                "novel_id": novel_id,
                "chapter_numbers": args.chapter_numbers,
                "content": combined_text,
                "outlines": outlines,
            })

            # === 等待用户审批 ===
            session_id = chat_session.session_id
            event, result = _get_approval(session_id)
            result.clear()
            await event.wait()
            approval_raw = dict(result)
            cleanup_approval(session_id)
            approved = approval_raw.get("approved", False)

            if not approved:
                feedback = approval_raw.get("feedback", "请重新生成")
                return MCPToolResult(
                    success=True,
                    data={
                        "approved": False,
                        "feedback": feedback,
                        "message": f"大纲审批未通过：{feedback}",
                    },
                )

            # === 审批通过，构建 Layer3 ===
            from core.database import AsyncSessionLocal
            from context.context_builder import build_layer3_context

            inject_msgs: list[dict] = []
            for idx, ol in enumerate(outlines):
                chapter_number = ol.get("chapter_number", args.chapter_numbers[idx])
                async with AsyncSessionLocal() as db:
                    layer3 = (await build_layer3_context(db, novel_id, ol)) or ""

                estimated_words = ol.get("estimated_words", 3000)
                inject_msgs.append({
                    "role": "user",
                    "content": (
                        f"大纲：\n{outline_texts[idx]}\n\n"
                        f"内容：\n{layer3}\n\n"
                        f"请根据以上大纲和内容创作第{chapter_number}章正文。"
                        f"字数要求：约{estimated_words}字。"
                    ),
                    "workflow_event": "write_instruction",
                })

            return MCPToolResult(
                success=True,
                data={"approved": True, "chapter_numbers": args.chapter_numbers},
                inject=inject_msgs,
            )

        except Exception as e:
            logger.error(f"Create outline failed: {e}", exc_info=True)
            return MCPToolResult(success=False, error=str(e))

    @classmethod
    def register_all(cls, registry: MCPToolRegistry):
        registry.register(cls())
