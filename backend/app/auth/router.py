"""
认证模块 - API路由
"""
from fastapi import APIRouter, status
from sqlalchemy import select, or_

from app.core.response import ApiResponse
from app.core.jwt import hash_password, verify_password, create_tokens, decode_token, create_access_token
from app.core.database import DBSession
from app.core.auth import CurrentUser
from .models import User
from .schemas import UserRegister, UserLogin

router = APIRouter(prefix="/auth", tags=["auth"])


@router.post("/register", status_code=201)
async def register(user_data: UserRegister, db: DBSession):
    """
    用户注册
    
    - username: 用户名 (3-50字符)
    - email: 邮箱地址
    - password: 密码 (6-100字符)
    """
    result = await db.execute(
        select(User).where(
            or_(User.username == user_data.username, User.email == user_data.email)
        )
    )
    existing_user = result.scalar_one_or_none()
    
    if existing_user:
        if existing_user.username == user_data.username:
            return ApiResponse.error(
                code="VALIDATION_001",
                message="用户名已存在",
                status_code=status.HTTP_422_UNPROCESSABLE_ENTITY
            )
        else:
            return ApiResponse.error(
                code="VALIDATION_001",
                message="邮箱已被注册",
                status_code=status.HTTP_422_UNPROCESSABLE_ENTITY
            )
    
    new_user = User(
        username=user_data.username,
        email=user_data.email,
        password_hash=hash_password(user_data.password)
    )
    db.add(new_user)
    await db.commit()
    await db.refresh(new_user)
    
    return ApiResponse.success(
        {
            "user_id": new_user.id,
            "username": new_user.username,
            "email": new_user.email,
            "created_at": new_user.created_at
        },
        message="注册成功"
    )


@router.post("/login")
async def login(login_data: UserLogin, db: DBSession):
    """
    用户登录
    
    - username: 用户名
    - password: 密码
    """
    result = await db.execute(
        select(User).where(User.username == login_data.username)
    )
    user = result.scalar_one_or_none()
    
    if not user or not verify_password(login_data.password, user.password_hash):
        return ApiResponse.error(
            code="AUTH_001",
            message="用户名或密码错误",
            status_code=status.HTTP_401_UNAUTHORIZED
        )
    
    tokens = create_tokens(user.id, user.username, user.email)
    
    return ApiResponse.success(tokens, message="登录成功")


@router.post("/refresh")
async def refresh_token(
    refresh_token: str,
    db: DBSession
):
    """
    刷新Token
    
    - refresh_token: 刷新令牌
    - db: 数据库会话
    """
    payload = decode_token(refresh_token)
    
    if payload is None:
        return ApiResponse.error(
            code="AUTH_002",
            message="Token无效或已过期",
            status_code=status.HTTP_401_UNAUTHORIZED
        )
    
    if payload.get("type") != "refresh":
        return ApiResponse.error(
            code="AUTH_002",
            message="无效的Token类型",
            status_code=status.HTTP_401_UNAUTHORIZED
        )
    
    user_id = payload.get("sub")
    if user_id is None:
        return ApiResponse.error(
            code="AUTH_002",
            message="Token格式错误",
            status_code=status.HTTP_401_UNAUTHORIZED
        )
    
    new_access_token = create_access_token({
        "sub": user_id,
        "username": payload.get("username"),
        "email": payload.get("email")
    })
    
    return ApiResponse.success({
        "access_token": new_access_token,
        "token_type": "bearer",
        "expires_in": 86400
    })


@router.get("/me")
async def get_me(current_user: CurrentUser):
    """
    获取当前用户信息
    
    需要在请求头中携带: Authorization: Bearer <access_token>
    """
    return ApiResponse.success({
        "user_id": current_user.id,
        "username": current_user.username,
        "email": current_user.email,
        "created_at": current_user.created_at
    })
