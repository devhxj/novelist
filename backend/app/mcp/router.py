"""
MCP工具API路由
提供MCP工具的HTTP接口
"""
from fastapi import APIRouter, Query, Body
from typing import Optional

from app.core.response import ApiResponse
from app.core.database import DBSession
from app.core.auth import CurrentUserDep
from app.core.dependencies import NovelOwner
from app.novels.models import Novel
from .base import MCPToolCategory
from .registry import get_mcp_registry
from app.agents.registry import get_agent_for_task, get_all_specs

router = APIRouter(prefix="/mcp", tags=["mcp"])

@router.get("/tools")
async def list_tools(
    db: DBSession,
    current_user: CurrentUserDep,
    category: str | None = Query(None, description="工具分类筛选")
):
    """
    列出所有可用的MCP工具
    
    - category: 可选的分类筛选 (novel_management/memory_retrieval/consistency_check/writing_assistant)
    """
    registry = get_mcp_registry()
    cat = None
    if category:
        try:
            cat = MCPToolCategory(category)
        except ValueError:
            pass
    
    tools = registry.list_tools(cat)
    
    return ApiResponse.success({"tools": tools, "total": len(tools)})


@router.get("/tools/categories")
async def list_categories(
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    按分类列出所有工具
    """
    registry = get_mcp_registry()
    result = registry.list_by_category()
    
    return ApiResponse.success(result)


@router.get("/tools/subagents/{task_type}")
async def list_subagent_tools(
    task_type: str,
    db: DBSession,
    current_user: CurrentUserDep
):
    """列出某个子Agent允许使用的工具和资源范围"""
    entry = get_agent_for_task(task_type)
    if not entry:
        available = list(get_all_specs().keys())
        return ApiResponse.error(
            "NOT_FOUND",
            f"Unknown subagent task type: {task_type}",
            details={"available_task_types": available},
            status_code=404,
        )

    _, spec = entry
    registry = get_mcp_registry()
    return ApiResponse.success(
        {
            "task_type": task_type,
            "display_name": spec.display_name,
            "allowed_tools": registry.list_tools(allowed_names=spec.allowed_tools),
            "allowed_tool_names": spec.allowed_tools,
            "allowed_resources": spec.allowed_resources,
            "allow_subagent_spawn": spec.allow_subagent_spawn,
        }
    )


@router.get("/tools/{tool_name}")
async def get_tool_info(
    tool_name: str,
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    获取指定工具的详细信息
    """
    registry = get_mcp_registry()
    tool = registry.get(tool_name)
    if not tool:
        return ApiResponse.error("NOT_FOUND", f"Tool not found: {tool_name}")
    
    return ApiResponse.success(tool.get_info())


@router.post("/tools/{tool_name}/execute")
async def execute_tool(
    tool_name: str,
    params: dict,
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    执行指定的MCP工具
    
    - params: 工具参数，根据不同工具有不同的参数要求
    """
    registry = get_mcp_registry()
    result = await registry.execute(tool_name, db=db, user_id=current_user.id, **params)
    
    return ApiResponse.success(
        {"success": result.success, "data": result.data, "error": result.error, "metadata": result.metadata},
        message="工具执行完成" if result.success else "工具执行失败"
    )


@router.post("/novels/{novel_id}/summary")
async def get_novel_summary(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep
):
    registry = get_mcp_registry()
    result = await registry.execute("get_novel_summary", db=db, user_id=current_user.id, novel_id=novel.id)
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/novels/{novel_id}/chapters/list")
async def get_chapter_list(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep,
    status: Optional[str] = Query(None),
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100)
):
    registry = get_mcp_registry()
    result = await registry.execute(
        "get_chapter_list",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        status=status,
        page=page,
        page_size=page_size
    )
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/chapters/{chapter_id}/content")
async def get_chapter_content(
    chapter_id: int,
    db: DBSession,
    current_user: CurrentUserDep,
    include_summary: bool = Query(True)
):
    from app.chapters.models import Chapter
    from app.core.exceptions import NotFoundException, UnauthorizedException
    from sqlalchemy import select
    
    result = await db.execute(select(Chapter).where(Chapter.id == chapter_id))
    chapter = result.scalar_one_or_none()
    if not chapter:
        raise NotFoundException("章节")
    
    result = await db.execute(select(Novel).where(Novel.id == chapter.novel_id))
    novel = result.scalar_one_or_none()
    if not novel or novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此章节")
    
    registry = get_mcp_registry()
    result = await registry.execute(
        "get_chapter_content",
        db=db,
        user_id=current_user.id,
        novel_id=chapter.novel_id,
        chapter_id=chapter_id,
        include_summary=include_summary
    )
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/novels/{novel_id}/progress")
async def get_novel_progress(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep
):
    registry = get_mcp_registry()
    result = await registry.execute("get_novel_progress", db=db, user_id=current_user.id, novel_id=novel.id)
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/novels/{novel_id}/characters/list")
async def get_character_list(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep,
    search: Optional[str] = Query(None)
):
    registry = get_mcp_registry()
    result = await registry.execute(
        "get_character_list",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        search=search
    )
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/characters/{character_id}/detail")
async def get_character_detail(
    character_id: int,
    db: DBSession,
    current_user: CurrentUserDep
):
    from app.characters.models import Character
    from app.core.exceptions import NotFoundException, UnauthorizedException
    from sqlalchemy import select
    
    result = await db.execute(select(Character).where(Character.id == character_id))
    character = result.scalar_one_or_none()
    if not character:
        raise NotFoundException("角色")
    
    result = await db.execute(select(Novel).where(Novel.id == character.novel_id))
    novel = result.scalar_one_or_none()
    if not novel or novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此角色")
    
    registry = get_mcp_registry()
    result = await registry.execute(
        "get_character_detail",
        db=db,
        user_id=current_user.id,
        character_id=character_id
    )
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/novels/{novel_id}/memory/search")
async def search_plot_memory(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep,
    query: str = Query(..., description="搜索查询文本"),
    top_k: int = Query(10, ge=1, le=50, description="返回结果数量"),
    chapter_ids: Optional[str] = Query(None, description="限定章节ID，逗号分隔")
):
    ids = None
    if chapter_ids:
        ids = [int(x.strip()) for x in chapter_ids.split(",") if x.strip().isdigit()]
    
    registry = get_mcp_registry()
    result = await registry.execute(
        "search_plot_memory",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        query=query,
        top_k=top_k,
        chapter_ids=ids
    )
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/characters/{character_id}/memory")
async def get_character_memory(
    character_id: int,
    db: DBSession,
    current_user: CurrentUserDep,
):
    from app.characters.models import Character
    from app.core.exceptions import NotFoundException, UnauthorizedException
    from sqlalchemy import select

    result = await db.execute(select(Character).where(Character.id == character_id))
    character = result.scalar_one_or_none()
    if not character:
        raise NotFoundException("角色")

    result = await db.execute(select(Novel).where(Novel.id == character.novel_id))
    novel = result.scalar_one_or_none()
    if not novel or novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此角色")

    registry = get_mcp_registry()
    result = await registry.execute(
        "get_character_memory",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        character_id=character_id,
    )

    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/chapters/{chapter_id}/context")
async def get_recent_context(
    chapter_id: int,
    db: DBSession,
    current_user: CurrentUserDep,
    window_size: int = Query(3, ge=1, le=10, description="前文章节数量"),
    context_size: int = Query(3000, ge=500, le=10000, description="上下文最大字符数")
):
    from app.chapters.models import Chapter
    from app.core.exceptions import NotFoundException, UnauthorizedException
    from sqlalchemy import select
    
    result = await db.execute(select(Chapter).where(Chapter.id == chapter_id))
    chapter = result.scalar_one_or_none()
    if not chapter:
        raise NotFoundException("章节")
    
    result = await db.execute(select(Novel).where(Novel.id == chapter.novel_id))
    novel = result.scalar_one_or_none()
    if not novel or novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此章节")
    
    registry = get_mcp_registry()
    result = await registry.execute(
        "get_recent_context",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        chapter_id=chapter_id,
        window_size=window_size,
        context_size=context_size
    )
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/novels/{novel_id}/consistency/character")
async def check_character_consistency(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep,
    chapter_ids: Optional[str] = Query(None, description="章节ID，逗号分隔"),
    character_id: Optional[int] = Query(None, description="指定角色ID")
):
    ids = None
    if chapter_ids:
        ids = [int(x.strip()) for x in chapter_ids.split(",") if x.strip().isdigit()]

    registry = get_mcp_registry()
    result = await registry.execute(
        "run_review",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        scope="character",
        chapter_ids=ids,
    )

    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/novels/{novel_id}/consistency/plot")
async def check_plot_consistency(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep,
    chapter_ids: Optional[str] = Query(None, description="章节ID，逗号分隔")
):
    ids = None
    if chapter_ids:
        ids = [int(x.strip()) for x in chapter_ids.split(",") if x.strip().isdigit()]

    registry = get_mcp_registry()
    result = await registry.execute(
        "run_review",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        scope="plot",
        chapter_ids=ids,
    )

    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/novels/{novel_id}/consistency/full")
async def run_full_consistency_check(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep,
    chapter_ids: Optional[str] = Query(None, description="章节ID，逗号分隔"),
):
    ids = None
    if chapter_ids:
        ids = [int(x.strip()) for x in chapter_ids.split(",") if x.strip().isdigit()]

    registry = get_mcp_registry()
    result = await registry.execute(
        "run_review",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        scope="full",
        chapter_ids=ids,
    )

    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.get("/novels/{novel_id}/foreshadowing/unresolved")
async def list_unresolved_plots(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep,
    min_importance: Optional[int] = Query(None, ge=1, le=5, description="最小重要程度"),
):
    registry = get_mcp_registry()
    result = await registry.execute(
        "run_review",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        scope="foreshadowing",
        min_importance=min_importance,
    )

    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.get("/novels/{novel_id}/foreshadowing/status")
async def get_foreshadowing_status(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep
):
    registry = get_mcp_registry()
    result = await registry.execute("run_review", db=db, user_id=current_user.id, novel_id=novel.id, scope="foreshadowing_status")

    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


@router.post("/novels/{novel_id}/chapters/create")
async def create_new_chapter(
    novel: NovelOwner,
    db: DBSession,
    current_user: CurrentUserDep,
    chapter_number: Optional[int] = Body(None, description="章节号，不传则自动创建下一章"),
    title: Optional[str] = Body(None, description="章节标题"),
    content: Optional[str] = Body(None, description="章节内容")
):
    """
    创建新章节
    """
    registry = get_mcp_registry()
    result = await registry.execute(
        "create_new_chapter",
        db=db,
        user_id=current_user.id,
        novel_id=novel.id,
        chapter_number=chapter_number,
        title=title,
        content=content
    )
    
    if result.success:
        return ApiResponse.success(result.data)
    return ApiResponse.error("TOOL_ERROR", result.error or "Unknown error")


