# MCP 工具开发规范

## 1. 文件结构

- 一个领域一个文件：`xxx_tools.py`
- 公共工具函数放 `utils.py`
- 注册函数必须是独立顶层函数：`register_xxx_tools(registry: MCPToolRegistry) -> None`
- 文件末尾放 register 函数

## 2. 工具类模板

```python
from pydantic import BaseModel, Field
from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry

class XxxArgs(BaseModel):
    """每个工具必须定义 Args 模型，作为参数定义的唯一来源"""
    required_param: str = Field(description="必填参数说明")
    optional_param: str | None = Field(default=None, description="可选参数说明")


class XxxTool(BaseMCPTool):
    name = "xxx"
    description = "工具描述，给 LLM 看的，要说明适用场景和参数用法"
    category = MCPToolCategory.XXX
    args_schema = XxxArgs   # 必设，JSON Schema 通过 model_json_schema() 自动生成

    async def _execute(
        self,
        args: XxxArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        # 实现业务逻辑
        ...
```

**禁止**：
- 手写 `parameters_schema` 字典
- 覆盖 `execute()` 方法
- 在 `_execute()` 签名中省略 `**extra`

## 3. 参数定义规则

- Python 关键字字段名用 `Field(alias="xxx")` 处理，如 `entry_type: str = Field(alias="type")`
- 更新类工具不用 `XxxUpdateArgs` 中 `exclude=True` 标记定位字段（如 `entry_id`），在 `_execute()` 中 `pop` 掉
- 更新类工具使用 `model_dump(exclude_unset=True)` 实现 PATCH 语义
- `model_dump(exclude_unset=True)` 检查的是构造时传了哪些字段，不是默认值

## 4. 基类机制

`BaseMCPTool.execute()` 是 template method，按以下顺序执行：
1. `verify_novel_ownership(db, novel_id, user_id)` — 鉴权
2. `args_schema.model_validate(tool_params)` — Pydantic 校验
3. 提取 `websocket`、`chat_session`、`session_id` 到 `**extra`
4. 分发 `_execute(args=validated_args, db=db, user_id=uid, novel_id=nid, **extra)`

系统参数 `db`、`user_id`、`novel_id` 由基类注入，不在 Args 模型中定义。

## 5. 错误处理

| 类型 | 做法 |
|------|------|
| 业务错误（参数不合法、资源不存在、权限不足） | `return MCPToolResult(success=False, error="明确的中文消息")` |
| 意外异常（DB 断连、网络超时） | 不 catch，让 `registry.execute()` 统一兜底 |

工具 **不应** 用 `try/except Exception` 包裹整个 `_execute()` 方法体。

例外：非关键富化数据（如列表附加的关系/事件摘要），可用 scoped catch 降级，但必须打日志：
```python
try:
    enrichment = await load_enrichment(...)
except Exception:
    logger.warning("Failed to load enrichment", exc_info=True)
```

## 6. 异步状态清理

如有 `await` 阻塞等待（如审批 Event），必须用 `try/finally` 保证清理：
```python
event, result = _get_approval(session_id)
try:
    await event.wait()
    ...
finally:
    cleanup_approval(session_id)
```

## 7. 注册与暴露

- `expose_to_llm` 默认 `True`，不设即全暴露
- 新增工具后需在 `registry.py` 导入并调用 register 函数
- 新工具需在 `chat/edit_mode.py` 的 `MODE_ALLOWED_TOOLS` 中注册
- 如需关键词触发，在 `TOOL_BUNDLES` 和 `TOOL_BUNDLE_CUES` 添加对应项

## 8. 缓存失效

使用 `utils.py` 的统一函数，不要直接调 `redis_service` 或 `context_cache`：
```python
from .utils import _invalidate_novel_cache, _invalidate_character_cache, _invalidate_chapter_cache
```

## 9. 类型注解

- 必须使用现代语法：`X | None`、`list[X]`、`dict[K, V]`
- register 函数签名必须标注：`def register_xxx_tools(registry: MCPToolRegistry) -> None:`
- `_execute` 的 `db` 参数标注 `AsyncSession`

## 10. Category 选择

| Category | 适用工具 |
|----------|---------|
| `NOVEL_MANAGEMENT` | 小说/章节/角色/地点 CRUD |
| `WRITING_ASSISTANT` | 创作辅助（大纲、弧线、时间线、编辑、子Agent） |
| `MEMORY_RETRIEVAL` | 检索查询（记忆、时间线、弧线、读者视角） |
| `CONSISTENCY_CHECK` | 一致性审查 |
