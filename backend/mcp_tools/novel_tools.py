"""
小说管理类MCP工具
提供小说信息查询的标准接口
"""
from typing import Any
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from sqlalchemy.orm import selectinload
from sqlalchemy.exc import IntegrityError

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from pydantic import BaseModel, Field
from typing import Literal
from novels.models import Novel, NovelCreativeProfile
from chapters.models import Chapter
from text.utils import count_words
from .utils import _invalidate_chapter_cache


def _build_creative_profile_summary(
    author_intent: str | None = None,
    preferred_tone: str | None = None,
    scene_planning_notes: str | None = None,
    must_keep: list[str] | None = None,
    must_avoid: list[str] | None = None,
    long_term_goals: list[str] | None = None
) -> str:
    parts: list[str] = []
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


def _attach_profile_summary(extra_metadata: dict[str, Any] | None, summary: str) -> dict[str, Any]:
    merged = dict(extra_metadata or {})
    if summary.strip():
        merged["llm_brief"] = summary.strip()
    return merged


class GetNovelInfoArgs(BaseModel):
    mode: Literal["summary", "progress"] = Field(default="summary", description="查询模式：summary=整体摘要，progress=写作进度")

class GetNovelInfoTool(BaseMCPTool):
    """获取小说信息（摘要或进度）"""

    name = "get_novel_info"
    description = (
        "获取小说信息，支持两种模式："
        "\n- summary: 获取小说整体摘要（标题、类型、描述、状态、章节数、字数、角色数等）"
        "\n- progress: 获取小说写作进度（章节完成情况、字数统计、最新章节等）"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = GetNovelInfoArgs

    async def _execute(
        self,
        args: GetNovelInfoArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
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

        if args.mode == "progress":
            total_chapters = len(chapters)
            draft_chapters = total_chapters - completed_chapters
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

            data = {
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
        else:
            data = {
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
            data=data,
            metadata={"tool": self.name, "novel_id": novel_id, "mode": args.mode}
        )


class GetChapterListArgs(BaseModel):
    status: Literal["draft", "completed"] | None = Field(default=None, description="章节状态筛选（可选）")
    page: int = Field(default=1, description="页码")
    page_size: int = Field(default=20, description="每页数量")

class GetChapterListTool(BaseMCPTool):
    """获取章节列表"""
    
    name = "get_chapter_list"
    description = "获取小说的章节列表，支持分页和状态筛选。返回可用于 edit_chapter 的 chapter_id。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = GetChapterListArgs
    
    async def _execute(
        self,
        args: GetChapterListArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        query = select(Chapter).where(Chapter.novel_id == novel_id)

        if args.status:
            query = query.filter(Chapter.status == args.status)

        from sqlalchemy import func
        count_query = select(func.count()).select_from(query.subquery())
        total_result = await db.execute(count_query)
        total = total_result.scalar()

        query = query.order_by(Chapter.chapter_number).offset((args.page - 1) * args.page_size).limit(args.page_size)
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
                "page": args.page,
                "page_size": args.page_size,
                "total_pages": (total + args.page_size - 1) // args.page_size
            },
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class GetChapterContentArgs(BaseModel):
    chapter_id: int | None = Field(default=None, description="章节ID（可选，不提供则返回第一章）")
    chapter_number: int | None = Field(default=None, description="章节号（可选）")
    include_summary: bool = Field(default=True, description="是否包含摘要")
    include_lines: bool = Field(default=False, description="是否返回带行号的行数组（用于按行号编辑）")

class GetChapterContentTool(BaseMCPTool):
    """获取章节内容"""
    
    name = "get_chapter_content"
    description = "获取指定章节的完整内容。可以通过章节号或章节ID获取。如果不提供chapter_id，则返回第一章的内容。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = GetChapterContentArgs

    async def _execute(
        self,
        args: GetChapterContentArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        if not args.chapter_id and not args.chapter_number:
            result = await db.execute(
                select(Chapter).where(Chapter.novel_id == novel_id).order_by(Chapter.chapter_number).limit(1)
            )
            chapter = result.scalar_one_or_none()
        else:
            query = select(Chapter).where(Chapter.novel_id == novel_id)
            if args.chapter_id:
                query = query.where(Chapter.id == args.chapter_id)
            elif args.chapter_number:
                query = query.where(Chapter.chapter_number == args.chapter_number)

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

        if args.include_summary:
            data["summary"] = chapter.summary

        if args.include_lines:
            chapter_text = chapter.content or ""
            lines = chapter_text.splitlines()
            data["lines"] = [{"line_number": i + 1, "content": line} for i, line in enumerate(lines)]
            data["line_count"] = len(lines)

        return MCPToolResult(
            success=True,
            data=data,
            metadata={"tool": self.name, "chapter_id": args.chapter_id}
        )


class GetCreativeProfileArgs(BaseModel):
    pass

class GetCreativeProfileTool(BaseMCPTool):
    """获取作者创作配置（双层：全局+单书）"""

    name = "get_creative_profile"
    description = "获取当前小说的作者创作配置，包含两层：(1) 作者全局偏好 — 跨所有书的写作习惯；(2) 本书的专属偏好。当准备生成章节、规划情节、审阅方向时，应优先调用此工具确认长期规则。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = GetCreativeProfileArgs

    async def _execute(
        self,
        args: GetCreativeProfileArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        result = await db.execute(
            select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == novel_id)
        )
        novel_profile = result.scalar_one_or_none()

        from novels.models import UserCreativeProfile
        up_result = await db.execute(
            select(UserCreativeProfile).where(UserCreativeProfile.user_id == user_id)
        )
        user_profile = up_result.scalar_one_or_none()

        merged_must_keep: list[str] = []
        merged_must_avoid: list[str] = []

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

        profile_summary_parts: list[str] = []
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


class UpdateCreativeProfileArgs(BaseModel):
    author_intent: str | None = Field(default=None, description="作者长期创作意图（本书专属）")
    preferred_tone: str | None = Field(default=None, description="默认语气/文风偏好（本书专属）")
    global_writing_style: str | None = Field(default=None, description="全局写作风格习惯（跨所有书生效）")
    must_keep: list[str] | None = Field(default=None, description="长期必须保留、必须遵守的规则（上限15条，本书专属）")
    must_avoid: list[str] | None = Field(default=None, description="长期必须避免的内容（上限15条，本书专属）")
    long_term_goals: list[str] | None = Field(default=None, description="长线创作目标（本书专属）")
    merge_with_existing: bool = Field(default=True, description="是否与现有配置增量合并；默认 true")

class UpdateCreativeProfileTool(BaseMCPTool):
    """更新作者创作配置（双层 + 防膨胀）"""

    name = "update_creative_profile"
    description = (
        "更新当前小说的作者创作配置。"
        "\n⚠️ 重要规则："
        "\n- must_keep 和 must_avoid 每类最多 15 条，超出时自动合并语义相近的条目。保持简洁，不要无限追加。"
        "\n- 如果是'这本书的风格/目标/禁忌'，更新到本书偏好；如果是'我个人的写作习惯'，考虑是否应设为全局规则。"
        "\n- 默认增量合并(merge_with_existing=true)；若要完全替换旧规则，传 merge_with_existing=false。"
        "\n- 更新后会自动生成精简摘要(llm_brief)供后续上下文注入使用。"
        "\n建议先调用 get_creative_profile 确认当前状态再修改。"
    )
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = UpdateCreativeProfileArgs

    MAX_LIST_ITEMS = 15

    @staticmethod
    def _enforce_limit(items: list[str] | None, limit: int = MAX_LIST_ITEMS) -> list[str]:
        if items is None:
            return []
        if len(items) <= limit:
            return [str(i).strip() for i in items if str(i).strip()]
        return [str(i).strip() for i in items[:limit] if str(i).strip()]

    @staticmethod
    def _merge_unique_list(existing: list[str] | None, incoming: list[str] | None) -> list[str] | None:
        if incoming is None:
            return existing
        merged: list[str] = []
        seen: set[str] = set()
        for item in (existing or []) + (incoming or []):
            text = str(item).strip()
            if text and text not in seen:
                merged.append(text)
                seen.add(text)
        return merged

    @staticmethod
    def _merge_dict(existing: dict[str, Any] | None, incoming: dict[str, Any] | None) -> dict[str, Any] | None:
        if incoming is None:
            return existing
        merged = dict(existing or {})
        merged.update(incoming)
        return merged

    async def _execute(
        self,
        args: UpdateCreativeProfileArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:

        must_keep_limited = self._enforce_limit(args.must_keep)
        must_avoid_limited = self._enforce_limit(args.must_avoid)

        if args.global_writing_style and user_id:
            from novels.models import UserCreativeProfile
            up_result = await db.execute(
                select(UserCreativeProfile).where(UserCreativeProfile.user_id == user_id)
            )
            user_profile = up_result.scalar_one_or_none()
            if not user_profile:
                user_profile = UserCreativeProfile(user_id=user_id)
                db.add(user_profile)
            user_profile.global_writing_style = args.global_writing_style

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

        if args.author_intent is not None:
            profile.author_intent = args.author_intent
        if args.preferred_tone is not None:
            profile.preferred_tone = args.preferred_tone

        if args.merge_with_existing:
            profile.must_keep = self._merge_unique_list(profile.must_keep, must_keep_limited)
            profile.must_avoid = self._merge_unique_list(profile.must_avoid, must_avoid_limited)
            profile.long_term_goals = self._merge_unique_list(profile.long_term_goals, args.long_term_goals)
        else:
            if must_keep_limited is not None:
                profile.must_keep = self._merge_unique_list([], must_keep_limited)
            if must_avoid_limited is not None:
                profile.must_avoid = self._merge_unique_list([], must_avoid_limited)
            if args.long_term_goals is not None:
                profile.long_term_goals = self._merge_unique_list([], args.long_term_goals)

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

        from core.redis_service import redis_service
        await redis_service.clear_pattern(f"novel:{novel_id}:*")
        from context.context_builder import context_cache
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
                "merge_with_existing": args.merge_with_existing
            },
            metadata={"tool": self.name, "novel_id": novel_id}
        )


class CreateNewChapterArgs(BaseModel):
    chapter_number: int | None = Field(default=None, description="章节号")
    title: str | None = Field(default=None, description="章节标题（可选）")
    content: str | None = Field(default=None, description="章节内容（可选）")

class CreateNewChapterTool(BaseMCPTool):
    """创建新章节"""
    
    name = "create_new_chapter"
    description = "创建小说的新空章节。chapter_number 可省略，系统会自动创建下一章。创建后可用 edit_chapter 写入正文。"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = CreateNewChapterArgs
    
    async def _execute(
        self,
        args: CreateNewChapterArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        ch_num = args.chapter_number
        if ch_num is None:
            latest_result = await db.execute(
                select(Chapter.chapter_number)
                .where(Chapter.novel_id == novel_id)
                .order_by(Chapter.chapter_number.desc())
                .limit(1)
            )
            latest_chapter_number = latest_result.scalar_one_or_none()
            ch_num = (latest_chapter_number or 0) + 1

        existing = await db.execute(
            select(Chapter).where(Chapter.novel_id == novel_id, Chapter.chapter_number == ch_num)
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
            chapter_number=ch_num,
            title=args.title or f"第{ch_num}章",
            content=args.content or "",
            status="draft",
            word_count=count_words(args.content or "")
        )
        db.add(chapter)
        try:
            await db.commit()
        except IntegrityError:
            await db.rollback()
            existing_after_conflict = await db.execute(
                select(Chapter).where(Chapter.novel_id == novel_id, Chapter.chapter_number == ch_num)
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
        registry.register(GetNovelInfoTool())
        registry.register(GetChapterListTool())
        registry.register(GetChapterContentTool())
        registry.register(GetCreativeProfileTool())
        registry.register(UpdateCreativeProfileTool())
        registry.register(CreateNewChapterTool())
