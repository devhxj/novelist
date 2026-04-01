"""
小说管理类MCP工具
提供小说信息查询的标准接口
"""
from typing import Any, Dict, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from sqlalchemy.orm import selectinload

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from app.novels.models import Novel
from app.chapters.models import Chapter
from app.characters.models import Character


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
    description = "创建小说的新章节"
    category = MCPToolCategory.NOVEL_MANAGEMENT
    expose_to_llm = False
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
        "required": ["novel_id", "chapter_number"]
    }
    
    async def execute(
        self,
        db: AsyncSession,
        novel_id: int,
        chapter_number: int,
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
        
        existing = await db.execute(
            select(Chapter).where(Chapter.novel_id == novel_id, Chapter.chapter_number == chapter_number)
        )
        if existing.scalar_one_or_none():
            return MCPToolResult(success=False, error="章节号已存在")
        
        chapter = Chapter(
            novel_id=novel_id,
            chapter_number=chapter_number,
            title=title or f"第{chapter_number}章",
            content=content or "",
            word_count=len(content or "")
        )
        db.add(chapter)
        await db.commit()
        await db.refresh(chapter)
        
        return MCPToolResult(
            success=True,
            data=chapter.to_dict(),
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
        registry.register(GetCharacterListTool())
        registry.register(GetCharacterDetailTool())
        registry.register(CreateNewChapterTool())
