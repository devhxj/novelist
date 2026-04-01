"""
记忆检索类MCP工具
提供记忆检索的标准接口
"""
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from sqlalchemy.orm import selectinload
from datetime import datetime

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.core.vector_store import vector_store, VectorStoreError
from app.core.context_builder import ContextBuilder
from app.novels.models import Novel
from app.chapters.models import Chapter
from app.characters.models import Character
from app.plot_events.models import PlotEvent


class SearchPlotMemoryTool(BaseMCPTool):
    """搜索情节记忆"""
    
    name = "search_plot_memory"
    description = "使用语义检索搜索小说中的情节记忆，返回相关内容片段。无需传novel_id，系统会注入当前小说ID。"
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "query": {"type": "string", "description": "搜索查询文本"},
            "top_k": {"type": "integer", "default": 10, "description": "返回结果数量"},
            "chapter_ids": {"type": "array", "items": {"type": "integer"}, "description": "限定章节ID列表（可选）"}
        },
        "required": ["novel_id", "query"]
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        query: str,
        top_k: int = 10,
        chapter_ids: Optional[List[int]] = None,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")
        
        try:
            filters = None
            if chapter_ids:
                filters = {"chapter_ids": chapter_ids}
            
            results = await vector_store.search(novel_id=novel_id, query=query, top_k=top_k, filters=filters)
            
            formatted_results = []
            for r in results:
                formatted_results.append({
                    "chunk_id": r["id"],
                    "content": r["content"],
                    "chapter_id": r["metadata"].get("chapter_id"),
                    "chapter_number": r["metadata"].get("chapter_number"),
                    "chapter_title": r["metadata"].get("chapter_title"),
                    "relevance_score": round(1 - r["distance"], 4)
                })
            
            return MCPToolResult(
                success=True,
                data={"query": query, "results": formatted_results, "total": len(formatted_results)},
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except VectorStoreError as e:
            return MCPToolResult(success=False, error=f"Search failed: {str(e)}")


class GetCharacterMemoryTool(BaseMCPTool):
    """获取角色记忆"""
    
    name = "get_character_memory"
    description = "获取指定角色在小说中的所有相关信息和出场记录。无需传novel_id，系统会注入当前小说ID。"
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "character_id": {"type": "integer", "description": "角色ID"},
            "include_plot_events": {"type": "boolean", "default": True, "description": "是否包含角色参与的情节事件"}
        },
        "required": ["novel_id", "character_id"]
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        character_id: int,
        include_plot_events: bool = True,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")
        
        result = await db.execute(
            select(Character).where(Character.id == character_id, Character.novel_id == novel_id)
        )
        character = result.scalar_one_or_none()
        if not character:
            return MCPToolResult(success=False, error=f"Character not found: {character_id}")
        
        memory = {
            "character": {
                "id": character.id,
                "name": character.name,
                "personality": character.personality,
                "abilities": character.abilities,
                "relationships": character.relationships
            }
        }
        
        if include_plot_events:
            result = await db.execute(
                select(PlotEvent).where(PlotEvent.novel_id == novel_id)
            )
            all_events = result.scalars().all()
            
            character_events = []
            for event in all_events:
                involved = event.characters_involved or []
                if character_id in involved:
                    character_events.append({
                        "id": event.id,
                        "event_type": event.event_type,
                        "description": event.description,
                        "chapter_id": event.chapter_id,
                        "timeline": event.timeline.isoformat() if event.timeline else None,
                        "consequences": event.consequences
                    })
            
            memory["plot_events"] = character_events
            memory["event_count"] = len(character_events)
        
        try:
            search_results = await vector_store.search(novel_id=novel_id, query=character.name, top_k=5)
            memory["relevant_content"] = [
                {"content": r["content"][:200] + "..." if len(r["content"]) > 200 else r["content"], "chapter_id": r["metadata"].get("chapter_id")}
                for r in search_results
            ]
        except VectorStoreError:
            memory["relevant_content"] = []
        
        return MCPToolResult(
            success=True,
            data=memory,
            metadata={"tool": self.name, "novel_id": novel_id, "character_id": character_id}
        )


class GetTimelineTool(BaseMCPTool):
    """获取时间线"""
    
    name = "get_timeline"
    description = "获取小说的情节时间线，按时间顺序排列事件"
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "start_chapter": {"type": "integer", "description": "起始章节号（可选）"},
            "end_chapter": {"type": "integer", "description": "结束章节号（可选）"},
            "event_types": {"type": "array", "items": {"type": "string"}, "description": "事件类型筛选（可选）"}
        },
        "required": ["novel_id"]
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        start_chapter: Optional[int] = None,
        end_chapter: Optional[int] = None,
        event_types: Optional[List[str]] = None,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")
        
        query = select(PlotEvent).where(PlotEvent.novel_id == novel_id)
        
        if start_chapter is not None:
            result = await db.execute(
                select(Chapter.id).where(Chapter.novel_id == novel_id, Chapter.chapter_number >= start_chapter)
            )
            chapter_ids = [r[0] for r in result.fetchall()]
            query = query.where(PlotEvent.chapter_id.in_(chapter_ids))
        
        if end_chapter is not None:
            result = await db.execute(
                select(Chapter.id).where(Chapter.novel_id == novel_id, Chapter.chapter_number <= end_chapter)
            )
            chapter_ids = [r[0] for r in result.fetchall()]
            query = query.where(PlotEvent.chapter_id.in_(chapter_ids))
        
        if event_types:
            query = query.where(PlotEvent.event_type.in_(event_types))
        
        query = query.order_by(PlotEvent.timeline)
        result = await db.execute(query)
        events = result.scalars().all()
        
        timeline = []
        for event in events:
            result = await db.execute(select(Chapter).where(Chapter.id == event.chapter_id))
            chapter = result.scalar_one_or_none()
            
            characters = []
            if event.characters_involved:
                for char_id in event.characters_involved:
                    result = await db.execute(select(Character).where(Character.id == char_id))
                    char = result.scalar_one_or_none()
                    if char:
                        characters.append({"id": char.id, "name": char.name})
            
            timeline.append({
                "id": event.id,
                "event_type": event.event_type,
                "description": event.description,
                "chapter": {"id": chapter.id if chapter else None, "chapter_number": chapter.chapter_number if chapter else None, "title": chapter.title if chapter else None},
                "characters_involved": characters,
                "timeline": event.timeline.isoformat() if event.timeline else None,
                "consequences": event.consequences
            })
        
        return MCPToolResult(
            success=True,
            data={"novel_id": novel_id, "timeline": timeline, "total_events": len(timeline), "filters": {"start_chapter": start_chapter, "end_chapter": end_chapter, "event_types": event_types}},
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class GetRecentContextTool(BaseMCPTool):
    """获取最近上下文"""
    
    name = "get_recent_context"
    description = "获取指定章节附近的写作上下文，包括前文摘要、角色信息、情节线索"
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "chapter_id": {"type": "integer", "description": "章节ID"},
            "window_size": {"type": "integer", "default": 3, "description": "前文章节数量"},
            "context_size": {"type": "integer", "default": 3000, "description": "上下文最大字符数"}
        },
        "required": ["novel_id", "chapter_id"]
    }
    
    def __init__(self):
        pass
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        chapter_id: int,
        window_size: int = 3,
        context_size: int = 3000,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")
        
        result = await db.execute(
            select(Chapter).where(Chapter.id == chapter_id, Chapter.novel_id == novel_id)
        )
        chapter = result.scalar_one_or_none()
        if not chapter:
            return MCPToolResult(success=False, error=f"Chapter not found: {chapter_id}")
        
        try:
            context_builder = ContextBuilder(db, novel_id)
            context = await context_builder.build_writing_context(
                chapter_id=chapter_id,
                context_size=context_size,
                include_previous_chapters=True,
                include_characters=True,
                include_plot_events=True
            )
            
            result = await db.execute(
                select(Chapter)
                .where(Chapter.novel_id == novel_id, Chapter.chapter_number < chapter.chapter_number, Chapter.status == "completed")
                .order_by(Chapter.chapter_number.desc())
                .limit(window_size)
            )
            previous_chapters = result.scalars().all()
            
            recent_chapters = [
                {"id": ch.id, "chapter_number": ch.chapter_number, "title": ch.title, "summary": ch.summary, "word_count": len(ch.content or "")}
                for ch in reversed(previous_chapters)
            ]
            
            return MCPToolResult(
                success=True,
                data={
                    "novel_id": novel_id,
                    "chapter_id": chapter_id,
                    "chapter_number": chapter.chapter_number,
                    "chapter_title": chapter.title,
                    "context": context.get("context", ""),
                    "context_length": context.get("context_length", 0),
                    "previous_summary": context.get("previous_summary"),
                    "characters": context.get("characters", []),
                    "plot_hints": context.get("plot_hints", []),
                    "recent_chapters": recent_chapters
                },
                metadata={"tool": self.name, "novel_id": novel_id, "chapter_id": chapter_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"Failed to build context: {str(e)}")


class MemoryRetrievalTools:
    """记忆检索工具集合"""
    
    @staticmethod
    def register_all(registry: MCPToolRegistry) -> None:
        """注册所有记忆检索工具"""
        registry.register(SearchPlotMemoryTool())
        registry.register(GetCharacterMemoryTool())
        registry.register(GetTimelineTool())
        registry.register(GetRecentContextTool())
