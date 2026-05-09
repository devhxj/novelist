from __future__ import annotations

from .base import MCPToolRegistry
from .novel_tools import register_novel_tools
from .memory_tools import register_memory_tools
from .consistency_tools import register_consistency_tools
from .editing_tools import register_editing_tools
from .timeline_tools import register_timeline_tools
from .character_tools import register_character_tools
from .location_tools import register_location_tools
from .story_arc_tools import register_story_arc_tools
from .story_state_tools import register_story_state_tools
from .reader_perspective_tools import register_reader_perspective_tools
from .workflow_tools import register_workflow_tools

_registry: MCPToolRegistry | None = None


def get_mcp_registry() -> MCPToolRegistry:
    global _registry
    if _registry is None:
        registry = MCPToolRegistry()
        register_novel_tools(registry)
        register_memory_tools(registry)
        register_consistency_tools(registry)
        register_editing_tools(registry)
        register_timeline_tools(registry)
        register_character_tools(registry)
        register_location_tools(registry)
        register_story_arc_tools(registry)
        register_story_state_tools(registry)
        register_reader_perspective_tools(registry)
        register_workflow_tools(registry)
        _registry = registry
    return _registry
