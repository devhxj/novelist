# JWT认证方案

## 文档信息
- **版本**: v2.0.0
- **最后更新**: 2026-03-28
- **适用范围**: 前端开发Agent、后端开发Agent

---

## 1. 概述

### 1.1 认证方式
本项目采用 **JWT (JSON Web Token)** 进行用户认证和授权。

### 1.2 JWT优势
- **无状态**: 服务器不需要存储session
- **跨域友好**: 适合前后端分离架构
- **可扩展**: 支持分布式系统

---

## 2. Token设计

### 2.1 Token类型
| Token类型 | 有效期 | 存储位置 | 用途 |
|---------|--------|---------|------|
| Access Token | 24小时 | 内存/LocalStorage | API请求认证 |
| Refresh Token | 7天 | LocalStorage | 刷新Access Token |

### 2.2 Token结构

**Access Token Payload**
```json
{
  "sub": "user_id",
  "username": "string",
  "email": "string",
  "exp": 1234567890,
  "iat": 1234567890,
  "type": "access"
}
```

**Refresh Token Payload**
```json
{
  "sub": "user_id",
  "exp": 1234567890,
  "iat": 1234567890,
  "type": "refresh"
}
```

---

## 3. 后端实现

### 3.1 依赖安装
```bash
pip install python-jose[cryptography] passlib[bcrypt] python-multipart
```

### 3.2 JWT工具类
```python
# backend/app/core/jwt.py
from datetime import datetime, timedelta
from jose import jwt
from passlib.context import CryptContext

pwd_context = CryptContext(schemes=["bcrypt"], deprecated="auto")

class JWTHandler:
    SECRET_KEY = "your-secret-key"
    ALGORITHM = "HS256"
    ACCESS_TOKEN_EXPIRE_HOURS = 24
    REFRESH_TOKEN_EXPIRE_DAYS = 7
    
    @staticmethod
    def create_access_token(data: dict) -> str:
        to_encode = data.copy()
        expire = datetime.utcnow() + timedelta(hours=JWTHandler.ACCESS_TOKEN_EXPIRE_HOURS)
        to_encode.update({"exp": expire, "iat": datetime.utcnow(), "type": "access"})
        return jwt.encode(to_encode, JWTHandler.SECRET_KEY, algorithm=JWTHandler.ALGORITHM)
    
    @staticmethod
    def create_refresh_token(data: dict) -> str:
        to_encode = data.copy()
        expire = datetime.utcnow() + timedelta(days=JWTHandler.REFRESH_TOKEN_EXPIRE_DAYS)
        to_encode.update({"exp": expire, "iat": datetime.utcnow(), "type": "refresh"})
        return jwt.encode(to_encode, JWTHandler.SECRET_KEY, algorithm=JWTHandler.ALGORITHM)
    
    @staticmethod
    def decode_token(token: str) -> dict:
        return jwt.decode(token, JWTHandler.SECRET_KEY, algorithms=[JWTHandler.ALGORITHM])
```

### 3.3 认证依赖
```python
# backend/app/core/auth.py
from fastapi import Depends, HTTPException
from fastapi.security import HTTPBearer, HTTPAuthorizationCredentials
from app.core.jwt import JWTHandler

security = HTTPBearer()

async def get_current_user(
    credentials: HTTPAuthorizationCredentials = Depends(security)
):
    token = credentials.credentials
    payload = JWTHandler.decode_token(token)
    if payload.get("type") != "access":
        raise HTTPException(status_code=401, detail="无效的Token类型")
    return payload
```

---

## 4. 前端实现

### 4.1 Token存储
```typescript
// frontend/src/utils/tokenStorage.ts
const TOKEN_KEY = 'access_token';
const REFRESH_TOKEN_KEY = 'refresh_token';

export const TokenStorage = {
  setAccessToken(token: string) { localStorage.setItem(TOKEN_KEY, token); },
  getAccessToken() { return localStorage.getItem(TOKEN_KEY); },
  setRefreshToken(token: string) { localStorage.setItem(REFRESH_TOKEN_KEY, token); },
  getRefreshToken() { return localStorage.getItem(REFRESH_TOKEN_KEY); },
  clearTokens() {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(REFRESH_TOKEN_KEY);
  }
};
```

### 4.2 API客户端配置
```typescript
// frontend/src/services/apiClient.ts
import axios from 'axios';
import { TokenStorage } from '../utils/tokenStorage';

const apiClient = axios.create({
  baseURL: 'http://localhost:8000/api/v1',
  timeout: 10000,
});

apiClient.interceptors.request.use((config) => {
  const token = TokenStorage.getAccessToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

apiClient.interceptors.response.use(
  (response) => response.data,
  async (error) => {
    if (error.response?.status === 401) {
      TokenStorage.clearTokens();
      window.location.href = '/login';
    }
    return Promise.reject(error.response?.data);
  }
);

export default apiClient;
```

---

## 5. 安全考虑

- 使用HTTPS传输
- 设置合理的过期时间
- 使用强密钥（至少32字符）
- 不在Token中存储敏感信息
- 使用bcrypt加密密码

---

## 6. 环境配置

```bash
SECRET_KEY=your-secret-key-at-least-32-characters-long
ALGORITHM=HS256
ACCESS_TOKEN_EXPIRE_HOURS=24
REFRESH_TOKEN_EXPIRE_DAYS=7
```

---

## 7. 相关文档
- [API接口文档](../api-specification.md)
- [前后端协作指南](./frontend-backend-guide.md)
- [Agent协作协议](../../AGENT_PROTOCOL.md)
