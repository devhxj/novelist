"""
主控Agent - 负责任务调度和协调
"""
import logging
from typing import Any
from datetime import datetime, timezone
import uuid

from .base import BaseAgent, AgentTask, AgentResult, AgentRole, TaskType, TaskStatus
from app.core.database import AsyncSessionLocal

logger = logging.getLogger(__name__)


ROLE_ALIASES = {
    "writer": AgentRole.WRITER.value,
    "写作专家": AgentRole.WRITER.value,
    "写手": AgentRole.WRITER.value,
    "作者": AgentRole.WRITER.value,
    "reviewer": AgentRole.REVIEWER.value,
    "审稿专家": AgentRole.REVIEWER.value,
    "审核专家": AgentRole.REVIEWER.value,
    "审阅专家": AgentRole.REVIEWER.value,
    "review": AgentRole.REVIEWER.value,
    "coordinator": AgentRole.COORDINATOR.value,
    "主控": AgentRole.COORDINATOR.value,
}


class CoordinatorAgent(BaseAgent):
    """主控Agent - 负责任务调度和协调"""
    
    def __init__(self, agent_id: str = "coordinator_001"):
        super().__init__(agent_id, AgentRole.COORDINATOR)
        self.agents: dict[str, BaseAgent] = {}
        self.task_queue: list[AgentTask] = []
        self.completed_tasks: dict[str, AgentResult] = {}
        self.max_auto_depth = 8
    
    def register_agent(self, agent: BaseAgent):
        """注册Agent"""
        self.agents[agent.agent_id] = agent
        self.logger.info(f"Registered agent: {agent.agent_id} with role {agent.role}")
    
    def can_handle(self, task_type: TaskType) -> bool:
        """主控Agent可以处理所有任务类型的调度"""
        return True
    
    async def execute(self, task: AgentTask) -> AgentResult:
        """执行任务调度"""
        self.log_task_start(task)
        task.root_task_id = task.root_task_id or task.task_id
        task.status = TaskStatus.IN_PROGRESS
        task.updated_at = datetime.now(timezone.utc)
        
        try:
            await self._save_task_record(task)
            result = await self._execute_task_chain(task, depth=0)
            self.completed_tasks[task.task_id] = result
            task.status = TaskStatus.COMPLETED if result.success else TaskStatus.FAILED
            task.updated_at = datetime.now(timezone.utc)
            await self._save_task_record(task, result=result)
            self.log_task_complete(result)
            return result
            
        except Exception as e:
            self.logger.error(f"Error executing task {task.task_id}: {e}")
            task.status = TaskStatus.FAILED
            task.updated_at = datetime.now(timezone.utc)
            await self._save_task_record(task, error=str(e))
            return self.create_result(
                task=task,
                success=False,
                error=str(e)
            )

    async def _execute_task_chain(self, task: AgentTask, depth: int) -> AgentResult:
        if depth > self.max_auto_depth:
            return self.create_result(
                task=task,
                success=False,
                error="任务自动编排深度超过限制"
            )

        task.status = TaskStatus.IN_PROGRESS
        task.updated_at = datetime.now(timezone.utc)
        await self._save_task_record(task)

        suitable_agent = self._find_suitable_agent(task)
        if not suitable_agent:
            return self.create_result(
                task=task,
                success=False,
                error=f"No suitable agent found for task type {task.task_type}"
            )

        self.logger.info(f"Dispatching task {task.task_id} to agent {suitable_agent.agent_id}")
        result = await suitable_agent.execute(task)
        task.status = TaskStatus.COMPLETED if result.success else TaskStatus.FAILED
        task.updated_at = datetime.now(timezone.utc)
        await self._save_task_record(task, result=result)

        if result.success and result.next_actions:
            subtask_results: list[dict[str, Any]] = []
            for action in result.next_actions:
                next_task = self._build_next_task(action, task)
                if not next_task:
                    continue
                next_task.status = TaskStatus.PENDING
                await self._save_task_record(next_task)
                self.task_queue.append(next_task)
                child_result = await self._execute_task_chain(next_task, depth + 1)
                self.completed_tasks[next_task.task_id] = child_result
                subtask_results.append(child_result.to_dict())
            if subtask_results:
                result.result["subtasks"] = subtask_results

        return result

    async def _save_task_record(
        self,
        task: AgentTask,
        result: AgentResult | None = None,
        error: str | None = None
    ) -> None:
        try:
            from .models import AgentTaskRecord
            from sqlalchemy import select

            async with AsyncSessionLocal() as db:
                query = await db.execute(select(AgentTaskRecord).where(AgentTaskRecord.task_id == task.task_id))
                existing = query.scalar_one_or_none()

                payload_result = result.to_dict() if result else None
                payload_error = error or (result.error if result and result.error else None)
                status = task.status.value
                if result:
                    status = TaskStatus.COMPLETED.value if result.success else TaskStatus.FAILED.value
                persisted_context = {
                    **task.context,
                    "_task_meta": {
                        "parent_task_id": task.parent_task_id,
                        "root_task_id": task.root_task_id or task.task_id,
                        "depth": task.depth
                    }
                }

                if existing is None:
                    record = AgentTaskRecord(
                        task_id=task.task_id,
                        novel_id=task.novel_id,
                        chapter_id=task.chapter_id,
                        task_type=task.task_type.value,
                        status=status,
                        parameters=task.parameters,
                        context=persisted_context,
                        result=payload_result,
                        error=payload_error,
                        agent_id=result.agent_id if result else None,
                        completed_at=result.completed_at if result else None
                    )
                    db.add(record)
                else:
                    existing.novel_id = task.novel_id
                    existing.chapter_id = task.chapter_id
                    existing.task_type = task.task_type.value
                    existing.status = status
                    existing.parameters = task.parameters
                    existing.context = persisted_context
                    if payload_result is not None:
                        existing.result = payload_result
                    existing.error = payload_error
                    if result:
                        existing.agent_id = result.agent_id
                        existing.completed_at = result.completed_at

                await db.commit()
        except Exception as persist_error:
            self.logger.warning(f"Failed to persist task {task.task_id}: {persist_error}")
    
    def _find_suitable_agent(self, task: AgentTask) -> BaseAgent | None:
        """找到能处理该任务的Agent"""
        agent_id = task.parameters.get("agent_id")
        agent_role = task.parameters.get("agent_role")
        normalized_role = ROLE_ALIASES.get(agent_role, agent_role) if agent_role else None
        if agent_id and agent_id in self.agents:
            agent = self.agents[agent_id]
            if agent.can_handle(task.task_type):
                return agent
        if normalized_role:
            for agent in self.agents.values():
                if agent.role.value == normalized_role and agent.can_handle(task.task_type):
                    return agent
        for agent in self.agents.values():
            if agent.can_handle(task.task_type):
                return agent
        return None
    
    def _build_next_task(self, action: dict[str, Any], parent_task: AgentTask) -> AgentTask | None:
        """构建后续任务"""
        action_type = action.get("type")

        if action_type != "create_task":
            return None

        unique_suffix = action.get("suffix") or uuid.uuid4().hex[:8]

        return AgentTask(
                task_id=f"{parent_task.task_id}_{unique_suffix}",
                task_type=TaskType(action.get("task_type")),
                novel_id=parent_task.novel_id,
                chapter_id=action.get("chapter_id", parent_task.chapter_id),
                parameters=action.get("parameters", {}),
                context={**parent_task.context, **action.get("context", {})},
                parent_task_id=parent_task.task_id,
                root_task_id=parent_task.root_task_id or parent_task.task_id,
                depth=parent_task.depth + 1
            )
    
    def get_task_status(self, task_id: str) -> dict[str, Any] | None:
        """获取任务状态"""
        if task_id in self.completed_tasks:
            return self.completed_tasks[task_id].to_dict()
        
        for task in self.task_queue:
            if task.task_id == task_id:
                return task.to_dict()
        
        return None
    
    def get_pending_tasks(self) -> list[dict[str, Any]]:
        """获取待处理任务"""
        return [task.to_dict() for task in self.task_queue if task.status == TaskStatus.PENDING]
    
    def get_agent_status(self) -> dict[str, Any]:
        """获取所有Agent状态"""
        return {
            "coordinator_id": self.agent_id,
            "registered_agents": len(self.agents),
            "agents": [
                {
                    "agent_id": agent.agent_id,
                    "role": agent.role.value
                }
                for agent in self.agents.values()
            ],
            "pending_tasks": len(self.task_queue),
            "completed_tasks": len(self.completed_tasks)
        }
