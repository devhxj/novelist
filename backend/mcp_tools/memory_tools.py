"""
记忆检索类MCP工具
提供记忆检索的标准接口
"""
import logging

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from pydantic import BaseModel, Field

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from characters.models import Character
from characters.service import CharacterService
from context.context_builder import ContextBuilder
from core.redis_service import NovelCache

logger = logging.getLogger(__name__)


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


class GetCharacterMemoryArgs(BaseModel):
    character_id: int | None = Field(default=None, description="角色ID。与 character_name 至少提供一个。")
    character_name: str | None = Field(default=None, description="角色名称。与 character_id 至少提供一个。")
    query: str | None = Field(default=None, description="可选：聚焦某类记忆，例如'和师父的冲突'、'最近一次出场'。")
    top_k: int = Field(default=8, description="返回相关记忆片段数量")
    min_relevance_score: float = Field(default=0.35, description="最低相关度阈值")
    include_relations: bool = Field(default=True, description="是否附带该角色当前关系网络")


class GetCharacterMemoryTool(BaseMCPTool):
    """按角色聚合查询记忆"""

    name = "get_character_memory"
    description = (
        "按角色聚合查询故事记忆，返回该角色的基础档案、关系网络、相关章节和关键记忆片段。"
        "\n适用场景：想系统回忆某个角色经历过什么、和谁互动过、最近处于什么状态。"
        "\n参数：优先传 character_id；不知道ID时也可传 character_name。query 可进一步限定记忆范围。"
    )
    category = MCPToolCategory.MEMORY_RETRIEVAL
    args_schema = GetCharacterMemoryArgs

    async def _execute(
        self,
        args: GetCharacterMemoryArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        character = await self._resolve_character(db, novel_id, args.character_id, args.character_name)
        if isinstance(character, MCPToolResult):
            return character

        use_cache = (
            not args.query
            and args.top_k == 8
            and abs(args.min_relevance_score - 0.35) < 1e-9
            and args.include_relations
        )
        if use_cache:
            cached = await NovelCache.get_character_memory(character.id)
            if cached:
                return MCPToolResult(
                    success=True,
                    data=cached,
                    metadata={"tool": self.name, "novel_id": novel_id, "character_id": character.id, "cache_hit": True},
                )

        search_query = (args.query or "").strip() or f"{character.name} 的经历 事件 互动 出场"
        builder = ContextBuilder(db, novel_id)
        raw_memories = await builder.search_relevant_context(
            query=search_query,
            top_k=args.top_k,
            min_relevance_score=args.min_relevance_score,
        )
        memories = self._normalize_memories(character.name, raw_memories)
        involved_chapters = self._build_involved_chapters(memories)

        relations: list[dict] = []
        if args.include_relations:
            try:
                service = CharacterService(db, novel_id)
                relations = await service.get_character_relationships(character.id, include_inactive=False)
            except Exception:
                logger.warning("Failed to load character relations for memory view", exc_info=True)

        payload = {
            "query": search_query,
            "character": {
                "id": character.id,
                "name": character.name,
                "personality": character.personality,
                "abilities": character.abilities or [],
                "relationships": character.relationships,
                "created_at": character.created_at.isoformat() if character.created_at else None,
            },
            "relations": relations,
            "memories": memories,
            "involved_chapters": involved_chapters,
            "total_memories": len(memories),
        }

        if use_cache:
            await NovelCache.set_character_memory(character.id, payload)

        return MCPToolResult(
            success=True,
            data=payload,
            metadata={"tool": self.name, "novel_id": novel_id, "character_id": character.id},
        )

    async def _resolve_character(
        self,
        db: AsyncSession,
        novel_id: int,
        character_id: int | None,
        character_name: str | None,
    ) -> Character | MCPToolResult:
        if character_id is not None:
            result = await db.execute(
                select(Character).where(
                    Character.id == character_id,
                    Character.novel_id == novel_id,
                )
            )
            character = result.scalar_one_or_none()
            if not character:
                return MCPToolResult(success=False, error=f"角色不存在: {character_id}")
            return character

        if not character_name or not character_name.strip():
            return MCPToolResult(success=False, error="character_id 和 character_name 至少提供一个")

        normalized_name = character_name.strip()
        result = await db.execute(
            select(Character).where(Character.novel_id == novel_id)
        )
        characters = result.scalars().all()
        exact = [c for c in characters if c.name == normalized_name]
        if len(exact) == 1:
            return exact[0]

        partial = [c for c in characters if normalized_name.lower() in c.name.lower()]
        if len(partial) == 1:
            return partial[0]
        if len(exact) > 1 or len(partial) > 1:
            candidates = exact if len(exact) > 1 else partial
            names = "、".join(c.name for c in candidates[:8])
            return MCPToolResult(success=False, error=f"匹配到多个角色，请改用 character_id。候选：{names}")
        return MCPToolResult(success=False, error=f"未找到角色：{normalized_name}")

    @staticmethod
    def _normalize_memories(character_name: str, memories: list[dict]) -> list[dict]:
        normalized: list[dict] = []
        seen_chunks: set[str] = set()
        for item in memories:
            chunk_id = str(item.get("chunk_id") or "")
            if chunk_id and chunk_id in seen_chunks:
                continue
            if chunk_id:
                seen_chunks.add(chunk_id)

            chapter = item.get("chapter") or {}
            content = str(item.get("content") or "").strip()
            excerpt = content[:280] + "..." if len(content) > 280 else content
            normalized.append({
                "chunk_id": item.get("chunk_id"),
                "excerpt": excerpt,
                "relevance_score": item.get("relevance_score"),
                "source_type": item.get("source_type"),
                "chapter_id": item.get("source_id"),
                "chapter_number": chapter.get("chapter_number"),
                "chapter_title": chapter.get("title"),
                "chapter_summary": chapter.get("summary"),
                "mentions_character_name": character_name in content,
            })
        normalized.sort(
            key=lambda m: (
                0 if m.get("mentions_character_name") else 1,
                -(m.get("relevance_score") or 0),
                m.get("chapter_number") or 999999,
            )
        )
        return normalized

    @staticmethod
    def _build_involved_chapters(memories: list[dict]) -> list[dict]:
        chapter_map: dict[int, dict] = {}
        for item in memories:
            chapter_id = item.get("chapter_id")
            if not chapter_id or chapter_id in chapter_map:
                continue
            chapter_map[chapter_id] = {
                "chapter_id": chapter_id,
                "chapter_number": item.get("chapter_number"),
                "chapter_title": item.get("chapter_title"),
                "chapter_summary": item.get("chapter_summary"),
            }
        return sorted(
            chapter_map.values(),
            key=lambda chapter: (
                chapter.get("chapter_number") is None,
                chapter.get("chapter_number") or 999999,
                chapter.get("chapter_id") or 999999,
            ),
        )


def register_memory_tools(registry: MCPToolRegistry) -> None:
    registry.register(SearchStoryMemoryTool())
    registry.register(GetCharacterMemoryTool())
