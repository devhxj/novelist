from __future__ import annotations

from typing import Any, Optional, List

from mcp.server.fastmcp import FastMCP, Context

from app.core.auth import decode_token
from app.core.database import AsyncSessionLocal
from app.core.edit_mode import EditMode, EditModeConfig
from app.mcp.registry import get_mcp_registry
from app.chapters.models import Chapter
from sqlalchemy import select

mcp = FastMCP("AI Novel Generator")


async def _get_user_id_from_token(token: str) -> Optional[int]:
    try:
        payload = decode_token(token)
        if payload and payload.get("sub"):
            return int(payload["sub"])
    except Exception:
        return None
    return None


async def _get_user_id_from_context(ctx: Optional[Context]) -> Optional[int]:
    if not ctx:
        return None
    request = ctx.request_context.request if ctx.request_context else None
    if not request:
        return None
    auth_header = request.headers.get("Authorization") or request.headers.get("authorization")
    token = ""
    if auth_header and auth_header.startswith("Bearer "):
        token = auth_header.split(" ", 1)[1].strip()
    return await _get_user_id_from_token(token) if token else None


async def _execute_tool(name: str, ctx: Optional[Context] = None, **params) -> dict:
    user_id = await _get_user_id_from_context(ctx)
    if not user_id:
        return {"success": False, "error": "Unauthorized"}
    async with AsyncSessionLocal() as db:
        registry = get_mcp_registry()
        result = await registry.execute(
            name,
            db=db,
            user_id=user_id,
            **params
        )
        return result.model_dump()


@mcp.tool()
async def get_novel_summary(novel_id: int, ctx: Context) -> dict:
    return await _execute_tool("get_novel_summary", ctx, novel_id=novel_id)


@mcp.tool()
async def get_chapter_list(
    novel_id: int,
    status: Optional[str] = None,
    page: int = 1,
    page_size: int = 20,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_chapter_list",
        ctx,
        novel_id=novel_id,
        status=status,
        page=page,
        page_size=page_size
    )


@mcp.tool()
async def get_chapter_content(
    novel_id: int,
    chapter_id: Optional[int] = None,
    chapter_number: Optional[int] = None,
    include_summary: bool = True,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_chapter_content",
        ctx,
        novel_id=novel_id,
        chapter_id=chapter_id,
        chapter_number=chapter_number,
        include_summary=include_summary
    )


@mcp.tool()
async def get_novel_progress(novel_id: int, ctx: Context) -> dict:
    return await _execute_tool("get_novel_progress", ctx, novel_id=novel_id)


@mcp.tool()
async def get_character_list(
    novel_id: int,
    search: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool("get_character_list", ctx, novel_id=novel_id, search=search)


@mcp.tool()
async def get_character_detail(character_id: int, ctx: Context) -> dict:
    return await _execute_tool("get_character_detail", ctx, character_id=character_id)


@mcp.tool()
async def create_new_chapter(
    novel_id: int,
    chapter_number: int,
    title: Optional[str] = None,
    content: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "create_new_chapter",
        ctx,
        novel_id=novel_id,
        chapter_number=chapter_number,
        title=title,
        content=content
    )


@mcp.tool()
async def search_plot_memory(
    novel_id: int,
    query: str,
    top_k: int = 10,
    chapter_ids: Optional[List[int]] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "search_plot_memory",
        ctx,
        novel_id=novel_id,
        query=query,
        top_k=top_k,
        chapter_ids=chapter_ids
    )


@mcp.tool()
async def get_character_memory(
    novel_id: int,
    character_id: int,
    include_plot_events: bool = True,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_character_memory",
        ctx,
        novel_id=novel_id,
        character_id=character_id,
        include_plot_events=include_plot_events
    )


@mcp.tool()
async def get_timeline(
    novel_id: int,
    start_chapter: Optional[int] = None,
    end_chapter: Optional[int] = None,
    event_types: Optional[List[str]] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_timeline",
        ctx,
        novel_id=novel_id,
        start_chapter=start_chapter,
        end_chapter=end_chapter,
        event_types=event_types
    )


@mcp.tool()
async def get_recent_context(
    novel_id: int,
    chapter_id: int,
    window_size: int = 3,
    context_size: int = 3000,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_recent_context",
        ctx,
        novel_id=novel_id,
        chapter_id=chapter_id,
        window_size=window_size,
        context_size=context_size
    )


@mcp.tool()
async def check_character_consistency(
    novel_id: int,
    chapter_ids: Optional[List[int]] = None,
    character_id: Optional[int] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "check_character_consistency",
        ctx,
        novel_id=novel_id,
        chapter_ids=chapter_ids,
        character_id=character_id
    )


@mcp.tool()
async def check_plot_consistency(
    novel_id: int,
    chapter_ids: Optional[List[int]] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "check_plot_consistency",
        ctx,
        novel_id=novel_id,
        chapter_ids=chapter_ids
    )


@mcp.tool()
async def run_full_consistency_check(
    novel_id: int,
    chapter_ids: Optional[List[int]] = None,
    check_types: Optional[List[str]] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "run_full_consistency_check",
        ctx,
        novel_id=novel_id,
        chapter_ids=chapter_ids,
        check_types=check_types
    )


@mcp.tool()
async def list_unresolved_plots(
    novel_id: int,
    min_importance: Optional[int] = None,
    days_pending: Optional[int] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "list_unresolved_plots",
        ctx,
        novel_id=novel_id,
        min_importance=min_importance,
        days_pending=days_pending
    )


@mcp.tool()
async def get_foreshadowing_status(novel_id: int, ctx: Context) -> dict:
    return await _execute_tool("get_foreshadowing_status", ctx, novel_id=novel_id)


@mcp.tool()
async def start_edit_session(
    novel_id: int,
    chapter_id: Optional[int] = None,
    session_id: str = "",
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "start_edit_session",
        ctx,
        novel_id=novel_id,
        chapter_id=chapter_id,
        session_id=session_id
    )


@mcp.tool()
async def apply_edit(
    edit_session_id: str,
    change_type: str,
    new_content: str,
    start_line: Optional[int] = None,
    end_line: Optional[int] = None,
    reason: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "apply_edit",
        ctx,
        edit_session_id=edit_session_id,
        change_type=change_type,
        new_content=new_content,
        start_line=start_line,
        end_line=end_line,
        reason=reason
    )


@mcp.tool()
async def edit_chapter_content(
    session_id: str,
    chapter_id: int,
    change_type: str,
    new_content: str,
    start_line: Optional[int] = None,
    end_line: Optional[int] = None,
    reason: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "edit_chapter_content",
        ctx,
        session_id=session_id,
        chapter_id=chapter_id,
        change_type=change_type,
        new_content=new_content,
        start_line=start_line,
        end_line=end_line,
        reason=reason
    )


@mcp.tool()
async def get_edit_status(chapter_id: int, ctx: Context) -> dict:
    return await _execute_tool("get_edit_status", ctx, chapter_id=chapter_id)


@mcp.tool()
async def get_pending_changes(
    chapter_id: Optional[int] = None,
    session_id: Optional[str] = None,
    limit: int = 10,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_pending_changes",
        ctx,
        chapter_id=chapter_id,
        session_id=session_id,
        limit=limit
    )


@mcp.tool()
async def read_chapter_for_edit(chapter_id: int, ctx: Context) -> dict:
    return await _execute_tool("read_chapter_for_edit", ctx, chapter_id=chapter_id)


@mcp.tool()
async def run_agent_task(
    task_type: str,
    novel_id: int,
    chapter_id: Optional[int] = None,
    parameters: Optional[dict] = None,
    agent_role: Optional[str] = None,
    agent_id: Optional[str] = None,
    model: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "run_agent_task",
        ctx,
        task_type=task_type,
        novel_id=novel_id,
        chapter_id=chapter_id,
        parameters=parameters,
        agent_role=agent_role,
        agent_id=agent_id,
        model=model
    )


@mcp.resource("novel://{novel_id}/summary")
async def novel_summary_resource(novel_id: int, ctx: Context) -> dict:
    result = await _execute_tool("get_novel_summary", ctx, novel_id=novel_id)
    return result.get("data", result)


@mcp.resource("novel://{novel_id}/chapters")
async def novel_chapters_resource(novel_id: int, ctx: Context) -> dict:
    result = await _execute_tool("get_chapter_list", ctx, novel_id=novel_id, page=1, page_size=100)
    return result.get("data", result)


@mcp.resource("chapter://{chapter_id}")
async def chapter_resource(chapter_id: int, ctx: Context) -> dict:
    async with AsyncSessionLocal() as db:
        chapter_result = await db.execute(select(Chapter).where(Chapter.id == chapter_id))
        chapter = chapter_result.scalar_one_or_none()
        if not chapter:
            return {"error": "Chapter not found"}
        novel_id = chapter.novel_id
    result = await _execute_tool("get_chapter_content", ctx, novel_id=novel_id, chapter_id=chapter_id, include_summary=True)
    return result.get("data", result)


@mcp.resource("novel://{novel_id}/characters")
async def novel_characters_resource(novel_id: int, ctx: Context) -> dict:
    result = await _execute_tool("get_character_list", ctx, novel_id=novel_id)
    return result.get("data", result)


@mcp.prompt("edit_mode_prompt")
async def edit_mode_prompt(mode: str = "agent") -> list[dict]:
    try:
        edit_mode = EditMode(mode)
    except ValueError:
        edit_mode = EditMode.AGENT
    return [{"role": "system", "content": EditModeConfig.get_system_prompt(edit_mode)}]


def get_mcp_transport():
    return mcp.streamable_http_app()
