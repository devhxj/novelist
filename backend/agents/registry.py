"""
SubAgent注册表

管理 task_type -> Agent类 + SubAgentSpec 的映射。
主Agent通过 run_subagent 工具指定 task_type，
Registry 自动找到对应的 Agent 和规格。
"""
import logging
from typing import Any

from agents.base import BaseAgent, SubAgentSpec

logger = logging.getLogger(__name__)

_REGISTRY: dict[str, tuple[type[BaseAgent], SubAgentSpec]] = {}


def register_agent(task_type: str, spec: SubAgentSpec):
    def decorator(cls: type[BaseAgent]):
        _REGISTRY[task_type] = (cls, spec)
        logger.info(f"Registered sub-agent: {task_type} -> {cls.__name__}")
        return cls
    return decorator


def get_agent_for_task(task_type: str) -> tuple[type[BaseAgent], SubAgentSpec] | None:
    return _REGISTRY.get(task_type)


def get_all_specs() -> dict[str, SubAgentSpec]:
    return {task_type: spec for task_type, (_, spec) in _REGISTRY.items()}


def get_available_task_types() -> list[str]:
    return list(_REGISTRY.keys())


def build_tool_description() -> str:
    lines = ["调度子Agent执行专业任务。可用任务类型："]
    for task_type, (_, spec) in _REGISTRY.items():
        req_chapter = " (需要chapter_id)" if spec.requires_chapter_id else ""
        lines.append(f"- {task_type}: {spec.description}{req_chapter}")
    lines.append("")
    lines.append("你只需指定任务类型和目标（如章节ID），后端会自动准备上下文。")
    lines.append("子Agent会返回结构化报告，包含摘要、关键发现和建议。")
    return "\n".join(lines)
