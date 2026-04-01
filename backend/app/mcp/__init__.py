"""
MCP (Model Context Protocol) 工具模块
为AI Agent提供标准化的工具接口
"""
from .base import BaseMCPTool, MCPToolRegistry, MCPToolResult, MCPToolCategory
from .novel_tools import NovelManagementTools
from .memory_tools import MemoryRetrievalTools
from .consistency_tools import ConsistencyCheckTools
from .editing_tools import EditingTools

from .registry import get_mcp_registry
from .router import router

__all__ = [
    "BaseMCPTool",
    "MCPToolRegistry",
    "MCPToolResult",
    "MCPToolCategory",
    "NovelManagementTools",
    "MemoryRetrievalTools",
    "ConsistencyCheckTools",
    "EditingTools",
    "get_mcp_registry",
    "router",
]
