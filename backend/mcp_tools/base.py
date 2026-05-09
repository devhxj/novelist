"""
MCP工具基类和注册表
"""
import logging
import time
from abc import ABC, abstractmethod
from typing import Any
from pydantic import BaseModel
from enum import Enum
from sqlalchemy.ext.asyncio import AsyncSession

logger = logging.getLogger(__name__)


class MCPToolResult(BaseModel):
    """MCP工具执行结果"""
    success: bool
    data: Any | None = None
    error: str | None = None
    metadata: dict[str, Any] | None = None
    inject: list[dict[str, Any]] | None = None


class MCPToolCategory(str, Enum):
    """MCP工具分类"""
    NOVEL_MANAGEMENT = "novel_management"
    MEMORY_RETRIEVAL = "memory_retrieval"
    CONSISTENCY_CHECK = "consistency_check"
    WRITING_ASSISTANT = "writing_assistant"


class BaseMCPTool(ABC):
    """MCP工具基类 — 模板方法模式"""

    name: str
    description: str
    category: MCPToolCategory
    args_schema: type[BaseModel] | None = None
    expose_to_llm: bool = True

    def __init_subclass__(cls, **kwargs: Any) -> None:
        super().__init_subclass__(**kwargs)
        if "execute" in cls.__dict__:
            raise TypeError(f"{cls.__name__}: 禁止覆盖 execute()，请实现 _execute()")

    @property
    def parameters_schema(self) -> dict[str, Any]:
        if self.args_schema is not None:
            return self.args_schema.model_json_schema()
        return getattr(self, "_parameters_schema", {"type": "object"})

    @parameters_schema.setter
    def parameters_schema(self, value: dict[str, Any]) -> None:
        self._parameters_schema = value

    # ── 模板方法 ──────────────────────────────────────

    async def execute(self, *, db: AsyncSession, user_id: int,
                      novel_id: int, **tool_params: Any) -> MCPToolResult:
        """统一入口：鉴权 → 校验 → 分发 _execute()"""

        from core.permissions import verify_novel_ownership
        if not await verify_novel_ownership(db, novel_id, user_id):
            return MCPToolResult(
                success=False, error="无权访问此小说或小说不存在"
            )

        if self.args_schema is None:
            return MCPToolResult(
                success=False, error=f"Tool {self.name}: args_schema not set"
            )

        system_extra: dict[str, Any] = {}
        for key in ("websocket", "chat_session", "session_id"):
            if key in tool_params:
                system_extra[key] = tool_params.pop(key)

        try:
            args = self.args_schema.model_validate(tool_params)
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))

        return await self._execute(args=args, db=db, user_id=user_id, novel_id=novel_id, **system_extra)

    @abstractmethod
    async def _execute(self, *args: Any, **kwargs: Any) -> MCPToolResult:
        """子类实现业务逻辑 — args 已校验"""
        ...

    # ── 工具信息 ──────────────────────────────────────

    def get_info(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "description": self.description,
            "category": self.category.value,
            "parameters_schema": self.parameters_schema,
        }

    def to_openai_function(self) -> dict[str, Any]:
        parameters: dict[str, Any] = self.parameters_schema or {"type": "object"}
        return {
            "type": "function",
            "function": {
                "name": self.name,
                "description": self.description,
                "parameters": parameters,
            },
        }


class MCPToolRegistry:
    """MCP工具注册表"""

    def __init__(self) -> None:
        self._tools: dict[str, BaseMCPTool] = {}

    def register(self, tool: BaseMCPTool) -> None:
        self._tools[tool.name] = tool

    def get(self, name: str) -> BaseMCPTool | None:
        return self._tools.get(name)

    def iter_tools(self) -> list[BaseMCPTool]:
        return list(self._tools.values())

    def _filter_tools(
        self,
        category: MCPToolCategory | None = None,
        allowed_names: list[str] | None = None,
    ) -> list[BaseMCPTool]:
        tools = list(self._tools.values())
        if category:
            tools = [t for t in tools if t.category == category]
        if allowed_names is not None:
            allowed_set = set(allowed_names)
            tools = [t for t in tools if t.name in allowed_set]
        return tools

    def list_tools(
        self, category: MCPToolCategory | None = None,
        allowed_names: list[str] | None = None,
    ) -> list[dict[str, Any]]:
        return [t.get_info() for t in self._filter_tools(category, allowed_names)]

    def list_by_category(
        self, allowed_names: list[str] | None = None,
    ) -> dict[str, list[dict[str, Any]]]:
        result: dict[str, list[dict[str, Any]]] = {}
        for tool in self._filter_tools(allowed_names=allowed_names):
            cat = tool.category.value
            result.setdefault(cat, []).append(tool.get_info())
        return result

    def get_openai_functions(
        self, allowed_names: list[str] | None = None,
    ) -> list[dict[str, Any]]:
        return [
            t.to_openai_function()
            for t in self._filter_tools(allowed_names=allowed_names)
            if getattr(t, "expose_to_llm", True)
        ]

    async def execute(self, tool_name: str, *, db: AsyncSession,
                      user_id: int, novel_id: int,
                      **tool_params: Any) -> MCPToolResult:
        tool = self.get(tool_name)
        if not tool:
            return MCPToolResult(success=False, error=f"Tool not found: {tool_name}")

        t0 = time.monotonic()
        try:
            result = await tool.execute(db=db, user_id=user_id,
                                        novel_id=novel_id, **tool_params)
            elapsed = (time.monotonic() - t0) * 1000
            logger.info("tool=%s elapsed=%.0fms success=%s", tool_name, elapsed, result.success)
            return result
        except Exception as e:
            elapsed = (time.monotonic() - t0) * 1000
            logger.error("tool=%s elapsed=%.0fms error=%s", tool_name, elapsed, e)
            try:
                await db.rollback()
            except Exception:
                pass
            return MCPToolResult(success=False, error="服务器内部错误，请稍后重试")
