from __future__ import annotations

from .base import MCPToolRegistry
from .novel_tools import NovelManagementTools
from .memory_tools import MemoryRetrievalTools
from .consistency_tools import ConsistencyCheckTools
from .editing_tools import EditingTools
from .timeline_tools import register_timeline_tools
from .character_tools import register_character_tools
from .location_tools import register_location_tools
from .story_arc_tools import register_story_arc_tools
from .story_state_tools import register_story_state_tools
from .reader_perspective_tools import register_reader_perspective_tools

_registry: MCPToolRegistry | None = None


def get_mcp_registry() -> MCPToolRegistry:
    global _registry
    if _registry is None:
        registry = MCPToolRegistry()
        NovelManagementTools.register_all(registry)
        MemoryRetrievalTools.register_all(registry)
        ConsistencyCheckTools.register_all(registry)
        EditingTools.register_all(registry)
        register_timeline_tools(registry)
        register_character_tools(registry)
        register_location_tools(registry)
        register_story_arc_tools(registry)
        register_story_state_tools(registry)
        register_reader_perspective_tools(registry)
        _registry = registry
    return _registry
