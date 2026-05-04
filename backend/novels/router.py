"""
小说管理模块 - API路由
"""
from fastapi import APIRouter, Query
from sqlalchemy import select, func
from typing import Optional

from core.response import ApiResponse
from core.database import DBSession
from core.auth import CurrentUserDep
from core.dependencies import NovelOwner
from core.redis_service import redis_service, NovelCache
from .models import Novel, NovelCreativeProfile
from .schemas import NovelCreate, NovelUpdate, CreativeProfileUpdate, CreativeProfileResponse

router = APIRouter(prefix="/novels", tags=["novels"])


@router.get("")
async def get_novels(
    db: DBSession,
    current_user: CurrentUserDep,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    status: Optional[str] = None,
    genre: Optional[str] = None,
    search: Optional[str] = Query(None, max_length=100)
):
    """
    获取小说列表（仅返回当前用户的小说）
    
    - page: 页码，默认1
    - page_size: 每页数量，默认20
    - status: 状态筛选 (draft/writing/completed/published)
    - genre: 类型筛选
    - search: 标题搜索
    """
    cache_key = f"user:{current_user.id}:novels:{page}:{page_size}:{status}:{genre}:{search}"
    cached = await redis_service.get(cache_key)
    if cached:
        return ApiResponse.paginated(cached["items"], cached["total"], page, page_size)
    
    query = select(Novel).where(Novel.author_id == current_user.id)
    
    if status:
        query = query.where(Novel.status == status)
    if genre:
        query = query.where(Novel.genre == genre)
    if search:
        query = query.where(Novel.title.contains(search))
    
    count_query = select(func.count()).select_from(query.subquery())
    total_result = await db.execute(count_query)
    total = total_result.scalar()
    
    query = query.offset((page - 1) * page_size).limit(page_size)
    result = await db.execute(query)
    novels = result.scalars().all()
    
    items = []
    for novel in novels:
        items.append({
            "id": novel.id,
            "title": novel.title,
            "genre": novel.genre,
            "description": novel.description,
            "author_id": novel.author_id,
            "status": novel.status,
            "created_at": novel.created_at,
            "updated_at": novel.updated_at
        })
    
    await redis_service.set(cache_key, {"items": items, "total": total}, ttl=60)
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.post("", status_code=201)
async def create_novel(
    novel: NovelCreate, 
    db: DBSession,
    current_user: CurrentUserDep
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
    await db.commit()
    await db.refresh(db_novel)
    
    await redis_service.clear_pattern(f"user:{current_user.id}:novels:*")
    
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
async def get_novel(novel: NovelOwner):
    """
    获取小说详情
    """
    cache_key = f"novel:{novel.id}:detail"
    cached = await redis_service.get(cache_key)
    if cached:
        return ApiResponse.success(cached)
    
    data = {
        "id": novel.id,
        "title": novel.title,
        "genre": novel.genre,
        "description": novel.description,
        "author_id": novel.author_id,
        "status": novel.status,
        "created_at": novel.created_at,
        "updated_at": novel.updated_at
    }
    
    await redis_service.set(cache_key, data, ttl=300)
    
    return ApiResponse.success(data)


@router.get("/{novel_id}/creative-profile")
async def get_creative_profile(
    novel: NovelOwner,
    db: DBSession
):
    """获取作者创作配置"""
    result = await db.execute(
        select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == novel.id)
    )
    profile = result.scalar_one_or_none()

    if not profile:
        return ApiResponse.success({
            "novel_id": novel.id,
            "author_intent": None,
            "preferred_tone": None,
            "collaboration_style": "ai_ide",
            "scene_planning_notes": None,
            "must_keep": [],
            "must_avoid": [],
            "long_term_goals": [],
            "extra_metadata": {}
        })

    return ApiResponse.success(CreativeProfileResponse.model_validate(profile))


@router.put("/{novel_id}/creative-profile")
async def update_creative_profile(
    novel: NovelOwner,
    data: CreativeProfileUpdate,
    db: DBSession
):
    """更新作者创作配置"""
    result = await db.execute(
        select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == novel.id)
    )
    profile = result.scalar_one_or_none()

    if not profile:
        profile = NovelCreativeProfile(novel_id=novel.id, collaboration_style="ai_ide")
        db.add(profile)

    update_data = data.model_dump(exclude_unset=True)
    for key, value in update_data.items():
        setattr(profile, key, value)

    await db.commit()
    await db.refresh(profile)

    await redis_service.delete(f"novel:{novel.id}:detail")
    await redis_service.clear_pattern(f"user:{novel.author_id}:novels:*")

    return ApiResponse.success(
        CreativeProfileResponse.model_validate(profile),
        message="创作配置已更新"
    )


@router.put("/{novel_id}")
async def update_novel(
    novel: NovelOwner,
    novel_data: NovelUpdate,
    db: DBSession
):
    """
    更新小说
    """
    update_data = novel_data.model_dump(exclude_unset=True)
    for key, value in update_data.items():
        setattr(novel, key, value)
    
    await db.commit()
    await db.refresh(novel)
    
    await redis_service.delete(f"novel:{novel.id}:detail")
    await redis_service.clear_pattern(f"user:{novel.author_id}:novels:*")
    
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
async def delete_novel(
    novel: NovelOwner,
    db: DBSession
):
    """
    删除小说
    """
    author_id = novel.author_id
    novel_id = novel.id
    
    await db.delete(novel)
    await db.commit()
    
    await NovelCache.invalidate_novel(novel_id)
    await redis_service.clear_pattern(f"user:{author_id}:novels:*")
    
    return ApiResponse.success(message="小说删除成功")
