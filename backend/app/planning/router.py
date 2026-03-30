"""
情节规划API路由
"""
import logging
from fastapi import APIRouter, Depends
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from app.core.database import get_db, DBSession
from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException, UnauthorizedException
from app.core.auth import CurrentUser
from app.core.dependencies import NovelOwner
from app.planning.planner import PlotPlanner
from app.planning.models import PlotLine, PlotNode, PlotLineType, PlotNodeStatus
from app.planning.schemas import (
    PlotLineCreate,
    PlotLineUpdate,
    PlotLineResponse,
    PlotNodeCreate,
    PlotNodeUpdate,
    PlotNodeResponse,
    PlotOutlineCreate,
    PlotOutlineUpdate,
    PlotOutlineResponse,
    PlotSuggestionRequest,
    PlotSuggestionResponse
)
from app.auth.models import User
from app.novels.models import Novel

router = APIRouter(prefix="/planning", tags=["planning"])
logger = logging.getLogger(__name__)


async def check_plot_line_ownership(
    plot_line_id: int,
    db: DBSession,
    current_user: CurrentUser
) -> PlotLine:
    """检查情节线所有权"""
    result = await db.execute(
        select(PlotLine).where(PlotLine.id == plot_line_id)
    )
    plot_line = result.scalar_one_or_none()
    if not plot_line:
        raise NotFoundException("情节线")
    
    novel_result = await db.execute(
        select(Novel).where(Novel.id == plot_line.novel_id)
    )
    novel = novel_result.scalar_one_or_none()
    if not novel or novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此情节线")
    
    return plot_line


async def check_plot_node_ownership(
    node_id: int,
    db: DBSession,
    current_user: CurrentUser
) -> PlotNode:
    """检查情节节点所有权"""
    result = await db.execute(
        select(PlotNode).where(PlotNode.id == node_id)
    )
    node = result.scalar_one_or_none()
    if not node:
        raise NotFoundException("情节节点")
    
    novel_result = await db.execute(
        select(Novel).where(Novel.id == node.novel_id)
    )
    novel = novel_result.scalar_one_or_none()
    if not novel or novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此情节节点")
    
    return node


@router.get("/novels/{novel_id}/outline")
async def get_plot_outline(
    novel: NovelOwner,
    db: DBSession
):
    """获取情节大纲"""
    planner = PlotPlanner(db, novel.id)
    outline = await planner.get_outline()
    
    if not outline:
        return ApiResponse.success({
            "exists": False,
            "message": "尚未创建情节大纲"
        })
    
    return ApiResponse.success(PlotOutlineResponse.model_validate(outline))


@router.post("/novels/{novel_id}/outline")
async def create_or_update_outline(
    novel: NovelOwner,
    data: PlotOutlineCreate,
    db: DBSession
):
    """创建或更新情节大纲"""
    planner = PlotPlanner(db, novel.id)
    outline = await planner.create_or_update_outline(data)
    
    return ApiResponse.success({
        "id": outline.id,
        "title": outline.title,
        "message": "情节大纲已保存"
    })


@router.put("/novels/{novel_id}/outline")
async def update_plot_outline(
    novel: NovelOwner,
    data: PlotOutlineUpdate,
    db: DBSession
):
    """更新情节大纲"""
    planner = PlotPlanner(db, novel.id)
    outline = await planner.update_outline(data)
    
    if not outline:
        raise NotFoundException("情节大纲")
    
    return ApiResponse.success({
        "id": outline.id,
        "title": outline.title,
        "message": "情节大纲已更新"
    })


@router.get("/novels/{novel_id}/plot-lines")
async def list_plot_lines(
    novel: NovelOwner,
    db: DBSession,
    line_type: str = None,
    status: str = None
):
    """获取情节线列表"""
    planner = PlotPlanner(db, novel.id)
    plot_lines = await planner.list_plot_lines(line_type=line_type, status=status)
    
    return ApiResponse.success({
        "items": [PlotLineResponse.model_validate(pl) for pl in plot_lines],
        "total": len(plot_lines)
    })


@router.post("/novels/{novel_id}/plot-lines")
async def create_plot_line(
    novel: NovelOwner,
    data: PlotLineCreate,
    db: DBSession
):
    """创建情节线"""
    planner = PlotPlanner(db, novel.id)
    plot_line = await planner.create_plot_line(data)
    
    return ApiResponse.success({
        "id": plot_line.id,
        "name": plot_line.name,
        "line_type": plot_line.line_type,
        "message": "情节线创建成功"
    })


@router.get("/plot-lines/{plot_line_id}")
async def get_plot_line(
    plot_line: PlotLine = Depends(check_plot_line_ownership)
):
    """获取情节线详情"""
    return ApiResponse.success(PlotLineResponse.model_validate(plot_line))


@router.put("/plot-lines/{plot_line_id}")
async def update_plot_line(
    data: PlotLineUpdate,
    db: DBSession,
    plot_line: PlotLine = Depends(check_plot_line_ownership)
):
    """更新情节线"""
    planner = PlotPlanner(db, plot_line.novel_id)
    updated = await planner.update_plot_line(plot_line.id, data)
    
    return ApiResponse.success({
        "id": updated.id,
        "name": updated.name,
        "message": "情节线更新成功"
    })


@router.delete("/plot-lines/{plot_line_id}")
async def delete_plot_line(
    db: DBSession,
    plot_line: PlotLine = Depends(check_plot_line_ownership)
):
    """删除情节线"""
    planner = PlotPlanner(db, plot_line.novel_id)
    await planner.delete_plot_line(plot_line.id)
    
    return ApiResponse.success({
        "message": "情节线已删除"
    })


@router.get("/novels/{novel_id}/plot-nodes")
async def list_plot_nodes(
    novel: NovelOwner,
    db: DBSession,
    plot_line_id: int = None,
    chapter_number: int = None,
    status: str = None
):
    """获取情节节点列表"""
    planner = PlotPlanner(db, novel.id)
    nodes = await planner.list_plot_nodes(
        plot_line_id=plot_line_id,
        chapter_number=chapter_number,
        status=status
    )
    
    return ApiResponse.success({
        "items": [PlotNodeResponse.model_validate(n) for n in nodes],
        "total": len(nodes)
    })


@router.post("/novels/{novel_id}/plot-nodes")
async def create_plot_node(
    novel: NovelOwner,
    data: PlotNodeCreate,
    db: DBSession
):
    """创建情节节点"""
    planner = PlotPlanner(db, novel.id)
    node = await planner.create_plot_node(data)
    
    return ApiResponse.success({
        "id": node.id,
        "title": node.title,
        "status": node.status,
        "message": "情节节点创建成功"
    })


@router.get("/plot-nodes/{node_id}")
async def get_plot_node(
    node: PlotNode = Depends(check_plot_node_ownership)
):
    """获取情节节点详情"""
    return ApiResponse.success(PlotNodeResponse.model_validate(node))


@router.put("/plot-nodes/{node_id}")
async def update_plot_node(
    data: PlotNodeUpdate,
    db: DBSession,
    node: PlotNode = Depends(check_plot_node_ownership)
):
    """更新情节节点"""
    planner = PlotPlanner(db, node.novel_id)
    updated = await planner.update_plot_node(node.id, data)
    
    return ApiResponse.success({
        "id": updated.id,
        "title": updated.title,
        "status": updated.status,
        "message": "情节节点更新成功"
    })


@router.delete("/plot-nodes/{node_id}")
async def delete_plot_node(
    db: DBSession,
    node: PlotNode = Depends(check_plot_node_ownership)
):
    """删除情节节点"""
    planner = PlotPlanner(db, node.novel_id)
    await planner.delete_plot_node(node.id)
    
    return ApiResponse.success({
        "message": "情节节点已删除"
    })


@router.post("/plot-nodes/{node_id}/complete")
async def complete_plot_node(
    db: DBSession,
    node: PlotNode = Depends(check_plot_node_ownership)
):
    """标记情节节点为完成"""
    node.status = PlotNodeStatus.COMPLETED.value
    await db.commit()
    
    return ApiResponse.success({
        "id": node.id,
        "status": node.status,
        "message": "情节节点已完成"
    })


@router.post("/novels/{novel_id}/suggestions")
async def generate_plot_suggestions(
    novel: NovelOwner,
    data: PlotSuggestionRequest,
    db: DBSession
):
    """生成情节建议"""
    planner = PlotPlanner(db, novel.id)
    result = await planner.generate_plot_suggestions(
        chapter_number=data.chapter_number,
        context=data.context,
        plot_line_id=data.plot_line_id
    )
    
    return ApiResponse.success(result)


@router.get("/novels/{novel_id}/progress")
async def get_plot_progress(
    novel: NovelOwner,
    db: DBSession
):
    """获取情节进度分析"""
    planner = PlotPlanner(db, novel.id)
    progress = await planner.get_plot_progress()
    
    return ApiResponse.success(progress)


@router.get("/novels/{novel_id}/chapters/{chapter_number}/nodes")
async def get_chapter_plot_nodes(
    novel: NovelOwner,
    chapter_number: int,
    db: DBSession
):
    """获取指定章节的情节节点"""
    planner = PlotPlanner(db, novel.id)
    nodes = await planner.get_nodes_by_chapter(chapter_number)
    
    return ApiResponse.success({
        "chapter_number": chapter_number,
        "nodes": [PlotNodeResponse.model_validate(n) for n in nodes],
        "total": len(nodes)
    })
