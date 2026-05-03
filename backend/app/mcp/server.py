from __future__ import annotations
# pyright: reportArgumentType=false, reportCallIssue=false

from typing import Any, Optional, List, Dict

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
    try:
        async with AsyncSessionLocal() as db:
            registry = get_mcp_registry()
            result = await registry.execute(
                name,
                db=db,
                user_id=user_id,
                **params
            )
            return result.model_dump()
    except Exception as e:
        import logging
        logging.getLogger("mcp.server").error(f"Tool '{name}' execution failed: {e}", exc_info=True)
        return {"success": False, "error": f"工具执行失败: {str(e)}"}


@mcp.tool()
async def get_novel_info(novel_id: int, mode: str, ctx: Context) -> dict:
    return await _execute_tool("get_novel_info", ctx, novel_id=novel_id, mode=mode)


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
    include_lines: bool = False,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_chapter_content",
        ctx,
        novel_id=novel_id,
        chapter_id=chapter_id,
        chapter_number=chapter_number,
        include_summary=include_summary,
        include_lines=include_lines,
    )


@mcp.tool()
async def get_creative_profile(novel_id: int, ctx: Context) -> dict:
    return await _execute_tool("get_creative_profile", ctx, novel_id=novel_id)


@mcp.tool()
async def update_creative_profile(
    novel_id: int,
    author_intent: Optional[str] = None,
    preferred_tone: Optional[str] = None,
    collaboration_style: Optional[str] = None,
    scene_planning_notes: Optional[str] = None,
    must_keep: Optional[List[str]] = None,
    must_avoid: Optional[List[str]] = None,
    long_term_goals: Optional[List[str]] = None,
    extra_metadata: Optional[dict] = None,
    merge_with_existing: bool = True,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "update_creative_profile",
        ctx,
        novel_id=novel_id,
        author_intent=author_intent,
        preferred_tone=preferred_tone,
        collaboration_style=collaboration_style,
        scene_planning_notes=scene_planning_notes,
        must_keep=must_keep,
        must_avoid=must_avoid,
        long_term_goals=long_term_goals,
        extra_metadata=extra_metadata,
        merge_with_existing=merge_with_existing
    )


@mcp.tool()
async def get_characters(
    novel_id: int,
    mode: str,
    character_id: Optional[int] = None,
    search: Optional[str] = None,
    include_relations: bool = True,
    include_recent_events: bool = True,
    include_memory: bool = False,
    include_inactive: bool = False,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_characters", ctx,
        novel_id=novel_id, mode=mode, character_id=character_id,
        search=search, include_relations=include_relations,
        include_recent_events=include_recent_events,
        include_memory=include_memory, include_inactive=include_inactive
    )


@mcp.tool()
async def search_story_memory(
    novel_id: int,
    query: str,
    top_k: int = 5,
    min_relevance_score: float = 0.35,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "search_story_memory",
        ctx,
        novel_id=novel_id,
        query=query,
        top_k=top_k,
        min_relevance_score=min_relevance_score
    )


@mcp.tool()
async def create_character(
    novel_id: int,
    name: str,
    personality: Optional[Dict[str, Any]] = None,
    abilities: Optional[List[str]] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "create_character", ctx,
        novel_id=novel_id, name=name,
        personality=personality, abilities=abilities
    )


@mcp.tool()
async def update_character(
    novel_id: int,
    character_id: int,
    name: Optional[str] = None,
    personality: Optional[Dict[str, Any]] = None,
    abilities: Optional[List[str]] = None,
    ctx: Context|None = None
) -> dict:
    return await _execute_tool(
        "update_character", ctx,
        novel_id=novel_id, character_id=character_id,
        name=name, personality=personality, abilities=abilities
    )


@mcp.tool()
async def create_new_chapter(
    novel_id: int,
    chapter_number: Optional[int] = None,
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
async def edit_chapter(
    novel_id: int,
    chapter_id: int,
    change_type: str = "full_replace",
    new_content: Optional[str] = None,
    search_text: Optional[str] = None,
    match_mode: str = "first",
    edits: Optional[List[Dict[str, str]]] = None,
    start_line: Optional[int] = None,
    end_line: Optional[int] = None,
    reason: Optional[str] = None,
    dry_run: bool = False,
    undo: bool = False,
    undo_from_snapshot: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "edit_chapter",
        ctx,
        novel_id=novel_id,
        chapter_id=chapter_id,
        change_type=change_type,
        new_content=new_content,
        search_text=search_text,
        match_mode=match_mode,
        edits=edits,
        start_line=start_line,
        end_line=end_line,
        reason=reason,
        dry_run=dry_run,
        undo=undo,
        undo_from_snapshot=undo_from_snapshot,
    )


@mcp.tool()
async def run_review(
    novel_id: int,
    scope: str = "full",
    chapter_ids: Optional[List[int]] = None,
    min_importance: Optional[int] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "run_review",
        ctx,
        novel_id=novel_id,
        scope=scope,
        chapter_ids=chapter_ids,
        min_importance=min_importance
    )


@mcp.tool()
async def get_timeline(
    novel_id: int,
    mode: str = "context",
    current_chapter: Optional[int] = None,
    max_entries: int = 15,
    category: Optional[str] = None,
    status: Optional[str] = None,
    time_horizon: Optional[str] = None,
    search: Optional[str] = None,
    page: int = 1,
    page_size: int = 20,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_timeline", ctx,
        novel_id=novel_id, mode=mode, current_chapter=current_chapter,
        max_entries=max_entries, category=category, status=status,
        time_horizon=time_horizon, search=search, page=page, page_size=page_size
    )


@mcp.tool()
async def add_timeline_entry(
    novel_id: int,
    entries: List[Dict[str, Any]],
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "add_timeline_entry", ctx,
        novel_id=novel_id, entries=entries,
    )


@mcp.tool()
async def update_timeline_entry(
    novel_id: int,
    entry_id: int,
    title: Optional[str] = None,
    description: Optional[str] = None,
    detail_json: Optional[dict] = None,
    target_chapter: Optional[int] = None,
    time_horizon: Optional[str] = None,
    status: Optional[str] = None,
    importance: Optional[int] = None,
    tags: Optional[List[str]] = None,
    resolved_chapter_id: Optional[int] = None,
    resolution_notes: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "update_timeline_entry", ctx,
        novel_id=novel_id, entry_id=entry_id, title=title,
        description=description, detail_json=detail_json,
        target_chapter=target_chapter, time_horizon=time_horizon,
        status=status, importance=importance, tags=tags,
        resolved_chapter_id=resolved_chapter_id, resolution_notes=resolution_notes,
    )


@mcp.tool()
async def update_character_relationship(
    novel_id: int,
    source_character_id: Optional[int] = None,
    target_character_id: Optional[int] = None,
    relation_id: Optional[int] = None,
    relationship_type: Optional[str] = None,
    description: Optional[str] = None,
    intensity: int = 3,
    status: str = "active",
    evolve: bool = False,
    evolution_notes: Optional[str] = None,
    established_chapter_id: Optional[int] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "update_character_relationship",
        ctx,
        novel_id=novel_id,
        source_character_id=source_character_id,
        target_character_id=target_character_id,
        relation_id=relation_id,
        relationship_type=relationship_type,
        description=description,
        intensity=intensity,
        status=status,
        evolve=evolve,
        evolution_notes=evolution_notes,
        established_chapter_id=established_chapter_id
    )


@mcp.tool()
async def run_subagent(
    task_type: str,
    novel_id: int,
    chapter_id: Optional[int] = None,
    instruction: Optional[str] = None,
    parameters: Optional[dict] = None,
    agent_role: Optional[str] = None,
    agent_id: Optional[str] = None,
    model: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "run_subagent",
        ctx,
        task_type=task_type,
        novel_id=novel_id,
        chapter_id=chapter_id,
        instruction=instruction,
        parameters=parameters,
        agent_role=agent_role,
        agent_id=agent_id,
        model=model
    )


@mcp.resource("novel://{novel_id}/summary")
async def novel_summary_resource(novel_id: int, ctx: Context) -> dict:
    result = await _execute_tool("get_novel_info", ctx, novel_id=novel_id, mode="summary")
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
    result = await _execute_tool("get_characters", ctx, novel_id=novel_id, mode="list")
    return result.get("data", result)


@mcp.prompt("edit_mode_prompt")
async def edit_mode_prompt(mode: str = "agent") -> list[dict]:
    return [{"role": "system", "content": EditModeConfig.get_system_prompt(EditMode.AGENT)}]


@mcp.tool()
async def get_locations(
    novel_id: int,
    mode: str,
    location_id: Optional[int] = None,
    location_type: Optional[str] = None,
    search: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_locations", ctx,
        novel_id=novel_id, mode=mode, location_id=location_id,
        location_type=location_type, search=search
    )


@mcp.tool()
async def create_location(
    novel_id: int,
    name: str,
    location_type: Optional[str] = None,
    description: Optional[str] = None,
    tags: Optional[List[str]] = None,
    parent_location_id: Optional[int] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "create_location", ctx,
        novel_id=novel_id, name=name,
        location_type=location_type, description=description,
        tags=tags, parent_location_id=parent_location_id
    )


@mcp.tool()
async def update_location(
    novel_id: int,
    location_id: int,
    name: Optional[str] = None,
    location_type: Optional[str] = None,
    description: Optional[str] = None,
    tags: Optional[List[str]] = None,
    parent_location_id: Optional[int] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "update_location", ctx,
        novel_id=novel_id, location_id=location_id,
        name=name, location_type=location_type, description=description,
        tags=tags, parent_location_id=parent_location_id
    )


@mcp.tool()
async def delete_location(
    novel_id: int,
    location_id: int,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "delete_location", ctx,
        novel_id=novel_id, location_id=location_id
    )


@mcp.tool()
async def get_story_arcs(
    novel_id: int,
    arc_type: Optional[str] = None,
    status: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "get_story_arcs", ctx,
        novel_id=novel_id, arc_type=arc_type, status=status
    )


@mcp.tool()
async def add_story_arc(
    novel_id: int,
    name: str,
    description: Optional[str] = None,
    arc_type: str = "sub",
    start_chapter: Optional[int] = None,
    end_chapter: Optional[int] = None,
    importance: int = 1,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "add_story_arc", ctx,
        novel_id=novel_id, name=name, description=description,
        arc_type=arc_type, start_chapter=start_chapter,
        end_chapter=end_chapter, importance=importance
    )


@mcp.tool()
async def update_story_arc(
    novel_id: int,
    arc_id: int,
    name: Optional[str] = None,
    description: Optional[str] = None,
    arc_type: Optional[str] = None,
    start_chapter: Optional[int] = None,
    end_chapter: Optional[int] = None,
    importance: Optional[int] = None,
    status: Optional[str] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "update_story_arc", ctx,
        novel_id=novel_id, arc_id=arc_id, name=name,
        description=description, arc_type=arc_type,
        start_chapter=start_chapter, end_chapter=end_chapter,
        importance=importance, status=status
    )


@mcp.tool()
async def get_story_state(novel_id: int, ctx: Context = None) -> dict:
    return await _execute_tool("get_story_state", ctx, novel_id=novel_id)


@mcp.tool()
async def update_story_state(novel_id: int, content: str, ctx: Context = None) -> dict:
    return await _execute_tool("update_story_state", ctx, novel_id=novel_id, content=content)


@mcp.tool()
async def get_reader_perspective(novel_id: int, ctx: Context = None) -> dict:
    return await _execute_tool("get_reader_perspective", ctx, novel_id=novel_id)


@mcp.tool()
async def add_reader_perspective_entry(
    novel_id: int,
    type: str,
    content: str,
    planted_chapter: int,
    related_truth: Optional[str] = None,
    planned_reveal_chapter: Optional[int] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "add_reader_perspective_entry", ctx,
        novel_id=novel_id, type=type, content=content,
        planted_chapter=planted_chapter, related_truth=related_truth,
        planned_reveal_chapter=planned_reveal_chapter,
    )


@mcp.tool()
async def update_reader_perspective_entry(
    novel_id: int,
    entry_id: int,
    last_mentioned_chapter: Optional[int] = None,
    revealed_chapter: Optional[int] = None,
    ctx: Context = None
) -> dict:
    return await _execute_tool(
        "update_reader_perspective_entry", ctx,
        novel_id=novel_id, entry_id=entry_id,
        last_mentioned_chapter=last_mentioned_chapter,
        revealed_chapter=revealed_chapter,
    )


def get_mcp_transport():
    return mcp.streamable_http_app()
