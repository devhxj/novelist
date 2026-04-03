"""
小说管理类MCP工具
提供小说信息查询的标准接口
"""
from datetime import datetime
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from sqlalchemy.orm import selectinload
from sqlalchemy.exc import IntegrityError

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.novels.models import Novel, NovelCreativeProfile
from app.chapters.models import Chapter
from app.characters.models import Character
from app.generation.service import ChapterGenerationService


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
        novel_id: int = 0,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(
            select(Novel)
            .options(selectinload(Novel.chapters), selectinload(Novel.characters))
            .where(Novel.id == novel_id)
        )
        novel = result.scalar_one_or_none()
        
        if not novel:
            return MCPToolResult(
                success=False,
                error=f"Novel not found: {novel_id}"
            )
        
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")
        
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
    description = "获取小说的章节列表，支持分页和状态筛选。无需传novel_id，系统会注入当前小说ID。返回可用于 start_edit_session 的 chapter_id。"
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
        novel_id: int = 0,
        status: Optional[str] = None,
        page: int = 1,
        page_size: int = 20,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(
            select(Novel).where(Novel.id == novel_id)
        )
        novel = result.scalar_one_or_none()
        
        if not novel:
            return MCPToolResult(
                success=False,
                error=f"Novel not found: {novel_id}"
            )
        
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")
        
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
                "word_count": len(ch.content or ""),
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
            }
        },
        "required": []
    }
    
    async def execute(
        self,
        db: AsyncSession,
        chapter_id: Optional[int] = None,
        chapter_number: Optional[int] = None,
        include_summary: bool = True,
        novel_id: int = 0,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        if user_id:
            novel_result = await db.execute(select(Novel).where(Novel.id == novel_id))
            novel = novel_result.scalar_one_or_none()
            if not novel:
                return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
            if novel.author_id != user_id:
                return MCPToolResult(success=False, error="无权访问此小说")
        
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
            "word_count": len(chapter.content or ""),
            "status": chapter.status,
            "created_at": chapter.created_at.isoformat() if chapter.created_at else None,
            "updated_at": chapter.updated_at.isoformat() if chapter.updated_at else None
        }
        
        if include_summary:
            data["summary"] = chapter.summary
        
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
        novel_id: int = 0,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")
        
        result = await db.execute(
            select(Novel)
            .options(
                selectinload(Novel.chapters),
                selectinload(Novel.characters),
                selectinload(Novel.plot_events)
            )
            .where(Novel.id == novel_id)
        )
        novel = result.scalar_one_or_none()
        
        if not novel:
            return MCPToolResult(
                success=False,
                error=f"Novel not found: {novel_id}"
            )
        
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")
        
        chapters = novel.chapters
        characters = novel.characters
        plot_events = novel.plot_events
        
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
            "plot_events": {
                "total": len(plot_events)
            },
            "latest_chapter": latest_chapter
        }
        
        return MCPToolResult(
            success=True,
            data=progress,
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class GetCreativeProfileTool(BaseMCPTool):
    """获取作者创作配置"""

    name = "get_creative_profile"
    description = "获取当前小说的作者长期创作配置，包括作者意图、默认语气、长期必须保留/避免项、长线目标等。无需传novel_id，系统会注入当前小说ID。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {},
        "required": []
    }

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int = 0,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")

        novel_result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = novel_result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")

        result = await db.execute(
            select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == novel_id)
        )
        profile = result.scalar_one_or_none()
        if not profile:
            return MCPToolResult(
                success=True,
                data={
                    "novel_id": novel_id,
                    "author_intent": None,
                    "preferred_tone": None,
                    "collaboration_style": "ai_ide",
                    "scene_planning_notes": None,
                    "must_keep": [],
                    "must_avoid": [],
                    "long_term_goals": [],
                    "extra_metadata": {},
                    "profile_summary": ""
                },
                metadata={"tool": self.name, "novel_id": novel_id}
            )

        profile_summary = (
            (profile.extra_metadata or {}).get("llm_brief")
            or _build_creative_profile_summary(
                author_intent=profile.author_intent,
                preferred_tone=profile.preferred_tone,
                scene_planning_notes=profile.scene_planning_notes,
                must_keep=profile.must_keep or [],
                must_avoid=profile.must_avoid or [],
                long_term_goals=profile.long_term_goals or []
            )
        )
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
                "created_at": profile.created_at.isoformat() if profile.created_at else None,
                "updated_at": profile.updated_at.isoformat() if profile.updated_at else None
            },
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class UpdateCreativeProfileTool(BaseMCPTool):
    """更新作者创作配置"""

    name = "update_creative_profile"
    description = "更新当前小说的作者长期创作配置。无需传novel_id，系统会注入当前小说ID。默认会和已有配置做增量合并，适合在作者明确表达长期要求后调用，把要求沉淀为后续章节默认遵守的规则。若准备修改已有长期规则，建议先调用 get_creative_profile。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "author_intent": {
                "type": "string",
                "description": "作者长期创作意图"
            },
            "preferred_tone": {
                "type": "string",
                "description": "默认语气/文风偏好"
            },
            "collaboration_style": {
                "type": "string",
                "description": "协作风格，例如 ai_ide"
            },
            "scene_planning_notes": {
                "type": "string",
                "description": "章节推进与场景规划备注"
            },
            "must_keep": {
                "type": "array",
                "items": {"type": "string"},
                "description": "长期必须保留、必须遵守的规则"
            },
            "must_avoid": {
                "type": "array",
                "items": {"type": "string"},
                "description": "长期必须避免的内容、走向或表达"
            },
            "long_term_goals": {
                "type": "array",
                "items": {"type": "string"},
                "description": "长线创作目标"
            },
            "extra_metadata": {
                "type": "object",
                "description": "额外协作配置"
            },
            "merge_with_existing": {
                "type": "boolean",
                "default": True,
                "description": "是否与现有长期配置增量合并；默认 true"
            }
        },
        "required": []
    }

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
        novel_id: int = 0,
        author_intent: Optional[str] = None,
        preferred_tone: Optional[str] = None,
        collaboration_style: Optional[str] = None,
        scene_planning_notes: Optional[str] = None,
        must_keep: Optional[List[str]] = None,
        must_avoid: Optional[List[str]] = None,
        long_term_goals: Optional[List[str]] = None,
        extra_metadata: Optional[Dict[str, Any]] = None,
        merge_with_existing: bool = True,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")

        novel_result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = novel_result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权更新此小说")

        result = await db.execute(
            select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == novel_id)
        )
        profile = result.scalar_one_or_none()
        if not profile:
            profile = NovelCreativeProfile(
                novel_id=novel_id,
                collaboration_style=collaboration_style or "ai_ide"
            )
            db.add(profile)

        if author_intent is not None:
            profile.author_intent = author_intent
        if preferred_tone is not None:
            profile.preferred_tone = preferred_tone
        if collaboration_style is not None:
            profile.collaboration_style = collaboration_style
        if scene_planning_notes is not None:
            profile.scene_planning_notes = scene_planning_notes

        if merge_with_existing:
            profile.must_keep = self._merge_unique_list(profile.must_keep, must_keep)
            profile.must_avoid = self._merge_unique_list(profile.must_avoid, must_avoid)
            profile.long_term_goals = self._merge_unique_list(profile.long_term_goals, long_term_goals)
            profile.extra_metadata = self._merge_dict(profile.extra_metadata, extra_metadata)
        else:
            if must_keep is not None:
                profile.must_keep = self._merge_unique_list([], must_keep)
            if must_avoid is not None:
                profile.must_avoid = self._merge_unique_list([], must_avoid)
            if long_term_goals is not None:
                profile.long_term_goals = self._merge_unique_list([], long_term_goals)
            if extra_metadata is not None:
                profile.extra_metadata = dict(extra_metadata)

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
    description = "获取小说的角色列表。无需提供novel_id。"
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
        novel_id: int = 0,
        search: Optional[str] = None,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        novel_id = novel_id or kwargs.get("novel_id", 0)
        if not novel_id:
            return MCPToolResult(success=False, error="novel_id is required")
        
        result = await db.execute(
            select(Novel).where(Novel.id == novel_id)
        )
        novel = result.scalar_one_or_none()
        
        if not novel:
            return MCPToolResult(
                success=False,
                error=f"Novel not found: {novel_id}"
            )
        
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此小说")
        
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
    description = "获取指定角色的详细信息"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "character_id": {
                "type": "integer",
                "description": "角色ID"
            }
        },
        "required": ["character_id"]
    }
    
    async def execute(
        self,
        db: AsyncSession,
        character_id: int,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
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
        
        if user_id and character.novel and character.novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权访问此角色")
        
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


class CreateNewChapterTool(BaseMCPTool):
    """创建新章节"""
    
    name = "create_new_chapter"
    description = "创建小说的新空章节草稿。无需传novel_id，系统会注入当前小说ID。chapter_number 可省略，系统会自动创建下一章。如果你希望模型直接写出正文，请优先使用 generate_chapter_draft。"
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
        chapter_number: Optional[int] = None,
        title: Optional[str] = None,
        content: Optional[str] = None,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权创建章节")

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
            word_count=len(content or "")
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

        data = chapter.to_dict()
        data["reused_existing"] = False
        return MCPToolResult(
            success=True,
            data=data,
            metadata={"tool": self.name, "novel_id": novel_id, "chapter_id": chapter.id, "reused_existing": False}
        )


class GenerateChapterDraftTool(BaseMCPTool):
    """直接创建并生成章节"""

    name = "generate_chapter_draft"
    description = (
        "直接创建并生成一个新章节正文。无需传novel_id，系统会注入当前小说ID。"
        "chapter_number 可省略，系统会自动生成下一章。适合需要大模型直接开始写新章节时调用。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "chapter_number": {
                "type": "integer",
                "description": "章节号，可选；不提供时自动生成下一章"
            },
            "title": {
                "type": "string",
                "description": "章节标题，可选"
            },
            "target_length": {
                "type": "integer",
                "default": 3000,
                "description": "目标字数"
            },
            "style": {
                "type": "string",
                "default": "narrative",
                "description": "写作风格"
            },
            "writing_task": {
                "type": "string",
                "description": "本章核心写作任务"
            },
            "author_intent": {
                "type": "string",
                "description": "作者本人的明确创作意图，优先级高于一般写作提示"
            },
            "scene_goal": {
                "type": "string",
                "description": "本章或本场景必须完成的目标"
            },
            "outline": {
                "type": "string",
                "description": "章节提纲"
            },
            "tone": {
                "type": "string",
                "description": "语气风格要求"
            },
            "must_keep": {
                "type": "array",
                "items": {"type": "string"},
                "description": "必须保留、必须写到的要点"
            },
            "must_avoid": {
                "type": "array",
                "items": {"type": "string"},
                "description": "明确不要出现的内容、走向或表达"
            },
            "key_events": {
                "type": "array",
                "items": {"type": "string"},
                "description": "本章必须覆盖的关键事件"
            },
            "model": {
                "type": "string",
                "description": "指定模型，可选"
            },
            "use_workflow": {
                "type": "boolean",
                "description": "是否优先使用完整工作流"
            },
            "overwrite_existing": {
                "type": "boolean",
                "default": False,
                "description": "若指定章节已存在，是否允许覆盖重写"
            }
        },
        "required": []
    }

    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        chapter_number: Optional[int] = None,
        title: Optional[str] = None,
        target_length: int = 3000,
        style: str = "narrative",
        writing_task: Optional[str] = None,
        author_intent: Optional[str] = None,
        scene_goal: Optional[str] = None,
        outline: Optional[str] = None,
        tone: Optional[str] = None,
        must_keep: Optional[List[str]] = None,
        must_avoid: Optional[List[str]] = None,
        key_events: Optional[List[str]] = None,
        model: Optional[str] = None,
        use_workflow: Optional[bool] = None,
        overwrite_existing: bool = False,
        user_id: Optional[int] = None,
        **kwargs
    ) -> MCPToolResult:
        result = await db.execute(select(Novel).where(Novel.id == novel_id))
        novel = result.scalar_one_or_none()
        if not novel:
            return MCPToolResult(success=False, error=f"Novel not found: {novel_id}")
        if user_id and novel.author_id != user_id:
            return MCPToolResult(success=False, error="无权生成章节")

        if chapter_number is None:
            latest_result = await db.execute(
                select(Chapter.chapter_number)
                .where(Chapter.novel_id == novel_id)
                .order_by(Chapter.chapter_number.desc())
                .limit(1)
            )
            latest_chapter_number = latest_result.scalar_one_or_none()
            chapter_number = (latest_chapter_number or 0) + 1
        else:
            existing_result = await db.execute(
                select(Chapter).where(
                    Chapter.novel_id == novel_id,
                    Chapter.chapter_number == chapter_number
                )
            )
            existing = existing_result.scalar_one_or_none()
            if existing and not overwrite_existing:
                return MCPToolResult(
                    success=False,
                    error="目标章节已存在。如需重写，请显式设置 overwrite_existing=true。"
                )

        service = ChapterGenerationService(db, novel_id)
        generation_result = await service.generate_chapter(
            chapter_number=chapter_number,
            target_length=target_length,
            style=style,
            additional_context={
                "user_prompt": writing_task,
                "author_intent": author_intent,
                "scene_goal": scene_goal,
                "chapter_outline": outline,
                "tone": tone,
                "must_keep": must_keep or [],
                "must_avoid": must_avoid or [],
                "key_events": key_events or []
            },
            model=model,
            use_workflow=use_workflow
        )
        if not generation_result.get("success"):
            return MCPToolResult(
                success=False,
                error=generation_result.get("error") or "章节生成失败"
            )

        chapter_result = await db.execute(
            select(Chapter).where(
                Chapter.novel_id == novel_id,
                Chapter.chapter_number == chapter_number
            )
        )
        chapter = chapter_result.scalar_one_or_none()
        if not chapter:
            return MCPToolResult(success=False, error="章节已生成但保存失败")

        if title:
            chapter.title = title
            chapter.updated_at = datetime.now()
            await db.commit()
            await db.refresh(chapter)

        return MCPToolResult(
            success=True,
            data={
                "chapter_id": chapter.id,
                "chapter_number": chapter.chapter_number,
                "title": chapter.title,
                "summary": chapter.summary,
                "status": chapter.status,
                "word_count": chapter.word_count,
                "content": chapter.content,
                "review_result": generation_result.get("review_result"),
                "consistency_result": generation_result.get("consistency_result"),
                "iterations": generation_result.get("iterations", 0)
            },
            metadata={"tool": self.name, "novel_id": novel_id, "chapter_id": chapter.id}
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
        registry.register(CreateNewChapterTool())
        registry.register(GenerateChapterDraftTool())
