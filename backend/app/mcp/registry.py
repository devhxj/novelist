from __future__ import annotations

from .base import MCPToolRegistry
from .novel_tools import NovelManagementTools
from .memory_tools import MemoryRetrievalTools
from .consistency_tools import ConsistencyCheckTools
from .editing_tools import EditingTools

_registry: MCPToolRegistry | None = None


def get_mcp_registry() -> MCPToolRegistry:
    global _registry
    if _registry is None:
        registry = MCPToolRegistry()
        NovelManagementTools.register_all(registry)
        MemoryRetrievalTools.register_all(registry)
        ConsistencyCheckTools.register_all(registry)
        EditingTools.register_all(registry)
        _registry = registry
    return _registry
