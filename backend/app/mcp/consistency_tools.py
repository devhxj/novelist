"""
一致性检查类MCP工具
提供一致性检查的标准接口
"""
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select, func
from datetime import datetime

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.novels.models import Novel
from app.chapters.models import Chapter
from app.characters.models import Character
from app.plot_events.models import PlotEvent
from app.foreshadowing.models import Foreshadowing, ForeshadowingStatus
from app.consistency.service import ConsistencyChecker


class CheckCharacterConsistencyTool(BaseMCPTool):
    """检查角色一致性"""
    
    name = "check_character_consistency"
    description = "检查小说中角色的性格、能力、关系是否前后一致"
    category = MCPToolCategory.CONSISTENCY_CHECK
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "chapter_ids": {"type": "array", "items": {"type": "integer"}, "description": "指定检查的章节ID列表（可选）"},
            "character_id": {"type": "integer", "description": "指定检查的角色ID（可选）"}
        },
        "required": ["novel_id"]
    }
    
    def __init__(self, db: AsyncSession):
        self.db = db
    
    async def execute(self, novel_id: int, chapter_ids: Optional[List[int]] = None, character_id: Optional[int] = None, **kwargs) -> MCPToolResult:
        result = await self.db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        
        try:
            checker = ConsistencyChecker(self.db, novel_id)
            
            query = select(Chapter).where(Chapter.novel_id == novel_id, Chapter.status == "completed")
            if chapter_ids:
                query = query.where(Chapter.id.in_(chapter_ids))
            query = query.order_by(Chapter.chapter_number)
            result = await self.db.execute(query)
            chapters = result.scalars().all()
            
            issues = await checker.check_character_consistency(chapters)
            
            if character_id:
                issues = [i for i in issues if i.details.get("character_id") == character_id]
            
            return MCPToolResult(
                success=True,
                data={"novel_id": novel_id, "check_type": "character", "issues": [issue.model_dump() for issue in issues], "total_issues": len(issues), "checked_chapters": len(chapters)},
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"Character consistency check failed: {str(e)}")


class CheckPlotConsistencyTool(BaseMCPTool):
    """检查情节一致性"""
    
    name = "check_plot_consistency"
    description = "检查小说情节发展的逻辑性、因果关系是否合理"
    category = MCPToolCategory.CONSISTENCY_CHECK
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "chapter_ids": {"type": "array", "items": {"type": "integer"}, "description": "指定检查的章节ID列表（可选）"}
        },
        "required": ["novel_id"]
    }
    
    def __init__(self, db: AsyncSession):
        self.db = db
    
    async def execute(self, novel_id: int, chapter_ids: Optional[List[int]] = None, **kwargs) -> MCPToolResult:
        result = await self.db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        
        try:
            checker = ConsistencyChecker(self.db, novel_id)
            
            query = select(Chapter).where(Chapter.novel_id == novel_id, Chapter.status == "completed")
            if chapter_ids:
                query = query.where(Chapter.id.in_(chapter_ids))
            query = query.order_by(Chapter.chapter_number)
            result = await self.db.execute(query)
            chapters = result.scalars().all()
            
            issues = await checker.check_plot_consistency(chapters)
            
            return MCPToolResult(
                success=True,
                data={"novel_id": novel_id, "check_type": "plot", "issues": [issue.model_dump() for issue in issues], "total_issues": len(issues), "checked_chapters": len(chapters)},
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"Plot consistency check failed: {str(e)}")


class ListUnresolvedPlotsTool(BaseMCPTool):
    """列出未解决的伏笔"""
    
    name = "list_unresolved_plots"
    description = "列出小说中所有未解决的伏笔（挖坑未填）"
    category = MCPToolCategory.CONSISTENCY_CHECK
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "min_importance": {"type": "integer", "description": "最小重要程度筛选（1-5）"},
            "days_pending": {"type": "integer", "description": "挂起天数筛选（超过指定天数）"}
        },
        "required": ["novel_id"]
    }
    
    def __init__(self, db: AsyncSession):
        self.db = db
    
    async def execute(self, novel_id: int, min_importance: Optional[int] = None, days_pending: Optional[int] = None, **kwargs) -> MCPToolResult:
        result = await self.db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        
        query = select(Foreshadowing).where(
            Foreshadowing.novel_id == novel_id,
            Foreshadowing.status == ForeshadowingStatus.UNRESOLVED.value
        )
        
        if min_importance:
            query = query.where(Foreshadowing.importance >= min_importance)
        
        query = query.order_by(Foreshadowing.importance.desc(), Foreshadowing.created_at.desc())
        result = await self.db.execute(query)
        foreshadowings = result.scalars().all()
        
        result_list = []
        for fs in foreshadowings:
            pending_days = (datetime.now() - fs.created_at).days if fs.created_at else 0
            
            if days_pending and pending_days < days_pending:
                continue
            
            created_chapter = None
            if fs.created_chapter_id:
                result = await self.db.execute(select(Chapter).where(Chapter.id == fs.created_chapter_id))
                chapter = result.scalar_one_or_none()
                if chapter:
                    created_chapter = {"id": chapter.id, "chapter_number": chapter.chapter_number, "title": chapter.title}
            
            result_list.append({
                "id": fs.id,
                "title": fs.title,
                "description": fs.description,
                "foreshadowing_type": fs.foreshadowing_type,
                "importance": fs.importance,
                "status": fs.status,
                "created_chapter": created_chapter,
                "created_at": fs.created_at.isoformat() if fs.created_at else None,
                "days_pending": pending_days,
                "metadata": fs.extra_metadata
            })
        
        return MCPToolResult(
            success=True,
            data={"novel_id": novel_id, "unresolved_plots": result_list, "total": len(result_list), "filters": {"min_importance": min_importance, "days_pending": days_pending}},
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class GetForeshadowingStatusTool(BaseMCPTool):
    """获取伏笔状态"""
    
    name = "get_foreshadowing_status"
    description = "获取小说伏笔的整体状态统计和详情"
    category = MCPToolCategory.CONSISTENCY_CHECK
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"}
        },
        "required": ["novel_id"]
    }
    
    def __init__(self, db: AsyncSession):
        self.db = db
    
    async def execute(self, novel_id: int, **kwargs) -> MCPToolResult:
        result = await self.db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        
        total_result = await self.db.execute(
            select(func.count()).where(Foreshadowing.novel_id == novel_id)
        )
        total = total_result.scalar()
        
        unresolved_result = await self.db.execute(
            select(func.count()).where(
                Foreshadowing.novel_id == novel_id,
                Foreshadowing.status == ForeshadowingStatus.UNRESOLVED.value
            )
        )
        unresolved = unresolved_result.scalar()
        
        resolved_result = await self.db.execute(
            select(func.count()).where(
                Foreshadowing.novel_id == novel_id,
                Foreshadowing.status == ForeshadowingStatus.RESOLVED.value
            )
        )
        resolved = resolved_result.scalar()
        
        abandoned_result = await self.db.execute(
            select(func.count()).where(
                Foreshadowing.novel_id == novel_id,
                Foreshadowing.status == ForeshadowingStatus.ABANDONED.value
            )
        )
        abandoned = abandoned_result.scalar()
        
        high_importance_result = await self.db.execute(
            select(Foreshadowing).where(
                Foreshadowing.novel_id == novel_id,
                Foreshadowing.status == ForeshadowingStatus.UNRESOLVED.value,
                Foreshadowing.importance >= 4
            )
        )
        high_importance_unresolved = high_importance_result.scalars().all()
        
        by_type = {}
        for status in [ForeshadowingStatus.UNRESOLVED.value, ForeshadowingStatus.RESOLVED.value, ForeshadowingStatus.ABANDONED.value]:
            count_result = await self.db.execute(
                select(func.count()).where(Foreshadowing.novel_id == novel_id, Foreshadowing.status == status)
            )
            by_type[status] = count_result.scalar()
        
        by_importance = {}
        for i in range(1, 6):
            count_result = await self.db.execute(
                select(func.count()).where(Foreshadowing.novel_id == novel_id, Foreshadowing.importance == i)
            )
            by_importance[str(i)] = count_result.scalar()
        
        high_priority_items = [
            {"id": fs.id, "title": fs.title, "importance": fs.importance, "days_pending": (datetime.now() - fs.created_at).days if fs.created_at else 0}
            for fs in high_importance_unresolved[:5]
        ]
        
        return MCPToolResult(
            success=True,
            data={
                "novel_id": novel_id,
                "statistics": {
                    "total": total,
                    "unresolved": unresolved,
                    "resolved": resolved,
                    "abandoned": abandoned,
                    "resolution_rate": round(resolved / total * 100, 1) if total > 0 else 0,
                    "by_status": by_type,
                    "by_importance": by_importance
                },
                "high_priority_unresolved": high_priority_items
            },
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class RunFullConsistencyCheckTool(BaseMCPTool):
    """执行完整一致性检查"""
    
    name = "run_full_consistency_check"
    description = "执行完整的一致性检查，包括角色、情节、时间线、伏笔"
    category = MCPToolCategory.CONSISTENCY_CHECK
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "chapter_ids": {"type": "array", "items": {"type": "integer"}, "description": "指定检查的章节ID列表（可选）"},
            "check_types": {"type": "array", "items": {"type": "string", "enum": ["character", "plot", "timeline", "foreshadowing"]}, "description": "检查类型列表"}
        },
        "required": ["novel_id"]
    }
    
    def __init__(self, db: AsyncSession):
        self.db = db
    
    async def execute(self, novel_id: int, chapter_ids: Optional[List[int]] = None, check_types: Optional[List[str]] = None, **kwargs) -> MCPToolResult:
        result = await self.db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        
        try:
            checker = ConsistencyChecker(self.db, novel_id)
            result = await checker.check_all(chapter_ids=chapter_ids, check_types=check_types)
            
            return MCPToolResult(
                success=True,
                data=result,
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"Full consistency check failed: {str(e)}")


class ConsistencyCheckTools:
    """一致性检查工具集合"""
    
    @staticmethod
    def register_all(db: AsyncSession, registry: MCPToolRegistry) -> None:
        """注册所有一致性检查工具"""
        registry.register(CheckCharacterConsistencyTool(db))
        registry.register(CheckPlotConsistencyTool(db))
        registry.register(ListUnresolvedPlotsTool(db))
        registry.register(GetForeshadowingStatusTool(db))
        registry.register(RunFullConsistencyCheckTool(db))
