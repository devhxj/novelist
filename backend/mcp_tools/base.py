"""
MCP工具基类和注册表
定义MCP工具的标准接口和注册机制
"""
from abc import ABC, abstractmethod
from typing import Any
from pydantic import BaseModel
from enum import Enum
from jsonschema import validate, ValidationError
from sqlalchemy.ext.asyncio import AsyncSession


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
    parameters_schema: dict[str, Any]
    expose_to_llm: bool = True
    
    @abstractmethod
    async def execute(self, **kwargs) -> MCPToolResult:
        """执行工具"""
        pass
    
    def get_info(self) -> dict[str, Any]:
        """获取工具信息"""
        return {
            "name": self.name,
            "description": self.description,
            "category": self.category.value,
            "parameters_schema": self.parameters_schema
        }
    
    def to_openai_function(self) -> dict[str, Any]:
        """转换为OpenAI function calling格式"""
        parameters: dict[str, Any] = (self.parameters_schema or {"type": "object"}).copy()
        raw_properties = parameters.get("properties")
        properties = raw_properties.copy() if isinstance(raw_properties, dict) else {}
        raw_required = parameters.get("required")
        required = list(raw_required) if isinstance(raw_required, list) else []

        if (
            "novel_id" in properties
            and ("无需传novel_id" in self.description or "无需提供novel_id" in self.description)
        ):
            properties.pop("novel_id", None)
            required = [item for item in required if item != "novel_id"]
            parameters["properties"] = properties
            parameters["required"] = required

        return {
            "type": "function",
            "function": {
                "name": self.name,
                "description": self.description,
                "parameters": parameters
            }
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

    def _validate_params(self, tool: BaseMCPTool, params: dict[str, Any]) -> str | None:
        schema = tool.parameters_schema or {"type": "object"}
        # 只保留 schema 中声明的字段，避免 chat_session 等内部对象
        # 被 jsonschema 塞进报错消息，泄露整个会话上下文给 LLM
        #
        # 白名单基于 schema.properties.keys()，注意：
        # - 使用 patternProperties / additionalProperties 的复杂 schema
        #   不会有 properties 键，allowed 为空，走 else 分支不过滤（回退到原始行为）
        # - 如果同时用了 properties + patternProperties，patternProperties 的
        #   字段会被过滤掉，导致误杀。现有工具无此用法，若新增工具需注意
        allowed = set(schema.get("properties", {}).keys())
        if allowed:
            params = {k: v for k, v in params.items() if k in allowed}
        try:
            validate(instance=params, schema=schema)
        except ValidationError as e:
            return str(e)
        return None
    
    async def execute(self, tool_name: str, **kwargs) -> MCPToolResult:
        """执行工具"""
        tool = self.get(tool_name)
        if not tool:
            return MCPToolResult(
                success=False,
                error=f"Tool not found: {tool_name}"
            )
        validation_params = {k: v for k, v in kwargs.items() if k != "db"}
        validation_error = self._validate_params(tool, validation_params)
        if validation_error:
            return MCPToolResult(success=False, error=validation_error)
        try:
            return await tool.execute(**kwargs)
        except Exception as e:
            db = kwargs.get("db")
            if isinstance(db, AsyncSession):
                try:
                    await db.rollback()
                except Exception:
                    pass
            return MCPToolResult(
                success=False,
                error=str(e)
            )
