"""
Agent基类和核心数据结构
"""
import logging
from abc import ABC, abstractmethod
from typing import Dict, Any, Optional, List
from enum import Enum
from dataclasses import dataclass, field
from datetime import datetime

logger = logging.getLogger(__name__)


class AgentRole(str, Enum):
    """Agent角色枚举"""
    COORDINATOR = "coordinator"
    WRITER = "writer"
    REVIEWER = "reviewer"
    MEMORY = "memory"


class TaskType(str, Enum):
    """任务类型枚举"""
    GENERATE_CHAPTER = "generate_chapter"
    REVIEW_CHAPTER = "review_chapter"
    CHECK_CONSISTENCY = "check_consistency"
    UPDATE_MEMORY = "update_memory"
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
class AgentTask:
    """Agent任务"""
    task_id: str
    task_type: TaskType
    novel_id: int
    chapter_id: Optional[int] = None
    parameters: Dict[str, Any] = field(default_factory=dict)
    context: Dict[str, Any] = field(default_factory=dict)
    status: TaskStatus = TaskStatus.PENDING
    created_at: datetime = field(default_factory=datetime.now)
    updated_at: datetime = field(default_factory=datetime.now)
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "task_id": self.task_id,
            "task_type": self.task_type.value,
            "novel_id": self.novel_id,
            "chapter_id": self.chapter_id,
            "parameters": self.parameters,
            "context": self.context,
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
    result: Dict[str, Any] = field(default_factory=dict)
    error: Optional[str] = None
    suggestions: List[str] = field(default_factory=list)
    next_actions: List[Dict[str, Any]] = field(default_factory=list)
    completed_at: datetime = field(default_factory=datetime.now)
    
    def to_dict(self) -> Dict[str, Any]:
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
        result: Dict[str, Any] = None,
        error: str = None,
        suggestions: List[str] = None,
        next_actions: List[Dict[str, Any]] = None
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
