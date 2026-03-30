"""
会话管理API路由 - 支持三层粒度会话
"""
import logging
from datetime import datetime
from fastapi import APIRouter, Query
from typing import Optional, List

from app.core.response import ApiResponse
from app.core.database import DBSession
from app.core.dependencies import CurrentUser
from app.core.session_manager import (
    Session, SessionManager, SessionConfig, MessageRole,
    SessionLevel, NovelContext, ChapterContext,
    session_manager
)
from app.core.llm_service import llm_service
from app.core.prompt_templates import get_system_prompt, GenerationType

router = APIRouter(prefix="/sessions", tags=["sessions"])
logger = logging.getLogger(__name__)


@router.post("/create")
async def create_session(
    user: CurrentUser,
    novel_id: Optional[int] = None,
    chapter_number: Optional[int] = None,
    level: str = "free",
    system_prompt: Optional[str] = None,
    model: str = "deepseek-chat"
):
    """
    创建新会话
    
    三层粒度：
    - level=novel + novel_id: 小说级会话（全局讨论、大纲生成）
    - level=chapter + novel_id + chapter_number: 章节级会话（章节生成、修改）
    - level=free: 自由对话（通用问答）
    
    参数：
    - novel_id: 小说ID
    - chapter_number: 章节号（章节级必填）
    - level: 会话层级 (novel|chapter|free)
    - system_prompt: 自定义系统提示词
    - model: LLM模型
    """
    try:
        session_level = SessionLevel(level)
    except ValueError:
        return ApiResponse.error(f"无效的会话层级: {level}", status_code=400)
    
    if session_level == SessionLevel.CHAPTER and not chapter_number:
        return ApiResponse.error("章节级会话需要指定chapter_number", status_code=400)
    
    if session_level in [SessionLevel.NOVEL, SessionLevel.CHAPTER] and not novel_id:
        return ApiResponse.error("小说级/章节级会话需要指定novel_id", status_code=400)
    
    if not system_prompt:
        system_prompt = get_system_prompt(GenerationType.CHAPTER if session_level == SessionLevel.CHAPTER else "chat")
    
    session = session_manager.create_session(
        user_id=user.id,
        novel_id=novel_id,
        chapter_number=chapter_number,
        level=session_level,
        system_prompt=system_prompt,
        model=model
    )
    
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "session_id": session.session_id,
        "level": session.level.value,
        "display_name": session.get_display_name(),
        "novel_id": session.novel_id,
        "chapter_number": session.chapter_number,
        "model": session.model,
        "created_at": session.created_at.isoformat(),
        "message": "会话创建成功"
    })


@router.post("/{session_id}/chat")
async def chat(
    user: CurrentUser,
    session_id: str,
    message: str,
    model: Optional[str] = None,
    temperature: Optional[float] = None
):
    """
    发送消息并获取回复（非流式）
    
    - session_id: 会话ID
    - message: 用户消息
    - model: LLM模型（可选，默认使用会话设置的模型）
    - temperature: 温度参数
    """
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权访问此会话", status_code=403)
    
    try:
        response = await llm_service.chat_with_session(
            session=session,
            user_message=message,
            model=model or session.model,
            temperature=temperature,
            stream=False
        )
        
        stats = session_manager.get_session_stats(session)
        
        return ApiResponse.success({
            "session_id": session.session_id,
            "level": session.level.value,
            "message": response,
            "stats": stats
        })
        
    except Exception as e:
        logger.error(f"Chat error: {e}")
        return ApiResponse.error(f"对话失败: {str(e)}")


@router.get("/{session_id}")
async def get_session(
    user: CurrentUser,
    session_id: str
):
    """获取会话详情"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权访问此会话", status_code=403)
    
    stats = session_manager.get_session_stats(session)
    
    return ApiResponse.success({
        "session_id": session.session_id,
        "user_id": session.user_id,
        "level": session.level.value,
        "display_name": session.get_display_name(),
        "novel_id": session.novel_id,
        "chapter_number": session.chapter_number,
        "generation_type": session.generation_type,
        "messages": [m.to_dict() for m in session.messages],
        "summary": session.summary,
        "novel_context": session.novel_context.__dict__ if session.novel_context else None,
        "chapter_context": session.chapter_context.__dict__ if session.chapter_context else None,
        "stats": stats,
        "created_at": session.created_at.isoformat(),
        "updated_at": session.updated_at.isoformat()
    })


@router.get("/{session_id}/messages")
async def get_messages(
    user: CurrentUser,
    session_id: str,
    limit: int = 50,
    offset: int = 0
):
    """获取会话消息列表"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权访问此会话", status_code=403)
    
    messages = session.messages[offset:offset + limit]
    
    return ApiResponse.success({
        "session_id": session.session_id,
        "level": session.level.value,
        "messages": [m.to_dict() for m in messages],
        "total": len(session.messages),
        "limit": limit,
        "offset": offset
    })


@router.delete("/{session_id}")
async def delete_session(
    user: CurrentUser,
    session_id: str
):
    """删除会话"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权删除此会话", status_code=403)
    
    await session_manager.delete_session(session_id)
    
    return ApiResponse.success({"message": "会话已删除"})


@router.get("/list")
async def list_sessions(
    user: CurrentUser,
    novel_id: Optional[int] = None,
    level: Optional[str] = None,
    limit: int = 20
):
    """
    列出用户会话
    
    - novel_id: 按小说ID过滤
    - level: 按层级过滤 (novel|chapter|free)
    - limit: 返回数量限制
    """
    session_level = None
    if level:
        try:
            session_level = SessionLevel(level)
        except ValueError:
            return ApiResponse.error(f"无效的会话层级: {level}", status_code=400)
    
    sessions = await session_manager.list_user_sessions(
        user_id=user.id,
        novel_id=novel_id,
        level=session_level
    )
    
    return ApiResponse.success({
        "sessions": [
            {
                "session_id": s.session_id,
                "level": s.level.value,
                "display_name": s.get_display_name(),
                "novel_id": s.novel_id,
                "chapter_number": s.chapter_number,
                "message_count": s.get_message_count(),
                "model": s.model,
                "created_at": s.created_at.isoformat(),
                "updated_at": s.updated_at.isoformat(),
                "preview": s.messages[-1].content[:100] if s.messages else ""
            }
            for s in sessions[:limit]
        ],
        "total": len(sessions)
    })


@router.post("/{session_id}/clear")
async def clear_messages(
    user: CurrentUser,
    session_id: str,
    keep_system: bool = True
):
    """清空会话消息"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权操作此会话", status_code=403)
    
    if keep_system:
        session.messages = [
            m for m in session.messages
            if m.role == MessageRole.SYSTEM
        ]
    else:
        session.messages = []
    
    session.summary = None
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "消息已清空",
        "remaining_messages": len(session.messages)
    })


@router.post("/{session_id}/compress")
async def compress_session(
    user: CurrentUser,
    session_id: str
):
    """手动压缩会话"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权操作此会话", status_code=403)
    
    summary = await llm_service._generate_summary(session)
    session_manager.compress_session(session, summary)
    await session_manager.save_session(session)
    
    stats = session_manager.get_session_stats(session)
    
    return ApiResponse.success({
        "message": "会话已压缩",
        "stats": stats,
        "summary": session.summary
    })


@router.get("/{session_id}/stats")
async def get_session_stats(
    user: CurrentUser,
    session_id: str
):
    """获取会话统计信息"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权访问此会话", status_code=403)
    
    stats = session_manager.get_session_stats(session)
    return ApiResponse.success(stats)


@router.put("/{session_id}/context/novel")
async def update_novel_context(
    user: CurrentUser,
    session_id: str,
    title: str = "",
    description: str = "",
    genre: str = "",
    outline: str = "",
    world_setting: str = "",
    characters_summary: str = "",
    main_plot: str = ""
):
    """更新小说级上下文"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权操作此会话", status_code=403)
    
    novel_context = NovelContext(
        title=title,
        description=description,
        genre=genre,
        outline=outline,
        world_setting=world_setting,
        characters_summary=characters_summary,
        main_plot=main_plot
    )
    
    session_manager.update_novel_context(session, novel_context)
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "小说上下文已更新",
        "novel_context": novel_context.__dict__
    })


@router.put("/{session_id}/context/chapter")
async def update_chapter_context(
    user: CurrentUser,
    session_id: str,
    chapter_number: int,
    chapter_title: str = "",
    previous_summary: str = "",
    current_outline: str = "",
    key_events: List[str] = None,
    focus_characters: List[str] = None
):
    """更新章节级上下文"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权操作此会话", status_code=403)
    
    chapter_context = ChapterContext(
        chapter_number=chapter_number,
        chapter_title=chapter_title,
        previous_summary=previous_summary,
        current_outline=current_outline,
        key_events=key_events or [],
        focus_characters=focus_characters or []
    )
    
    session_manager.update_chapter_context(session, chapter_context)
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "章节上下文已更新",
        "chapter_context": chapter_context.__dict__
    })


@router.put("/{session_id}/title")
async def update_session_title(
    user: CurrentUser,
    session_id: str,
    title: str
):
    """更新会话标题（用户手动修改）"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权操作此会话", status_code=403)
    
    session.title = title[:50]
    session.updated_at = datetime.now()
    await session_manager.save_session(session)
    
    return ApiResponse.success({
        "message": "标题已更新",
        "title": session.title,
        "display_name": session.get_display_name()
    })


@router.post("/{session_id}/title/auto-generate")
async def auto_generate_title(
    user: CurrentUser,
    session_id: str
):
    """自动生成会话标题（基于对话内容）"""
    session = await session_manager.load_session(session_id)
    
    if not session:
        return ApiResponse.error("会话不存在", status_code=404)
    
    if session.user_id != user.id:
        return ApiResponse.error("无权操作此会话", status_code=403)
    
    if len(session.messages) < 2:
        default_title = session.generate_default_title()
        session.title = default_title
        await session_manager.save_session(session)
        return ApiResponse.success({
            "title": default_title,
            "auto_generated": False,
            "message": "对话内容太少，使用默认标题"
        })
    
    user_messages = [m.content for m in session.messages if m.role == MessageRole.USER]
    if not user_messages:
        default_title = session.generate_default_title()
        session.title = default_title
        await session_manager.save_session(session)
        return ApiResponse.success({
            "title": default_title,
            "auto_generated": False,
            "message": "无用户消息，使用默认标题"
        })
    
    first_user_msg = user_messages[0][:200]
    
    prompt = f"""请根据以下对话内容，生成一个简洁的会话标题（不超过15个字）。

用户消息：{first_user_msg}

要求：
1. 标题要简洁明了
2. 体现对话主题
3. 不要使用引号
4. 直接返回标题文本，不要其他内容

标题格式要求：
- 如果是章节相关：格式为"第X章 - xxx"或"第X-Y章 - xxx"
- 如果是小说全局：格式为"小说 - xxx"
- 如果是自由对话：直接描述主题

请生成标题："""

    try:
        generated_title = await llm_service.generate_text(
            prompt=prompt,
            model=session.model,
            temperature=0.3,
            max_tokens=50
        )
        
        generated_title = generated_title.strip().strip('"\'').strip()[:50]
        
        if session.level == SessionLevel.CHAPTER:
            if session.chapter_number:
                chapter_prefix = f"第{session.chapter_number}"
                if session.chapter_number_end and session.chapter_number_end != session.chapter_number:
                    chapter_prefix = f"第{session.chapter_number}-{session.chapter_number_end}"
                
                if not generated_title.startswith(chapter_prefix):
                    generated_title = f"{chapter_prefix}章 - {generated_title}"
        
        elif session.level == SessionLevel.NOVEL:
            if not generated_title.startswith("小说"):
                generated_title = f"小说 - {generated_title}"
        
        session.title = generated_title
        session.updated_at = datetime.now()
        await session_manager.save_session(session)
        
        return ApiResponse.success({
            "title": generated_title,
            "auto_generated": True,
            "message": "标题已自动生成"
        })
        
    except Exception as e:
        logger.error(f"Auto generate title failed: {e}")
        default_title = session.generate_default_title()
        session.title = default_title
        await session_manager.save_session(session)
        
        return ApiResponse.success({
            "title": default_title,
            "auto_generated": False,
            "message": f"自动生成失败，使用默认标题: {str(e)}"
        })
