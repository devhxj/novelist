"""
Agent系统API路由
"""
import logging
import uuid
from fastapi import APIRouter, Depends, Query
from sqlalchemy import select, func

from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException
from app.core.database import DBSession
from app.core.auth import get_current_user, CurrentUserDep
from app.core.dependencies import NovelOwner
from app.novels.models import Novel
from .base import AgentTask, TaskType, TaskStatus
from .factory import create_default_coordinator
from .models import AgentTaskRecord

router = APIRouter(prefix="/agents", tags=["agents"])
logger = logging.getLogger(__name__)

coordinator = create_default_coordinator()


@router.get("/status")
async def get_agent_status(
    current_user: CurrentUserDep
):
    """获取Agent系统状态"""
    status = coordinator.get_agent_status()
    return ApiResponse.success(status)


@router.post("/novels/{novel_id}/tasks")
async def create_task(
    novel: NovelOwner,
    task_type: str,
    chapter_id: int | None = None,
    parameters: dict[str, object] | None = None,
    agent_role: str | None = None,
    agent_id: str | None = None,
    model: str | None = None,
):
    """创建Agent任务"""
    try:
        task_parameters = parameters or {}
        if agent_role:
            task_parameters["agent_role"] = agent_role
        if agent_id:
            task_parameters["agent_id"] = agent_id
        if model:
            task_parameters["model"] = model
        
        task = AgentTask(
            task_id=f"task_{uuid.uuid4().hex}",
            task_type=TaskType(task_type),
            novel_id=novel.id,
            chapter_id=chapter_id,
            parameters=task_parameters
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
    db: DBSession,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100)
):
    """获取小说的Agent任务列表"""
    query = select(AgentTaskRecord).where(AgentTaskRecord.novel_id == novel.id)
    count_query = select(func.count()).select_from(query.subquery())
    total_result = await db.execute(count_query)
    total = total_result.scalar() or 0

    query = query.order_by(AgentTaskRecord.created_at.desc()).offset((page - 1) * page_size).limit(page_size)
    result = await db.execute(query)
    records = result.scalars().all()

    items = [
        {
            "task_id": record.task_id,
            "novel_id": record.novel_id,
            "chapter_id": record.chapter_id,
            "task_type": record.task_type,
            "status": record.status,
            "parent_task_id": (record.context or {}).get("_task_meta", {}).get("parent_task_id"),
            "root_task_id": (record.context or {}).get("_task_meta", {}).get("root_task_id"),
            "depth": (record.context or {}).get("_task_meta", {}).get("depth", 0),
            "parameters": record.parameters,
            "result": record.result,
            "error": record.error,
            "agent_id": record.agent_id,
            "created_at": record.created_at.isoformat() if record.created_at else None,
            "completed_at": record.completed_at.isoformat() if record.completed_at else None,
        }
        for record in records
    ]

    return ApiResponse.paginated(items, total, page, page_size)


@router.get("/tasks/{task_id}")
async def get_task_status(
    task_id: str,
    current_user: CurrentUserDep,
    db: DBSession
):
    """获取任务状态"""
    result = await db.execute(
        select(AgentTaskRecord)
        .join(Novel, AgentTaskRecord.novel_id == Novel.id)
        .where(
            AgentTaskRecord.task_id == task_id,
            Novel.author_id == current_user.id
        )
    )
    record = result.scalar_one_or_none()

    if not record:
        raise NotFoundException("任务")

    return ApiResponse.success({
        "task_id": record.task_id,
        "novel_id": record.novel_id,
        "chapter_id": record.chapter_id,
        "task_type": record.task_type,
        "status": record.status,
        "parent_task_id": (record.context or {}).get("_task_meta", {}).get("parent_task_id"),
        "root_task_id": (record.context or {}).get("_task_meta", {}).get("root_task_id"),
        "depth": (record.context or {}).get("_task_meta", {}).get("depth", 0),
        "parameters": record.parameters,
        "context": record.context,
        "result": record.result,
        "error": record.error,
        "agent_id": record.agent_id,
        "created_at": record.created_at.isoformat() if record.created_at else None,
        "updated_at": record.updated_at.isoformat() if record.updated_at else None,
        "completed_at": record.completed_at.isoformat() if record.completed_at else None,
    })
