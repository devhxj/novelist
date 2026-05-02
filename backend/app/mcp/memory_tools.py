"""
记忆检索类MCP工具
提供记忆检索的标准接口
"""
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.core.vector_store import vector_store, VectorStoreError
from app.core.text_utils import count_words
from app.chapters.models import Chapter
from app.characters.models import Character
from app.core.permissions import verify_novel_ownership
from app.core.context_builder import ContextBuilder


class SearchPlotMemoryTool(BaseMCPTool):
    """搜索情节记忆"""
    
    name = "search_plot_memory"
    description = "使用语义检索搜索小说中的情节记忆，返回相关内容片段。无需传novel_id，系统会注入当前小说ID。"
    category = MCPToolCategory.MEMORY_RETRIEVAL
    expose_to_llm = False
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
        user_id: int,
        query: str,
        top_k: int = 10,
        chapter_ids: Optional[List[int]] = None,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
        
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


class SearchStoryMemoryTool(BaseMCPTool):
    """搜索故事记忆（聚合入口）"""

    name = "search_story_memory"
    description = (
        "搜索与当前创作最相关的故事记忆。"
        "这是给 LLM 用的高层检索入口，会优先返回更适合写作的片段。无需传novel_id。"
        "\n适用场景：写新章前回忆某个伏笔、某个情节节点、某个人物最近发生过什么。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "query": {"type": "string", "description": "检索问题或关键词"},
            "top_k": {"type": "integer", "default": 5, "description": "返回结果数"},
            "min_relevance_score": {"type": "number", "default": 0.35, "description": "最低相关度阈值"}
        },
        "required": ["query"]
    }

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        query: str,
        top_k: int = 5,
        min_relevance_score: float = 0.35,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

        try:
            builder = ContextBuilder(db, novel_id)
            results = await builder.search_relevant_context(
                query=query,
                top_k=top_k,
                min_relevance_score=min_relevance_score
            )
            return MCPToolResult(
                success=True,
                data={
                    "query": query,
                    "results": results,
                    "total": len(results)
                },
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"Search failed: {str(e)}")


class GetRecentContextTool(BaseMCPTool):
    """获取最近上下文"""
    
    name = "get_recent_context"
    description = "获取指定章节附近的写作上下文，包括前文摘要、角色信息、情节线索"
    category = MCPToolCategory.MEMORY_RETRIEVAL
    expose_to_llm = False
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
        user_id: int,
        chapter_id: int,
        window_size: int = 3,
        context_size: int = 3000,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
        
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
            )
            
            result = await db.execute(
                select(Chapter)
                .where(Chapter.novel_id == novel_id, Chapter.chapter_number < chapter.chapter_number, Chapter.status == "completed")
                .order_by(Chapter.chapter_number.desc())
                .limit(window_size)
            )
            previous_chapters = result.scalars().all()
            
            recent_chapters = [
                {"id": ch.id, "chapter_number": ch.chapter_number, "title": ch.title, "summary": ch.summary, "word_count": count_words(ch.content or "")}
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
        registry.register(SearchStoryMemoryTool())
        registry.register(GetRecentContextTool())

