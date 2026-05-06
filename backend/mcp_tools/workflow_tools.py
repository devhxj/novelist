"""
章节创作工作流 MCP 工具

LLM 在主循环搜集上下文 + 生成大纲后调用本工具。
工具负责：大纲审批 → 通过后运行 LangGraph（Layer3注入 → 写正文 → 后处理）→ 返回 delta 注入 session。
"""
from __future__ import annotations

import logging

from pydantic import BaseModel, Field

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry

logger = logging.getLogger(__name__)


class CreateChapterWorkflowArgs(BaseModel):
    novel_id: int
    chapter_numbers: list[int] = Field(description="章节号列表，单章如[15]，多章如[15,16,17]")
    outline: dict = Field(description="结构化大纲 JSON，统一数组格式：{'chapters': [{...}, {...}]}")
    instruction: str = Field(description="用户的创作指令原文")
    model: str | None = Field(default=None)


class CreateChapterWorkflowTool(BaseMCPTool):
    name = "create_chapter_workflow"
    description = "提交章节大纲进行审批，审批通过后自动执行：精准上下文注入 → 正文写作 → 后处理。支持单章和多章批量创作。"
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
            "outline": {
                "type": "object",
                "description": "结构化大纲 JSON，统一数组格式：{'chapters': [{...}, {...}]}",
            },
            "instruction": {"type": "string", "description": "用户的创作指令原文"},
            "model": {"type": "string", "description": "LLM模型"},
        },
        "required": ["novel_id", "chapter_numbers", "outline", "instruction"],
    }

    async def execute(self, **kwargs) -> MCPToolResult:  # type: ignore[override]
        novel_id: int = kwargs["novel_id"]
        chapter_numbers: list[int] = kwargs["chapter_numbers"]
        outline_raw: dict = kwargs["outline"]
        instruction: str = kwargs["instruction"]
        model: str | None = kwargs.get("model")
        websocket = kwargs.get("websocket")
        chat_session = kwargs.get("chat_session")

        if not websocket or not chat_session:
            return MCPToolResult(success=False, error="工作流执行环境缺失：缺少 ws 或 session")

        try:
            import asyncio

            from chat.session_manager import session_manager
            from chapters.workflow import (
                create_initial_state, chapter_graph, _format_outline,
                _work_msgs, _delta, _current_ws,
            )

            # === 解析大纲：统一为 list[dict] ===
            if "chapters" in outline_raw and isinstance(outline_raw["chapters"], list):
                outlines: list[dict] = outline_raw["chapters"]
            else:
                outlines = [outline_raw]

            if not outlines:
                return MCPToolResult(success=False, error="大纲为空")

            # 补 chapter_number（如果 LLM 没填）
            for i, ol in enumerate(outlines):
                if not ol.get("chapter_number") and i < len(chapter_numbers):
                    ol["chapter_number"] = chapter_numbers[i]

            outline_texts: list[str] = [_format_outline(ol) for ol in outlines]

            # === delta 初始为空（指令和大纲均由 tool_call 参数注入 session，不重复） ===
            combined_text = "\n\n---\n\n".join(outline_texts)
            delta: list[dict] = []

            # === 发送大纲给前端审批 ===
            await websocket.send_json({
                "type": "outline_generated",
                "novel_id": novel_id,
                "chapter_numbers": chapter_numbers,
                "content": combined_text,
                "outlines": outlines,
            })

            # === 等待用户审批（主循环拦截审批消息后 set event） ===
            import chapters.workflow as _wf
            event = asyncio.Event()
            _wf._approval_event = event
            _wf._approval_result.clear()
            await event.wait()
            _wf._approval_event = None
            approval_raw = dict(_wf._approval_result)
            approved = approval_raw.get("approved", False)

            if not approved:
                feedback = approval_raw.get("feedback", "请重新生成")
                delta.append({
                    "role": "user",
                    "content": f"大纲审批未通过，用户意见：{feedback}",
                    "workflow_event": "outline_rejected",
                })
                return MCPToolResult(success=True, data={"inject": list(delta)})

            # === 审批通过，启动 LangGraph ===
            _current_ws.set(websocket)

            # 拷贝 session 到 work_msgs，注入大纲（指令在 _write_chapter 合并追加）
            session_msgs = session_manager.get_messages_for_api(chat_session, include_context=True)
            work: list[dict] = list(session_msgs)
            work.append({
                "role": "user",
                "content": f"大纲：\n{combined_text}",
            })
            _work_msgs.set(work)
            _delta.set(delta)

            state = create_initial_state(
                novel_id=novel_id,
                chapter_numbers=chapter_numbers,
                instruction=instruction,
                session_id=chat_session.session_id,
                outlines=outlines,
                outline_texts=outline_texts,
                model=model,
            )
            config: dict = {"configurable": {"thread_id": chat_session.session_id}}

            # 图执行：build_layer3 → write_chapter → post_process（→ 批量循环）
            await chapter_graph.ainvoke(state, config)  # type: ignore[arg-type]

            # === 追加维护指令（含 review 引导） ===
            delta.append({
                "role": "user",
                "content": (
                    "正文已写入完成。请：\n"
                    "1. 调用 run_subagent（task_type=\"review\"）对本章进行审核\n"
                    "2. 全面检查并维护小说状态：\n"
                    "   - 新出现的角色 → 创建角色；角色属性变化 → 更新角色\n"
                    "   - 角色关系变化 → 更新关系\n"
                    "   - 伏笔埋下/推进/回收 → 更新时间线\n"
                    "   - 更新故事状态文档\n"
                    "   - 更新读者认知（已知信息、悬念、误知）\n"
                    "   - 故事弧线推进或新增 → 更新或创建弧线\n"
                    "   - 如有创作偏好变化 → 更新 creative profile\n"
                    "3. 向用户汇报本章成果"
                ),
                "workflow_event": "state_maintenance_instruction",
            })

            return MCPToolResult(success=True, data={"inject": list(delta)})

        except Exception as e:
            logger.error(f"Chapter workflow failed: {e}", exc_info=True)
            return MCPToolResult(success=False, error=str(e))

    @classmethod
    def register_all(cls, registry: MCPToolRegistry):
        registry.register(cls())
