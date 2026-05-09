# MCP 模块彻底重构方案

## 动机

当前 MCP 工具模块存在以下结构性问题：

### 1. 参数契约双重维护（核心问题）

每个工具的参数定义同时存在于两个地方——手写 JSON Schema 字典和 Pydantic Args 模型（或 execute 方法签名）。加一个参数必须改两处，且两处可能不一致。

```python
# 当前：同一份参数契约维护两遍
class SomeArgs(BaseModel):
    novel_id: int
    mode: str = Field(default="list", description="查询模式")

class SomeTool(BaseMCPTool):
    parameters_schema = {               # 手写，与上面重复
        "type": "object",
        "properties": {
            "novel_id": {"type": "integer", ...},
            "mode": {"type": "string", ...},
        },
        "required": ["novel_id"],
    }
```

目标：Args 模型是唯一的参数定义，JSON Schema 通过 `model_json_schema()` 自动生成。

### 2. jsonschema 校验是冗余依赖

项目已全面使用 Pydantic v2，但工具参数校验走的是 `jsonschema.validate()`。为此还写了参数过滤逻辑（防止 `chat_session` 等内部对象被塞进校验报错泄露给 LLM）——这是在给错误的校验库打补丁。Pydantic 的 `model_validate()` 天然只取模型定义的字段，从根源上消灭泄露问题。

### 3. `execute()` 使用 `**kwargs` 无类型安全

```python
# 当前：拼写错误静默通过，IDE 无法补全
async def execute(self, db, novel_id, user_id, mode="summary", **kwargs):
    mdoe = kwargs.get("mdoe")  # 拼错了，永远拿不到值，不会报错
```

`CreateOutlineTool.execute()` 甚至直接用 `kwargs["novel_id"]` 取值——key 不存在就是 `KeyError` 500。

目标：Args 模型校验后作为类型化对象传入，编译期就能发现参数名错误。

### 4. 注册风格不统一

- `novel_tools.py`、`memory_tools.py`、`consistency_tools.py`、`editing_tools.py` 用 `class XxxTools` + `register_all()` 静态方法
- `timeline_tools.py`、`character_tools.py`、`location_tools.py`、`story_arc_tools.py`、`story_state_tools.py`、`reader_perspective_tools.py` 用顶层 `register_xxx_tools()` 函数
- `workflow_tools.py` 用 `CreateOutlineTool.register_all()` 类方法

三种写法没有理由。

### 5. 跨文件横向依赖

```
location_tools.py  →  from mcp_tools.novel_tools import _invalidate_novel_cache
character_tools.py →  from mcp_tools.novel_tools import _invalidate_character_cache
timeline_tools.py  →  from mcp_tools.novel_tools import _invalidate_novel_cache
```

缓存失效函数定义在 `novel_tools.py`，但被其他域的工具文件导入。`novel_tools.py` 是领域工具文件，不是 util 包。这些公共函数应抽到独立模块。

### 6. `to_openai_function()` 使用 magic string 做控制流

```python
if "无需传novel_id" in self.description:
    properties.pop("novel_id", None)
```

用中文描述字符串决定是否剥离字段，脆弱且隐式。Args 模型本身不含 `novel_id` 就不该出现在 schema 里。

### 7. 错误处理没有统一规则

- `GetNovelInfoTool`：不 catch，异常抛给 `registry.execute()` 统一兜底
- `CreateCharacterTool`：自己 `try/except` 返回 `MCPToolResult(success=False)`
- `CreateOutlineTool`：自己 `try/except` + `logger.error`

两种风格混用。应该明确：什么情况工具自己处理，什么情况交给 registry。

### 8. `_approval_events` 全局状态清理不完整

```python
# workflow_tools.py — 模块级 dict
_approval_events: dict[str, asyncio.Event] = {}
_approval_results: dict[str, dict] = {}
```

正常流程 `cleanup_approval()` 会清理。但如果 WS 异常断开且 `abort_approval` 的调用路径未覆盖（比如事件循环关闭时的竞态），这两个 dict 会永久持有已失效 session 的 Event。需确保所有断开路径都触发清理。（注意：审批本身不应加超时——设计上 WS 连着就一直等。）

### 9. `server.py` 600 行同构样板

30 个 `@mcp.tool()` 函数结构完全一样：接收参数 → 调 `_execute_tool()`。FastMCP 本身能从函数签名自动生成 JSON Schema，这个能力完全没有被利用。当前 MCP 端点挂在 `/mcp` 但前端没有任何消费者——它在运行但实际是死代码。

### 10. 角色工具跨文件放置

`GetCharactersTool`、`CreateCharacterTool`、`UpdateCharacterTool` 定义在 `novel_tools.py`，但 `UpdateCharacterRelationTool` 在 `character_tools.py`。同一领域（角色管理）被人为拆分到两个文件，应集中到 `character_tools.py`。

### 11. 工具专有辅助函数放错了位置

以下函数只被单个工具使用，却定义在模块顶层，应下放到工具类内：

| 函数 | 文件 | 被调用者 |
|------|------|----------|
| `_build_creative_profile_summary` | `novel_tools.py:52` | 仅 `UpdateCreativeProfileTool.execute()` |
| `_attach_profile_summary` | `novel_tools.py:76` | 仅 `UpdateCreativeProfileTool.execute()` |
| `_build_agent_task_id` | `editing_tools.py:19` | 仅 `_execute_subagent_task()` |
| `_normalize_subagent_task_type` | `editing_tools.py:23` | 仅 `_execute_subagent_task()` |

### 12. `UpdateCreativeProfileTool` 内联重复缓存失效逻辑

`execute()` 第 642-645 行直接操作 `redis_service.clear_pattern` + `context_cache.invalidate_novel`，与 `_invalidate_novel_cache()` 函数完全重复，却没有调用它。

### 13. `UpdateLocationTool` 参数访问方式不一致

`execute()` 用 `kwargs.get("name")` 方式获取参数，是唯一这样做的工具。其他工具全都逐一命名参数。

### 14. `CreateNewChapterTool` 的 schema 包含了 `novel_id`

所有其他工具描述里写"无需传novel_id"、`novel_id` 不在 schema 中，但 `CreateNewChapterTool.parameters_schema` 把 `novel_id` 作为显式参数声明。

### 15. 权限校验缺失（安全缺陷）

`story_state_tools.py`（2 个工具）和 `reader_perspective_tools.py`（3 个工具）调用了 `verify_novel_ownership(db, novel_id, user_id)` 但**不检查返回值**。其他所有工具都检查 `if not novel: return error`。未授权用户可以读写故事状态和读者认知数据。

### 16. 部分工具可能冗余待合并/移除

当前 30 个工具中，部分工具功能重叠或使用频率极低（如 `run_subagent` 的实际调度能力有限——见 issue #7）。重构完成后从 LLM 实际调用频率和功能重叠角度审查，合并或移除冗余工具。

---

## 不改的范围（明确边界）

| 保留 | 理由 |
|------|------|
| `MCPToolResult` 结构（含 `inject` 字段） | `inject` 是合法的工具能力——工具执行后向对话注入上下文 |
| `to_openai_function()` 放在 `BaseMCPTool` | 工具拥有自己的 schema，转换成 OpenAI 格式是其自然职责 |
| `MCPToolCategory` 枚举 | 分类合理，`list_by_category` 被 HTTP API 使用 |
| 注册表单例 `get_mcp_registry()` | 当前规模不需要更复杂的 DI |
| `router.py` HTTP 端点 | 属于 API 层重构，不在本次范围 |
| 工具内部业务逻辑 | 只改参数定义和校验层，不动业务 |

---

## 目标架构

```
┌──────────────────────────────────────────────┐
│ GetNovelInfoArgs(BaseModel)                  │  ← 唯一参数定义
│   mode: Literal["summary","progress"]        │
│   page: int = Field(default=1)               │
├──────────────────────────────────────────────┤
│        │                 │                   │
│        ▼                 ▼                   │
│  model_json_schema()  model_validate(dict)   │
│        │                 │                   │
│        ▼                 ▼                   │
│  parameters_schema    registry.execute()     │
│  (property, 自动)      (Pydantic 校验)       │
│        │                 │                   │
│        ▼                 ▼                   │
│  to_openai_function()  tool.execute(         │
│  (OpenAI format)        args=validated_args, │
│                         db=db, user_id=uid)  │
└──────────────────────────────────────────────┘
```

---

## 实施步骤

### Step 1：改造底座 `base.py`

**1a. 加 `args_schema` + property**

```python
class BaseMCPTool(ABC):
    name: str
    description: str
    category: MCPToolCategory
    args_schema: type[BaseModel] | None = None
    expose_to_llm: bool = True

    @property
    def parameters_schema(self) -> dict[str, Any]:
        if self.args_schema is not None:
            return self.args_schema.model_json_schema()
        return getattr(self, '_parameters_schema', {"type": "object"})
```

**1b. 改 `registry.execute()` — Pydantic 校验 + 分离系统参数**

```python
async def execute(self, tool_name: str, **kwargs) -> MCPToolResult:
    tool = self.get(tool_name)
    if not tool:
        return MCPToolResult(success=False, error=f"Tool not found: {tool_name}")

    # 分离系统参数
    db = kwargs.pop("db", None)
    user_id = kwargs.pop("user_id", None)
    system_extra = {}
    for key in ("websocket", "chat_session", "session_id"):
        if key in kwargs:
            system_extra[key] = kwargs.pop(key)

    # Pydantic 校验业务参数（替代 jsonschema）
    if tool.args_schema is not None:
        try:
            args = tool.args_schema.model_validate(kwargs)
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))
    else:
        args = kwargs

    try:
        return await tool.execute(args=args, db=db, user_id=user_id, **system_extra)
    except Exception as e:
        if isinstance(db, AsyncSession):
            try:
                await db.rollback()
            except Exception:
                pass
        return MCPToolResult(success=False, error=str(e))
```

**1c. 删除 `_validate_params()` 方法** — Pydantic 替代 jsonschema，手动过滤逻辑不再需要。

**1d. 基类 `execute()` 签名更新**

```python
@abstractmethod
async def execute(self, args, *, db=None, user_id=None, **extra) -> MCPToolResult:
    ...
```

### Step 2：逐个工具文件重构

每个文件按以下模式改：

**改前：**
```python
class GetNovelInfoTool(BaseMCPTool):
    name = "get_novel_info"
    description = "..."
    category = MCPToolCategory.NOVEL_MANAGEMENT
    parameters_schema = {
        "type": "object",
        "properties": {
            "mode": {"type": "string", "enum": ["summary", "progress"], "description": "查询模式"}
        },
        "required": ["mode"]
    }

    async def execute(self, db, novel_id, user_id, mode="summary", **kwargs):
        ...
```

**改后：**
```python
class GetNovelInfoArgs(BaseModel):
    mode: Literal["summary", "progress"] = Field(default="summary", description="查询模式：summary=整体摘要，progress=写作进度")

class GetNovelInfoTool(BaseMCPTool):
    name = "get_novel_info"
    description = "..."
    category = MCPToolCategory.NOVEL_MANAGEMENT
    args_schema = GetNovelInfoArgs

    async def execute(self, args: GetNovelInfoArgs, *, db: AsyncSession, user_id: int, novel_id: int, **extra) -> MCPToolResult:
        ...
```

涉及文件（分批提交）：

| 序号 | 文件 | 工具数 | 备注 |
|------|------|--------|------|
| 2a | `novel_tools.py` | 9→6 | 角色 3 工具待迁出；含公共函数待迁移 |
| 2b | `memory_tools.py` | 1 | |
| 2c | `consistency_tools.py` | 1 | |
| 2d | `timeline_tools.py` | 3 | 依赖 novel_tools 的 cache 函数 |
| 2e | `character_tools.py` | 1→4 | 接收从 novel_tools 迁入的角色 3 工具 |
| 2f | `location_tools.py` | 4 | 依赖 novel_tools 的 cache 函数 |
| 2g | `story_arc_tools.py` | 3 | |
| 2h | `story_state_tools.py` | 2 | 需修复权限校验 bug |
| 2i | `reader_perspective_tools.py` | 3 | 需修复权限校验 bug |
| 2j | `editing_tools.py` | 2 | |
| 2k | `workflow_tools.py` | 1 | 已有 `CreateOutlineArgs`，需改用 `args_schema` |

### Step 3：修复安全缺陷 — 权限校验

`story_state_tools.py` 和 `reader_perspective_tools.py` 共 5 个工具调了 `verify_novel_ownership` 但不检查返回值。添加 `if not novel: return MCPToolResult(success=False, error="无权访问此小说或小说不存在")` 守卫。

### Step 4：重组角色工具

将 `GetCharactersTool`、`CreateCharacterTool`、`UpdateCharacterTool` 从 `novel_tools.py` 移至 `character_tools.py`。角色关系工具已在 `character_tools.py`，迁移后角色领域统一。

### Step 5：下放工具专有辅助函数

以下只被单个工具使用的模块顶层函数，改为工具类的静态方法或内联：

| 函数 | 文件 | 处理 |
|------|------|------|
| `_build_creative_profile_summary` | `novel_tools.py` | 改为 `UpdateCreativeProfileTool` 的 `@staticmethod` |
| `_attach_profile_summary` | `novel_tools.py` | 改为 `UpdateCreativeProfileTool` 的 `@staticmethod` |
| `_build_agent_task_id` | `editing_tools.py` | 内联进 `_execute_subagent_task` |
| `_normalize_subagent_task_type` | `editing_tools.py` | 内联进 `_execute_subagent_task` |

### Step 6：消重复 — `UpdateCreativeProfileTool` 缓存失效

`execute()` 第 642-645 行改为调用 `_invalidate_novel_cache(novel_id)`，删除内联重复代码。

### Step 7：`UpdateLocationTool` 参数规范化

`execute()` 中 `kwargs.get("name")` 等改为逐一命名参数，与其他工具保持一致。

### Step 8：修正 `CreateNewChapterTool` 的 schema

从 `parameters_schema` 移除 `novel_id` 字段，与其他"无需传novel_id"的工具一致。

### Step 9：抽公共工具函数

将 `_invalidate_novel_cache`、`_invalidate_character_cache`、`_invalidate_chapter_cache` 从 `novel_tools.py` 迁到新文件 `mcp_tools/utils.py`，更新所有导入方（`timeline_tools.py`、`character_tools.py`、`location_tools.py`）。

### Step 10：统一注册风格

全部改为顶层函数 `register_xxx_tools(registry)`，删除 `class XxxTools` 包装类和 `CreateOutlineTool.register_all()` 类方法。

### Step 11：统一错误处理规则

规则：
- **业务校验失败**（参数不合法、资源不存在、权限不足）→ 工具 `return MCPToolResult(success=False, error="...")`
- **意外异常**（数据库断连、网络超时）→ 不 catch，让 `registry.execute()` 统一兜底 rollback + 返回错误

清除工具内部的 `try/except Exception` 包装（除非确实需要做资源清理）。

### Step 12：确保审批状态清理完整

审查 `workflow_tools.py` 中 `_approval_events` 和 `_approval_results` 的所有生命周期路径，确保 WS 断开、事件循环关闭、`CreateOutlineTool` 异常等所有路径都调用 `cleanup_approval(session_id)`。审批本身不加超时——设计上 WS 连着就一直等。

### Step 13：处理 `server.py`

如果确认前端不使用 MCP 协议端点，删除 `server.py` 并在 `main.py` 移除 mount。后续如需恢复，可用 `mcp.add_tool()` 从注册表动态生成，几十行代码即可。

如果暂时保留，则改为动态注册：

```python
def register_all_to_mcp(mcp: FastMCP, registry: MCPToolRegistry):
    """从注册表自动注册所有工具到 FastMCP"""
    for tool in registry._tools.values():
        if tool.args_schema is None:
            continue
        _register_single_tool(mcp, tool)
```

### Step 14：清理 `to_openai_function()` 的 magic string

Args 模型本身不含 `novel_id`（系统注入参数），schema 自动生成时就不会出现 `novel_id`，`"无需传novel_id" in description` 的判断逻辑自然失效——直接删除这段剥离代码。

### Step 15：审查合并/移除冗余工具（最后做）

重构完成后，从以下角度审查全部 30 个工具：
- LLM 实际调用频率（高频 / 偶尔 / 从未调用）
- 功能重叠（如多个工具做相似查询）
- 是否可通过组合现有工具实现

决定合并或移除冗余项。此步骤放在最后，避免在重构过程中过早删除导致遗漏依赖。

### Step 16：加工具调用日志（低优先级）

在 `registry.execute()` 中加统一耗时统计：

```python
t0 = time.monotonic()
try:
    result = await tool.execute(args=args, db=db, user_id=user_id, **system_extra)
    elapsed = (time.monotonic() - t0) * 1000
    logger.info(f"tool={tool_name} elapsed={elapsed:.0f}ms success={result.success}")
    return result
except Exception as e:
    elapsed = (time.monotonic() - t0) * 1000
    logger.error(f"tool={tool_name} elapsed={elapsed:.0f}ms error={e}")
    ...
```

对所有工具调用统一可观测。

### Step 17：公开注册表迭代器（低优先级）

当前 `server.py` 动态注册或外部遍历工具时需访问私有 `registry._tools`。加一个公开方法：

```python
class MCPToolRegistry:
    def iter_tools(self) -> list[BaseMCPTool]:
        """返回所有已注册工具的列表"""
        return list(self._tools.values())
```

---

## 验证

1. 后端启动无 import 错误
2. `ruff check --select UP045,UP006,UP035,F401 --fix backend/` 通过
3. 创建 chat session，触发各工具调用，确认 LLM 工具循环正常
4. 触发大纲审批流程，确认审批 + WS 断开清理正常
5. 确认 `inject` 注入上下文正常工作
6. 确认未授权用户无法访问故事状态和读者认知

---

## 提交计划

| 步骤 | 提交信息 |
|------|----------|
| Step 1 | `refactor(mcp): 改造 base.py — args_schema 属性 + Pydantic 校验替代 jsonschema` |
| Step 2a-2c | `refactor(mcp): 迁移 novel/memory/consistency 工具到 Args 模型` |
| Step 2d-2f | `refactor(mcp): 迁移 timeline/character/location 工具到 Args 模型` |
| Step 2g-2i | `refactor(mcp): 迁移 story_arc/state/reader_perspective 工具到 Args 模型` |
| Step 2j-2k | `refactor(mcp): 迁移 editing/workflow 工具到 Args 模型` |
| Step 3 | `fix(mcp): 修复故事状态和读者认知工具缺失的权限校验` |
| Step 4 | `refactor(mcp): 角色工具集中到 character_tools.py` |
| Step 5 | `refactor(mcp): 下放工具专有辅助函数到工具类` |
| Step 6 | `refactor(mcp): 消除 UpdateCreativeProfile 重复缓存失效代码` |
| Step 7 | `refactor(mcp): UpdateLocationTool 参数规范化` |
| Step 8 | `fix(mcp): CreateNewChapterTool schema 移除多余 novel_id` |
| Step 9 | `refactor(mcp): 抽公共工具函数到 utils.py` |
| Step 10 | `refactor(mcp): 统一注册风格为顶层函数` |
| Step 11 | `refactor(mcp): 统一错误处理规则` |
| Step 12 | `fix(mcp): 确保审批状态清理覆盖所有断开路径` |
| Step 13 | `refactor(mcp): server.py 动态注册 / 删除死代码` |
| Step 14 | `refactor(mcp): 清理 to_openai_function magic string` |
| Step 15 | `refactor(mcp): 审查合并/移除冗余工具` |
| Step 16 | `feat(mcp): 加工具调用统一日志` |
| Step 17 | `refactor(mcp): 公开注册表迭代器` |
