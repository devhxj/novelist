"""
认证依赖 - 获取当前用户
"""
from typing import Annotated
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPBearer, HTTPAuthorizationCredentials

from core.jwt import decode_token
from auth.schemas import CurrentUser

security = HTTPBearer()


async def get_current_user(
    credentials: HTTPAuthorizationCredentials = Depends(security)
) -> CurrentUser:
    """
    从Token获取当前用户（直接从JWT解析，不查数据库）
    """
    token = credentials.credentials
    payload = decode_token(token)
    
    if payload is None:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail={
                "success": False,
                "error": {
                    "code": "AUTH_002",
                    "message": "Token无效或已过期"
                }
            }
        )
    
    if payload.get("type") != "access":
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail={
                "success": False,
                "error": {
                    "code": "AUTH_002",
                    "message": "无效的Token类型"
                }
            }
        )
    
    user_id = payload.get("sub")
    username = payload.get("username")
    email = payload.get("email")
    
    if not user_id:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail={
                "success": False,
                "error": {
                    "code": "AUTH_002",
                    "message": "Token格式错误"
                }
            }
        )
    
    return CurrentUser(
        id=int(user_id),
        username=username or "",
        email=email or ""
    )


CurrentUserDep = Annotated[CurrentUser, Depends(get_current_user)]
