"""
MCP工具基类和注册表
定义MCP工具的标准接口和注册机制
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
    """MCP工具基类"""

    name: str
    description: str
    category: MCPToolCategory
    args_schema: type[BaseModel] | None = None
    expose_to_llm: bool = True

    @property
    def parameters_schema(self) -> dict[str, Any]:
        if self.args_schema is not None:
            return self.args_schema.model_json_schema()
        return getattr(self, "_parameters_schema", {"type": "object"})

    @parameters_schema.setter
    def parameters_schema(self, value: dict[str, Any]) -> None:
        self._parameters_schema = value

    @abstractmethod
    async def execute(self, args: Any = None, *, db: AsyncSession | None = None,
                      user_id: int | None = None, **extra: Any) -> MCPToolResult:
        """执行工具"""
        ...

    def get_info(self) -> dict[str, Any]:
        """获取工具信息"""
        return {
            "name": self.name,
            "description": self.description,
            "category": self.category.value,
            "parameters_schema": self.parameters_schema,
        }

    def to_openai_function(self) -> dict[str, Any]:
        """转换为OpenAI function calling格式"""
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
    """MCP工具注册表 - 实例化模式"""
    
    def __init__(self):
        self._tools: dict[str, BaseMCPTool] = {}
    
    def register(self, tool: BaseMCPTool) -> None:
        """注册工具"""
        self._tools[tool.name] = tool
    
    def get(self, name: str) -> BaseMCPTool | None:
        """获取工具"""
        return self._tools.get(name)

    def _filter_tools(
        self,
        category: MCPToolCategory | None = None,
        allowed_names: list[str] | None = None,
    ) -> list[BaseMCPTool]:
        tools = list(self._tools.values())
        if category:
            tools = [tool for tool in tools if tool.category == category]
        if allowed_names is not None:
            allowed_set = set(allowed_names)
            tools = [tool for tool in tools if tool.name in allowed_set]
        return tools

    def list_tools(
        self,
        category: MCPToolCategory | None = None,
        allowed_names: list[str] | None = None,
    ) -> list[dict[str, Any]]:
        """列出所有工具"""
        tools = self._filter_tools(category=category, allowed_names=allowed_names)
        return [t.get_info() for t in tools]

    def list_by_category(self, allowed_names: list[str] | None = None) -> dict[str, list[dict[str, Any]]]:
        """按分类列出工具"""
        result: dict[str, list[dict[str, Any]]] = {}
        for tool in self._filter_tools(allowed_names=allowed_names):
            cat = tool.category.value
            if cat not in result:
                result[cat] = []
            result[cat].append(tool.get_info())
        return result

    def get_openai_functions(self, allowed_names: list[str] | None = None) -> list[dict[str, Any]]:
        """获取所有工具的OpenAI function calling格式"""
        return [
            tool.to_openai_function()
            for tool in self._filter_tools(allowed_names=allowed_names)
            if getattr(tool, "expose_to_llm", True)
        ]

    def iter_tools(self) -> list[BaseMCPTool]:
        """返回所有已注册工具"""
        return list(self._tools.values())

    async def execute(self, tool_name: str, **kwargs: Any) -> MCPToolResult:
        """执行工具"""
        tool = self.get(tool_name)
        if not tool:
            return MCPToolResult(success=False, error=f"Tool not found: {tool_name}")

        db: AsyncSession | None = kwargs.pop("db", None)
        user_id: int | None = kwargs.pop("user_id", None)
        system_extra: dict[str, Any] = {}
        for key in ("websocket", "chat_session", "session_id"):
            if key in kwargs:
                system_extra[key] = kwargs.pop(key)

        if tool.args_schema is not None:
            try:
                args = tool.args_schema.model_validate(kwargs)
            except Exception as e:
                return MCPToolResult(success=False, error=str(e))
            call_kwargs = {"args": args}
        else:
            call_kwargs = dict(kwargs)

        call_kwargs.update(system_extra)

        t0 = time.monotonic()
        try:
            result = await tool.execute(db=db, user_id=user_id, **call_kwargs)
            elapsed = (time.monotonic() - t0) * 1000
            logger.info("tool=%s elapsed=%.0fms success=%s", tool_name, elapsed, result.success)
            return result
        except Exception as e:
            elapsed = (time.monotonic() - t0) * 1000
            logger.error("tool=%s elapsed=%.0fms error=%s", tool_name, elapsed, e)
            if isinstance(db, AsyncSession):
                try:
                    await db.rollback()
                except Exception:
                    pass
            return MCPToolResult(success=False, error=str(e))
