"""
工作流API路由
"""
import logging
from fastapi import APIRouter, Depends, BackgroundTasks
from sqlalchemy.orm import Session

from app.core.database import get_db
from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException
from app.core.dependencies import NovelOwner
from app.workflows.langgraph_workflow import workflow, LANGGRAPH_AVAILABLE

router = APIRouter(prefix="/workflows", tags=["workflows"])
logger = logging.getLogger(__name__)

_workflow_locks: dict = {}


@router.post("/novels/{novel_id}/chapters/{chapter_number}/generate")
async def generate_chapter_with_workflow(
    novel: NovelOwner,
    chapter_number: int,
    background_tasks: BackgroundTasks,
    target_length: int = 3000,
    style: str = "narrative",
    db: Session = Depends(get_db)
):
    """
    使用LangGraph工作流生成章节
    
    工作流步骤:
    1. 准备上下文 - 收集前文摘要、角色信息、情节线索
    2. 生成内容 - WriterAgent创作章节
    3. 审核内容 - ReviewerAgent审核质量
    4. 一致性检查 - 检查角色、情节、时间线一致性
    5. 保存章节 - 持久化到数据库
    6. 更新记忆 - 向量化索引
    
    如果审核或一致性检查不通过，会自动重试（最多3次）
    """
    if not LANGGRAPH_AVAILABLE:
        return ApiResponse.error(
            code="WORKFLOW_001",
            message="LangGraph未安装，请先安装: pip install langgraph",
            status_code=500
        )
    
    lock_key = f"{novel.id}_{chapter_number}"
    if lock_key in _workflow_locks and _workflow_locks[lock_key]:
        return ApiResponse.error(
            code="WORKFLOW_003",
            message="章节正在生成中，请稍后再试",
            status_code=409
        )
    
    import uuid
    task_id = f"workflow_{novel.id}_{chapter_number}_{uuid.uuid4().hex[:8]}"
    
    from app.chapters.models import Chapter
    existing = db.query(Chapter).filter(
        Chapter.novel_id == novel.id,
        Chapter.chapter_number == chapter_number
    ).first()
    
    if existing and existing.status == "generating":
        return ApiResponse.error(
            code="WORKFLOW_002",
            message="章节正在生成中",
            status_code=409
        )
    
    if not existing:
        chapter = Chapter(
            novel_id=novel.id,
            chapter_number=chapter_number,
            title=f"第{chapter_number}章",
            content="",
            status="generating"
        )
        db.add(chapter)
        db.commit()
    else:
        existing.status = "generating"
        db.commit()
    
    _workflow_locks[lock_key] = True
    
    background_tasks.add_task(
        _run_workflow_task,
        task_id, novel.id, chapter_number, target_length, style, lock_key
    )
    
    return ApiResponse.success({
        "task_id": task_id,
        "chapter_number": chapter_number,
        "status": "generating",
        "workflow_type": "langgraph",
        "message": "工作流任务已提交"
    })


async def _run_workflow_task(
    task_id: str,
    novel_id: int,
    chapter_number: int,
    target_length: int,
    style: str,
    lock_key: str
):
    """后台执行工作流任务"""
    from app.core.database import SessionLocal
    from app.chapters.models import Chapter
    
    db = SessionLocal()
    try:
        result = await workflow.run(
            task_id=task_id,
            novel_id=novel_id,
            chapter_number=chapter_number,
            target_length=target_length,
            style=style
        )
        
        if not result.get("success"):
            chapter = db.query(Chapter).filter(
                Chapter.novel_id == novel_id,
                Chapter.chapter_number == chapter_number
            ).first()
            
            if chapter:
                chapter.status = "failed"
                db.commit()
        
        logger.info(f"Workflow task {task_id} completed: {result.get('status')}")
        
    except Exception as e:
        logger.error(f"Workflow task {task_id} failed: {e}")
        
        chapter = db.query(Chapter).filter(
            Chapter.novel_id == novel_id,
            Chapter.chapter_number == chapter_number
        ).first()
        
        if chapter:
            chapter.status = "failed"
            db.commit()
            
    finally:
        _workflow_locks[lock_key] = False
        db.close()


@router.get("/tasks/{task_id}/status")
def get_workflow_status(task_id: str):
    """
    获取工作流任务状态
    
    返回工作流当前执行状态和各节点结果
    """
    if not LANGGRAPH_AVAILABLE:
        return ApiResponse.error(
            code="WORKFLOW_001",
            message="LangGraph未安装",
            status_code=500
        )
    
    state = workflow.get_state(task_id)
    
    if not state:
        raise NotFoundException("工作流任务")
    
    return ApiResponse.success({
        "task_id": task_id,
        "status": state.get("status"),
        "iteration": state.get("iteration"),
        "max_iterations": state.get("max_iterations"),
        "generated_content_length": len(state.get("generated_content", "")),
        "review_result": state.get("review_result"),
        "consistency_result": state.get("consistency_result"),
        "error": state.get("error"),
        "created_at": state.get("created_at"),
        "updated_at": state.get("updated_at")
    })


@router.get("/novels/{novel_id}/workflows")
def list_novel_workflows(
    novel: NovelOwner,
    status: str = None,
    page: int = 1,
    page_size: int = 20,
    db: Session = Depends(get_db)
):
    """
    获取小说的工作流任务列表
    
    - status: 状态筛选 (initialized/generating/completed/failed)
    """
    from app.agents.models import AgentTaskRecord
    
    query = db.query(AgentTaskRecord).filter(
        AgentTaskRecord.novel_id == novel.id
    )
    
    if status:
        query = query.filter(AgentTaskRecord.status == status)
    
    total = query.count()
    tasks = query.order_by(AgentTaskRecord.created_at.desc()).offset((page - 1) * page_size).limit(page_size).all()
    
    items = [
        {
            "task_id": task.task_id,
            "task_type": task.task_type,
            "status": task.status,
            "created_at": task.created_at.isoformat(),
            "updated_at": task.updated_at.isoformat() if task.updated_at else None,
            "error": task.error
        }
        for task in tasks
    ]
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.get("/health")
def check_workflow_health():
    """
    检查工作流系统健康状态
    """
    from app.workflows.langgraph_workflow import (
        LANGGRAPH_AVAILABLE,
        CONTEXT_BUILDER_AVAILABLE,
        CONSISTENCY_CHECKER_AVAILABLE,
        VECTOR_STORE_AVAILABLE
    )
    
    return ApiResponse.success({
        "langgraph_available": LANGGRAPH_AVAILABLE,
        "workflow_ready": workflow is not None,
        "components": {
            "context_builder": "ready" if CONTEXT_BUILDER_AVAILABLE else "unavailable",
            "consistency_checker": "ready" if CONSISTENCY_CHECKER_AVAILABLE else "unavailable",
            "vector_store": "ready" if VECTOR_STORE_AVAILABLE else "unavailable",
            "writer_agent": "ready",
            "reviewer_agent": "ready",
            "memory_saver": "ready" if LANGGRAPH_AVAILABLE else "unavailable"
        }
    })
