"""
章节创作工作流 MCP 工具

create_chapter_workflow: LLM 调用后，工具内部阻塞执行 LangGraph，
包含大纲生成、审批、正文写作、后处理。
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
            from langgraph.errors import GraphInterrupt

            from chat.session_manager import session_manager, MessageRole
            from chapters.workflow import create_initial_state, chapter_graph, _current_ws

            _current_ws.set(websocket)

            # 1. 追加 tool_result（紧跟 tool_call，符合协议）
            session_manager.add_message(
                chat_session,
                MessageRole.TOOL,
                "章节创作工作流已启动，请根据系统提示词和以下信息继续创作。",
                metadata={"tool_name": "create_chapter_workflow"},
            )

            state = create_initial_state(
                novel_id=novel_id,
                chapter_numbers=chapter_numbers,
                instruction=instruction,
                session_id=chat_session.session_id,
                model=model,
            )
            config: dict = {"configurable": {"thread_id": chat_session.session_id}}

            # 2. 运行图到 interrupt（大纲生成后暂停）
            try:
                await chapter_graph.ainvoke(state, config)  # type: ignore[arg-type]
            except GraphInterrupt as gi:
                interrupt_data = gi.args[0] if gi.args else None
                if isinstance(interrupt_data, dict) and interrupt_data.get("type") == "await_approval":
                    outline_texts: list[str] = interrupt_data.get("outline_texts", [])
                    outlines: list[dict] = interrupt_data.get("outlines", [])

                    # 3. 发送大纲给用户
                    combined_text = "\n\n---\n\n".join(outline_texts)
                    await websocket.send_json({
                        "type": "outline_generated",
                        "novel_id": novel_id,
                        "chapter_numbers": chapter_numbers,
                        "content": combined_text,
                        "outlines": outlines,
                    })

                    # 4. 等待用户审批
                    approval_raw = await websocket.receive_json()

                    approved = approval_raw.get("approved", False)

                    # 5. 追加大纲到 session
                    approval_msg = (
                        "【大纲已审批通过，开始创作章节...】"
                        if approved else f"【大纲审批未通过，用户意见：{approval_raw.get('feedback', '请重新生成')}】"
                    )
                    session_manager.add_message(
                        chat_session, MessageRole.USER, approval_msg,
                        metadata={"workflow_event": "approval"},
                    )
                    for i, ot in enumerate(outline_texts):
                        session_manager.add_message(
                            chat_session, MessageRole.ASSISTANT, ot,
                            metadata={"workflow_event": "outline", "chapter_idx": i},
                        )

                    if approved:
                        # 6. 恢复图：build_layer3 → write_chapter（流式）→ post_process
                        await chapter_graph.ainvoke(Command(resume=True), config)  # type: ignore[arg-type]

                        # 追加正文到 session
                        final_state = chapter_graph.get_state(config)  # type: ignore[arg-type]
                        if final_state and final_state.values:
                            completed = final_state.values.get("completed_chapters", [])
                            for ch in completed:
                                session_manager.add_message(
                                    chat_session, MessageRole.ASSISTANT,
                                    ch.get("content", ""),
                                    metadata={"workflow_event": "chapter_body", "chapter_number": ch.get("chapter_number")},
                                )

                        # 7. 追加 user 消息驱动 LLM 全面维护小说状态
                        session_manager.add_message(
                            chat_session, MessageRole.USER,
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
                            metadata={"workflow_event": "state_maintenance_instruction"},
                        )

                        return MCPToolResult(success=True, data={"__appended__": True, "status": "completed"})
                    else:
                        return MCPToolResult(success=True, data={"__appended__": True, "status": "outline_rejected"})

            # 不应该走到这里
            return MCPToolResult(success=True, data={"__appended__": True, "status": "completed"})

        except Exception as e:
            logger.error(f"Chapter workflow failed: {e}", exc_info=True)
            return MCPToolResult(success=False, error=str(e))

    @classmethod
    def register_all(cls, registry: MCPToolRegistry):
        registry.register(cls())
