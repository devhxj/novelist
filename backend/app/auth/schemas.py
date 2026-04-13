"""
认证模块 - Pydantic Schemas
"""
from pydantic import BaseModel, EmailStr, Field, ConfigDict
from datetime import datetime


class UserRegister(BaseModel):
    username: str = Field(..., min_length=3, max_length=50)
    email: EmailStr
    password: str = Field(..., min_length=6, max_length=100)


class UserLogin(BaseModel):
    username: str
    password: str


class CurrentUser(BaseModel):
    """当前用户信息（从JWT解析，不查数据库）"""
    id: int
    username: str = ""
    email: str = ""

    model_config = ConfigDict(from_attributes=True)


class UserResponse(BaseModel):
    user_id: int
    username: str
    email: str
    created_at: datetime

    model_config = ConfigDict(from_attributes=True)


class TokenResponse(BaseModel):
    access_token: str
    refresh_token: str
    token_type: str = "bearer"
    expires_in: int


class TokenRefresh(BaseModel):
    refresh_token: str
