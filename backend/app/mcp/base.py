"""
MCP工具基类和注册表
定义MCP工具的标准接口和注册机制
"""
from abc import ABC, abstractmethod
from typing import Any, Dict, List, Optional
from pydantic import BaseModel
from enum import Enum


class MCPToolResult(BaseModel):
    """MCP工具执行结果"""
    success: bool
    data: Optional[Any] = None
    error: Optional[str] = None
    metadata: Optional[Dict[str, Any]] = None


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
    parameters_schema: Dict[str, Any]
    
    @abstractmethod
    async def execute(self, **kwargs) -> MCPToolResult:
        """执行工具"""
        pass
    
    def get_info(self) -> Dict[str, Any]:
        """获取工具信息"""
        return {
            "name": self.name,
            "description": self.description,
            "category": self.category.value,
            "parameters_schema": self.parameters_schema
        }
    
    def to_openai_function(self) -> Dict[str, Any]:
        """转换为OpenAI function calling格式"""
        return {
            "type": "function",
            "function": {
                "name": self.name,
                "description": self.description,
                "parameters": self.parameters_schema
            }
        }


class MCPToolRegistry:
    """MCP工具注册表 - 实例化模式"""
    
    def __init__(self):
        self._tools: Dict[str, BaseMCPTool] = {}
    
    def register(self, tool: BaseMCPTool) -> None:
        """注册工具"""
        self._tools[tool.name] = tool
    
    def get(self, name: str) -> Optional[BaseMCPTool]:
        """获取工具"""
        return self._tools.get(name)
    
    def list_tools(self, category: Optional[MCPToolCategory] = None) -> List[Dict[str, Any]]:
        """列出所有工具"""
        tools = list(self._tools.values())
        if category:
            tools = [t for t in tools if t.category == category]
        return [t.get_info() for t in tools]
    
    def list_by_category(self) -> Dict[str, List[Dict[str, Any]]]:
        """按分类列出工具"""
        result = {}
        for tool in self._tools.values():
            cat = tool.category.value
            if cat not in result:
                result[cat] = []
            result[cat].append(tool.get_info())
        return result
    
    def get_openai_functions(self) -> List[Dict[str, Any]]:
        """获取所有工具的OpenAI function calling格式"""
        return [tool.to_openai_function() for tool in self._tools.values()]
    
    async def execute(self, name: str, **kwargs) -> MCPToolResult:
        """执行工具"""
        tool = self.get(name)
        if not tool:
            return MCPToolResult(
                success=False,
                error=f"Tool not found: {name}"
            )
        try:
            return await tool.execute(**kwargs)
        except Exception as e:
            return MCPToolResult(
                success=False,
                error=str(e)
            )
