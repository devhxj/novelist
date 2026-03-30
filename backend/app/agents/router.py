"""
Agent系统API路由
"""
import logging
from fastapi import APIRouter, Depends, Query

from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException
from app.core.auth import get_current_user, CurrentUser
from app.core.dependencies import NovelOwner
from .base import AgentTask, TaskType, TaskStatus
from .coordinator import CoordinatorAgent
from .writer import WriterAgent
from .reviewer import ReviewerAgent

router = APIRouter(prefix="/agents", tags=["agents"])
logger = logging.getLogger(__name__)

coordinator = CoordinatorAgent()
coordinator.register_agent(WriterAgent())
coordinator.register_agent(ReviewerAgent())


@router.get("/status")
async def get_agent_status(
    current_user: CurrentUser
):
    """获取Agent系统状态"""
    status = coordinator.get_agent_status()
    return ApiResponse.success(status)


@router.post("/novels/{novel_id}/tasks")
async def create_task(
    novel: NovelOwner,
    task_type: str,
    chapter_id: int = None,
    parameters: dict = None
):
    """创建Agent任务"""
    try:
        task = AgentTask(
            task_id=f"task_{novel.id}_{task_type}_{chapter_id or 'general'}",
            task_type=TaskType(task_type),
            novel_id=novel.id,
            chapter_id=chapter_id,
            parameters=parameters or {}
        )
        
        result = await coordinator.execute(task)
        
        return ApiResponse.success(result.to_dict())
        
    except ValueError as e:
        return ApiResponse.error(
            code="AGENT_001",
            message=f"Invalid task type: {task_type}",
            status_code=400
        )
    except Exception as e:
        logger.error(f"Task execution failed: {e}")
        return ApiResponse.error(
            code="AGENT_002",
            message=f"Task execution failed: {str(e)}",
            status_code=500
        )


@router.get("/novels/{novel_id}/tasks")
async def get_tasks(
    novel: NovelOwner,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100)
):
    """获取小说的Agent任务列表"""
    pending_tasks = coordinator.get_pending_tasks()
    
    novel_tasks = [t for t in pending_tasks if t.get("novel_id") == novel.id]
    
    return ApiResponse.paginated(
        novel_tasks,
        len(novel_tasks),
        page,
        page_size
    )


@router.get("/tasks/{task_id}")
async def get_task_status(
    task_id: str,
    current_user: CurrentUser
):
    """获取任务状态"""
    status = coordinator.get_task_status(task_id)
    
    if not status:
        raise NotFoundException("任务")
    
    return ApiResponse.success(status)
