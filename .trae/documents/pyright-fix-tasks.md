# Pyright 类型错误修复任务清单

**基于**: pyright-type-check-analysis.md
**生成日期**: 2026-04-12
**总需修复错误**: ~272-277 个 (真正的 bug + Model 类型注解改进)
  - P0-P1 真正 bug: ~72-77 个
  - SQLAlchemy 2.0 迁移: ~200 个 (Model 文件)
**预估总工时**: 5-6 小时
  - P0-P1 bug 修复: 2.5-3 小时
  - SQLAlchemy 2.0 迁移: 2.5-3 小时

---

## 🎯 P0 - 立即修复 (会导致运行时崩溃)

### 任务 1: 修复未定义变量 (17个错误)
**优先级**: 🔴🔴🔴 最高
**预估时间**: 30 分钟
**影响**: 运行时 NameError

#### 1.1 agents/reviewer.py - foreshadowing 未定义
- **文件**: `backend/app/agents/reviewer.py`
- **行号**: 394, 395, 396, 397, 398, 399 (共 7 处)
- **问题**: 变量 `foreshadowing` 未定义
- **状态**: ⬜ 待修复
- **修复方案**:
  ```python
  # 方案A: 如果是模块名，添加导入
  from app.consistency.models import Foreshadowing
  
  # 方案B: 如果是变量名，检查上下文确认正确的变量名
  # 可能是拼写错误或作用域问题
  ```

#### 1.2 characters/router.py - BadRequestException 未定义
- **文件**: `backend/app/characters/router.py`
- **行号**: 310, 330 (共 2 处)
- **问题**: 异常类未导入
- **状态**: ⬜ 待修复
- **修复方案**:
  ```python
  # 在文件顶部添加:
  from fastapi import HTTPException
  # 然后替换:
  # raise BadRequestException(...) → raise HTTPException(status_code=400, ...)
  
  # 或者如果项目有自定义异常:
  # from app.core.exceptions import BadRequestException
  ```

#### 1.3 locations/router.py - func 和 loc 未定义
- **文件**: `backend/app/locations/router.py`
- **行号**: 34 (`func`), 113 (`loc`)
- **问题**: 变量名错误或作用域问题
- **状态**: ⬜ 待修复
- **修复方案**: 检查上下文，修正变量名

#### 1.4 mcp/server.py - json 未定义
- **文件**: `backend/app/mcp/server.py`
- **行号**: 724, 745 (共 2 处)
- **问题**: 缺少 json 模块导入
- **状态**: ⬜ 待修复
- **修复方案**:
  ```python
  # 在文件顶部添加:
  import json
  ```

#### 1.5 text/service.py - select 和 Chapter 未定义
- **文件**: `backend/app/text/service.py`
- **行号**: 148 (select), 148 (Chapter x2) 共 3 处
- **问题**: SQLAlchemy select 和 Model 未导入
- **状态**: ⬜ 待修复
- **修复方案**:
  ```python
  # 在文件顶部添加:
  from sqlalchemy import select
  from app.chapters.models import Chapter
  ```

#### 1.6 timeline/router.py - select 未定义
- **文件**: `backend/app/timeline/router.py`
- **行号**: 167
- **问题**: SQLAlchemy select 未导入
- **状态**: ⬜ 待修复
- **修复方案**:
  ```python
  from sqlalchemy import select
  ```

---

### 任务 2: 修复函数调用参数错误 (25个错误)
**优先级**: 🔴🔴🔴 高
**预估时间**: 1 小时
**影响**: 运行时 TypeError 或逻辑错误

#### 2.1 timeline/service.py - 缺少参数
- **文件**: `backend/app/timeline/service.py`
- **行号**: 
  - 452: 缺少 `title` 参数
  - 463: 缺少 `related_entry_ids`, `tags` 参数
- **状态**: ⬜ 待修复
- **修复方案**: 
  - 检查 `_upsert_auto_entry()` 函数签名
  - 补充缺失的必填参数或提供默认值

#### 2.2 characters/router.py 和 service.py
- **文件**: 
  - `backend/app/characters/router.py:239` - 缺少 `data` 参数
  - `backend/app/characters/service.py:265` - 缺少多个参数
- **缺失参数**: `time_horizon`, `source_chapter_id`, `related_entry_ids`, `tags`
- **状态**: ⬜ 待修复
- **修复方案**: 更新函数调用以匹配最新签名

#### 2.3 locations/router.py
- **文件**: `backend/app/locations/router.py`
- **行号**: 
  - 85: 参数名错误 `novel_id` 不存在
  - 88: 参数名错误 `status_code` 不存在
  - 151: 缺少 `data` 参数
- **状态**: ⬜ 待修复
- **修复方案**: 检查函数签名，修正参数名称和补充缺失参数

#### 2.4 memory/router.py
- **文件**: `backend/app/memory/router.py`
- **行号**: 203, 206 (共 2 处)
- **问题**: 缺少 `data` 参数
- **状态**: ⬜ 待修复
- **修复方案**: 补充 data 参数

#### 2.5 novels/router.py
- **文件**: `backend/app/novels/router.py`
- **行号**: 248
- **问题**: 缺少 `data` 参数
- **状态**: ⬜ 待修复
- **修复方案**: 补充 data 参数

#### 2.6 plot_events/router.py
- **文件**: `backend/app/plot_events/router.py`
- **行号**: 225
- **问题**: 缺少 `data` 参数
- **状态**: ⬜ 待修复
- **修复方案**: 补充 data 参数

#### 2.7 mcp/ 工具文件参数问题
- **文件**:
  - `backend/app/mcp/timeline_tools.py:156` - 缺少 `related_entry_ids`
  - `backend/app/mcp/timeline_tools.py:253` - 缺少 `title`, `importance`
  - `backend/app/mcp/base.py:55, 62` - 构造函数/方法参数不匹配
  - `backend/app/mcp/server.py:220, 240, 727, 749` - 参数重复定义
  - `backend/app/mcp/memory_tools.py:266, 273` - select() 参数不匹配
  - `backend/app/mcp/novel_tools.py:1154, 1327` - select() 参数不匹配
  - `backend/app/core/context_builder.py:870` - select() 参数不匹配
- **状态**: ⬜ 待修复
- **修复方案**: 
  - 检查所有相关函数的最新签名
  - 补充缺失参数或更新调用方式
  - 修复参数重复定义问题

---

## 🎯 P1 - 尽快修复 (潜在运行时错误)

### 任务 3: 修复一般类型问题 (20个错误)
**优先级**: 🔴🔴 中高
**预估时间**: 30 分钟
**影响**: 潜在的运行时错误或意外行为

#### 3.1 chat/models.py - Column 条件判断
- **文件**: `backend/app/chat/models.py`
- **行号**: 61, 62, 97 (共 3 处)
- **问题**: 直接对 Column[datetime] 进行布尔判断
- **示例代码**:
  ```python
  if some_datetime_column:  # ❌ 类型错误
      ...
  ```
- **状态**: ⬜ 待修复
- **修复方案**:
  ```python
  # 方案1: 显式 None 检查
  if some_datetime_column is not None:
      
  # 方案2: 使用 SQLAlchemy 的 is_()/isnot()
  if some_datetime_column.isnot(None):
  ```

---

### 任务 4: 修复属性访问真错误 (10-15个错误)
**优先级**: 🔴🔴 中高
**预估时间**: 30 分钟
**影响**: 运行时 AttributeError

#### 4.1 characters/router.py - Schema 字段不存在
- **文件**: `backend/app/characters/router.py`
- **行号**: 
  - 312, 329, 333: 访问不存在的 `novel_id` 属性
  - 397: 访问不存在的 `mark_old_as_dormant` 属性
  - 50: str 类型没有 `contains` 方法
- **状态**: ⬜ 待修复
- **修复方案**:
  ```python
  # 对于 novel_id 问题:
  # 检查 CharacterRelationCreate schema 定义
  # 要么添加 novel_id 字段到 schema
  # 要么从其他地方获取 novel_id (如 path parameter)
  
  # 对于 contains 问题:
  # 改为: keyword in name
  # 或使用: Table.name.ilike(f"%{keyword}%")
  ```

#### 4.2 consistency/router.py - 导入符号不存在
- **文件**: `backend/app/consistency/router.py`
- **行号**: 63
- **问题**: `get_db_session` 导入错误
- **状态**: ⬜ 待修复
- **修复方案**: 检查正确的导入路径和函数名

#### 4.3 workflows/langgraph_workflow.py - API 方法不存在
- **文件**: `backend/app/workflows/langgraph_workflow.py`
- **行号**: 581
- **问题**: Pregel 对象没有 `get_state` 方法
- **可能原因**: LangGraph 版本 API 变更
- **状态**: ⬜ 待修复
- **修复方案**:
  ```python
  # 查看 LangGraph 文档确认正确的方法名
  # 可能是: .get_state() → .state() 或其他
  ```

#### 4.4 timeline/service.py - 导入不存在
- **文件**: `backend/app/timeline/service.py`
- **行号**: 307
- **问题**: `RelationStatus` 未正确导入
- **状态**: ⬜ 待修复
- **修复方案**: 找到 RelationStatus 的正确定义位置并导入

---

## ✅ 修复检查清单

### 修复前准备
- [ ] 备份当前代码 (git commit 或 branch)
- [ ] 确认测试环境可用
- [ ] 阅读相关文件的上下文代码

### 修复执行
- [ ] **任务 1.1**: agents/reviewer.py foreshadowing
- [ ] **任务 1.2**: characters/router.py BadRequestException
- [ ] **任务 1.3**: locations/router.py func/loc
- [ ] **任务 1.4**: mcp/server.py json import
- [ ] **任务 1.5**: text/service.py imports
- [ ] **任务 1.6**: timeline/router.py select import
- [ ] **任务 2.1**: timeline/service.py 缺失参数
- [ ] **任务 2.2**: characters/ 参数问题
- [ ] **任务 2.3**: locations/router.py 参数
- [ ] **任务 2.4**: memory/router.py data 参数
- [ ] **任务 2.5**: novels/router.py data 参数
- [ ] **任务 2.6**: plot_events/router.py data 参数
- [ ] **任务 2.7**: mcp/ 工具参数问题
- [ ] **任务 3.1**: chat/models.py 条件判断
- [ ] **任务 4.1**: characters/router.py 属性访问
- [ ] **任务 4.2**: consistency/router.py 导入
- [ ] **任务 4.3**: workflows/langgraph_workflow.py API
- [ ] **任务 4.4**: timeline/service.py 导入

### SQLAlchemy 2.0 迁移执行 (按顺序)
- [ ] **任务 5.1.1**: chat/models.py (最简单, 3 errors)
- [ ] **任务 5.1.2**: auth/models.py (5 errors)
- [ ] **任务 5.1.3**: editor/models.py (5 errors)
- [ ] **任务 5.1.4**: plot_events/models.py (9 errors)
- [ ] **任务 5.1.5**: rag/models.py (9 errors)
- [ ] **任务 5.1.6**: memory/models.py (10 errors)
- [ ] **任务 5.1.7**: chapters/models.py (10 errors)
- [ ] **任务 5.2.1**: agents/models.py (14 errors)
- [ ] **任务 5.2.2**: locations/models.py (15 errors)
- [ ] **任务 5.2.3**: novels/models.py (30 errors)
- [ ] **任务 5.3.1**: timeline/models.py (24 errors) - 复杂
- [ ] **任务 5.3.2**: characters/models.py (23 errors) - 复杂
- [ ] **任务 5.3.3**: planning/models.py (43 errors) - 最复杂

---

## 🎯 P2 - SQLAlchemy 2.0 注解风格迁移 (提升类型安全性)

### 任务 5: 迁移所有 Model 文件到 SQLAlchemy 2.0 风格 (200个错误)
**优先级**: 🟡🟡 中等 (推荐改进)
**预估时间**: 2.5-3 小时
**影响**: 消除 Model 文件的 200 个类型错误，提升 IDE 支持和代码质量

#### 📋 迁移概述

**需要迁移的文件 (13 个)**:

| 文件路径 | 当前错误数 | 复杂度 | 状态 |
|---------|-----------|--------|------|
| `backend/app/planning/models.py` | 43 errors | 🔴 高 (多模型+Enum) | ⬜ 待迁移 |
| `backend/app/novels/models.py` | 30 errors | 🟡 中 | ⬜ 待迁移 |
| `backend/app/characters/models.py` | 23 errors | 🔴 高 (relationship) | ⬜ 待迁移 |
| `backend/app/timeline/models.py` | 24 errors | 🔴 高 (多模型+Enum) | ⬜ 待迁移 |
| `backend/app/agents/models.py` | 14 errors | 🟢 低 | ⬜ 待迁移 |
| `backend/app/locations/models.py` | 15 errors | 🟢 低 | ⬜ 待迁移 |
| `backend/app/chapters/models.py` | 10 errors | 🟢 低 | ⬜ 待迁移 |
| `backend/app/memory/models.py` | 10 errors | 🟢 低 | ⬜ 待迁移 |
| `backend/app/plot_events/models.py` | 9 errors | 🟢 低 | ⬜ 待迁移 |
| `backend/app/rag/models.py` | 9 errors | 🟢 低 | ⬜ 待迁移 |
| `backend/app/editor/models.py` | 5 errors | 🟢 低 | ⬜ 待迁移 |
| `backend/app/auth/models.py` | 5 errors | 🟢 低 | ⬜ 待迁移 |
| `backend/app/chat/models.py` | 3 errors | 🟢 低 | ⬜ 待迁移 |
| **总计** | **200 errors** | - | **⬜ 0%** |

---

#### 🔄 迁移规则和示例

##### 基本类型映射

```python
# ❌ 旧风格 (SQLAlchemy 1.x)
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON
from datetime import datetime
from typing import Optional, Dict, Any, List

class MyModel(Base):
    __tablename__ = "my_table"

    id: int = Column(Integer, primary_key=True, autoincrement=True)
    name: str = Column(String(100), nullable=False)
    description: Optional[str] = Column(Text)
    data: Optional[Dict[str, Any]] = Column(JSON)
    items: Optional[List[int]] = Column(JSON)
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    updated_at: Optional[datetime] = Column(TIMESTAMP, onupdate=func.now())

# ✅ 新风格 (SQLAlchemy 2.0)
from sqlalchemy import String, Text, ForeignKey, JSON
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime
from typing import Optional, Dict, Any, List

class MyModel(Base):
    __tablename__ = "my_table"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    name: Mapped[str] = mapped_column(String(100), nullable=False)
    description: Mapped[Optional[str]] = mapped_column(Text)
    data: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    items: Mapped[Optional[List[int]]] = mapped_column(JSON)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(onupdate=func.now())
```

##### 完整迁移示例

**示例 1: auth/models.py (简单模型)**

```python
# ❌ 旧代码
"""
认证模块 - 数据库模型
"""
from sqlalchemy import Column, Integer, String, TIMESTAMP, Index, func
from sqlalchemy.orm import relationship
from datetime import datetime

from app.core.database import Base


class User(Base):
    """用户模型 - 存储用户账户信息"""
    __tablename__ = "users"

    id: int = Column(Integer, primary_key=True, autoincrement=True)
    username: str = Column(String(50), nullable=False, unique=True, index=True)
    email: str = Column(String(100), nullable=False, unique=True, index=True)
    password_hash: str = Column(String(255), nullable=False)
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())

    __table_args__ = (
        Index('idx_user_username_email', 'username', 'email'),
    )
```

```python
# ✅ 新代码
"""
认证模块 - 数据库模型
"""
from sqlalchemy import String, Index, func
from sqlalchemy.orm import Mapped, mapped_column, relationship
from datetime import datetime

from app.core.database import Base


class User(Base):
    """用户模型 - 存储用户账户信息"""
    __tablename__ = "users"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    username: Mapped[str] = mapped_column(String(50), nullable=False, unique=True, index=True)
    email: Mapped[str] = mapped_column(String(100), nullable=False, unique=True, index=True)
    password_hash: Mapped[str] = mapped_column(String(255), nullable=False)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())

    __table_args__ = (
        Index('idx_user_username_email', 'username', 'email'),
    )
```

**示例 2: agents/models.py (包含 Optional 和 Dict)**

```python
# ❌ 旧代码
from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON, Index, func
from datetime import datetime
from typing import Optional, Dict, Any

from app.core.database import Base

class AgentTaskRecord(Base):
    """Agent任务记录 - 持久化任务状态"""
    __tablename__ = "agent_tasks"

    id: int = Column(Integer, primary_key=True, autoincrement=True)
    task_id: str = Column(String(100), unique=True, nullable=False, index=True)
    novel_id: int = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Optional[int] = Column(Integer, ForeignKey("chapters.id", ondelete="SET NULL"))
    task_type: str = Column(String(50), nullable=False, index=True)
    status: str = Column(String(50), nullable=False, default='pending', index=True)
    parameters: Optional[Dict[str, Any]] = Column(JSON)
    context: Optional[Dict[str, Any]] = Column(JSON)
    result: Optional[Dict[str, Any]] = Column(JSON)
    error: Optional[str] = Column(Text)
    agent_id: Optional[str] = Column(String(100))
    created_at: datetime = Column(TIMESTAMP, server_default=func.now())
    updated_at: Optional[datetime] = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    completed_at: Optional[datetime] = Column(TIMESTAMP)

    __table_args__ = (
        Index('idx_agent_task_novel_status', 'novel_id', 'status'),
        Index('idx_agent_task_type_status', 'task_type', 'status'),
    )
```

```python
# ✅ 新代码
from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Index, func
from sqlalchemy.orm import Mapped, mapped_column
from datetime import datetime
from typing import Optional, Dict, Any

from app.core.database import Base

class AgentTaskRecord(Base):
    """Agent任务记录 - 持久化任务状态"""
    __tablename__ = "agent_tasks"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    task_id: Mapped[str] = mapped_column(String(100), unique=True, nullable=False, index=True)
    novel_id: Mapped[int] = mapped_column(ForeignKey("novels.id", ondelete="CASCADE"), nullable=False, index=True)
    chapter_id: Mapped[Optional[int]] = mapped_column(ForeignKey("chapters.id", ondelete="SET NULL"))
    task_type: Mapped[str] = mapped_column(String(50), nullable=False, index=True)
    status: Mapped[str] = mapped_column(String(50), nullable=False, default='pending', index=True)
    parameters: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    context: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    result: Mapped[Optional[Dict[str, Any]]] = mapped_column(JSON)
    error: Mapped[Optional[str]] = mapped_column(Text)
    agent_id: Mapped[Optional[str]] = mapped_column(String(100))
    created_at: Mapped[datetime] = mapped_column(server_default=func.now())
    updated_at: Mapped[Optional[datetime]] = mapped_column(server_default=func.now(), onupdate=func.now())
    completed_at: Mapped[Optional[datetime]] = mapped_column()

    __table_args__ = (
        Index('idx_agent_task_novel_status', 'novel_id', 'status'),
        Index('idx_agent_task_type_status', 'task_type', 'status'),
    )
```

**示例 3: characters/models.py (包含 relationship)**

```python
# ❌ 旧代码中的 relationship 定义
from sqlalchemy.orm import relationship

class Character(Base):
    # ... 其他字段 ...
    
    novel = relationship("Novel", back_populates="characters")
    relations = relationship(
        "CharacterRelation",
        back_populates="character",
        foreign_keys="CharacterRelation.character_id",
        cascade="all, delete-orphan"
    )
```

```python
# ✅ 新代码中保持不变 (relationship 不需要改)
# relationship() 的使用方式在 1.x 和 2.0 中相同
from sqlalchemy.orm import Mapped, mapped_column, relationship

class Character(Base):
    # ... 使用 Mapped 的字段 ...
    
    novel: Mapped["Novel"] = relationship(back_populates="characters")
    relations: Mapped[list["CharacterRelation"]] = relationship(
        back_populates="character",
        foreign_keys="CharacterRelation.character_id",
        cascade="all, delete-orphan"
    )
```

---

#### 📝 详细迁移步骤清单

##### 5.1 简单模型文件 (每个约 10-15 分钟)

按照从易到难的顺序迁移：

- [ ] **5.1.1 chat/models.py** (3 errors) - 最简单，优先做
  ```bash
  pyright backend/app/chat/models.py  # 验证迁移后错误数
  ```

- [ ] **5.1.2 auth/models.py** (5 errors)
  
- [ ] **5.1.3 editor/models.py** (5 errors)

- [ ] **5.1.4 plot_events/models.py** (9 errors)

- [ ] **5.1.5 rag/models.py** (9 errors)

- [ ] **5.1.6 memory/models.py** (10 errors)

- [ ] **5.1.7 chapters/models.py** (10 errors)

##### 5.2 中等复杂度模型 (每个约 15-20 分钟)

- [ ] **5.2.1 agents/models.py** (14 errors)

- [ ] **5.2.2 locations/models.py** (15 errors)

- [ ] **5.2.3 novels/models.py** (30 errors) - 字段较多

##### 5.3 复杂模型文件 (每个约 25-30 分钟)

- [ ] **5.3.1 timeline/models.py** (24 errors)
  - 包含多个 Enum 类
  - 包含 multiple foreign keys
  - 包含复杂的 relationships

- [ ] **5.3.2 characters/models.py** (23 errors)
  - 包含 self-referential relationship
  - 包含 complex JSON fields
  - 包含 multiple models in one file

- [ ] **5.3.3 planning/models.py** (43 errors) - 最复杂
  - 包含 3 个 Enum 类
  - 包含 2 个 Model 类 (PlotLine, PlotNode)
  - 包含丰富的 relationships
  - 字段最多

---

#### 🔧 迁移检查清单 (针对每个文件)

对每个 Model 文件执行以下步骤：

- [ ] **步骤 1: 备份当前文件**
  ```bash
  cp backend/app/xxx/models.py backend/app/xxx/models.py.bak
  ```

- [ ] **步骤 2: 更新导入语句**
  ```python
  # 删除:
  from sqlalchemy import Column, Integer, String, ...  # 所有 Column 类型
  
  # 替换为:
  from sqlalchemy import String, Text, Integer, ForeignKey, JSON, Boolean, Float, func  # 只保留类型构造器
  from sqlalchemy.orm import Mapped, mapped_column, relationship
  
  # 保留:
  from datetime import datetime
  from typing import Optional, Dict, Any, List
  from app.core.database import Base
  ```

- [ ] **步骤 3: 转换字段定义**
  ```python
  # 对每个字段应用规则:
  # 1. 将 : Type 改为 : Mapped[Type]
  # 2. 将 = Column(Type, ...) 改为 = mapped_column(...)
  # 3. 从 Column() 参数中移除类型参数 (如 Integer, String)
  ```

- [ ] **步骤 4: 更新 relationship 注解**
  ```python
  # 如果有 relationship，添加类型注解:
  novel = relationship("Novel", ...)
  # 改为:
  novel: Mapped["Novel"] = relationship(back_populates="...")
  
  # 对于一对多:
  items = relationship("Item", ...)
  # 改为:
  items: Mapped[list["Item"]] = relationship(back_populates="...")
  ```

- [ ] **步骤 5: 验证迁移结果**
  ```bash
  # 检查该文件的错误是否消除
  pyright backend/app/xxx/models.py
  
  # 应该看到 0 errors 或显著减少
  ```

- [ ] **步骤 6: 运行测试确认功能正常**
  ```bash
  pytest tests/ -k "test_xxx" -v
  ```

- [ ] **步骤 7: 提交更改**
  ```bash
  git add backend/app/xxx/models.py
  git commit -m "refactor(xxx): migrate to SQLAlchemy 2.0 annotation style"
  ```

---

#### ⚠️ 注意事项和特殊情况

##### 1. 没有类型注解的字段 (chat/models.py, editor/models.py)

部分旧文件可能没有 Python 类型注解：

```python
# ❌ 旧代码 (无注解)
class ChatSession(Base):
    id = Column(Integer, primary_key=True)
    session_id = Column(String(64))

# ✅ 新代码 (添加完整注解)
class ChatSession(Base):
    id: Mapped[int] = mapped_column(primary_key=True)
    session_id: Mapped[str] = mapped_column(String(64))
```

##### 2. 混合使用的情况

如果某些字段已经使用了部分注解，只需统一即可。

##### 3. 保持向后兼容

确保：
- `__tablename__` 不变
- 列名不变 (mapped_column 会自动使用变量名作为列名)
- `__table_args__` 不变
- relationship 配置不变
- ForeignKey 约束不变

##### 4. 特殊类型处理

```python
# Boolean 类型
active: Mapped[bool] = mapped_column(Boolean, default=True)

# Float 类型
score: Mapped[Optional[float]] = mapped_column(Float)

# Enum 类型 (保持不变)
status: Mapped[str] = mapped_column(String(50), default=StatusEnum.PENDING.value)

# 自定义类型
# 如果有自定义类型，通常不需要特殊处理
data: Mapped[Optional[bytes]] = mapped_column(LargeBinary)
```

---

#### 📊 迁移进度跟踪表

| 文件 | 错误数 | 复杂度 | 状态 | 开始时间 | 完成时间 | 备注 |
|-----|--------|--------|------|---------|---------|------|
| chat/models.py | 3 | 🟢 低 | ⬜ | - | - | 最简单 |
| auth/models.py | 5 | 🟢 低 | ⬜ | - | - | |
| editor/models.py | 5 | 🟢 低 | ⬜ | - | - | |
| plot_events/models.py | 9 | 🟢 低 | ⬜ | - | - | |
| rag/models.py | 9 | 🟢 低 | ⬜ | - | - | |
| memory/models.py | 10 | 🟢 低 | ⬜ | - | - | |
| chapters/models.py | 10 | 🟢 低 | ⬜ | - | - | |
| agents/models.py | 14 | 🟡 中 | ⬜ | - | - | |
| locations/models.py | 15 | 🟡 中 | ⬜ | - | - | |
| novels/models.py | 30 | 🟡 中 | ⬜ | - | - | |
| timeline/models.py | 24 | 🔴 高 | ⬜ | - | - | 多Enum |
| characters/models.py | 23 | 🔴 高 | ⬜ | - | - | 多关系 |
| planning/models.py | 43 | 🔴 高 | ⬜ | - | - | 最复杂 |
| **总计** | **200** | - | **⬜ 0%** | - | - | **预计 2.5-3h** |

---

#### 🎯 迁移完成后的预期效果

**迁移前 (当前状态)**:
```
918 total errors
├── 200 Model file errors (AssignmentType 等)
├── ~72-77 real bugs (P0-P1)
└── ~640 SQLAlchemy query pattern false positives
```

**迁移后 (预期状态)**:
```
~718 total errors (减少 200个)
├── 0 Model file errors ✅
├── ~72-77 real bugs (P0-P1) ← 需要另外修复
└── ~640 SQLAlchemy query pattern false positives (可接受)
```

**如果同时修复 P0-P1 的真正 bug**:
```
~640-650 remaining errors (全部为 SQLAlchemy 特性导致的可接受误报)
```

---

### 修复后验证
- [ ] 运行 `pyright` 确认错误数减少
- [ ] 运行单元测试确保无回归
- [ ] 手动测试修改的功能点
- [ ] 更新本文档标记完成状态

---

## 📊 进度跟踪

| 任务 | 错误数 | 状态 | 完成日期 | 备注 |
|-----|--------|------|---------|------|
| 任务 1: 未定义变量 | 17 | ⬜ 待开始 | - | - |
| 任务 2: 函数参数错误 | 25 | ⬜ 待开始 | - | - |
| 任务 3: 一般类型问题 | 20 | ⬜ 待开始 | - | - |
| 任务 4: 属性访问错误 | 10-15 | ⬜ 待开始 | - | - |
| **任务 5: SQLAlchemy 2.0 迁移** | **200** | **⬜ 待开始** | **-** | **13个Model文件** |
| **P0-P1 小计** | **72-77** | **⬜ 0%** | **-** | **预计 2.5-3h** |
| **P2 迁移小计** | **200** | **⬜ 0%** | **-** | **预计 2.5-3h** |
| **总计** | **272-277** | **⬜ 0%** | **-** | **预计 5-6h** |

---

## 🔧 快速修复命令参考

### 单文件类型检查
```bash
# 检查特定文件
pyright backend/app/agents/reviewer.py

# 只显示错误 (忽略警告)
pyright --level error backend/app/
```

### Git 工作流建议
```bash
# 创建修复分支
git checkout -b fix/pyright-type-errors

# 修复前提交当前状态
git add -A
git commit -m "chore: snapshot before pyright fixes"

# 修复后提交
git add -A
git commit -m "fix: resolve pyright type errors (P0-P1)"

# 推送并创建 PR
git push origin fix/pyright-type-errors
```

---

## 📝 修复日志模板

```markdown
### [日期] - 修复 [任务编号].[子任务编号]

**文件**: `路径/到/文件.py`
**行号**: xxx
**问题描述**: 
**修复前代码**:
```python
# 修复前的代码
```
**修复后代码**:
```python
# 修复后的代码
```
**验证结果**: ✅ 通过 / ❌ 失败
**备注**: 其他说明
```

---

**文档维护者**: AI Assistant
**最后更新**: 2026-04-12
**版本**: 2.0 (新增 SQLAlchemy 2.0 迁移任务)
