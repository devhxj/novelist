"""
记忆检索类MCP工具
提供记忆检索的标准接口
"""
from sqlalchemy.ext.asyncio import AsyncSession

from pydantic import BaseModel, Field

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from context.context_builder import ContextBuilder


class SearchStoryMemoryArgs(BaseModel):
    query: str = Field(description="检索问题或关键词")
    top_k: int = Field(default=5, description="返回结果数")
    min_relevance_score: float = Field(default=0.35, description="最低相关度阈值")


class SearchStoryMemoryTool(BaseMCPTool):
    """搜索故事记忆（聚合入口）"""

    name = "search_story_memory"
    description = (
        "搜索与当前创作最相关的故事记忆。"
        "这是给 LLM 用的高层检索入口，会优先返回更适合写作的片段。"
        "\n适用场景：写新章前回忆某个伏笔、某个情节节点、某个人物最近发生过什么。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    args_schema = SearchStoryMemoryArgs

    async def _execute(
        self,
        args: SearchStoryMemoryArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        try:
            builder = ContextBuilder(db, novel_id)
            results = await builder.search_relevant_context(
                query=args.query,
                top_k=args.top_k,
                min_relevance_score=args.min_relevance_score
            )
            return MCPToolResult(
                success=True,
                data={
                    "query": args.query,
                    "results": results,
                    "total": len(results)
                },
                metadata={"tool": self.name, "novel_id": novel_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"Search failed: {str(e)}")


class MemoryRetrievalTools:
    """记忆检索工具集合"""
    
    @staticmethod
    def register_all(registry: MCPToolRegistry) -> None:
        """注册所有记忆检索工具"""
        registry.register(SearchStoryMemoryTool())

