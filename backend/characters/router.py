"""
角色管理模块 - API路由
"""
from fastapi import APIRouter, Query
from sqlalchemy import select, func
from sqlalchemy.orm import selectinload
from typing import Optional

from core.response import ApiResponse
from core.database import DBSession
from core.auth import CurrentUserDep
from core.dependencies import NovelOwner
from core.exceptions import NotFoundException, UnauthorizedException, BadRequestException
from core.redis_service import redis_service
from novels.models import Novel
from .models import Character, CharacterRelation
from .schemas import (
    CharacterCreate, CharacterUpdate,
    CharacterRelationCreate, CharacterRelationUpdate,
    CharacterRelationEvolve
)

router = APIRouter(prefix="/characters", tags=["characters"])


@router.get("/novel/{novel_id}")
async def get_characters_by_novel(
    novel: NovelOwner,
    db: DBSession,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    search: Optional[str] = Query(None, max_length=50)
):
    """
    获取小说角色列表
    
    - novel_id: 小说ID
    - page: 页码
    - page_size: 每页数量
    - search: 角色名搜索
    """
    cache_key = f"novel:{novel.id}:characters:{page}:{page_size}:{search}"
    cached = await redis_service.get(cache_key)
    if cached:
        return ApiResponse.paginated(cached["items"], cached["total"], page, page_size)
    
    query = select(Character).where(Character.novel_id == novel.id)
    
    if search:
        query = query.where(Character.name.contains(search))
    
    count_query = select(func.count()).select_from(query.subquery())
    total_result = await db.execute(count_query)
    total = total_result.scalar()
    
    query = query.offset((page - 1) * page_size).limit(page_size)
    result = await db.execute(query)
    characters = result.scalars().all()
    
    items = [
        {
            "id": ch.id,
            "novel_id": ch.novel_id,
            "name": ch.name,
            "personality": ch.personality,
            "relationships": ch.relationships,
            "abilities": ch.abilities,
            "created_at": ch.created_at
        }
        for ch in characters
    ]
    
    await redis_service.set(cache_key, {"items": items, "total": total}, ttl=120)
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.post("", status_code=201)
async def create_character(
    character: CharacterCreate, 
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    创建角色
    """
    result = await db.execute(
        select(Novel).where(Novel.id == character.novel_id)
    )
    novel = result.scalar_one_or_none()
    
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此小说")
    
    db_character = Character(**character.model_dump())
    db.add(db_character)
    await db.commit()
    await db.refresh(db_character)
    
    await redis_service.clear_pattern(f"novel:{character.novel_id}:characters:*")
    
    from core.context_builder import context_cache
    context_cache.invalidate_novel(character.novel_id)
    
    return ApiResponse.success(
        {
            "id": db_character.id,
            "novel_id": db_character.novel_id,
            "name": db_character.name,
            "personality": db_character.personality,
            "relationships": db_character.relationships,
            "abilities": db_character.abilities,
            "created_at": db_character.created_at
        },
        message="角色创建成功"
    )


@router.get("/{character_id}")
async def get_character(
    character_id: int, 
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    获取角色详情
    """
    cache_key = f"character:{character_id}:detail"
    cached = await redis_service.get(cache_key)
    if cached:
        return ApiResponse.success(cached)
    
    result = await db.execute(
        select(Character)
        .options(selectinload(Character.novel))
        .where(Character.id == character_id)
    )
    character = result.scalar_one_or_none()
    
    if character is None:
        raise NotFoundException("角色")
    
    if character.novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此角色")
    
    data = {
        "id": character.id,
        "novel_id": character.novel_id,
        "name": character.name,
        "personality": character.personality,
        "relationships": character.relationships,
        "abilities": character.abilities,
        "created_at": character.created_at,
        "novel": {
            "id": character.novel.id,
            "title": character.novel.title
        }
    }
    
    await redis_service.set(cache_key, data, ttl=300)
    
    return ApiResponse.success(data)


@router.put("/{character_id}")
async def update_character(
    character_id: int, 
    character: CharacterUpdate, 
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    更新角色
    """
    result = await db.execute(
        select(Character)
        .options(selectinload(Character.novel))
        .where(Character.id == character_id)
    )
    db_character = result.scalar_one_or_none()
    
    if db_character is None:
        raise NotFoundException("角色")
    
    if db_character.novel.author_id != current_user.id:
        raise UnauthorizedException("无权修改此角色")
    
    update_data = character.model_dump(exclude_unset=True)
    for key, value in update_data.items():
        setattr(db_character, key, value)
    
    await db.commit()
    await db.refresh(db_character)
    
    await redis_service.delete(f"character:{character_id}:detail")
    await redis_service.clear_pattern(f"novel:{db_character.novel_id}:characters:*")
    
    return ApiResponse.success(
        {
            "id": db_character.id,
            "name": db_character.name,
            "personality": db_character.personality,
            "relationships": db_character.relationships,
            "abilities": db_character.abilities
        },
        message="角色更新成功"
    )


@router.delete("/{character_id}")
async def delete_character(
    character_id: int, 
    db: DBSession,
    current_user: CurrentUserDep
):
    """
    删除角色
    """
    result = await db.execute(
        select(Character)
        .options(selectinload(Character.novel))
        .where(Character.id == character_id)
    )
    db_character = result.scalar_one_or_none()
    
    if db_character is None:
        raise NotFoundException("角色")
    
    if db_character.novel.author_id != current_user.id:
        raise UnauthorizedException("无权删除此角色")
    
    novel_id = db_character.novel_id
    
    await db.delete(db_character)
    await db.commit()
    
    await redis_service.delete(f"character:{character_id}:detail")
    await redis_service.clear_pattern(f"novel:{novel_id}:characters:*")
    
    return ApiResponse.success(message="角色删除成功")


def _relation_to_dict(rel: CharacterRelation) -> dict:
    return {
        "id": rel.id,
        "novel_id": rel.novel_id,
        "source_character_id": rel.source_character_id,
        "target_character_id": rel.target_character_id,
        "relationship_type": rel.relationship_type,
        "description": rel.description,
        "intensity": rel.intensity,
        "status": rel.status,
        "established_chapter_id": rel.established_chapter_id,
        "evolved_from_id": rel.evolved_from_id,
        "extra_metadata": rel.extra_metadata,
        "created_at": rel.created_at,
        "updated_at": rel.updated_at
    }


@router.get("/relations")
async def list_relations(
    db: DBSession,
    current_user: CurrentUserDep,
    novel_id: int = Query(..., description="小说ID"),
    character_id: Optional[int] = Query(None, description="角色ID筛选(source或target)"),
    relationship_type: Optional[str] = Query(None, description="关系类型筛选"),
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100)
):
    result = await db.execute(select(Novel).where(Novel.id == novel_id))
    novel = result.scalar_one_or_none()
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此小说")

    query = select(CharacterRelation).where(CharacterRelation.novel_id == novel_id)

    if character_id:
        from sqlalchemy import or_
        query = query.where(
            or_(
                CharacterRelation.source_character_id == character_id,
                CharacterRelation.target_character_id == character_id
            )
        )
    if relationship_type:
        query = query.where(CharacterRelation.relationship_type == relationship_type)

    count_query = select(func.count()).select_from(query.subquery())
    total_result = await db.execute(count_query)
    total = total_result.scalar()

    query = query.order_by(CharacterRelation.created_at.desc())
    query = query.offset((page - 1) * page_size).limit(page_size)
    result = await db.execute(query)
    relations = result.scalars().all()

    items = [_relation_to_dict(r) for r in relations]
    return ApiResponse.paginated(items, total, page, page_size)


@router.post("/relations", status_code=201)
async def create_relation(
    body: CharacterRelationCreate,
    db: DBSession,
    current_user: CurrentUserDep
):
    if body.source_character_id == body.target_character_id:
        raise BadRequestException("source与target不能是同一角色")

    result = await db.execute(select(Novel).where(Novel.id == body.novel_id))
    novel = result.scalar_one_or_none()
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != current_user.id:
        raise UnauthorizedException("无权操作此小说")

    src_result = await db.execute(
        select(Character).where(Character.id == body.source_character_id)
    )
    source = src_result.scalar_one_or_none()
    tgt_result = await db.execute(
        select(Character).where(Character.id == body.target_character_id)
    )
    target = tgt_result.scalar_one_or_none()
    if source is None or target is None:
        raise NotFoundException("角色")
    if source.novel_id != body.novel_id or target.novel_id != body.novel_id:
        raise BadRequestException("两个角色必须属于同一本小说")

    db_rel = CharacterRelation(
        novel_id=body.novel_id,
        source_character_id=body.source_character_id,
        target_character_id=body.target_character_id,
        relationship_type=body.relationship_type,
        description=body.description,
        intensity=body.intensity,
        established_chapter_id=body.established_chapter_id,
        extra_metadata=body.extra_metadata
    )
    db.add(db_rel)
    await db.commit()
    await db.refresh(db_rel)

    return ApiResponse.success(_relation_to_dict(db_rel), message="关系创建成功")


@router.put("/relations/{relation_id}")
async def update_relation(
    relation_id: int,
    body: CharacterRelationUpdate,
    db: DBSession,
    current_user: CurrentUserDep
):
    result = await db.execute(
        select(CharacterRelation).where(CharacterRelation.id == relation_id)
    )
    rel = result.scalar_one_or_none()
    if rel is None:
        raise NotFoundException("关系")

    novel_result = await db.execute(select(Novel).where(Novel.id == rel.novel_id))
    novel = novel_result.scalar_one()
    if novel.author_id != current_user.id:
        raise UnauthorizedException("无权修改此关系")

    update_data = body.model_dump(exclude_unset=True)
    for key, value in update_data.items():
        setattr(rel, key, value)

    await db.commit()
    await db.refresh(rel)

    return ApiResponse.success(_relation_to_dict(rel), message="关系更新成功")


@router.post("/relations/{relation_id}/evolve", status_code=201)
async def evolve_relation(
    relation_id: int,
    body: CharacterRelationEvolve,
    db: DBSession,
    current_user: CurrentUserDep
):
    result = await db.execute(
        select(CharacterRelation).where(CharacterRelation.id == relation_id)
    )
    old_rel = result.scalar_one_or_none()
    if old_rel is None:
        raise NotFoundException("关系")

    novel_result = await db.execute(select(Novel).where(Novel.id == old_rel.novel_id))
    novel = novel_result.scalar_one()
    if novel.author_id != current_user.id:
        raise UnauthorizedException("无权操作此关系")

    if body.mark_old_as_dormant:
        old_rel.status = "dormant"

    new_rel = CharacterRelation(
        novel_id=old_rel.novel_id,
        source_character_id=old_rel.source_character_id,
        target_character_id=old_rel.target_character_id,
        relationship_type=body.relationship_type,
        description=body.description,
        intensity=body.intensity or old_rel.intensity,
        established_chapter_id=body.established_chapter_id,
        evolved_from_id=relation_id,
        extra_metadata=body.extra_metadata
    )
    db.add(new_rel)
    await db.commit()
    await db.refresh(new_rel)

    return ApiResponse.success(_relation_to_dict(new_rel), message="关系演变成功")


@router.get("/relations/network")
async def get_relation_network(
    db: DBSession,
    current_user: CurrentUserDep,
    novel_id: int = Query(..., description="小说ID")
):
    result = await db.execute(select(Novel).where(Novel.id == novel_id))
    novel = result.scalar_one_or_none()
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此小说")

    char_result = await db.execute(
        select(Character).where(Character.novel_id == novel_id)
    )
    characters = char_result.scalars().all()

    nodes = [
        {"id": ch.id, "name": ch.name, "novel_id": ch.novel_id}
        for ch in characters
    ]

    rel_result = await db.execute(
        select(CharacterRelation).where(
            CharacterRelation.novel_id == novel_id,
            CharacterRelation.status == "active"
        )
    )
    relations = rel_result.scalars().all()

    edges = [
        {
            "id": r.id,
            "source": r.source_character_id,
            "target": r.target_character_id,
            "type": r.relationship_type,
            "intensity": r.intensity
        }
        for r in relations
    ]

    return ApiResponse.success({
        "nodes": nodes,
        "edges": edges,
        "total_nodes": len(nodes),
        "total_edges": len(edges)
    })


@router.get("/relations/{character_id}/for-character")
async def get_relations_for_character(
    character_id: int,
    db: DBSession,
    current_user: CurrentUserDep
):
    from sqlalchemy import or_

    result = await db.execute(
        select(Character)
        .options(selectinload(Character.novel))
        .where(Character.id == character_id)
    )
    character = result.scalar_one_or_none()
    if character is None:
        raise NotFoundException("角色")
    if character.novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此角色")

    rel_result = await db.execute(
        select(CharacterRelation).where(
            CharacterRelation.novel_id == character.novel_id,
            or_(
                CharacterRelation.source_character_id == character_id,
                CharacterRelation.target_character_id == character_id
            )
        ).order_by(CharacterRelation.created_at.desc())
    )
    relations = rel_result.scalars().all()

    items = [_relation_to_dict(r) for r in relations]
    return ApiResponse.success(items)
