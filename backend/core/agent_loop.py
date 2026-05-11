"""
通用 Agent 循环 — 提取自 ws_chat.py 的 _run_chat_with_tools

主 chat 和子 agent 共用：接收消息列表、LLM 流式调用、工具执行、WS 推送、消息拼接、护栏检查。
差异化逻辑（权限校验、参数清洗、缓存、失败计数等）通过回调注入。
"""
from __future__ import annotations

import asyncio
import json
import logging
from dataclasses import dataclass
from datetime import datetime, timezone
from collections.abc import Callable, Awaitable
from typing import Any, TYPE_CHECKING

from fastapi import WebSocket

from core.websocket import ws_manager
from core.llm_service import llm_service

if TYPE_CHECKING:
    from mcp_tools.base import MCPToolResult

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# 数据类型
# ---------------------------------------------------------------------------

@dataclass
class AgentLoopResult:
    """Agent 循环结束后的返回"""
    final_text: str
    turn_count: int


# 回调类型
type ToolCallHandler = Callable[[str, str, dict[str, Any]], Awaitable[MCPToolResult]]
"""工具执行回调：async (tool_name, tool_id, arguments) -> MCPToolResult"""

type DisplayHandler = Callable[[str, dict[str, Any], str], Awaitable[tuple[str | None, str | None]]]
"""展示文本回调：async (tool_name, arguments, status) -> (display_text, activity_kind)
循环在 selected / executing / completed 三个阶段统一调用，status 为 "executing" / "completed" / "failed" """

type OnArgsStreamHandler = Callable[[str, str, str], Awaitable[None]]
"""参数流式回调：async (tool_name, tool_id, arguments_text) -> None
用于 edit_chapter 的 new_content 实时预览等场景"""

type OnMessageHandler = Callable[[dict[str, Any]], Awaitable[None]]
"""消息持久化回调：async (message) -> None
循环每追加一条 assistant/tool/system 消息时调用，实现任意状态可恢复"""


# ---------------------------------------------------------------------------
# 只读工具集合 — 用于死循环检测
# ---------------------------------------------------------------------------
READ_ONLY_TOOLS: frozenset[str] = frozenset({
    "search_story_memory", "get_timeline", "get_chapter_content",
    "get_chapter_list", "get_chapter_detail", "get_characters", "get_character_memory",
    "get_locations", "get_novel_info", "get_creative_profile",
    "get_story_arcs", "get_story_state", "get_reader_perspective",
    "check_consistency",
})


# ---------------------------------------------------------------------------
# 通用 Agent 循环
# ---------------------------------------------------------------------------
async def run_agent_loop(
    *,
    messages: list[dict[str, Any]],
    tools: list[dict[str, Any]],
    websocket: WebSocket,
    task_id: str,
    parent_task_id: str | None,
    cancel_event: asyncio.Event,
    tool_call_handler: ToolCallHandler,
    display_handler: DisplayHandler | None = None,
    on_args_stream: OnArgsStreamHandler | None = None,
    on_message: OnMessageHandler | None = None,
    model: str | None = None,
    reasoning_effort: str | None = None,
    max_turns: int = 50,
    max_context_tokens: int = 800000,
    read_only_tools: frozenset[str] = READ_ONLY_TOOLS,
) -> AgentLoopResult:
    """
    通用 Agent 循环。

    Parameters
    ----------
    messages : 初始消息列表（LLM API 格式）。循环会在每次工具调用后原地追加
              assistant + tool 消息。调用方负责初始构建和最终持久化。
    cancel_event : 初始为 clear（运行中），调用方 set() 后循环在下一检查点退出。
    display_handler : 展示文本回调，selected / executing / completed 三个阶段统一调用。
                     传入则所有 tool_call 事件自动携带 display_text / activity_kind。
    on_args_stream : LLM 流式传输工具参数时的回调，用于 edit_chapter 实时预览等。
    """
    loop_count = 0
    full_response = ""        # 工具调用轮次间累积（每轮 reset）
    response_buffer = ""      # 当前轮次的文本累积
    thinking_buffer = ""      # DeepSeek reasoning_content
    is_thinking = False
    recent_tool_patterns: list[str] = []
    tool_failed_cnt: dict[str, int] = {}

    async def _append_msg(msg: dict[str, Any]) -> None:
        """追加消息到列表，同时回调持久化"""
        messages.append(msg)
        if on_message:
            try:
                await on_message(msg)
            except Exception:
                logger.warning("on_message callback failed", exc_info=True)

    while loop_count < max_turns:
        tool_outputs: list[dict[str, Any]] = []
        pending_injects: dict[str, list[dict]] = {}

        # ---- LLM 流式调用 ----
        try:
            async for event in llm_service.chat_stream_with_tools(
                messages=messages,
                model=model,
                tools=tools,
                reasoning_effort=reasoning_effort,
            ):
                # 取消检查：cancel_event 初始为 clear（运行中），set() 后为 cancelled
                if cancel_event.is_set():
                    logger.info(f"Agent loop {task_id} cancelled")
                    partial = response_buffer.strip() or full_response.strip()
                    return AgentLoopResult(
                        final_text=partial,
                        turn_count=loop_count,
                    )

                event_type: str = event.get("type", "")

                # ======== thinking ========
                if event_type == "thinking":
                    thinking_content: str = event.get("content", "")
                    if not is_thinking and thinking_content:
                        is_thinking = True
                    thinking_buffer += thinking_content
                    await ws_manager.send_personal_message({
                        "type": "thinking_chunk",
                        "task_id": task_id,
                        "parent_task_id": parent_task_id,
                        "chunk": thinking_content,
                        "timestamp": datetime.now(timezone.utc).isoformat()
                    }, websocket)

                # ======== content ========
                elif event_type == "content":
                    if is_thinking:
                        is_thinking = False
                        await ws_manager.send_personal_message({
                            "type": "thinking_done",
                            "task_id": task_id,
                            "parent_task_id": parent_task_id,
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)
                    chunk: str = event["content"]
                    full_response += chunk
                    response_buffer += chunk
                    await ws_manager.send_personal_message({
                        "type": "content_chunk",
                        "task_id": task_id,
                        "parent_task_id": parent_task_id,
                        "chunk": chunk,
                        "accumulated_length": len(full_response),
                        "timestamp": datetime.now(timezone.utc).isoformat()
                    }, websocket)

                # ======== tool_call_start ========
                elif event_type == "tool_call_start":
                    if is_thinking:
                        is_thinking = False
                        await ws_manager.send_personal_message({
                            "type": "thinking_done",
                            "task_id": task_id,
                            "parent_task_id": parent_task_id,
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)
                    tool_name_start = event.get("tool_name", "")
                    tool_id_selected = event.get("tool_id") or event.get("id") or ""
                    logger.info(f"Agent loop {task_id}: 工具调用请求开始: {tool_name_start}")
                    # 提前获取泛用展示文本，发出 selected 阶段事件，前端即时反馈
                    display_text_selected: str | None = None
                    activity_kind_selected: str | None = None
                    if display_handler and tool_name_start:
                        try:
                            display_text_selected, activity_kind_selected = await display_handler(tool_name_start, {}, "executing")
                        except Exception:
                            logger.warning("display_handler failed at tool_call_start", exc_info=True)
                    if tool_name_start:
                        await ws_manager.send_personal_message({
                            "type": "tool_call",
                            "task_id": task_id,
                            "parent_task_id": parent_task_id,
                            "tool_name": tool_name_start,
                            "tool_id": tool_id_selected or None,
                            "status": "executing",
                            "phase": "selected",
                            "display_text": display_text_selected,
                            "activity_kind": activity_kind_selected,
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)

                # ======== tool_call_arguments ========
                elif event_type == "tool_call_arguments":
                    if on_args_stream:
                        try:
                            await on_args_stream(
                                event.get("tool_name", ""),
                                event.get("tool_id", ""),
                                event.get("arguments_text", ""),
                            )
                        except Exception:
                            logger.warning("on_args_stream failed", exc_info=True)

                # ======== tool_call_end ========
                elif event_type == "tool_call_end":
                    tool_name: str = event.get("tool_name", "")
                    tool_id: str = event.get("tool_id") or event.get("id") or ""
                    arguments: dict[str, Any] = event.get("arguments", {})

                    if not tool_name:
                        continue

                    # -- pre-display（"executing" 展示文本）--
                    display_text: str | None = None
                    activity_kind: str | None = None
                    if display_handler:
                        try:
                            display_text, activity_kind = await display_handler(tool_name, arguments, "executing")
                        except Exception:
                            logger.warning("display_handler failed", exc_info=True)

                    await ws_manager.send_personal_message({
                        "type": "tool_call",
                        "task_id": task_id,
                        "parent_task_id": parent_task_id,
                        "tool_name": tool_name,
                        "tool_id": tool_id,
                        "status": "executing",
                        "phase": "executing",
                        "arguments": arguments,
                        "display_text": display_text,
                        "activity_kind": activity_kind,
                        "timestamp": datetime.now(timezone.utc).isoformat()
                    }, websocket)

                    # -- 执行工具（回调）--
                    logger.info(f"收到完整toolcall 开始执行: {tool_name}, 参数: {arguments}")
                    tool_result = await tool_call_handler(tool_name, tool_id, arguments)

                    # 失败计数：连续失败达阈值时注入 system 提示，不修改工具列表
                    if not tool_result.success:
                        tool_failed_cnt[tool_name] = tool_failed_cnt.get(tool_name, 0) + 1
                    else:
                        tool_failed_cnt[tool_name] = 0
                    if tool_failed_cnt.get(tool_name, 0) >= 3:
                        await _append_msg({
                            "role": "system",
                            "content": f"工具 {tool_name} 已多次失败并被禁用，请不要再调用此工具。如果已有信息足够，请直接回应用户。"
                        })
                        await ws_manager.send_personal_message({
                            "type": "system_warning",
                            "task_id": task_id,
                            "parent_task_id": parent_task_id,
                            "message": f"工具 {tool_name} 已连续失败 3 次，已暂时禁用。",
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        }, websocket)

                    if tool_result.inject:
                        pending_injects[tool_id] = tool_result.inject

                    tool_result_payload = tool_result.model_dump(exclude={"inject"})
                    data_payload = (tool_result.data or {}) if isinstance(tool_result.data, dict) else {}
                    logger.info(f"Tool result payload: {tool_result_payload}")

                    # -- completed 展示文本（回调统一生成）--
                    status_result = "completed" if tool_result.success else "failed"
                    display_text_result = None
                    activity_kind_result = None
                    if display_handler:
                        try:
                            display_text_result, activity_kind_result = await display_handler(
                                tool_name, arguments, status_result
                            )
                        except Exception:
                            logger.warning("display_handler failed at completed", exc_info=True)

                    await ws_manager.send_personal_message({
                        "type": "tool_call",
                        "task_id": task_id,
                        "parent_task_id": parent_task_id,
                        "tool_name": tool_name,
                        "status": status_result,
                        "tool_id": tool_id,
                        "phase": status_result,
                        "arguments": arguments,
                        "result_summary": {
                            "success": tool_result.success,
                            "error": tool_result.error,
                            "metadata": tool_result.metadata or {},
                            "data_keys": list(data_payload.keys()),
                        },
                        "display_text": display_text_result,
                        "activity_kind": activity_kind_result,
                        "error": tool_result.error,
                        "timestamp": datetime.now(timezone.utc).isoformat()
                    }, websocket)

                    tool_outputs.append({
                        "tool": tool_name,
                        "tool_id": tool_id,
                        "arguments": arguments,
                        "result": tool_result_payload,
                        "display_text": display_text_result,
                        "activity_kind": activity_kind_result,
                    })
        except (asyncio.CancelledError, Exception):
            partial = response_buffer.strip() or full_response.strip()
            if partial or thinking_buffer:
                msg: dict[str, Any] = {"role": "assistant", "content": partial}
                if thinking_buffer:
                    msg["reasoning_content"] = thinking_buffer
                await _append_msg(msg)
            raise

        # ======== 流结束，判断是否有工具调用 ========
        if tool_outputs:
            # 合并本轮所有工具调用为一条 assistant 消息（DeepSeek 要求）
            # content 为模型在工具调用前输出的文本，reasoning_content 为思考内容
            combined_tool_calls: list[dict[str, Any]] = []
            for item in tool_outputs:
                combined_tool_calls.append({
                    "id": item["tool_id"] or f"call_{item['tool']}",
                    "type": "function",
                    "function": {
                        "name": item["tool"],
                        "arguments": json.dumps(item.get("arguments", {}), ensure_ascii=False)
                    },
                    "display_text": item.get("display_text"),
                    "activity_kind": item.get("activity_kind"),
                })

            tool_call_assistant: dict[str, Any] = {
                "role": "assistant",
                "content": response_buffer.strip(),
                "tool_calls": combined_tool_calls,
            }
            if thinking_buffer:
                tool_call_assistant["reasoning_content"] = thinking_buffer
            await _append_msg(tool_call_assistant)

            thinking_buffer = ""
            response_buffer = ""
            full_response = ""

            # -- 构建 TOOL 消息（全部先行） --
            for item in tool_outputs:
                tid = item["tool_id"] or f"call_{item['tool']}"
                tool_msg: dict[str, Any] = {
                    "role": "tool",
                    "tool_call_id": tid,
                    "content": json.dumps(item["result"], ensure_ascii=False),
                }
                await _append_msg(tool_msg)

            # -- inject 消息紧随全部 TOOL 之后（LLM 下轮可见） --
            for item in tool_outputs:
                inject_msgs = pending_injects.pop(item["tool_id"], None)
                if inject_msgs and isinstance(inject_msgs, list):
                    for inj in inject_msgs:
                        await _append_msg({
                            "role": inj.get("role", "user"),
                            "content": inj.get("content", ""),
                        })

            # -- 循环详情日志 --
            for i, m in enumerate(messages):
                if m.get("role") == "assistant" and m.get("tool_calls"):
                    has_rc = "reasoning_content" in m
                    rc_len = len(str(m.get("reasoning_content", ""))) if has_rc else -1
                    logger.info(
                        f"Loop {loop_count + 1} msg[{i}]: assistant+tool_calls, "
                        f"reasoning_content={'present(' + str(rc_len) + ' chars)' if has_rc else 'MISSING'}"
                    )

            # -- token 预算检查 --
            estimated_tokens = sum(
                len(str(m.get("content", ""))) // 2
                for m in messages
                if m.get("content")
            )
            logger.info(
                f"Loop {loop_count + 1}: {len(messages)} messages, ~{estimated_tokens} tokens"
            )
            if estimated_tokens > max_context_tokens:
                logger.warning(
                    f"Agent loop {task_id}: token budget exceeded at turn {loop_count + 1}: "
                    f"{estimated_tokens} > {max_context_tokens}, forcing stop"
                )
                await _append_msg({
                    "role": "system",
                    "content": "上下文长度已接近模型限制，请基于已有信息直接输出结论，不要再调用工具。"
                })

            # -- 死循环检测 --
            current_pattern = "|".join(sorted(
                f"{item['tool']}:{json.dumps(item.get('arguments', {}), ensure_ascii=False, sort_keys=True)[:100]}"
                for item in tool_outputs
            ))
            recent_tool_patterns.append(current_pattern)
            if len(recent_tool_patterns) > 6:
                recent_tool_patterns.pop(0)

            is_read_only_round = all(item["tool"] in read_only_tools for item in tool_outputs)
            has_repetitive_pattern = (
                len(recent_tool_patterns) >= 4
                and len(set(recent_tool_patterns[-4:])) <= 2
            )
            is_stuck_in_loop = is_read_only_round and has_repetitive_pattern and loop_count >= 4

            if is_stuck_in_loop:
                logger.warning(
                    f"Agent loop {task_id}: detected infinite loop: "
                    f"read_only={is_read_only_round}, repetitive={has_repetitive_pattern}"
                )
                await ws_manager.send_personal_message({
                    "type": "tool_call",
                    "task_id": task_id,
                    "parent_task_id": parent_task_id,
                    "status": "loop_detected",
                    "message": "检测到重复的工具调用模式，已自动停止。请基于已有信息继续创作或提出新的指令。",
                    "timestamp": datetime.now(timezone.utc).isoformat()
                }, websocket)
                await _append_msg({
                    "role": "system",
                    "content": "系统检测到你可能陷入了重复查询。请基于已获取的信息直接开始写作，或者明确告诉我你需要什么新的操作。"
                })

            loop_count += 1
            continue

        # ---- 无工具调用：结束 ----
        if is_thinking:
            await ws_manager.send_personal_message({
                "type": "thinking_done",
                "task_id": task_id,
                "parent_task_id": parent_task_id,
                "timestamp": datetime.now(timezone.utc).isoformat()
            }, websocket)
        break

    final_text = response_buffer.strip() or full_response.strip()
    return AgentLoopResult(
        final_text=final_text,
        turn_count=loop_count,
    )
