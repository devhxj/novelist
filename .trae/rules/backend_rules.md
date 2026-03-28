# 后端开发规则

## 环境要求

### Python虚拟环境
**必须**先激活虚拟环境再运行任何Python命令：
```bash
cd backend
source venv/bin/activate  # Linux/Mac
# 或
venv\Scripts\activate  # Windows
```

### 启动服务
```bash
cd backend
source venv/bin/activate
uvicorn app.main:app --host 0.0.0.0 --port 8000 --reload
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
│   └── main.py         # 应用入口
├── venv/               # 虚拟环境
└── requirements.txt
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

### 新增模块
1. 在 `app/` 下创建模块目录
2. 创建 `models.py`, `schemas.py`, `router.py`, `__init__.py`
3. 在 `main.py` 中导入并注册路由
4. 导入模型以确保表创建

### 代码风格
- 使用类型注解
- 遵循PEP 8规范
- 函数和类添加docstring
