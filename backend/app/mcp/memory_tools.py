"""
记忆检索类MCP工具
提供记忆检索的标准接口
"""
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.core.vector_store import vector_store, VectorStoreError
from app.core.context_builder import ContextBuilder
from app.core.text_utils import count_words
from app.chapters.models import Chapter
from app.characters.models import Character
from app.core.permissions import verify_novel_ownership


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


class GetCharacterMemoryTool(BaseMCPTool):
    """获取角色记忆"""
    
    name = "get_character_memory"
    description = (
        "获取指定角色在小说中的所有相关信息和出场记录（动态信息）。"
        "无需传novel_id，系统会注入当前小说ID。"
        "\n与 get_character_detail 不同，此工具不仅返回静态档案，还返回该角色相关的正文片段。"
        "\n适用场景：写某个角色的戏份前调用，了解他/她最近做了什么、经历了什么。"
        "\n参数说明：character_id 为必填项，需先从 get_character_list 获取。"
        "\n💡 提示：get_writing_characters 已包含各角色的最近动态，可优先使用。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", "description": "小说ID"},
            "character_id": {"type": "integer", "description": "角色ID"},
        },
        "required": ["novel_id", "character_id"]
    }

    def __init__(self):
        pass

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        character_id: int,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

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


class PrepareStoryBriefTool(BaseMCPTool):
    """构建写前 StoryBrief"""

    name = "prepare_story_brief"
    description = (
        "为指定章节构建写前 StoryBrief。"
        "会明确区分 Plot（情节骨架）、Timeline（近期安排/用户指令）、Foreshadowing（伏笔钩子）。无需传novel_id。"
        "\n适用场景：正式创作前先快速建立全局认知，确认本章该推进什么、回收什么、是否需要埋新伏笔。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    parameters_schema = {
        "type": "object",
        "properties": {
            "chapter_number": {"type": "integer", "description": "目标章节号"},
            "context_size": {"type": "integer", "default": 3600, "description": "上下文大小"},
            "retrieval_top_k": {"type": "integer", "default": 3, "description": "每个检索问题返回数量"}
        },
        "required": ["chapter_number"]
    }

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        chapter_number: int,
        context_size: int = 3600,
        retrieval_top_k: int = 3,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

        try:
            builder = ContextBuilder(db, novel_id)
            brief = await builder.build_story_brief(
                chapter_number=chapter_number,
                context_size=context_size,
                retrieval_top_k=retrieval_top_k
            )
            return MCPToolResult(
                success=True,
                data=brief,
                metadata={"tool": self.name, "novel_id": novel_id, "chapter_number": chapter_number}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"Failed to build StoryBrief: {str(e)}")


class MemoryRetrievalTools:
    """记忆检索工具集合"""
    
    @staticmethod
    def register_all(registry: MCPToolRegistry) -> None:
        """注册所有记忆检索工具"""
        registry.register(SearchPlotMemoryTool())
        registry.register(SearchStoryMemoryTool())
        registry.register(GetCharacterMemoryTool())
        registry.register(GetRecentContextTool())
        registry.register(PrepareStoryBriefTool())
