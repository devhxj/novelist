"""
章节创作 LangGraph 工作流

build_layer2 → generate_outline → [interrupt审批] → build_layer3 → write_chapter → post_process

消息组织：
- _work_msgs (ContextVar): 工具从 session 拷贝 + 图节点追加，供 LLM 调用用
- _delta (ContextVar): 图节点生成的消息，最终返回给循环注入 session
- 图节点不直接操作 session，只读写 ContextVar
"""
from __future__ import annotations

import asyncio
import logging
from contextvars import ContextVar
from typing import TypedDict, Any
from dataclasses import dataclass

from langgraph.graph import StateGraph, END
from langgraph.checkpoint.memory import MemorySaver
from langgraph.types import interrupt

logger = logging.getLogger(__name__)

# 工具设置，图节点读取
_current_ws: ContextVar = ContextVar("workflow_ws", default=None)
_work_msgs: ContextVar[list[dict]] = ContextVar("workflow_work_msgs", default=[])
# 图节点追加，工具最终取回
_delta: ContextVar[list[dict]] = ContextVar("workflow_delta", default=[])

# 审批机制：ws_chat 主循环收到审批消息后，通过 event 通知工具
import asyncio as _asyncio
_approval_event: _asyncio.Event | None = None
_approval_result: dict = {}


class WorkflowState(TypedDict):
    novel_id: int
    chapter_numbers: list[int]
    instruction: str
    model: str | None
    session_id: str

    layer2_context: str
    layer3_context: str

    is_batch: bool
    outlines: list[dict]
    outline_texts: list[str]
    user_approved: bool
    user_feedback: str | None

    current_chapter_idx: int
    completed_chapters: list[dict]
    errors: list[str]
    status: str


def create_initial_state(
    novel_id: int,
    chapter_numbers: list[int],
    instruction: str,
    session_id: str,
    model: str | None = None,
) -> WorkflowState:
    return WorkflowState(
        novel_id=novel_id,
        chapter_numbers=sorted(chapter_numbers),
        instruction=instruction,
        model=model,
        session_id=session_id,
        layer2_context="",
        layer3_context="",
        is_batch=len(chapter_numbers) > 1,
        outlines=[],
        outline_texts=[],
        user_approved=False,
        user_feedback=None,
        current_chapter_idx=0,
        completed_chapters=[],
        errors=[],
        status="initialized",
    )


@dataclass
class ChapterResult:
    chapter_number: int
    title: str
    content: str
    word_count: int
    outline_json: dict | None = None


def _format_outline(outline: dict) -> str:
    """将大纲 JSON 格式化为 Markdown 文本"""
    lines = [
        f"## 第{outline.get('chapter_number', '?')}章：{outline.get('title', '未命名')}",
        "",
        f"**语调**：{outline.get('tone', '未指定')}　|　**预估字数**：{outline.get('estimated_words', '?')}",
        "",
        "### 场景",
    ]
    for i, scene in enumerate(outline.get("scenes", []), 1):
        lines.append(f"{i}. **{scene.get('name', '场景' + str(i))}**")
        lines.append(f"   {scene.get('description', '')}")
        lines.append(f"   > 目的：{scene.get('purpose', '')}")
        lines.append("")

    if outline.get("key_events"):
        lines.append("### 关键事件")
        for event in outline["key_events"]:
            lines.append(f"- {event}")
        lines.append("")

    if outline.get("focus_characters"):
        lines.append("### 重点角色")
        for fc in outline["focus_characters"]:
            if isinstance(fc, dict):
                lines.append(f"- **{fc.get('name', '?')}**：{fc.get('role_in_chapter', '')}")
            else:
                lines.append(f"- {fc}")
        lines.append("")

    if outline.get("foreshadowing_ops"):
        lines.append("### 伏笔操作")
        for op in outline["foreshadowing_ops"]:
            labels = {"plant": "埋下", "advance": "推进", "resolve": "回收"}
            label = labels.get(op.get("action", ""), op.get("action", ""))
            lines.append(f"- [{label}] {op.get('content', '')}")
        lines.append("")

    lines.append(f"**章末钩子**：{outline.get('chapter_hook', '无')}")
    return "\n".join(lines)


# ======== nodes ========

async def _build_layer2(state: WorkflowState) -> dict[str, Any]:
    """构建 Layer2 详细上下文，追加到 work_msgs 和 delta"""
    from core.database import AsyncSessionLocal
    from context.context_builder import build_layer2_context

    logger.info(f"Building Layer2 for ch{state['chapter_numbers']}")
    async with AsyncSessionLocal() as db:
        layer2 = await build_layer2_context(db, state["novel_id"], state["instruction"])

    work = _work_msgs.get()
    delta = _delta.get()
    msg = {"role": "user", "content": layer2 or "", "context_layer": "layer2"}
    work.append(msg)
    delta.append(msg)

    return {"layer2_context": layer2 or "", "status": "layer2_built"}


async def _generate_outline(state: WorkflowState) -> dict[str, Any]:
    """生成大纲：从 work_msgs 取完整上下文，调 LLM 生成结构化 JSON，追加格式化文本到 work_msgs 和 delta"""
    from context.prompt_templates import CHAPTER_OUTLINE_SYSTEM_PROMPT, build_chapter_outline_user_prompt
    from core.llm_service import llm_service

    # 用 instruction + layer2 构建本次 user_prompt，work_msgs 已含 system1/system2/history/layer2
    user_prompt = build_chapter_outline_user_prompt(
        chapter_numbers=state["chapter_numbers"],
        instruction=state["instruction"],
        layer2_context=state["layer2_context"],
    )

    result = await llm_service.generate_json(
        prompt=user_prompt,
        system_prompt=CHAPTER_OUTLINE_SYSTEM_PROMPT,
        model=state.get("model"),
    )
    # 解包 {"chapters": [...]} 或单章 dict
    if isinstance(result, dict) and "chapters" in result and isinstance(result["chapters"], list):
        outlines = result["chapters"]
    elif isinstance(result, dict):
        outlines = [result]
    else:
        outlines = []
    if not outlines:
        return {"errors": state["errors"] + ["大纲生成失败：空结果"], "status": "error"}

    # 补 chapter_number（如果 LLM 没填）
    for i, ol in enumerate(outlines):
        if not ol.get("chapter_number") and i < len(state["chapter_numbers"]):
            ol["chapter_number"] = state["chapter_numbers"][i]

    outline_texts = [_format_outline(ol) for ol in outlines]
    combined = "\n\n---\n\n".join(outline_texts)

    # 追加到 work_msgs（后续节点如 write_chapter 可用）
    work = _work_msgs.get()
    delta = _delta.get()
    msg = {"role": "assistant", "content": combined, "workflow_event": "outline"}
    work.append(msg)
    delta.append(msg)

    # interrupt: 暂停图等外部审批，审批逻辑在工具中处理
    approved = interrupt({
        "type": "await_approval",
        "outlines": outlines,
        "outline_texts": outline_texts,
    })

    return {
        "outlines": outlines,
        "outline_texts": outline_texts,
        "user_approved": bool(approved),
        "status": "outline_approved" if approved else "outline_rejected",
    }


async def _build_layer3(state: WorkflowState) -> dict[str, Any]:
    """构建 Layer3 精准上下文，追加到 work_msgs 和 delta"""
    from core.database import AsyncSessionLocal
    from context.context_builder import build_layer3_context

    idx = state["current_chapter_idx"]
    chapter_number = state["chapter_numbers"][idx]
    outline = state["outlines"][idx] if idx < len(state["outlines"]) else {}

    logger.info(f"Building Layer3 for ch{chapter_number}")
    async with AsyncSessionLocal() as db:
        layer3 = await build_layer3_context(db, state["novel_id"], outline)

    work = _work_msgs.get()
    delta = _delta.get()
    msg = {"role": "user", "content": layer3 or "", "context_layer": "layer3"}
    work.append(msg)
    delta.append(msg)

    return {"layer3_context": layer3 or "", "status": "layer3_built"}


async def _write_chapter(state: WorkflowState) -> dict[str, Any]:
    """写正文：从 work_msgs 组装完整上下文，流式调 LLM，追加到 work_msgs 和 delta"""
    idx = state["current_chapter_idx"]
    chapter_number = state["chapter_numbers"][idx]
    outline = state["outlines"][idx] if idx < len(state["outlines"]) else {}
    outline_text = state["outline_texts"][idx] if idx < len(state["outline_texts"]) else ""

    from core.llm_service import llm_service

    # work_msgs 已包含 system1 + system2 + history + layer2 + outline + layer3
    # 注意：system1 和 system2 在 work_msgs 的前两条，LLM 会自然看到
    # 我们在末尾追加一条 user 指令即可
    user_prompt = (
        f"请根据以上大纲和上下文创作第{chapter_number}章正文。\n\n"
        f"大纲：\n{outline_text}\n\n"
        f"字数要求：约{outline.get('estimated_words', 3000)}字。"
    )

    # 流式输出到前端
    ws = _current_ws.get()
    content_parts: list[str] = []
    async for chunk in llm_service.generate_stream(
        prompt=user_prompt,
        system_prompt="你是一位专业的小说作家，严格遵循大纲，语言自然流畅。",
        model=state.get("model"),
    ):
        if chunk:
            content_parts.append(chunk)
            if ws:
                await ws.send_json({
                    "type": "content_chunk",
                    "content": chunk,
                    "chapter_number": chapter_number,
                })

    content = "".join(content_parts)
    title = outline.get("title") or f"第{chapter_number}章"
    word_count = len(content)

    # 保存到 Chapter 表
    from core.database import AsyncSessionLocal
    from sqlalchemy import select
    from chapters.models import Chapter

    async with AsyncSessionLocal() as db:
        result = await db.execute(
            select(Chapter).where(
                Chapter.novel_id == state["novel_id"],
                Chapter.chapter_number == chapter_number,
            )
        )
        chapter = result.scalar_one_or_none()
        if chapter:
            chapter.content = content
            chapter.title = title
            chapter.status = "completed"
            chapter.word_count = word_count
            chapter.outline_json = outline
            chapter.writing_status = "completed"
        else:
            chapter = Chapter(
                novel_id=state["novel_id"],
                chapter_number=chapter_number,
                title=title,
                content=content,
                status="completed",
                word_count=word_count,
                outline_json=outline,
                writing_status="completed",
            )
            db.add(chapter)
        await db.commit()

    # 追加到 work_msgs（下一批节点可见）和 delta
    work = _work_msgs.get()
    delta = _delta.get()
    msg = {"role": "assistant", "content": content, "workflow_event": "chapter_body", "chapter_number": chapter_number}
    work.append(msg)
    delta.append(msg)

    ch_result = ChapterResult(
        chapter_number=chapter_number,
        title=title,
        content=content,
        word_count=word_count,
        outline_json=outline,
    )

    return {
        "completed_chapters": state["completed_chapters"] + [ch_result.__dict__],
        "status": "chapter_written",
    }


async def _post_process(state: WorkflowState) -> dict[str, Any]:
    """后处理：摘要 + review + 向量记忆入库（后端操作，不涉及 session）"""
    chapter = state["completed_chapters"][-1]
    chapter_number = chapter["chapter_number"]
    content = chapter["content"]

    # 1. 摘要 + review + 向量记忆 并行
    async def save_summary():
        from core.llm_service import llm_service
        return await llm_service.generate_text(
            prompt=content[:3000],
            system_prompt="用200字以内总结以下章节，只输出摘要。",
            model=state.get("model"),
        )

    async def do_review():
        from core.llm_service import llm_service
        from context.prompt_templates import REVIEW_SYSTEM_PROMPT
        return await llm_service.generate_json(
            prompt=content[:8000],
            system_prompt=REVIEW_SYSTEM_PROMPT,
            model=state.get("model"),
        )

    async def update_memory():
        from core.database import AsyncSessionLocal
        from sqlalchemy import select
        from chapters.models import Chapter
        from rag.vector_store import vector_store

        async with AsyncSessionLocal() as db:
            result = await db.execute(
                select(Chapter).where(
                    Chapter.novel_id == state["novel_id"],
                    Chapter.chapter_number == chapter_number,
                )
            )
            ch = result.scalar_one_or_none()
            if not ch or not ch.content:
                return
            chunk_data = vector_store.build_chapter_chunks(
                chapter_id=ch.id,
                chapter_number=ch.chapter_number,
                chapter_title=ch.title,
                content=ch.content,
                summary=ch.summary,
            )
            if chunk_data:
                vector_store.delete_chapter_chunks(state["novel_id"], ch.id)
                vector_store.add_chunks(state["novel_id"], chunk_data)

    results = await asyncio.gather(save_summary(), do_review(), update_memory(), return_exceptions=True)
    summary = results[0] if not isinstance(results[0], Exception) else None

    if summary and isinstance(summary, str):
        from core.database import AsyncSessionLocal
        from sqlalchemy import select
        from chapters.models import Chapter

        async with AsyncSessionLocal() as db:
            result = await db.execute(
                select(Chapter).where(
                    Chapter.novel_id == state["novel_id"],
                    Chapter.chapter_number == chapter_number,
                )
            )
            ch = result.scalar_one_or_none()
            if ch:
                ch.summary = summary
                await db.commit()

    logger.info(f"Post-processing done for ch{chapter_number}")

    # 推进章节索引（批量时用于下一轮循环）
    return {
        "current_chapter_idx": state["current_chapter_idx"] + 1,
        "status": "chapter_completed",
    }


# ======== routing ========

def _route_after_outline(state: WorkflowState) -> str:
    if state.get("user_approved"):
        return "build_layer3"
    return END  # type: ignore[return-value]


def _route_after_post_process(state: WorkflowState) -> str:
    if state["current_chapter_idx"] < len(state["chapter_numbers"]):
        return "build_layer3"
    return END  # type: ignore[return-value]


# ======== graph ========

def _build_graph():  # type: ignore[no-any-return]
    graph = StateGraph(WorkflowState)

    graph.add_node("build_layer2", _build_layer2)
    graph.add_node("generate_outline", _generate_outline)
    graph.add_node("build_layer3", _build_layer3)
    graph.add_node("write_chapter", _write_chapter)
    graph.add_node("post_process", _post_process)

    graph.set_entry_point("build_layer2")
    graph.add_edge("build_layer2", "generate_outline")

    graph.add_conditional_edges(
        "generate_outline",
        _route_after_outline,
        {"build_layer3": "build_layer3", END: END},
    )

    graph.add_edge("build_layer3", "write_chapter")
    graph.add_edge("write_chapter", "post_process")

    graph.add_conditional_edges(
        "post_process",
        _route_after_post_process,
        {"build_layer3": "build_layer3", END: END},
    )

    return graph.compile(checkpointer=MemorySaver())


chapter_graph = _build_graph()
