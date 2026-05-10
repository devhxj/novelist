"""
子 Agent MCP 工具 — 主 LLM 调度独立 Agent 执行专项任务

每种子 Agent 由 system prompt + 工具白名单定义，通过 run_agent_loop 执行。
防止递归：run_subagent 不在任何子 Agent 的工具列表中。
"""
from __future__ import annotations

import asyncio
import logging
import uuid
from typing import Any, Literal

from pydantic import BaseModel, Field
from sqlalchemy.ext.asyncio import AsyncSession

from core.agent_loop import run_agent_loop, ToolCallResult
from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# System Prompts
# ---------------------------------------------------------------------------

MEMORY_AGENT_PROMPT = """\
你是小说创作的分析师。接受主 Agent 的任务指令，深入探索小说内容，
自由调用工具获取所需信息，最终输出结构化探索报告。

行为准则：
- 根据任务指令制定探索计划，先广搜再深挖
- 每次工具调用后评估信息是否充足，不足则换角度继续
- 报告中每条发现注明来源（章节号、角色名、弧线名等）
- 信息确实不足时诚实说明，不要编造

报告格式：以"# 探索报告"开头，按主题分块。"""

REVIEW_AGENT_PROMPT = """\
你是小说创作的审核编辑。接受主 Agent 的审阅指令，调用工具获取所需信息，
对章节进行全面质量审核，最终输出结构化审核报告。

审核维度：
- 角色一致性：性格、对白、行为是否前后一致
- 情节逻辑：因果关系是否合理，有无时间线冲突
- 伏笔管理：未回收的关键伏笔
- 读者认知：信息揭露节奏是否恰当

报告格式：以"# 审阅报告"开头，包含问题清单、严重程度、改进建议。"""


# ---------------------------------------------------------------------------
# Agent 配置 — (system_prompt, allowed_tools, max_turns)
# ---------------------------------------------------------------------------

AGENT_CONFIG: dict[str, tuple[str, frozenset[str], int]] = {
    "memory": (
        MEMORY_AGENT_PROMPT,
        frozenset({
            "search_story_memory",
            "get_timeline",
            "get_characters",
            "get_chapter_content",
            "get_chapter_list",
            "get_novel_info",
            "get_locations",
            "get_story_arcs",
            "get_reader_perspective",
            "get_story_state",
            "get_creative_profile",
            "run_review",
        }),
        20,
    ),
    "review": (
        REVIEW_AGENT_PROMPT,
        frozenset({
            "search_story_memory",
            "get_timeline",
            "get_characters",
            "get_chapter_content",
            "get_chapter_list",
            "get_novel_info",
            "get_story_arcs",
            "get_reader_perspective",
            "get_creative_profile",
            "run_review",
            "get_locations",
        }),
        30,
    ),
}


# ---------------------------------------------------------------------------
# MCP 工具定义
# ---------------------------------------------------------------------------

class RunSubagentArgs(BaseModel):
    task_type: Literal["memory", "review"] = Field(
        description="子Agent类型：memory(记忆探索，搜索和分析小说信息)、review(章节审阅，全面质量审核)")
    chapter_id: int | None = Field(default=None)
    instruction: str | None = Field(default=None, description="给子Agent的具体任务指令")


class RunSubagentTool(BaseMCPTool):
    """调度子Agent执行专项任务 — 子Agent拥有独立的思考循环和工具调用能力"""

    name = "run_subagent"
    description = (
        "调度子Agent执行专项任务。子Agent会自主调用工具获取信息，"
        "多轮思考后返回结构化报告。任务类型：\n"
        "- memory：记忆探索，搜索和分析小说中的角色、时间线、伏笔、情节等信息\n"
        "- review：章节审阅，全面审核指定章节的角色一致性、情节逻辑、伏笔管理等\n\n"
        "传入明确的 instruction 以获得更好的结果。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    args_schema = RunSubagentArgs

    async def _execute(
        self,
        args: RunSubagentArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        websocket = extra.get("websocket")
        on_message = extra.get("on_message")
        pre_display = extra.get("pre_display")

        cfg = AGENT_CONFIG[args.task_type]
        system_prompt, allowed_tools, max_turns = cfg

        # 构建用户指令
        user_prompt = args.instruction or "请根据上下文执行任务"
        if args.chapter_id:
            user_prompt += f"\n目标章节 ID: {args.chapter_id}"

        # 构建子 Agent 工具列表（从注册表中筛选，不包含 run_subagent）
        from .registry import get_mcp_registry
        registry = get_mcp_registry()
        all_tools = registry.get_openai_functions()
        sub_tools = [t for t in all_tools if t["function"]["name"] in allowed_tools]

        # 初始消息
        messages: list[dict[str, Any]] = [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
        ]

        # 子 Agent 工具回调：白名单门控
        sub_task_id = f"sub_{uuid.uuid4().hex[:12]}"

        # 包装 on_message：每条消息自动打上子 agent 来源标记
        sub_on_message = None
        if on_message:
            async def sub_on_message(msg: dict[str, Any]) -> None:
                msg["source"] = "subagent"
                msg["parent_task_id"] = sub_task_id
                await on_message(msg)
        else:
            sub_on_message = None

        async def sub_handler(
            tool_name: str, tool_id: str, arguments: dict[str, Any]
        ) -> ToolCallResult:
            if tool_name not in allowed_tools:
                return ToolCallResult(
                    success=False, result={},
                    error=f"子 Agent 不允许调用 {tool_name}",
                )
            from core.database import AsyncSessionLocal
            async with AsyncSessionLocal() as sub_db:
                result = await registry.execute(
                    tool_name,
                    db=sub_db,
                    user_id=user_id,
                    novel_id=novel_id,
                    **arguments,
                )
            return ToolCallResult(
                success=result.success,
                result=result.data or {},
                error=result.error,
            )

        # 执行循环
        cancel_event = asyncio.Event()
        loop_result = await run_agent_loop(
            messages=messages,
            tools=sub_tools,
            websocket=websocket,
            task_id=sub_task_id,
            parent_task_id=sub_task_id,
            cancel_event=cancel_event,
            tool_call_handler=sub_handler,
            pre_display_handler=pre_display,
            on_args_stream=None,
            on_message=sub_on_message,
            max_turns=max_turns,
            max_context_tokens=400000,
            read_only_tools=allowed_tools,
        )

        # 子 agent 最终回复也持久化到 session
        if on_message and loop_result.final_text:
            try:
                await on_message({"role": "assistant", "content": loop_result.final_text})
            except Exception:
                logger.warning("on_message failed for sub-agent final_text", exc_info=True)

        return MCPToolResult(
            success=True,
            data={
                "task_type": args.task_type,
                "report": loop_result.final_text,
                "turn_count": loop_result.turn_count,
            },
        )


def register_subagent_tools(registry: MCPToolRegistry) -> None:
    registry.register(RunSubagentTool())
