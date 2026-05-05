"""
章节创作工作流 MCP 工具

create_chapter_workflow: LLM 调用后，工具内部阻塞执行 LangGraph。
工具拷贝 session → work_msgs 供图节点 LLM 使用 → 图节点追加到 work_msgs 和 delta →
工具返回 delta 给循环注入 session。
"""
from __future__ import annotations

import logging

from pydantic import BaseModel, Field

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry

logger = logging.getLogger(__name__)


class CreateChapterWorkflowArgs(BaseModel):
    novel_id: int
    chapter_numbers: list[int] = Field(description="章节号列表，单章如[15]，多章如[15,16,17]")
    instruction: str = Field(description="用户的创作指令原文")
    model: str | None = Field(default=None)


class CreateChapterWorkflowTool(BaseMCPTool):
    name = "create_chapter_workflow"
    description = "启动章节创作工作流：结构化大纲 → 用户审批 → 正文写作 → 后处理。支持单章和多章批量创作。"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "chapter_numbers": {
                "type": "array",
                "items": {"type": "integer"},
                "description": "章节号列表，单章如[15]，多章如[15,16,17]",
            },
            "instruction": {"type": "string", "description": "用户的创作指令原文"},
            "model": {"type": "string", "description": "LLM模型"},
        },
        "required": ["novel_id", "chapter_numbers", "instruction"],
    }

    async def execute(self, **kwargs) -> MCPToolResult:  # type: ignore[override]
        novel_id: int = kwargs["novel_id"]
        chapter_numbers: list[int] = kwargs["chapter_numbers"]
        instruction: str = kwargs["instruction"]
        model: str | None = kwargs.get("model")
        websocket = kwargs.get("websocket")
        chat_session = kwargs.get("chat_session")

        if not websocket or not chat_session:
            return MCPToolResult(success=False, error="工作流执行环境缺失：缺少 ws 或 session")

        try:
            from langgraph.types import Command

            from chat.session_manager import session_manager, MessageRole
            from chapters.workflow import (
                create_initial_state, chapter_graph,
                _work_msgs, _delta, _current_ws,
            )

            # 设置 ContextVar，供图节点使用
            _current_ws.set(websocket)

            # === 拷贝 session 到 work_msgs ===
            session_msgs = session_manager.get_messages_for_api(chat_session, include_context=True)
            # 转换为普通 list，图节点可以追加
            work: list[dict] = list(session_msgs)  # [system1, system2, ...history..., user: 当前消息]
            delta: list[dict] = []
            _work_msgs.set(work)
            _delta.set(delta)

            state = create_initial_state(
                novel_id=novel_id,
                chapter_numbers=chapter_numbers,
                instruction=instruction,
                session_id=chat_session.session_id,
                model=model,
            )
            config: dict = {"configurable": {"thread_id": chat_session.session_id}}

            # === 运行图到 interrupt（build_layer2 → generate_outline → interrupt） ===
            # 图节点 _build_layer2 会把 Layer2 追加到 work 和 delta
            # 图节点 _generate_outline 会生成大纲并追加到 work 和 delta
            # langgraph 1.x: interrupt() 不抛异常，返回带 __interrupt__ 的 state
            first_result = await chapter_graph.ainvoke(state, config)  # type: ignore[arg-type]
            interrupts = (first_result or {}).get("__interrupt__", [])
            if not interrupts:
                # 没有 interrupt，图已跑完（不应该发生）
                return MCPToolResult(success=True, data={"inject": list(delta)})

            interrupt_obj = interrupts[0]  # Interrupt 对象
            interrupt_data = interrupt_obj.value  # 传给 interrupt() 的 dict
            if not isinstance(interrupt_data, dict) or interrupt_data.get("type") != "await_approval":
                return MCPToolResult(success=False, error="工作流中断数据异常")

            outline_texts: list[str] = interrupt_data.get("outline_texts", [])
            outlines: list[dict] = interrupt_data.get("outlines", [])

            # === 审批阶段 ===
            # 大纲已在 _generate_outline 追加到 work 和 delta
            # 发送大纲给用户
            combined_text = "\n\n---\n\n".join(outline_texts)
            await websocket.send_json({
                "type": "outline_generated",
                "novel_id": novel_id,
                "chapter_numbers": chapter_numbers,
                "content": combined_text,
                "outlines": outlines,
            })

            # 等待用户审批（通过主循环 Event 通知，不直接 recv）
            import asyncio
            import chapters.workflow as _wf
            event = asyncio.Event()
            _wf._approval_event = event
            _wf._approval_result.clear()
            await event.wait()
            _wf._approval_event = None
            approval_raw = dict(_wf._approval_result)
            approved = approval_raw.get("approved", False)

            if approved:
                # === 恢复图：build_layer3 → write_chapter → post_process ===
                # _build_layer3 追加 Layer3 到 work 和 delta
                # _write_chapter 流式输出 + 追加正文到 work 和 delta
                # _post_process 做摘要/review/向量记忆
                await chapter_graph.ainvoke(Command(resume=True), config)  # type: ignore[arg-type]

                # === 追加维护指令（仅 delta，不入 work） ===
                delta.append({
                    "role": "user",
                    "content": (
                        "正文已写入完成。请根据本章内容，全面检查并维护小说状态：\n"
                        "- 新出现的角色 → 创建角色；角色属性变化 → 更新角色\n"
                        "- 角色关系变化 → 更新关系\n"
                        "- 新出现的地点 → 创建或更新地点\n"
                        "- 伏笔埋下/推进/回收 → 更新时间线\n"
                        "- 更新故事状态文档\n"
                        "- 更新读者认知（已知信息、悬念、误知）\n"
                        "- 故事弧线推进或新增 → 更新或创建弧线\n"
                        "- 如有创作偏好变化 → 更新 creative profile\n"
                        "完成后向用户汇报本章成果。",
                    ),
                    "workflow_event": "state_maintenance_instruction",
                })

                return MCPToolResult(success=True, data={"inject": list(delta)})
            else:
                # 审批未通过，只追加结果说明到 delta
                feedback = approval_raw.get("feedback", "请重新生成")
                delta.append({
                    "role": "user",
                    "content": f"大纲审批未通过，用户意见：{feedback}",
                    "workflow_event": "outline_rejected",
                })
                return MCPToolResult(success=True, data={"inject": list(delta)})

        except Exception as e:
            logger.error(f"Chapter workflow failed: {e}", exc_info=True)
            return MCPToolResult(success=False, error=str(e))

    @classmethod
    def register_all(cls, registry: MCPToolRegistry):
        registry.register(cls())
