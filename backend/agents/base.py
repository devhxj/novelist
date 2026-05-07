"""
Agent基类和核心数据结构
"""
import logging
from abc import ABC, abstractmethod
from typing import Any
from enum import Enum
from dataclasses import dataclass, field
from datetime import datetime, timezone

logger = logging.getLogger(__name__)


class AgentRole(str, Enum):
    """Agent角色枚举"""
    COORDINATOR = "coordinator"
    WRITER = "writer"
    REVIEWER = "reviewer"


class TaskType(str, Enum):
    """任务类型枚举"""
    GENERATE_CHAPTER = "generate_chapter"
    WRITE_CHAPTER = "write_chapter"
    REVIEW_CHAPTER = "review_chapter"
    CHECK_CONSISTENCY = "check_consistency"
    PLAN_PLOT = "plan_plot"
    MANAGE_FORESHADOWING = "manage_foreshadowing"


class TaskStatus(str, Enum):
    """任务状态枚举"""
    PENDING = "pending"
    IN_PROGRESS = "in_progress"
    COMPLETED = "completed"
    FAILED = "failed"
    NEEDS_REVISION = "needs_revision"


@dataclass
class SubAgentSpec:
    """子Agent规格声明 - 描述子Agent的能力和需求"""
    task_type: str
    display_name: str
    description: str
    system_prompt: str
    required_context_keys: list[str] = field(default_factory=list)
    optional_context_keys: list[str] = field(default_factory=list)
    allowed_tools: list[str] = field(default_factory=list)
    allowed_resources: list[str] = field(default_factory=list)
    allow_subagent_spawn: bool = False
    requires_chapter_id: bool = False
    result_description: str = ""


@dataclass
class SubAgentReport:
    """子Agent执行报告 - 面向主Agent的结构化输出"""
    task_type: str
    success: bool
    summary: str
    key_findings: list[str] = field(default_factory=list)
    suggestions: list[str] = field(default_factory=list)
    data: dict[str, Any] = field(default_factory=dict)
    error: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "task_type": self.task_type,
            "success": self.success,
            "summary": self.summary,
            "key_findings": self.key_findings,
            "suggestions": self.suggestions,
            "data": self.data,
            "error": self.error,
        }


@dataclass
class AgentTask:
    """Agent任务"""
    task_id: str
    task_type: TaskType
    novel_id: int
    chapter_id: int | None = None
    parameters: dict[str, Any] = field(default_factory=dict)
    context: dict[str, Any] = field(default_factory=dict)
    parent_task_id: str | None = None
    root_task_id: str | None = None
    depth: int = 0
    status: TaskStatus = TaskStatus.PENDING
    created_at: datetime = field(default_factory=lambda: datetime.now(timezone.utc))
    updated_at: datetime = field(default_factory=lambda: datetime.now(timezone.utc))
    
    def to_dict(self) -> dict[str, Any]:
        return {
            "task_id": self.task_id,
            "task_type": self.task_type.value,
            "novel_id": self.novel_id,
            "chapter_id": self.chapter_id,
            "parameters": self.parameters,
            "context": self.context,
            "parent_task_id": self.parent_task_id,
            "root_task_id": self.root_task_id or self.task_id,
            "depth": self.depth,
            "status": self.status.value,
            "created_at": self.created_at.isoformat(),
            "updated_at": self.updated_at.isoformat()
        }


@dataclass
class AgentResult:
    """Agent执行结果"""
    task_id: str
    agent_id: str
    success: bool
    result: dict[str, Any] = field(default_factory=dict)
    error: str | None = None
    suggestions: list[str] = field(default_factory=list)
    next_actions: list[dict[str, Any]] = field(default_factory=list)
    completed_at: datetime = field(default_factory=lambda: datetime.now(timezone.utc))
    
    def to_dict(self) -> dict[str, Any]:
        return {
            "task_id": self.task_id,
            "agent_id": self.agent_id,
            "success": self.success,
            "result": self.result,
            "error": self.error,
            "suggestions": self.suggestions,
            "next_actions": self.next_actions,
            "completed_at": self.completed_at.isoformat()
        }


class BaseAgent(ABC):
    """Agent基类"""
    
    def __init__(self, agent_id: str, role: AgentRole):
        self.agent_id = agent_id
        self.role = role
        self.logger = logging.getLogger(f"agent.{role.value}")
    
    @abstractmethod
    async def execute(self, task: AgentTask) -> AgentResult:
        """执行任务"""
        pass
    
    @abstractmethod
    def can_handle(self, task_type: TaskType) -> bool:
        """判断是否能处理该任务"""
        pass
    
    def validate_task(self, task: AgentTask) -> bool:
        """验证任务"""
        if not self.can_handle(task.task_type):
            self.logger.warning(f"Agent {self.agent_id} cannot handle task type {task.task_type}")
            return False
        return True
    
    def create_result(
        self,
        task: AgentTask,
        success: bool,
        result: dict[str, Any] | None = None,
        error: str | None = None,
        suggestions: list[str] | None = None,
        next_actions: list[dict[str, Any]] | None = None
    ) -> AgentResult:
        """创建执行结果"""
        return AgentResult(
            task_id=task.task_id,
            agent_id=self.agent_id,
            success=success,
            result=result or {},
            error=error,
            suggestions=suggestions or [],
            next_actions=next_actions or []
        )
    
    def log_task_start(self, task: AgentTask):
        """记录任务开始"""
        self.logger.info(f"Agent {self.agent_id} starting task {task.task_id} of type {task.task_type}")
    
    def log_task_complete(self, result: AgentResult):
        """记录任务完成"""
        status = "success" if result.success else "failed"
        self.logger.info(f"Agent {self.agent_id} completed task {result.task_id} with status: {status}")
