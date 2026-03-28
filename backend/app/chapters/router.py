"""
章节管理模块 - API路由
"""
from fastapi import APIRouter, Depends, Query
from sqlalchemy.orm import Session
from typing import Optional

from app.core.database import get_db
from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException
from app.core.auth import get_current_user
from app.core.dependencies import NovelOwner
from app.auth.models import User
from app.novels.models import Novel
from .models import Chapter
from .schemas import ChapterCreate, ChapterUpdate

router = APIRouter(prefix="/chapters", tags=["chapters"])


@router.get("/novel/{novel_id}")
def get_chapters_by_novel(
    novel: NovelOwner,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    status: Optional[str] = None,
    order: str = Query("asc", regex="^(asc|desc)$"),
    db: Session = Depends(get_db)
):
    """
    获取小说章节列表
    
    - novel_id: 小说ID
    - page: 页码
    - page_size: 每页数量
    - status: 状态筛选 (draft/completed)
    - order: 排序 (asc/desc)
    """
    query = db.query(Chapter).filter(Chapter.novel_id == novel.id)
    
    if status:
        query = query.filter(Chapter.status == status)
    
    if order == "desc":
        query = query.order_by(Chapter.chapter_number.desc())
    else:
        query = query.order_by(Chapter.chapter_number.asc())
    
    total = query.count()
    chapters = query.offset((page - 1) * page_size).limit(page_size).all()
    
    items = [
        {
            "id": ch.id,
            "novel_id": ch.novel_id,
            "chapter_number": ch.chapter_number,
            "title": ch.title,
            "word_count": len(ch.content or ""),
            "status": ch.status,
            "summary": ch.summary,
            "created_at": ch.created_at,
            "updated_at": ch.updated_at
        }
        for ch in chapters
    ]
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.post("", status_code=201)
def create_chapter(
    chapter: ChapterCreate, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    创建章节
    """
    novel = db.query(Novel).filter(Novel.id == chapter.novel_id).first()
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != current_user.id:
        from app.core.exceptions import UnauthorizedException
        raise UnauthorizedException("无权访问此小说")
    
    db_chapter = Chapter(**chapter.dict())
    db.add(db_chapter)
    db.commit()
    db.refresh(db_chapter)
    
    return ApiResponse.success(
        {
            "id": db_chapter.id,
            "novel_id": db_chapter.novel_id,
            "chapter_number": db_chapter.chapter_number,
            "title": db_chapter.title,
            "content": db_chapter.content,
            "summary": db_chapter.summary,
            "status": db_chapter.status,
            "word_count": len(db_chapter.content or ""),
            "created_at": db_chapter.created_at
        },
        message="章节创建成功"
    )


@router.get("/{chapter_id}")
def get_chapter(
    chapter_id: int, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    获取章节详情
    """
    chapter = db.query(Chapter).filter(Chapter.id == chapter_id).first()
    if chapter is None:
        raise NotFoundException("章节")
    
    if chapter.novel.author_id != current_user.id:
        from app.core.exceptions import UnauthorizedException
        raise UnauthorizedException("无权访问此章节")
    
    return ApiResponse.success({
        "id": chapter.id,
        "novel_id": chapter.novel_id,
        "chapter_number": chapter.chapter_number,
        "title": chapter.title,
        "content": chapter.content,
        "summary": chapter.summary,
        "status": chapter.status,
        "word_count": len(chapter.content or ""),
        "created_at": chapter.created_at,
        "updated_at": chapter.updated_at,
        "novel": {
            "id": chapter.novel.id,
            "title": chapter.novel.title
        },
        "plot_events": [
            {
                "id": pe.id,
                "event_type": pe.event_type,
                "description": pe.description
            }
            for pe in chapter.plot_events
        ]
    })


@router.put("/{chapter_id}")
def update_chapter(
    chapter_id: int, 
    chapter: ChapterUpdate, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    更新章节
    """
    db_chapter = db.query(Chapter).filter(Chapter.id == chapter_id).first()
    if db_chapter is None:
        raise NotFoundException("章节")
    
    if db_chapter.novel.author_id != current_user.id:
        from app.core.exceptions import UnauthorizedException
        raise UnauthorizedException("无权修改此章节")
    
    update_data = chapter.dict(exclude_unset=True)
    for key, value in update_data.items():
        setattr(db_chapter, key, value)
    
    db.commit()
    db.refresh(db_chapter)
    
    return ApiResponse.success(
        {
            "id": db_chapter.id,
            "title": db_chapter.title,
            "content": db_chapter.content,
            "summary": db_chapter.summary,
            "status": db_chapter.status,
            "word_count": len(db_chapter.content or ""),
            "updated_at": db_chapter.updated_at
        },
        message="章节更新成功"
    )


@router.delete("/{chapter_id}")
def delete_chapter(
    chapter_id: int, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    删除章节
    """
    db_chapter = db.query(Chapter).filter(Chapter.id == chapter_id).first()
    if db_chapter is None:
        raise NotFoundException("章节")
    
    if db_chapter.novel.author_id != current_user.id:
        from app.core.exceptions import UnauthorizedException
        raise UnauthorizedException("无权删除此章节")
    
    db.delete(db_chapter)
    db.commit()
    
    return ApiResponse.success(message="章节删除成功")
