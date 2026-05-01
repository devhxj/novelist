"""
小说管理类MCP工具
提供小说信息查询的标准接口
"""
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from sqlalchemy.orm import selectinload
from sqlalchemy.exc import IntegrityError

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.novels.models import Novel, NovelCreativeProfile
from app.chapters.models import Chapter
from app.core.text_utils import count_words
from app.characters.models import Character
from app.generation.service import ChapterGenerationService
from app.core.permissions import verify_novel_ownership


async def _invalidate_novel_cache(novel_id: int) -> None:
    try:
        from app.core.redis_service import redis_service
        from app.core.context_builder import context_cache
        await redis_service.clear_pattern(f"novel:{novel_id}:*")
        context_cache.invalidate_novel(novel_id)
    except Exception:
        pass


async def _invalidate_character_cache(novel_id: int, character_id: int | None = None) -> None:
    try:
        from app.core.redis_service import redis_service
        if character_id:
            await redis_service.delete(f"character:{character_id}:detail")
        await redis_service.clear_pattern(f"novel:{novel_id}:characters:*")
    except Exception:
        pass
    await _invalidate_novel_cache(novel_id)


async def _invalidate_chapter_cache(novel_id: int, chapter_id: int | None = None) -> None:
    try:
        from app.core.redis_service import redis_service
        if chapter_id:
            await redis_service.delete(f"chapter:{chapter_id}:detail")
        await redis_service.clear_pattern(f"novel:{novel_id}:chapters:*")
    except Exception:
        pass
    await _invalidate_novel_cache(novel_id)


def _build_creative_profile_summary(
    author_intent: Optional[str] = None,
    preferred_tone: Optional[str] = None,
    scene_planning_notes: Optional[str] = None,
    must_keep: Optional[List[str]] = None,
    must_avoid: Optional[List[str]] = None,
    long_term_goals: Optional[List[str]] = None
) -> str:
    parts: List[str] = []
    if author_intent:
        parts.append(f"长期意图：{author_intent.strip()}")
    if preferred_tone:
        parts.append(f"默认语气：{preferred_tone.strip()}")
    if scene_planning_notes:
        parts.append(f"规划备注：{scene_planning_notes.strip()}")
    if must_keep:
        parts.append("必须保留：" + "；".join(str(item).strip() for item in must_keep[:5] if str(item).strip()))
    if must_avoid:
        parts.append("必须避免：" + "；".join(str(item).strip() for item in must_avoid[:5] if str(item).strip()))
    if long_term_goals:
        parts.append("长线目标：" + "；".join(str(item).strip() for item in long_term_goals[:5] if str(item).strip()))
    return "\n".join(parts[:6])


def _attach_profile_summary(extra_metadata: Optional[Dict[str, Any]], summary: str) -> Dict[str, Any]:
    merged = dict(extra_metadata or {})
    if summary.strip():
        merged["llm_brief"] = summary.strip()
    return merged


class GetNovelSummaryTool(BaseMCPTool):
    """获取小说整体摘要"""
    
    name = "get_novel_summary"
    description = "获取小说的整体摘要信息，包括标题、类型、描述、状态、章节数、字数、角色数等。无需传novel_id，系统会注入当前小说ID。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {},
        "required": []
    }
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
        
        result = await db.execute(
            select(Novel)
            .options(selectinload(Novel.chapters), selectinload(Novel.characters))
            .where(Novel.id == novel_id)
        )
        novel = result.scalar_one_or_none()
        
        chapters = novel.chapters
        characters = novel.characters
        total_words = sum(len(ch.content or "") for ch in chapters)
        completed_chapters = len([ch for ch in chapters if ch.status == "completed"])
        
        summary = {
            "id": novel.id,
            "title": novel.title,
            "genre": novel.genre,
            "description": novel.description,
            "status": novel.status,
            "chapter_count": len(chapters),
            "completed_chapters": completed_chapters,
            "word_count": total_words,
            "character_count": len(characters),
            "created_at": novel.created_at.isoformat() if novel.created_at else None,
            "updated_at": novel.updated_at.isoformat() if novel.updated_at else None
        }
        
        return MCPToolResult(
            success=True,
            data=summary,
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class GetChapterListTool(BaseMCPTool):
    """获取章节列表"""
    
    name = "get_chapter_list"
    description = "获取小说的章节列表，支持分页和状态筛选。无需传novel_id，系统会注入当前小说ID。返回可用于 edit_chapter 的 chapter_id。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "status": {
                "type": "string",
                "enum": ["draft", "completed"],
                "description": "章节状态筛选（可选）"
            },
            "page": {
                "type": "integer",
                "default": 1,
                "description": "页码"
            },
            "page_size": {
                "type": "integer",
                "default": 20,
                "description": "每页数量"
            }
        },
        "required": []
    }
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        status: Optional[str] = None,
        page: int = 1,
        page_size: int = 20,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
        
        query = select(Chapter).where(Chapter.novel_id == novel_id)
        
        if status:
            query = query.filter(Chapter.status == status)
        
        from sqlalchemy import func
        count_query = select(func.count()).select_from(query.subquery())
        total_result = await db.execute(count_query)
        total = total_result.scalar()
        
        query = query.order_by(Chapter.chapter_number).offset((page - 1) * page_size).limit(page_size)
        result = await db.execute(query)
        chapters = result.scalars().all()
        
        items = [
            {
                "id": ch.id,
                "chapter_number": ch.chapter_number,
                "title": ch.title,
                "word_count": count_words(ch.content or ""),
                "status": ch.status,
                "summary": ch.summary,
                "created_at": ch.created_at.isoformat() if ch.created_at else None,
                "updated_at": ch.updated_at.isoformat() if ch.updated_at else None
            }
            for ch in chapters
        ]
        
        return MCPToolResult(
            success=True,
            data={
                "items": items,
                "total": total,
                "page": page,
                "page_size": page_size,
                "total_pages": (total + page_size - 1) // page_size
            },
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class GetChapterContentTool(BaseMCPTool):
    """获取章节内容"""
    
    name = "get_chapter_content"
    description = "获取指定章节的完整内容。可以通过章节号或章节ID获取。如果不提供chapter_id，则返回第一章的内容。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "chapter_id": {
                "type": "integer",
                "description": "章节ID（可选，不提供则返回第一章）"
            },
            "chapter_number": {
                "type": "integer",
                "description": "章节号（可选）"
            },
            "include_summary": {
                "type": "boolean",
                "default": True,
                "description": "是否包含摘要"
            },
            "include_lines": {
                "type": "boolean",
                "default": False,
                "description": "是否返回带行号的行数组（用于按行号编辑）"
            }
        },
        "required": []
    }

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        chapter_id: Optional[int] = None,
        chapter_number: Optional[int] = None,
        include_summary: bool = True,
        include_lines: bool = False,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
        
        if not chapter_id and not chapter_number:
            result = await db.execute(
                select(Chapter).where(Chapter.novel_id == novel_id).order_by(Chapter.chapter_number).limit(1)
            )
            chapter = result.scalar_one_or_none()
        else:
            query = select(Chapter).where(Chapter.novel_id == novel_id)
            if chapter_id:
                query = query.where(Chapter.id == chapter_id)
            elif chapter_number:
                query = query.where(Chapter.chapter_number == chapter_number)
            
            result = await db.execute(query)
            chapter = result.scalar_one_or_none()
        
        if not chapter:
            return MCPToolResult(
                success=False,
                error=f"Chapter not found"
            )
        
        data = {
            "id": chapter.id,
            "novel_id": chapter.novel_id,
            "chapter_number": chapter.chapter_number,
            "title": chapter.title,
            "content": chapter.content,
            "word_count": count_words(chapter.content or ""),
            "status": chapter.status,
            "created_at": chapter.created_at.isoformat() if chapter.created_at else None,
            "updated_at": chapter.updated_at.isoformat() if chapter.updated_at else None
        }
        
        if include_summary:
            data["summary"] = chapter.summary

        if include_lines:
            content = chapter.content or ""
            lines = content.splitlines()
            data["lines"] = [{"line_number": i + 1, "content": line} for i, line in enumerate(lines)]
            data["line_count"] = len(lines)

        return MCPToolResult(
            success=True,
            data=data,
            metadata={"tool": self.name, "chapter_id": chapter_id}
        )


class GetNovelProgressTool(BaseMCPTool):
    """获取小说进度"""
    
    name = "get_novel_progress"
    description = "获取小说的写作进度，包括章节完成情况、字数统计、角色数量等。无需提供novel_id。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {},
        "required": []
    }
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")
        
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
        
        result = await db.execute(
            select(Novel)
            .options(
                selectinload(Novel.chapters),
                selectinload(Novel.characters)
            )
            .where(Novel.id == novel_id)
        )
        novel = result.scalar_one_or_none()
        
        chapters = novel.chapters
        characters = novel.characters
        
        total_chapters = len(chapters)
        completed_chapters = len([ch for ch in chapters if ch.status == "completed"])
        draft_chapters = total_chapters - completed_chapters
        total_words = sum(len(ch.content or "") for ch in chapters)
        
        avg_words_per_chapter = total_words / total_chapters if total_chapters > 0 else 0
        
        progress_percentage = (completed_chapters / total_chapters * 100) if total_chapters > 0 else 0
        
        latest_chapter = None
        if chapters:
            latest = max(chapters, key=lambda x: x.chapter_number)
            latest_chapter = {
                "chapter_number": latest.chapter_number,
                "title": latest.title,
                "status": latest.status
            }
        
        progress = {
            "novel_id": novel.id,
            "novel_title": novel.title,
            "novel_status": novel.status,
            "chapters": {
                "total": total_chapters,
                "completed": completed_chapters,
                "draft": draft_chapters,
                "progress_percentage": round(progress_percentage, 2)
            },
            "words": {
                "total": total_words,
                "average_per_chapter": round(avg_words_per_chapter, 2)
            },
            "characters": {
                "total": len(characters)
            },
            "latest_chapter": latest_chapter
        }
        
        return MCPToolResult(
            success=True,
            data=progress,
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class GetCreativeProfileTool(BaseMCPTool):
    """获取作者创作配置（双层：全局+单书）"""

    name = "get_creative_profile"
    description = "获取当前小说的作者创作配置，包含两层：(1) 作者全局偏好 — 跨所有书的写作习惯；(2) 本书的专属偏好。无需传novel_id，系统会注入当前小说ID。当准备生成章节、规划情节、审阅方向时，应优先调用此工具确认长期规则。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {},
        "required": []
    }

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")

        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

        result = await db.execute(
            select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == novel_id)
        )
        novel_profile = result.scalar_one_or_none()

        from app.novels.models import UserCreativeProfile
        up_result = await db.execute(
            select(UserCreativeProfile).where(UserCreativeProfile.user_id == user_id)
        )
        user_profile = up_result.scalar_one_or_none()

        merged_must_keep: List[str] = []
        merged_must_avoid: List[str] = []

        if user_profile and user_profile.global_must_keep:
            merged_must_keep.extend(user_profile.global_must_keep)
        if novel_profile and novel_profile.must_keep:
            merged_must_keep.extend(novel_profile.must_keep)

        if user_profile and user_profile.global_must_avoid:
            merged_must_avoid.extend(user_profile.global_must_avoid)
        if novel_profile and novel_profile.must_avoid:
            merged_must_avoid.extend(novel_profile.must_avoid)

        seen_keep, seen_avoid = set(), set()
        unique_keep = []
        for item in merged_must_keep:
            text = str(item).strip()
            if text and text not in seen_keep:
                unique_keep.append(text)
                seen_keep.add(text)
        unique_avoid = []
        for item in merged_must_avoid:
            text = str(item).strip()
            if text and text not in seen_avoid:
                unique_avoid.append(text)
                seen_avoid.add(text)

        profile_summary_parts: List[str] = []
        if user_profile and user_profile.global_writing_style:
            profile_summary_parts.append(f"全局风格：{user_profile.global_writing_style.strip()}")
        if novel_profile and novel_profile.author_intent:
            profile_summary_parts.append(f"本书意图：{novel_profile.author_intent.strip()}")
        if novel_profile and novel_profile.preferred_tone:
            profile_summary_parts.append(f"默认语气：{novel_profile.preferred_tone.strip()}")
        if unique_keep:
            profile_summary_parts.append("必须保留：" + "；".join(unique_keep[:8]))
        if unique_avoid:
            profile_summary_parts.append("必须避免：" + "；".join(unique_avoid[:8]))
        if novel_profile and novel_profile.long_term_goals:
            goals_str = "；".join(str(g).strip() for g in (novel_profile.long_term_goals or [])[:5] if str(g).strip())
            if goals_str:
                profile_summary_parts.append(f"长线目标：{goals_str}")
        profile_summary = "\n".join(profile_summary_parts[:6])

        return MCPToolResult(
            success=True,
            data={
                "novel_id": novel_id,
                "user_global": {
                    "global_writing_style": user_profile.global_writing_style if user_profile else None,
                    "preferred_sentence_length": user_profile.preferred_sentence_length if user_profile else None,
                    "default_pov": user_profile.default_pov if user_profile else None,
                    "global_must_keep": user_profile.global_must_keep if user_profile else [],
                    "global_must_avoid": user_profile.global_must_avoid if user_profile else [],
                    "exists": user_profile is not None,
                } if user_profile else {"exists": False},
                "novel_specific": {
                    "author_intent": novel_profile.author_intent if novel_profile else None,
                    "preferred_tone": novel_profile.preferred_tone if novel_profile else None,
                    "collaboration_style": novel_profile.collaboration_style if novel_profile else "ai_ide",
                    "scene_planning_notes": novel_profile.scene_planning_notes if novel_profile else None,
                    "must_keep": novel_profile.must_keep or [] if novel_profile else [],
                    "must_avoid": novel_profile.must_avoid or [] if novel_profile else [],
                    "long_term_goals": novel_profile.long_term_goals or [] if novel_profile else [],
                    "exists": novel_profile is not None,
                },
                "merged": {
                    "must_keep": unique_keep,
                    "must_avoid": unique_avoid,
                },
                "profile_summary": profile_summary,
            },
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class UpdateCreativeProfileTool(BaseMCPTool):
    """更新作者创作配置（双层 + 防膨胀）"""

    name = "update_creative_profile"
    description = (
        "更新当前小说的作者创作配置。无需传novel_id，系统会注入当前小说ID。"
        "\n⚠️ 重要规则："
        "\n- must_keep 和 must_avoid 每类最多 15 条，超出时自动合并语义相近的条目。保持简洁，不要无限追加。"
        "\n- 如果是'这本书的风格/目标/禁忌'，更新到本书偏好；如果是'我个人的写作习惯'，考虑是否应设为全局规则。"
        "\n- 默认增量合并(merge_with_existing=true)；若要完全替换旧规则，传 merge_with_existing=false。"
        "\n- 更新后会自动生成精简摘要(llm_brief)供后续上下文注入使用。"
        "\n建议先调用 get_creative_profile 确认当前状态再修改。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "author_intent": {
                "type": "string",
                "description": "作者长期创作意图（本书专属）"
            },
            "preferred_tone": {
                "type": "string",
                "description": "默认语气/文风偏好（本书专属）"
            },
            "global_writing_style": {
                "type": "string",
                "description": "全局写作风格习惯（跨所有书生效）"
            },
            "must_keep": {
                "type": "array",
                "items": {"type": "string"},
                "description": "长期必须保留、必须遵守的规则（上限15条，本书专属）"
            },
            "must_avoid": {
                "type": "array",
                "items": {"type": "string"},
                "description": "长期必须避免的内容（上限15条，本书专属）"
            },
            "long_term_goals": {
                "type": "array",
                "items": {"type": "string"},
                "description": "长线创作目标（本书专属）"
            },
            "merge_with_existing": {
                "type": "boolean",
                "default": True,
                "description": "是否与现有配置增量合并；默认 true"
            }
        },
        "required": []
    }

    MAX_LIST_ITEMS = 15

    @staticmethod
    def _enforce_limit(items: Optional[List[str]], limit: int = MAX_LIST_ITEMS) -> List[str]:
        if items is None:
            return []
        if len(items) <= limit:
            return [str(i).strip() for i in items if str(i).strip()]
        return [str(i).strip() for i in items[:limit] if str(i).strip()]

    @staticmethod
    def _merge_unique_list(existing: Optional[List[str]], incoming: Optional[List[str]]) -> Optional[List[str]]:
        if incoming is None:
            return existing
        merged: List[str] = []
        seen: set[str] = set()
        for item in (existing or []) + (incoming or []):
            text = str(item).strip()
            if text and text not in seen:
                merged.append(text)
                seen.add(text)
        return merged

    @staticmethod
    def _merge_dict(existing: Optional[Dict[str, Any]], incoming: Optional[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
        if incoming is None:
            return existing
        merged = dict(existing or {})
        merged.update(incoming)
        return merged

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        author_intent: Optional[str] = None,
        preferred_tone: Optional[str] = None,
        global_writing_style: Optional[str] = None,
        must_keep: Optional[List[str]] = None,
        must_avoid: Optional[List[str]] = None,
        long_term_goals: Optional[List[str]] = None,
        merge_with_existing: bool = True,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")

        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

        must_keep_limited = self._enforce_limit(must_keep)
        must_avoid_limited = self._enforce_limit(must_avoid)

        if global_writing_style and user_id:
            from app.novels.models import UserCreativeProfile
            up_result = await db.execute(
                select(UserCreativeProfile).where(UserCreativeProfile.user_id == user_id)
            )
            user_profile = up_result.scalar_one_or_none()
            if not user_profile:
                user_profile = UserCreativeProfile(user_id=user_id)
                db.add(user_profile)
            user_profile.global_writing_style = global_writing_style

        result = await db.execute(
            select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == novel_id)
        )
        profile = result.scalar_one_or_none()
        if not profile:
            profile = NovelCreativeProfile(
                novel_id=novel_id,
                collaboration_style="ai_ide"
            )
            db.add(profile)

        if author_intent is not None:
            profile.author_intent = author_intent
        if preferred_tone is not None:
            profile.preferred_tone = preferred_tone

        if merge_with_existing:
            profile.must_keep = self._merge_unique_list(profile.must_keep, must_keep_limited)
            profile.must_avoid = self._merge_unique_list(profile.must_avoid, must_avoid_limited)
            profile.long_term_goals = self._merge_unique_list(profile.long_term_goals, long_term_goals)
        else:
            if must_keep_limited is not None:
                profile.must_keep = self._merge_unique_list([], must_keep_limited)
            if must_avoid_limited is not None:
                profile.must_avoid = self._merge_unique_list([], must_avoid_limited)
            if long_term_goals is not None:
                profile.long_term_goals = self._merge_unique_list([], long_term_goals)

        profile.must_keep = self._enforce_limit(profile.must_keep)
        profile.must_avoid = self._enforce_limit(profile.must_avoid)

        profile_summary = _build_creative_profile_summary(
            author_intent=profile.author_intent,
            preferred_tone=profile.preferred_tone,
            scene_planning_notes=profile.scene_planning_notes,
            must_keep=profile.must_keep or [],
            must_avoid=profile.must_avoid or [],
            long_term_goals=profile.long_term_goals or []
        )
        profile.extra_metadata = _attach_profile_summary(profile.extra_metadata, profile_summary)

        await db.commit()
        await db.refresh(profile)

        from app.core.redis_service import redis_service
        await redis_service.clear_pattern(f"novel:{novel_id}:*")
        from app.core.context_builder import context_cache
        context_cache.invalidate_novel(novel_id)

        return MCPToolResult(
            success=True,
            data={
                "id": profile.id,
                "novel_id": profile.novel_id,
                "author_intent": profile.author_intent,
                "preferred_tone": profile.preferred_tone,
                "collaboration_style": profile.collaboration_style,
                "scene_planning_notes": profile.scene_planning_notes,
                "must_keep": profile.must_keep or [],
                "must_avoid": profile.must_avoid or [],
                "long_term_goals": profile.long_term_goals or [],
                "extra_metadata": profile.extra_metadata or {},
                "profile_summary": profile_summary,
                "updated_at": profile.updated_at.isoformat() if profile.updated_at else None,
                "merge_with_existing": merge_with_existing
            },
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class GetCharacterListTool(BaseMCPTool):
    """获取角色列表"""
    
    name = "get_character_list"
    description = (
        "获取小说的角色列表。无需提供novel_id，系统会注入当前小说ID。"
        "\n适用场景：写作前了解角色阵容、确认角色基本信息、获取character_id用于详情查询。"
        "\n返回信息：角色名、性格（personality）、能力（abilities）、关系概要（relationships）。"
        "\n生成章节或规划情节前应优先调用此工具了解角色。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "search": {
                "type": "string",
                "description": "角色名搜索（可选）"
            }
        },
        "required": []
    }
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        search: Optional[str] = None,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")
        
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
        
        query = select(Character).where(Character.novel_id == novel_id)
        
        if search:
            query = query.filter(Character.name.contains(search))
        
        result = await db.execute(query)
        characters = result.scalars().all()
        
        items = [
            {
                "id": ch.id,
                "name": ch.name,
                "personality": ch.personality,
                "abilities": ch.abilities,
                "relationships": ch.relationships,
                "created_at": ch.created_at.isoformat() if ch.created_at else None
            }
            for ch in characters
        ]
        
        return MCPToolResult(
            success=True,
            data=items,
            metadata={"tool": self.name, "novel_id": novel_id, "total": len(items)}
        )


class GetCharacterDetailTool(BaseMCPTool):
    """获取角色详情"""
    
    name = "get_character_detail"
    description = (
        "获取指定角色的详细信息。"
        "\n适用场景：需要深入了解某个角色的完整档案时调用。"
        "\n返回信息：姓名、性格详情（personality）、能力列表（abilities）、人物关系（relationships）、所属小说。"
        "\n写作价值：帮助AI准确把握角色的言行风格、能力边界、与其他角色的关系定位。"
        "\n提示：先用 get_character_list 获取 character_id，再用此工具查询详情。"
        "\n💡 如果只是写作前快速了解角色阵容，用 get_writing_characters 更方便（一步到位）。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {
                "type": "integer",
                "description": "小说ID"
            },
            "character_id": {
                "type": "integer",
                "description": "角色ID"
            }
        },
        "required": ["novel_id", "character_id"]
    }
    
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
            select(Character)
            .options(selectinload(Character.novel))
            .where(Character.id == character_id)
        )
        character = result.scalar_one_or_none()
        
        if not character:
            return MCPToolResult(
                success=False,
                error=f"Character not found: {character_id}"
            )

        if character.novel_id != novel_id:
            return MCPToolResult(
                success=False,
                error=f"Character {character_id} does not belong to novel {novel_id}"
            )
        
        data = {
            "id": character.id,
            "novel_id": character.novel_id,
            "name": character.name,
            "personality": character.personality,
            "abilities": character.abilities,
            "relationships": character.relationships,
            "created_at": character.created_at.isoformat() if character.created_at else None,
            "novel": {
                "id": character.novel.id,
                "title": character.novel.title
            } if character.novel else None
        }
        
        return MCPToolResult(
            success=True,
            data=data,
            metadata={"tool": self.name, "character_id": character_id}
        )


class GetWritingCharactersTool(BaseMCPTool):
    """获取写作用角色概览（一步到位）"""

    name = "get_writing_characters"
    description = (
        "获取当前小说的角色写作概览，一步返回所有关键信息。"
        "无需传novel_id，系统会注入当前小说ID。"
        "\n这是写作前最推荐调用的角色工具——一次调用即可获得："
        "\n1. 角色名单（姓名+核心性格标签+角色定位）"
        "\n2. 人物关系网络概要（谁和谁是什么关系）"
        "\n3. 各角色的最近动态（近期参与的事件/章节）"
        "\n适用场景：开始写新章节、规划情节走向、需要回忆角色设定时。"
        "\n如果只需要某个角色的深度信息，再用 get_character_detail 或 get_character_memory 查询。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "include_relations": {
                "type": "boolean",
                "default": True,
                "description": "是否包含人物关系网络"
            },
            "include_recent_events": {
                "type": "boolean",
                "default": True,
                "description": "是否包含各角色的最近动态"
            },
        },
        "required": []
    }

    @staticmethod
    def _extract_personality_summary(personality: Optional[Dict[str, Any]]) -> str:
        if not personality or not isinstance(personality, dict):
            return ""
        parts: List[str] = []
        for key, value in personality.items():
            text = str(value).strip()
            if text and len(text) < 200:
                parts.append(f"{key}：{text}")
        return "；".join(parts[:5])

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        include_relations: bool = True,
        include_recent_events: bool = True,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")

        result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        
        from app.core.permissions import verify_novel_ownership
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

        result = await db.execute(
            select(Character).where(Character.novel_id == novel_id)
        )
        characters = result.scalars().all()

        char_id_map = {c.id: c for c in characters}

        characters_data = [
            {
                "id": c.id,
                "name": c.name,
                "personality_summary": self._extract_personality_summary(c.personality),
                "abilities": c.abilities or [],
                "role_hint": "",
            }
            for c in characters
        ]

        relations_data = []
        if include_relations and characters:
            try:
                from app.characters.models import CharacterRelation
                rel_result = await db.execute(
                    select(CharacterRelation).where(
                        CharacterRelation.novel_id == novel_id,
                        CharacterRelation.status == "active"
                    )
                )
                all_relations = rel_result.scalars().all()
                for rel in all_relations:
                    source_name = char_id_map.get(rel.source_character_id)
                    target_name = char_id_map.get(rel.target_character_id)
                    if source_name and target_name:
                        relations_data.append({
                            "source_name": source_name.name,
                            "target_name": target_name.name,
                            "type": rel.relationship_type,
                            "intensity": rel.intensity,
                            "status": rel.status,
                        })
            except Exception:
                pass

        recent_events_summary = ""
        if include_recent_events and characters:
            try:
                from app.timeline.models import TimelineEntry
                entry_result = await db.execute(
                    select(TimelineEntry)
                    .where(TimelineEntry.novel_id == novel_id)
                    .order_by(TimelineEntry.updated_at.desc())
                    .limit(10)
                )
                recent_entries = entry_result.scalars().all()
                if recent_entries:
                    event_lines = []
                    for entry in recent_entries:
                        status_label = {"pending": "待办", "active": "进行中", "completed": "已完成", "resolved": "已回收"}.get(entry.status, entry.status)
                        line = f"[{entry.category}/{status_label}] {entry.title}"
                        if entry.description:
                            line += f" — {entry.description[:50]}"
                        event_lines.append(line)
                    recent_events_summary = "\n".join(event_lines)
                else:
                    recent_events_summary = "暂无追踪记录。"
            except Exception:
                recent_events_summary = ""

        return MCPToolResult(
            success=True,
            data={
                "characters": characters_data,
                "relations": relations_data,
                "recent_events_summary": recent_events_summary,
                "total_characters": len(characters),
            },
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class CreateCharacterTool(BaseMCPTool):
    """创建新角色"""

    name = "create_character"
    description = (
        "为当前小说创建一个新角色。无需传novel_id，系统会注入当前小说ID。"
        "\n适用场景：用户要求添加新角色、AI写作时发现需要新角色、规划角色阵容时。"
        "\n创建后可通过 get_character_detail 查看详情，通过 update_character 修改设定。"
        "\n注意：name 为必填；personality 建议包含 role(角色定位)、traits(性格特点)、background(背景) 等字段。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "角色名称（必填）"},
            "personality": {
                "type": "object",
                "description": "角色性格/设定字典，建议包含: role(定位), traits(性格), background(背景), motivation(动机), appearance(外貌)"
            },
            "abilities": {
                "type": "array",
                "items": {"type": "string"},
                "description": "角色能力/技能列表"
            },
        },
        "required": ["name"]
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        name: str,
        personality: Optional[Dict] = None,
        abilities: Optional[List[str]] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

            from app.characters.models import Character
            character = Character(
                novel_id=novel_id,
                name=name,
                personality=personality or {},
                abilities=abilities or [],
            )
            db.add(character)
            await db.commit()
            await db.refresh(character)

            await _invalidate_character_cache(novel_id)

            return MCPToolResult(
                success=True,
                data={
                    "id": character.id,
                    "name": character.name,
                    "novel_id": character.novel_id,
                    "personality": character.personality,
                    "abilities": character.abilities,
                },
                metadata={"tool": self.name, "novel_id": novel_id, "character_id": character.id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"创建角色失败: {str(e)}")


class UpdateCharacterTool(BaseMCPTool):
    """更新角色信息"""

    name = "update_character"
    description = (
        "更新已有角色的设定信息。无需传novel_id，系统会注入当前小说ID。"
        "\n适用场景：用户要求修改角色设定、AI写作中发现需要调整角色属性时。"
        "\n只需传入要修改的字段，未传入的字段保持不变。"
        "\n修改后相关缓存会自动失效，下次查询获取最新数据。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "character_id": {"type": "integer", "description": "角色ID（必填）"},
            "name": {"type": "string", "description": "新的名称"},
            "personality": {
                "type": "object",
                "description": "新的性格/设定字典（完全替换旧的）"
            },
            "abilities": {
                "type": "array",
                "items": {"type": "string"},
                "description": "新的能力列表（完全替换旧的）"
            },
        },
        "required": ["character_id"]
    }

    async def execute(
        self,
        db,
        novel_id: int,
        user_id: int,
        character_id: int,
        name: Optional[str] = None,
        personality: Optional[Dict] = None,
        abilities: Optional[List[str]] = None,
        **kwargs
    ) -> MCPToolResult:
        try:
            novel = await verify_novel_ownership(db, novel_id, user_id)
            if not novel:
                return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
            
            from app.characters.models import Character
            result = await db.execute(
                select(Character).where(Character.id == character_id)
            )
            character = result.scalar_one_or_none()
            if not character:
                return MCPToolResult(success=False, error=f"角色 {character_id} 不存在")
            if character.novel_id != novel_id:
                return MCPToolResult(success=False, error=f"角色不属于当前小说")

            if name is not None:
                character.name = name
            if personality is not None:
                character.personality = personality
            if abilities is not None:
                character.abilities = abilities

            await db.commit()
            await db.refresh(character)

            await _invalidate_character_cache(novel_id, character_id)

            return MCPToolResult(
                success=True,
                data={
                    "id": character.id,
                    "name": character.name,
                    "novel_id": character.novel_id,
                    "personality": character.personality,
                    "abilities": character.abilities,
                },
                metadata={"tool": self.name, "novel_id": novel_id, "character_id": character_id}
            )
        except Exception as e:
            return MCPToolResult(success=False, error=f"更新角色失败: {str(e)}")


class CreateNewChapterTool(BaseMCPTool):
    """创建新章节"""
    
    name = "create_new_chapter"
    description = "创建小说的新空章节。无需传novel_id，系统会注入当前小说ID。chapter_number 可省略，系统会自动创建下一章。创建后可用 edit_chapter 写入正文。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "novel_id": {
                "type": "integer",
                "description": "小说ID"
            },
            "chapter_number": {
                "type": "integer",
                "description": "章节号"
            },
            "title": {
                "type": "string",
                "description": "章节标题（可选）"
            },
            "content": {
                "type": "string",
                "description": "章节内容（可选）"
            }
        },
        "required": []
    }
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        user_id: int,
        chapter_number: Optional[int] = None,
        title: Optional[str] = None,
        content: Optional[str] = None,
        **kwargs
    ) -> MCPToolResult:
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")

        if chapter_number is None:
            latest_result = await db.execute(
                select(Chapter.chapter_number)
                .where(Chapter.novel_id == novel_id)
                .order_by(Chapter.chapter_number.desc())
                .limit(1)
            )
            latest_chapter_number = latest_result.scalar_one_or_none()
            chapter_number = (latest_chapter_number or 0) + 1
        
        existing = await db.execute(
            select(Chapter).where(Chapter.novel_id == novel_id, Chapter.chapter_number == chapter_number)
        )
        existing_chapter = existing.scalar_one_or_none()
        if existing_chapter:
            data = existing_chapter.to_dict()
            data["reused_existing"] = True
            data["message"] = "章节已存在，已返回现有章节"
            return MCPToolResult(
                success=True,
                data=data,
                metadata={"tool": self.name, "novel_id": novel_id, "chapter_id": existing_chapter.id, "reused_existing": True}
            )
        
        chapter = Chapter(
            novel_id=novel_id,
            chapter_number=chapter_number,
            title=title or f"第{chapter_number}章",
            content=content or "",
            status="draft",
            word_count=count_words(content or "")
        )
        db.add(chapter)
        try:
            await db.commit()
        except IntegrityError:
            await db.rollback()
            existing_after_conflict = await db.execute(
                select(Chapter).where(Chapter.novel_id == novel_id, Chapter.chapter_number == chapter_number)
            )
            conflicted_chapter = existing_after_conflict.scalar_one_or_none()
            if conflicted_chapter:
                data = conflicted_chapter.to_dict()
                data["reused_existing"] = True
                data["message"] = "章节已存在，已返回现有章节"
                return MCPToolResult(
                    success=True,
                    data=data,
                    metadata={"tool": self.name, "novel_id": novel_id, "chapter_id": conflicted_chapter.id, "reused_existing": True}
                )
            raise
        await db.refresh(chapter)

        await _invalidate_chapter_cache(novel_id, chapter.id)

        data = chapter.to_dict()
        data["reused_existing"] = False
        return MCPToolResult(
            success=True,
            data=data,
            metadata={"tool": self.name, "novel_id": novel_id, "chapter_id": chapter.id, "reused_existing": False}
        )


class NovelManagementTools:
    """小说管理工具集合"""
    
    @staticmethod
    def register_all(registry: MCPToolRegistry) -> None:
        """注册所有小说管理工具"""
        registry.register(GetNovelSummaryTool())
        registry.register(GetChapterListTool())
        registry.register(GetChapterContentTool())
        registry.register(GetNovelProgressTool())
        registry.register(GetCreativeProfileTool())
        registry.register(UpdateCreativeProfileTool())
        registry.register(GetCharacterListTool())
        registry.register(GetCharacterDetailTool())
        registry.register(GetWritingCharactersTool())
        registry.register(CreateCharacterTool())
        registry.register(UpdateCharacterTool())
        registry.register(CreateNewChapterTool())
