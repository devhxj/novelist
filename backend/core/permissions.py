"""
通用权限验证工具
供MCP工具和业务逻辑复用
"""
from typing import Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from novels.models import Novel


async def verify_novel_ownership(
    db: AsyncSession,
    novel_id: int,
    user_id: int
) -> Optional[Novel]:
    """
    验证小说归属权
    
    Args:
        db: 数据库会话
        novel_id: 小说ID
        user_id: 用户ID（从JWT/session获取，非LLM提供）
    
    Returns:
        Novel对象（验证通过）或None（无权访问）
    
    使用示例:
        from core.permissions import verify_novel_ownership
        
        novel = await verify_novel_ownership(db, novel_id, user_id)
        if not novel:
            return MCPToolResult(success=False, error="无权访问此小说或小说不存在")
    """
    if not user_id:
        return None
    
    result = await db.execute(
        select(Novel).where(Novel.id == novel_id, Novel.author_id == user_id)
    )
    return result.scalar_one_or_none()
