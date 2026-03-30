"""
情节规划服务
"""
import logging
import json
from typing import Dict, Any, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from app.planning.models import (
    PlotLine,
    PlotNode,
    PlotOutline,
    PlotLineType,
    PlotNodeStatus
)
from app.planning.schemas import (
    PlotLineCreate,
    PlotLineUpdate,
    PlotNodeCreate,
    PlotNodeUpdate,
    PlotOutlineCreate,
    PlotOutlineUpdate
)
from app.core.llm_service import llm_service

logger = logging.getLogger(__name__)


class PlotPlanner:
    """情节规划服务"""
    
    def __init__(self, db: AsyncSession, novel_id: int):
        self.db = db
        self.novel_id = novel_id
    
    async def create_plot_line(self, data: PlotLineCreate) -> PlotLine:
        """创建情节线"""
        plot_line = PlotLine(
            novel_id=self.novel_id,
            name=data.name,
            description=data.description,
            line_type=data.line_type.value,
            start_chapter=data.start_chapter,
            end_chapter=data.end_chapter,
            importance=data.importance,
            metadata=data.metadata
        )
        self.db.add(plot_line)
        await self.db.commit()
        await self.db.refresh(plot_line)
        return plot_line
    
    async def get_plot_line(self, plot_line_id: int) -> Optional[PlotLine]:
        """获取情节线"""
        result = await self.db.execute(
            select(PlotLine).where(
                PlotLine.id == plot_line_id,
                PlotLine.novel_id == self.novel_id
            )
        )
        return result.scalar_one_or_none()
    
    async def list_plot_lines(
        self,
        line_type: Optional[str] = None,
        status: Optional[str] = None
    ) -> List[PlotLine]:
        """获取情节线列表"""
        query = select(PlotLine).where(
            PlotLine.novel_id == self.novel_id
        )
        
        if line_type:
            query = query.where(PlotLine.line_type == line_type)
        if status:
            query = query.where(PlotLine.status == status)
        
        query = query.order_by(PlotLine.importance.desc(), PlotLine.created_at)
        result = await self.db.execute(query)
        return list(result.scalars().all())
    
    async def update_plot_line(self, plot_line_id: int, data: PlotLineUpdate) -> Optional[PlotLine]:
        """更新情节线"""
        plot_line = await self.get_plot_line(plot_line_id)
        if not plot_line:
            return None
        
        update_data = data.model_dump(exclude_unset=True)
        for key, value in update_data.items():
            if key == "line_type" and value:
                value = value.value
            setattr(plot_line, key, value)
        
        await self.db.commit()
        await self.db.refresh(plot_line)
        return plot_line
    
    async def delete_plot_line(self, plot_line_id: int) -> bool:
        """删除情节线"""
        plot_line = await self.get_plot_line(plot_line_id)
        if not plot_line:
            return False
        
        await self.db.delete(plot_line)
        await self.db.commit()
        return True
    
    async def create_plot_node(self, data: PlotNodeCreate) -> PlotNode:
        """创建情节节点"""
        plot_node = PlotNode(
            plot_line_id=data.plot_line_id,
            novel_id=self.novel_id,
            title=data.title,
            description=data.description,
            chapter_number=data.chapter_number,
            sequence=data.sequence,
            characters_involved=data.characters_involved,
            prerequisites=data.prerequisites,
            consequences=data.consequences,
            notes=data.notes,
            extra_metadata=data.metadata
        )
        self.db.add(plot_node)
        await self.db.commit()
        await self.db.refresh(plot_node)
        return plot_node
    
    async def get_plot_node(self, node_id: int) -> Optional[PlotNode]:
        """获取情节节点"""
        result = await self.db.execute(
            select(PlotNode).where(
                PlotNode.id == node_id,
                PlotNode.novel_id == self.novel_id
            )
        )
        return result.scalar_one_or_none()
    
    async def list_plot_nodes(
        self,
        plot_line_id: Optional[int] = None,
        chapter_number: Optional[int] = None,
        status: Optional[str] = None
    ) -> List[PlotNode]:
        """获取情节节点列表"""
        query = select(PlotNode).where(
            PlotNode.novel_id == self.novel_id
        )
        
        if plot_line_id:
            query = query.where(PlotNode.plot_line_id == plot_line_id)
        if chapter_number:
            query = query.where(PlotNode.chapter_number == chapter_number)
        if status:
            query = query.where(PlotNode.status == status)
        
        query = query.order_by(PlotNode.chapter_number, PlotNode.sequence)
        result = await self.db.execute(query)
        return list(result.scalars().all())
    
    async def update_plot_node(self, node_id: int, data: PlotNodeUpdate) -> Optional[PlotNode]:
        """更新情节节点"""
        plot_node = await self.get_plot_node(node_id)
        if not plot_node:
            return None
        
        update_data = data.model_dump(exclude_unset=True)
        for key, value in update_data.items():
            if key == "status" and value:
                value = value.value
            setattr(plot_node, key, value)
        
        await self.db.commit()
        await self.db.refresh(plot_node)
        return plot_node
    
    async def delete_plot_node(self, node_id: int) -> bool:
        """删除情节节点"""
        plot_node = await self.get_plot_node(node_id)
        if not plot_node:
            return False
        
        await self.db.delete(plot_node)
        await self.db.commit()
        return True
    
    async def get_nodes_by_chapter(self, chapter_number: int) -> List[PlotNode]:
        """获取指定章节的所有情节节点"""
        result = await self.db.execute(
            select(PlotNode).where(
                PlotNode.novel_id == self.novel_id,
                PlotNode.chapter_number == chapter_number
            ).order_by(PlotNode.sequence)
        )
        return list(result.scalars().all())
    
    async def get_next_nodes(self, current_node_id: int) -> List[PlotNode]:
        """获取下一个情节节点（基于前置依赖）"""
        result = await self.db.execute(
            select(PlotNode).where(
                PlotNode.novel_id == self.novel_id,
                PlotNode.prerequisites.contains([current_node_id]),
                PlotNode.status == PlotNodeStatus.PLANNED.value
            )
        )
        return list(result.scalars().all())
    
    async def create_or_update_outline(self, data: PlotOutlineCreate) -> PlotOutline:
        """创建或更新情节大纲"""
        result = await self.db.execute(
            select(PlotOutline).where(
                PlotOutline.novel_id == self.novel_id
            )
        )
        outline = result.scalar_one_or_none()
        
        if outline:
            update_data = data.model_dump(exclude_unset=True)
            for key, value in update_data.items():
                setattr(outline, key, value)
        else:
            outline = PlotOutline(
                novel_id=self.novel_id,
                **data.model_dump()
            )
            self.db.add(outline)
        
        await self.db.commit()
        await self.db.refresh(outline)
        return outline
    
    async def get_outline(self) -> Optional[PlotOutline]:
        """获取情节大纲"""
        result = await self.db.execute(
            select(PlotOutline).where(
                PlotOutline.novel_id == self.novel_id
            )
        )
        return result.scalar_one_or_none()
    
    async def update_outline(self, data: PlotOutlineUpdate) -> Optional[PlotOutline]:
        """更新情节大纲"""
        outline = await self.get_outline()
        if not outline:
            return None
        
        update_data = data.model_dump(exclude_unset=True)
        for key, value in update_data.items():
            setattr(outline, key, value)
        
        await self.db.commit()
        await self.db.refresh(outline)
        return outline
    
    async def generate_plot_suggestions(
        self,
        chapter_number: int,
        context: str,
        plot_line_id: Optional[int] = None
    ) -> Dict[str, Any]:
        """
        生成情节建议
        
        Args:
            chapter_number: 目标章节号
            context: 当前上下文
            plot_line_id: 情节线ID（可选）
            
        Returns:
            情节建议
        """
        outline = await self.get_outline()
        plot_lines = await self.list_plot_lines()
        
        outline_info = ""
        if outline:
            outline_info = f"""
故事前提: {outline.premise or '未设定'}
主题: {outline.theme or '未设定'}
开端: {outline.beginning or '未设定'}
发展: {outline.middle or '未设定'}
高潮: {outline.climax or '未设定'}
结局: {outline.ending or '未设定'}
"""
        
        plot_lines_info = ""
        for pl in plot_lines[:5]:
            nodes_result = await self.db.execute(
                select(PlotNode).where(
                    PlotNode.plot_line_id == pl.id,
                    PlotNode.status != PlotNodeStatus.SKIPPED.value
                ).order_by(PlotNode.sequence).limit(5)
            )
            nodes = list(nodes_result.scalars().all())
            
            nodes_info = "\n".join([
                f"  - 第{n.chapter_number or '?'}章: {n.title} ({n.status})"
                for n in nodes
            ])
            
            plot_lines_info += f"""
情节线: {pl.name} ({pl.line_type})
状态: {pl.status}
节点:
{nodes_info}
"""
        
        target_plot_line = None
        if plot_line_id:
            target_plot_line = await self.get_plot_line(plot_line_id)
        
        prompt = f"""请为以下小说生成第{chapter_number}章的情节建议。

故事大纲:
{outline_info}

现有情节线:
{plot_lines_info}

当前上下文:
{context[:2000]}

{"目标情节线: " + target_plot_line.name if target_plot_line else ""}

请提供:
1. 本章应该推进的情节线
2. 建议的情节节点（2-3个）
3. 涉及的角色
4. 潜在的冲突点
5. 伏笔建议（如果有）

以JSON格式返回:
{{
    "suggested_plot_line": "情节线名称",
    "plot_nodes": [
        {{
            "title": "节点标题",
            "description": "节点描述",
            "chapter_number": 章节号,
            "characters_involved": ["角色1", "角色2"]
        }}
    ],
    "conflicts": ["冲突点1", "冲突点2"],
    "foreshadowing_suggestions": [
        {{
            "content": "伏笔内容",
            "type": "挖坑/填坑"
        }}
    ],
    "reasoning": "推理过程"
}}
"""
        
        try:
            result = await llm_service.generate_text(prompt)
            
            try:
                data = json.loads(result)
                return {
                    "success": True,
                    "suggestions": data
                }
            except json.JSONDecodeError:
                logger.warning(f"Failed to parse plot suggestions: {result[:200]}")
                return {
                    "success": False,
                    "error": "无法解析建议结果",
                    "raw_response": result[:500]
                }
                
        except Exception as e:
            logger.error(f"Failed to generate plot suggestions: {e}")
            return {
                "success": False,
                "error": str(e)
            }
    
    async def get_plot_progress(self) -> Dict[str, Any]:
        """获取情节进度分析"""
        plot_lines = await self.list_plot_lines()
        outline = await self.get_outline()
        
        total_nodes = 0
        completed_nodes = 0
        in_progress_nodes = 0
        planned_nodes = 0
        
        plot_lines_progress = []
        
        for pl in plot_lines:
            nodes_result = await self.db.execute(
                select(PlotNode).where(
                    PlotNode.plot_line_id == pl.id
                )
            )
            nodes = list(nodes_result.scalars().all())
            
            pl_completed = sum(1 for n in nodes if n.status == PlotNodeStatus.COMPLETED.value)
            pl_in_progress = sum(1 for n in nodes if n.status == PlotNodeStatus.IN_PROGRESS.value)
            pl_planned = sum(1 for n in nodes if n.status == PlotNodeStatus.PLANNED.value)
            pl_total = len(nodes)
            
            total_nodes += pl_total
            completed_nodes += pl_completed
            in_progress_nodes += pl_in_progress
            planned_nodes += pl_planned
            
            plot_lines_progress.append({
                "id": pl.id,
                "name": pl.name,
                "line_type": pl.line_type,
                "total_nodes": pl_total,
                "completed": pl_completed,
                "in_progress": pl_in_progress,
                "planned": pl_planned,
                "progress_percentage": round(pl_completed / pl_total * 100, 1) if pl_total > 0 else 0
            })
        
        return {
            "outline": {
                "exists": outline is not None,
                "total_chapters": outline.total_chapters if outline else None,
                "current_chapter": outline.current_chapter if outline else 1
            },
            "plot_lines": {
                "total": len(plot_lines),
                "main": sum(1 for pl in plot_lines if pl.line_type == PlotLineType.MAIN.value),
                "sub": sum(1 for pl in plot_lines if pl.line_type == PlotLineType.SUB.value),
                "character": sum(1 for pl in plot_lines if pl.line_type == PlotLineType.CHARACTER.value)
            },
            "nodes": {
                "total": total_nodes,
                "completed": completed_nodes,
                "in_progress": in_progress_nodes,
                "planned": planned_nodes,
                "completion_rate": round(completed_nodes / total_nodes * 100, 1) if total_nodes > 0 else 0
            },
            "plot_lines_detail": plot_lines_progress
        }
