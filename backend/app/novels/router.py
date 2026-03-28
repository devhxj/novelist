"""
小说管理模块 - API路由
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
from .models import Novel
from .schemas import NovelCreate, NovelUpdate

router = APIRouter(prefix="/novels", tags=["novels"])


@router.get("")
def get_novels(
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    status: Optional[str] = None,
    genre: Optional[str] = None,
    search: Optional[str] = Query(None, max_length=100),
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    获取小说列表（仅返回当前用户的小说）
    
    - page: 页码，默认1
    - page_size: 每页数量，默认20
    - status: 状态筛选 (draft/writing/completed/published)
    - genre: 类型筛选
    - search: 标题搜索
    """
    query = db.query(Novel).filter(Novel.author_id == current_user.id)
    
    if status:
        query = query.filter(Novel.status == status)
    if genre:
        query = query.filter(Novel.genre == genre)
    if search:
        query = query.filter(Novel.title.contains(search))
    
    total = query.count()
    novels = query.offset((page - 1) * page_size).limit(page_size).all()
    
    items = []
    for novel in novels:
        item = {
            "id": novel.id,
            "title": novel.title,
            "genre": novel.genre,
            "description": novel.description,
            "author_id": novel.author_id,
            "status": novel.status,
            "chapter_count": len(novel.chapters),
            "word_count": sum(len(ch.content or "") for ch in novel.chapters),
            "created_at": novel.created_at,
            "updated_at": novel.updated_at
        }
        items.append(item)
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.post("", status_code=201)
def create_novel(
    novel: NovelCreate, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    创建小说
    """
    db_novel = Novel(
        title=novel.title,
        genre=novel.genre,
        description=novel.description,
        author_id=current_user.id
    )
    db.add(db_novel)
    db.commit()
    db.refresh(db_novel)
    
    return ApiResponse.success(
        {
            "id": db_novel.id,
            "title": db_novel.title,
            "genre": db_novel.genre,
            "description": db_novel.description,
            "author_id": db_novel.author_id,
            "status": db_novel.status,
            "created_at": db_novel.created_at,
            "updated_at": db_novel.updated_at
        },
        message="小说创建成功"
    )


@router.get("/{novel_id}")
def get_novel(novel: NovelOwner):
    """
    获取小说详情
    """
    return ApiResponse.success({
        "id": novel.id,
        "title": novel.title,
        "genre": novel.genre,
        "description": novel.description,
        "author_id": novel.author_id,
        "status": novel.status,
        "chapter_count": len(novel.chapters),
        "word_count": sum(len(ch.content or "") for ch in novel.chapters),
        "character_count": len(novel.characters),
        "created_at": novel.created_at,
        "updated_at": novel.updated_at,
        "characters": [
            {
                "id": ch.id,
                "name": ch.name,
                "personality": ch.personality
            } for ch in novel.characters
        ],
        "chapters": [
            {
                "id": ch.id,
                "chapter_number": ch.chapter_number,
                "title": ch.title,
                "status": ch.status
            } for ch in sorted(novel.chapters, key=lambda x: x.chapter_number)
        ]
    })


@router.put("/{novel_id}")
def update_novel(
    novel: NovelOwner,
    novel_data: NovelUpdate,
    db: Session = Depends(get_db)
):
    """
    更新小说
    """
    update_data = novel_data.dict(exclude_unset=True)
    for key, value in update_data.items():
        setattr(novel, key, value)
    
    db.commit()
    db.refresh(novel)
    
    return ApiResponse.success(
        {
            "id": novel.id,
            "title": novel.title,
            "genre": novel.genre,
            "description": novel.description,
            "status": novel.status,
            "updated_at": novel.updated_at
        },
        message="小说更新成功"
    )


@router.delete("/{novel_id}")
def delete_novel(
    novel: NovelOwner,
    db: Session = Depends(get_db)
):
    """
    删除小说
    """
    db.delete(novel)
    db.commit()
    
    return ApiResponse.success(message="小说删除成功")
