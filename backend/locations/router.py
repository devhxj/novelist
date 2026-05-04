"""
地点管理模块 - HTTP API路由
"""
from typing import List, Optional
from fastapi import APIRouter, Depends, Query
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from core.database import get_db
from core.auth import CurrentUserDep
from core.response import ApiResponse
from core.exceptions import NotFoundException, BadRequestException
from .models import Location
from .schemas import (
    LocationCreate, LocationUpdate, LocationResponse, LocationNetworkResponse,
)
from .service import LocationService

router = APIRouter(prefix="/locations", tags=["地点管理"])


@router.get("")
async def list_locations(
    user: CurrentUserDep,
    novel_id: int = Query(...),
    location_type: Optional[str] = None,
    search: Optional[str] = None,
    page: int = Query(1, ge=1),
    page_size: int = Query(50, ge=1, le=100),
    db: AsyncSession = Depends(get_db),
):
    query = select(Location).where(Location.novel_id == novel_id)
    
    count_query = select(func.count()).select_from(query.subquery())
    
    if location_type:
        query = query.where(Location.location_type == location_type)
    if search:
        from sqlalchemy import or_
        query = query.where(or_(
            Location.name.ilike(f"%{search}%"),
            Location.description.ilike(f"%{search}%"),
        ))
    
    total_result = await db.execute(count_query)
    total = total_result.scalar() or 0
    
    query = query.order_by(Location.name).offset((page - 1) * page_size).limit(page_size)
    result = await db.execute(query)
    locations = result.scalars().all()
    
    items = []
    for loc in locations:
        parent_name = None
        if loc.parent_location_id:
            parent = await db.get(Location, location.parent_location_id)
            if parent:
                parent_name = parent.name
        items.append({
            **{k: v for k, v in {
                "id": loc.id, "name": loc.name, "location_type": loc.location_type,
                "description": loc.description, "geo_info": loc.geo_info,
                "tags": loc.tags, "parent_location_id": loc.parent_location_id,
                "created_at": loc.created_at.isoformat() if loc.created_at else None,
            }.items()},
            "parent_name": parent_name,
        })
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.post("", status_code=201)
async def create_location(
    user: CurrentUserDep,
    data: LocationCreate,
    db: AsyncSession = Depends(get_db),
):
    svc = LocationService(db, data.novel_id if hasattr(data, 'novel_id') else 0)
    from novels.models import Novel
    novel_result = await db.execute(select(Novel))
    novels = novel_result.scalars().first()
    if not novels:
        raise BadRequestException("请先创建小说")
    
    actual_data = LocationCreate(**data.model_dump(), novel_id=novels.id if isinstance(novels.id, int) else 1)
    svc = LocationService(db, novels.id)
    location = await svc.create(actual_data)
    return ApiResponse.success(LocationResponse.model_validate(location).model_dump(), status_code=201)


@router.get("/network")
async def get_location_network(
    user: CurrentUserDep,
    novel_id: int = Query(...),
    db: AsyncSession = Depends(get_db),
):
    svc = LocationService(db, novel_id)
    network = await svc.get_network()
    return ApiResponse.success(network)


@router.get("/{location_id}")
async def get_location_detail(
    user: CurrentUserDep,
    location_id: int,
    db: AsyncSession = Depends(get_db),
):
    location = await db.get(Location, location_id)
    if not location:
        raise NotFoundException("地点不存在")
    parent_name = None
    if location.parent_location_id:
        parent = await db.get(Location, loc.parent_location_id)
        if parent:
            parent_name = parent.name
    return ApiResponse.success({
        **{k: v for k, v in {
            "id": location.id, "name": location.name, "location_type": location.location_type,
            "description": location.description, "geo_info": location.geo_info,
            "related_characters": location.related_characters, "tags": location.tags,
            "created_at": location.created_at.isoformat() if location.created_at else None,
        }.items()},
        "parent_name": parent_name,
    })


@router.put("/{location_id}")
async def update_location(
    user: CurrentUserDep,
    location_id: int,
    data: LocationUpdate,
    db: AsyncSession = Depends(get_db),
):
    svc = LocationService(db, 0)
    location = await svc.update(location_id, data)
    if not location:
        raise NotFoundException("地点不存在或无权修改")
    return ApiResponse.success(LocationResponse.model_validate(location).model_dump())


@router.delete("/{location_id}")
async def delete_location(
    user: CurrentUserDep,
    location_id: int,
    db: AsyncSession = Depends(get_db),
):
    svc = LocationService(db, 0)
    success = await svc.delete(location_id)
    if not success:
        raise NotFoundException("地点不存在或无权删除")
    return ApiResponse.success(message="删除成功")
