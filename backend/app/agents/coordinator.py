"""
主控Agent - 负责任务调度和协调
"""
import logging
from typing import Dict, Any, List, Optional
from datetime import datetime

from .base import BaseAgent, AgentTask, AgentResult, AgentRole, TaskType, TaskStatus

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
        self.agents: Dict[str, BaseAgent] = {}
        self.task_queue: List[AgentTask] = []
        self.completed_tasks: Dict[str, AgentResult] = {}
    
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
        
        try:
            suitable_agent = self._find_suitable_agent(task)
            
            if not suitable_agent:
                return self.create_result(
                    task=task,
                    success=False,
                    error=f"No suitable agent found for task type {task.task_type}"
                )
            
            self.logger.info(f"Dispatching task {task.task_id} to agent {suitable_agent.agent_id}")
            
            result = await suitable_agent.execute(task)
            
            self.completed_tasks[task.task_id] = result
            
            if result.success and result.next_actions:
                for action in result.next_actions:
                    await self._handle_next_action(action, task)
            
            self.log_task_complete(result)
            return result
            
        except Exception as e:
            self.logger.error(f"Error executing task {task.task_id}: {e}")
            return self.create_result(
                task=task,
                success=False,
                error=str(e)
            )
    
    def _find_suitable_agent(self, task: AgentTask) -> Optional[BaseAgent]:
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
    
    async def _handle_next_action(self, action: Dict[str, Any], parent_task: AgentTask):
        """处理后续动作"""
        action_type = action.get("type")
        
        if action_type == "create_task":
            new_task = AgentTask(
                task_id=f"{parent_task.task_id}_{action.get('suffix', 'next')}",
                task_type=TaskType(action.get("task_type")),
                novel_id=parent_task.novel_id,
                chapter_id=action.get("chapter_id", parent_task.chapter_id),
                parameters=action.get("parameters", {}),
                context=action.get("context", {})
            )
            self.task_queue.append(new_task)
            self.logger.info(f"Created new task: {new_task.task_id}")
    
    def get_task_status(self, task_id: str) -> Optional[Dict[str, Any]]:
        """获取任务状态"""
        if task_id in self.completed_tasks:
            return self.completed_tasks[task_id].to_dict()
        
        for task in self.task_queue:
            if task.task_id == task_id:
                return task.to_dict()
        
        return None
    
    def get_pending_tasks(self) -> List[Dict[str, Any]]:
        """获取待处理任务"""
        return [task.to_dict() for task in self.task_queue]
    
    def get_agent_status(self) -> Dict[str, Any]:
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
