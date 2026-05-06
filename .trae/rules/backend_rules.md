---
alwaysApply: false
description: 编写后端代码时候必须遵守本规则
---
# 后端开发规则

## 环境要求

### Python虚拟环境
**必须**先激活虚拟环境再运行任何Python命令：
```bash

source backend/.venv/bin/activate  # Linux/Mac
# 或
backend\.venv\Scripts\activate  # Windows
```

### 启动服务
```bash

source backend/.venv/bin/activate
uvicorn backend.main:app --host 0.0.0.0 --port 8000 --reload
```

## 项目架构

### 模块化设计原则
每个模块作为**一等公民**，独立包含自己的：
- `models.py` - 数据库模型
- `schemas.py` - Pydantic验证模型
- `router.py` - API路由
- `__init__.py` - 模块导出

### 目录结构
```
backend/
├── app/
│   ├── core/           # 核心功能
│   │   ├── database.py # 数据库连接
│   │   ├── jwt.py      # JWT工具
│   │   ├── auth.py     # 认证依赖
│   │   ├── response.py # 响应格式
│   │   └── exceptions.py
│   ├── auth/           # 认证模块
│   │   ├── models.py
│   │   ├── schemas.py
│   │   ├── router.py
│   │   └── __init__.py
│   ├── novels/         # 小说管理模块
│   ├── characters/     # 角色管理模块
│   ├── chapters/       # 章节管理模块
│   ├── plot_events/    # 情节事件模块
│   ├── main.py         # 应用入口
│   └── requirements.txt
```

## API规范

### 响应格式
所有API响应遵循统一格式：
```json
{
  "success": true/false,
  "data": {},
  "message": "操作成功"
}
```

### 认证
- 使用JWT Bearer Token
- Token有效期：Access Token 24小时，Refresh Token 7天
- 请求头：`Authorization: Bearer <token>`

## 开发规范

### 异步优先原则
**所有IO操作必须使用异步**：
- 数据库操作：使用 `AsyncSession`，`await db.execute()`
- Redis操作：使用 `redis.asyncio`，所有方法都是 `async`
- HTTP请求：使用 `httpx.AsyncClient`
- 文件IO：使用 `aiofiles` 或 `asyncio.to_thread()`
- 外部API调用：必须 `await`

```python
# 正确示例
result = await db.execute(select(Novel))
cached = await redis_service.get(key)
async with httpx.AsyncClient() as client:
    response = await client.get(url)

# 错误示例（禁止）
result = db.query(Novel)  # 同步查询
cached = redis_client.get(key)  # 同步Redis
response = requests.get(url)  # 同步HTTP
```

### 新增模块
1. 在 `app/` 下创建模块目录
2. 创建 `models.py`, `schemas.py`, `router.py`, `__init__.py`
3. 在 `main.py` 中导入并注册路由
4. 导入模型以确保表创建

### 代码风格
- 使用类型注解
- 遵循PEP 8规范
- 函数和类添加docstring

## Pydantic Schemas 规范（必须遵守）

### 类型注解现代化（Python 3.10+）
**必须**使用现代类型注解语法，禁止使用旧式写法：

```python
# ✅ 正确 - 现代写法（必须使用）
def process_data(items: list[str] | None) -> dict[str, Any]:
    ...

# ❌ 错误 - 旧式写法（禁止使用）
from typing import Optional, List, Dict
def process_data(items: Optional[List[str]]) -> Dict[str, Any]:
    ...
```

**转换规则：**
- `Optional[X]` → `X | None`
- `List[X]` → `list[X]`
- `Dict[K, V]` → `dict[K, V]`
- 仅在需要 `Any` 时才从 `typing` 导入，其他情况不导入 `Optional`, `List`, `Dict`

### Field 默认值规范
**必须**显式使用 `default=` 参数：

```python
# ✅ 正确 - 必须使用 default=
class UserCreate(BaseModel):
    name: str = Field(..., min_length=1)
    age: int | None = Field(default=None)
    role: str = Field(default="user")

# ❌ 错误 - 禁止省略 default=
class UserCreate(BaseModel):
    name: str = Field(..., min_length=1)
    age: int | None = Field(None)  # 缺少 default=
    role: str = Field("user")      # 缺少 default=
```

**特殊说明：**
- `Field(...)` 用于必需字段（required fields），这是正确的
- 所有有默认值的字段**必须**写成 `Field(default=value)`
- 原来没有默认值的字段保持不变

### Model Config 规范
**必须**使用 Pydantic v2 的 `model_config` 写法：

```python
# ✅ 正确 - Pydantic v2 写法（必须使用）
from pydantic import BaseModel, ConfigDict

class UserResponse(BaseModel):
    id: int
    name: str

    model_config = ConfigDict(from_attributes=True)

# ❌ 错误 - Pydantic v1 写法（已废弃）
class UserResponse(BaseModel):
    id: int
    name: str

    class Config:
        from_attributes = True
```

### 导入规范
```python
# ✅ 正确的导入方式
from pydantic import BaseModel, Field, ConfigDict
from typing import Any  # 仅在需要时导入

# ❌ 错误 - 不要导入不需要的类型
from typing import Optional, List, Dict, Any  # Optional/List/Dict 已被现代语法替代
```

### 检查清单
创建或修改 schemas.py 文件时，必须确保：
- [ ] 所有类型注解使用 `X | None` 而非 `Optional[X]`
- [ ] 所有集合类型使用 `list[X]`, `dict[K,V]` 而非 `List[X]`, `Dict[K,V]`
- [ ] 所有带默认值的 Field 使用 `Field(default=value)` 格式
- [ ] Model 配置使用 `model_config = ConfigDict()` 而非 `class Config:`
- [ ] 运行 `pyright` 检查确保无类型错误
- [ ] 不从 `typing` 导入 `Optional`, `List`, `Dict`（除非必要）

## 开发阶段工作流（必须遵守）

每个开发阶段完成后，**必须**按以下顺序执行：

1. **Code Review** - 检查本阶段修改的文件，确认：
   - 类型注解符合规范（`X | None`, `list[X]`, `dict[K,V]`）
   - 所有IO操作使用异步（`await`）
   - Redis 操作通过 `redis_service` 而非直接导入 `redis`
   - 无逻辑错误或遗漏

2. **Pyright 扫描** - 激活虚拟环境后运行：
   ```bash
   source /home/nianhe/projects/todo/venv/bin/activate
   cd /home/nianhe/projects/todo/backend
   pyright <修改的文件列表>
   ```
   - 仅关注本阶段引入的新错误，预先存在的错误可忽略
   - 误报的类型错误可忽略，不强求修复

3. **英文 Commit 信息** - 编写本阶段的英文 commit 信息，格式：
   ```
   feat/fix/refactor(scope): 简短描述
   
   详细描述本阶段改动
   ```

4. **满意度询问** - 使用 AskUserQuestion 工具向用户发送满意度询问

5. **全量验证** - 所有阶段完成后，对照计划逐项查证，进行全量 review
