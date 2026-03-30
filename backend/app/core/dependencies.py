"""
公共依赖注入
"""
from typing import Annotated
from fastapi import Depends, Path
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from app.core.database import get_db
from app.core.exceptions import NotFoundException, UnauthorizedException
from app.core.auth import CurrentUser
from app.novels.models import Novel


async def check_novel_ownership(
    novel_id: Annotated[int, Path(..., description="小说ID")],
    current_user: CurrentUser,
    db: AsyncSession = Depends(get_db)
) -> Novel:
    """
    检查小说所有权 - 依赖注入版本
    
    使用方式:
        @router.get("/novels/{novel_id}")
        async def get_novel(novel: Novel = Depends(check_novel_ownership)):
            return novel
    
    或使用 Annotated:
        from app.core.dependencies import NovelOwner
        
        @router.get("/novels/{novel_id}")
        async def get_novel(novel: NovelOwner):
            return novel
    """
    result = await db.execute(
        select(Novel).where(Novel.id == novel_id)
    )
    novel = result.scalar_one_or_none()
    
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此小说")
    return novel


NovelOwner = Annotated[Novel, Depends(check_novel_ownership)]
