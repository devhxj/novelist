"""
审核Agent - 负责内容审核和一致性检查
"""
import logging
from typing import Dict, Any, List

from .base import BaseAgent, AgentTask, AgentResult, AgentRole, TaskType

logger = logging.getLogger(__name__)


class ReviewerAgent(BaseAgent):
    """审核Agent - 负责内容审核和一致性检查"""
    
    def __init__(self, agent_id: str = "reviewer_001"):
        super().__init__(agent_id, AgentRole.REVIEWER)
        self.supported_tasks = {
            TaskType.REVIEW_CHAPTER,
            TaskType.CHECK_CONSISTENCY,
            TaskType.MANAGE_FORESHADOWING
        }
    
    def can_handle(self, task_type: TaskType) -> bool:
        return task_type in self.supported_tasks
    
    async def execute(self, task: AgentTask) -> AgentResult:
        """执行审核任务"""
        self.log_task_start(task)
        
        try:
            if task.task_type == TaskType.REVIEW_CHAPTER:
                result = await self._review_chapter(task)
            elif task.task_type == TaskType.CHECK_CONSISTENCY:
                result = await self._check_consistency(task)
            elif task.task_type == TaskType.MANAGE_FORESHADOWING:
                result = await self._manage_foreshadowing(task)
            else:
                result = self.create_result(
                    task=task,
                    success=False,
                    error=f"Unsupported task type: {task.task_type}"
                )
            
            self.log_task_complete(result)
            return result
            
        except Exception as e:
            self.logger.error(f"Error in review task: {e}")
            return self.create_result(
                task=task,
                success=False,
                error=str(e)
            )
    
    async def _review_chapter(self, task: AgentTask) -> AgentResult:
        """审核章节内容"""
        content = task.parameters.get("content", "")
        context = task.context
        
        issues = []
        suggestions = []
        
        if len(content) < 500:
            issues.append({
                "type": "length",
                "severity": "warning",
                "message": "章节内容过短，建议扩充"
            })
        
        characters = context.get("characters", [])
        for char in characters:
            char_name = char.get("name", "")
            if char_name and char_name not in content:
                issues.append({
                    "type": "character_missing",
                    "severity": "info",
                    "message": f"角色 '{char_name}' 未在本章出现"
                })
        
        plot_hints = context.get("plot_hints", [])
        for hint in plot_hints:
            if hint.get("type") == "unresolved":
                suggestions.append(f"考虑解决伏笔：{hint.get('description', '')}")
        
        passed = len([i for i in issues if i.get("severity") == "error"]) == 0
        
        return self.create_result(
            task=task,
            success=passed,
            result={
                "content_length": len(content),
                "issues_found": len(issues),
                "issues": issues,
                "passed": passed
            },
            suggestions=suggestions,
            next_actions=[] if passed else [
                {
                    "type": "create_task",
                    "task_type": TaskType.GENERATE_CHAPTER.value,
                    "chapter_id": task.chapter_id,
                    "parameters": {
                        "revision": True,
                        "issues": issues
                    }
                }
            ]
        )
    
    async def _check_consistency(self, task: AgentTask) -> AgentResult:
        """检查一致性"""
        chapter_id = task.chapter_id
        parameters = task.parameters
        
        consistency_issues = []
        
        check_types = parameters.get("check_types", ["character", "plot", "timeline"])
        
        if "character" in check_types:
            char_issues = await self._check_character_consistency(task)
            consistency_issues.extend(char_issues)
        
        if "plot" in check_types:
            plot_issues = await self._check_plot_consistency(task)
            consistency_issues.extend(plot_issues)
        
        if "timeline" in check_types:
            timeline_issues = await self._check_timeline_consistency(task)
            consistency_issues.extend(timeline_issues)
        
        passed = len(consistency_issues) == 0
        
        return self.create_result(
            task=task,
            success=passed,
            result={
                "chapter_id": chapter_id,
                "consistency_issues": consistency_issues,
                "checks_performed": check_types,
                "passed": passed
            }
        )
    
    async def _manage_foreshadowing(self, task: AgentTask) -> AgentResult:
        """管理伏笔"""
        parameters = task.parameters
        action = parameters.get("action", "list")
        
        if action == "list":
            foreshadowing = await self._list_foreshadowing(task)
            return self.create_result(
                task=task,
                success=True,
                result={
                    "action": "list",
                    "foreshadowing": foreshadowing
                }
            )
        elif action == "create":
            new_fs = await self._create_foreshadowing(task)
            return self.create_result(
                task=task,
                success=True,
                result={
                    "action": "create",
                    "foreshadowing": new_fs
                }
            )
        elif action == "resolve":
            resolved = await self._resolve_foreshadowing(task)
            return self.create_result(
                task=task,
                success=True,
                result={
                    "action": "resolve",
                    "foreshadowing": resolved
                }
            )
        else:
            return self.create_result(
                task=task,
                success=False,
                error=f"Unknown foreshadowing action: {action}"
            )
    
    async def _check_character_consistency(self, task: AgentTask) -> List[Dict[str, Any]]:
        """检查角色一致性"""
        return []
    
    async def _check_plot_consistency(self, task: AgentTask) -> List[Dict[str, Any]]:
        """检查情节一致性"""
        return []
    
    async def _check_timeline_consistency(self, task: AgentTask) -> List[Dict[str, Any]]:
        """检查时间线一致性"""
        return []
    
    async def _list_foreshadowing(self, task: AgentTask) -> List[Dict[str, Any]]:
        """列出伏笔"""
        return []
    
    async def _create_foreshadowing(self, task: AgentTask) -> Dict[str, Any]:
        """创建伏笔"""
        return {}
    
    async def _resolve_foreshadowing(self, task: AgentTask) -> Dict[str, Any]:
        """解决伏笔"""
        return {}
