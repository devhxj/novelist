"""
情节事件管理模块 - API路由
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
from .models import PlotEvent
from .schemas import PlotEventCreate, PlotEventUpdate

router = APIRouter(prefix="/plot-events", tags=["plot-events"])


@router.get("/novel/{novel_id}")
def get_plot_events_by_novel(
    novel: NovelOwner,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    chapter_id: Optional[int] = None,
    event_type: Optional[str] = Query(None, max_length=50),
    db: Session = Depends(get_db)
):
    """
    获取情节事件列表
    
    - novel_id: 小说ID
    - page: 页码
    - page_size: 每页数量
    - chapter_id: 章节ID筛选
    - event_type: 事件类型筛选
    """
    query = db.query(PlotEvent).filter(PlotEvent.novel_id == novel.id)
    
    if chapter_id:
        query = query.filter(PlotEvent.chapter_id == chapter_id)
    if event_type:
        query = query.filter(PlotEvent.event_type == event_type)
    
    total = query.count()
    events = query.offset((page - 1) * page_size).limit(page_size).all()
    
    items = [
        {
            "id": pe.id,
            "novel_id": pe.novel_id,
            "chapter_id": pe.chapter_id,
            "event_type": pe.event_type,
            "description": pe.description,
            "characters_involved": pe.characters_involved,
            "timeline": pe.timeline,
            "consequences": pe.consequences,
            "created_at": pe.created_at
        }
        for pe in events
    ]
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.post("", status_code=201)
def create_plot_event(
    event: PlotEventCreate, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    创建情节事件
    """
    novel = db.query(Novel).filter(Novel.id == event.novel_id).first()
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != current_user.id:
        from app.core.exceptions import UnauthorizedException
        raise UnauthorizedException("无权访问此小说")
    
    db_event = PlotEvent(**event.dict())
    db.add(db_event)
    db.commit()
    db.refresh(db_event)
    
    return ApiResponse.success(
        {
            "id": db_event.id,
            "novel_id": db_event.novel_id,
            "chapter_id": db_event.chapter_id,
            "event_type": db_event.event_type,
            "description": db_event.description,
            "characters_involved": db_event.characters_involved,
            "timeline": db_event.timeline,
            "consequences": db_event.consequences,
            "created_at": db_event.created_at
        },
        message="情节事件创建成功"
    )


@router.get("/{event_id}")
def get_plot_event(
    event_id: int, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    获取情节事件详情
    """
    event = db.query(PlotEvent).filter(PlotEvent.id == event_id).first()
    if event is None:
        raise NotFoundException("情节事件")
    
    if event.novel.author_id != current_user.id:
        from app.core.exceptions import UnauthorizedException
        raise UnauthorizedException("无权访问此情节事件")
    
    return ApiResponse.success({
        "id": event.id,
        "novel_id": event.novel_id,
        "chapter_id": event.chapter_id,
        "event_type": event.event_type,
        "description": event.description,
        "characters_involved": event.characters_involved,
        "timeline": event.timeline,
        "consequences": event.consequences,
        "created_at": event.created_at,
        "novel": {
            "id": event.novel.id,
            "title": event.novel.title
        },
        "chapter": {
            "id": event.chapter.id,
            "chapter_number": event.chapter.chapter_number,
            "title": event.chapter.title
        } if event.chapter else None
    })


@router.put("/{event_id}")
def update_plot_event(
    event_id: int, 
    event: PlotEventUpdate, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    更新情节事件
    """
    db_event = db.query(PlotEvent).filter(PlotEvent.id == event_id).first()
    if db_event is None:
        raise NotFoundException("情节事件")
    
    if db_event.novel.author_id != current_user.id:
        from app.core.exceptions import UnauthorizedException
        raise UnauthorizedException("无权修改此情节事件")
    
    update_data = event.dict(exclude_unset=True)
    for key, value in update_data.items():
        setattr(db_event, key, value)
    
    db.commit()
    db.refresh(db_event)
    
    return ApiResponse.success(
        {
            "id": db_event.id,
            "event_type": db_event.event_type,
            "description": db_event.description,
            "updated_at": db_event.created_at
        },
        message="情节事件更新成功"
    )


@router.delete("/{event_id}")
def delete_plot_event(
    event_id: int, 
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    删除情节事件
    """
    db_event = db.query(PlotEvent).filter(PlotEvent.id == event_id).first()
    if db_event is None:
        raise NotFoundException("情节事件")
    
    if db_event.novel.author_id != current_user.id:
        from app.core.exceptions import UnauthorizedException
        raise UnauthorizedException("无权删除此情节事件")
    
    db.delete(db_event)
    db.commit()
    
    return ApiResponse.success(message="情节事件删除成功")
