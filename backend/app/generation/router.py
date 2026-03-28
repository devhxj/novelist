"""
章节生成API路由
"""
import logging
import asyncio
from functools import wraps
from fastapi import APIRouter, Depends, BackgroundTasks
from sqlalchemy.orm import Session

from app.core.database import get_db
from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException
from app.core.dependencies import NovelOwner
from app.core.chapter_generation import ChapterGenerationService
from app.auth.models import User
from app.chapters.models import Chapter
from app.agents.models import AgentTaskRecord
from app.agents.base import TaskType, TaskStatus

router = APIRouter(prefix="/generation", tags=["generation"])
logger = logging.getLogger(__name__)

_generation_locks: dict = {}


def with_retry(max_retries: int = 3, delay: float = 1.0):
    """重试装饰器"""
    def decorator(func):
        @wraps(func)
        async def wrapper(*args, **kwargs):
            last_error = None
            for attempt in range(max_retries):
                try:
                    return await func(*args, **kwargs)
                except Exception as e:
                    last_error = e
                    if attempt < max_retries - 1:
                        logger.warning(f"Attempt {attempt + 1} failed, retrying in {delay}s: {e}")
                        await asyncio.sleep(delay * (attempt + 1))
            raise last_error
        return wrapper
    return decorator


@router.post("/novels/{novel_id}/chapters/{chapter_number}")
async def generate_chapter(
    novel: NovelOwner,
    chapter_number: int,
    background_tasks: BackgroundTasks,
    target_length: int = 3000,
    style: str = "narrative",
    db: Session = Depends(get_db)
):
    """
    生成章节（异步）
    
    - chapter_number: 章节号
    - target_length: 目标字数
    - style: 写作风格
    """
    logger.info(f"Request to generate chapter {chapter_number} for novel {novel.id}")
    
    lock_key = f"{novel.id}_{chapter_number}"
    if lock_key in _generation_locks and _generation_locks[lock_key]:
        return ApiResponse.error(
            code="GEN_001",
            message="章节正在生成中，请稍后再试",
            status_code=409
        )
    
    existing = db.query(Chapter).filter(
        Chapter.novel_id == novel.id,
        Chapter.chapter_number == chapter_number
    ).first()
    
    if existing and existing.status == "generating":
        return ApiResponse.error(
            code="GEN_001",
            message="章节正在生成中",
            status_code=409
        )
    
    task_record = AgentTaskRecord(
        task_id=f"gen_{novel.id}_{chapter_number}",
        novel_id=novel.id,
        task_type=TaskType.GENERATE_CHAPTER.value,
        status=TaskStatus.PENDING.value,
        parameters={
            "chapter_number": chapter_number,
            "target_length": target_length,
            "style": style
        }
    )
    db.add(task_record)
    db.commit()
    
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
    
    _generation_locks[lock_key] = True
    
    background_tasks.add_task(
        _generate_chapter_task,
        novel.id, chapter_number, target_length, style, task_record.task_id
    )
    
    return ApiResponse.success({
        "task_id": task_record.task_id,
        "chapter_number": chapter_number,
        "status": "generating",
        "message": "章节生成任务已提交"
    })


async def _generate_chapter_task(
    novel_id: int,
    chapter_number: int,
    target_length: int,
    style: str,
    task_id: str
):
    """后台任务：生成章节"""
    from app.core.database import SessionLocal
    
    db = SessionLocal()
    lock_key = f"{novel_id}_{chapter_number}"
    
    try:
        task_record = db.query(AgentTaskRecord).filter(
            AgentTaskRecord.task_id == task_id
        ).first()
        
        if task_record:
            task_record.status = TaskStatus.IN_PROGRESS.value
            db.commit()
        
        service = ChapterGenerationService(db, novel_id)
        result = await _generate_with_retry(
            service, chapter_number, target_length, style, max_retries=3
        )
        
        if task_record:
            task_record.status = TaskStatus.COMPLETED.value if result["success"] else TaskStatus.FAILED.value
            task_record.result = result
            if not result["success"]:
                task_record.error = result.get("error", "Unknown error")
            db.commit()
        
        logger.info(f"Chapter generation task {task_id} completed: {result['success']}")
        
    except Exception as e:
        logger.error(f"Chapter generation task {task_id} failed: {e}")
        try:
            task_record = db.query(AgentTaskRecord).filter(
                AgentTaskRecord.task_id == task_id
            ).first()
            if task_record:
                task_record.status = TaskStatus.FAILED.value
                task_record.error = str(e)
                db.commit()
        except Exception as db_error:
            logger.error(f"Failed to update task status: {db_error}")
    finally:
        _generation_locks[lock_key] = False
        db.close()


async def _generate_with_retry(
    service: ChapterGenerationService,
    chapter_number: int,
    target_length: int,
    style: str,
    max_retries: int = 3
) -> dict:
    """带重试的章节生成"""
    last_error = None
    for attempt in range(max_retries):
        try:
            result = await service.generate_chapter(
                chapter_number=chapter_number,
                target_length=target_length,
                style=style
            )
            if result["success"]:
                return result
            last_error = result.get("error", "Unknown error")
            logger.warning(f"Generation attempt {attempt + 1} failed: {last_error}")
        except Exception as e:
            last_error = str(e)
            logger.warning(f"Generation attempt {attempt + 1} raised error: {e}")
        
        if attempt < max_retries - 1:
            await asyncio.sleep(2.0 * (attempt + 1))
    
    return {"success": False, "error": f"Failed after {max_retries} retries: {last_error}"}


@router.post("/novels/{novel_id}/chapters/{chapter_id}/regenerate")
async def regenerate_chapter(
    novel: NovelOwner,
    chapter_id: int,
    feedback: str = None,
    background_tasks: BackgroundTasks = BackgroundTasks(),
    db: Session = Depends(get_db)
):
    """
    重新生成章节
    
    - chapter_id: 章节ID
    - feedback: 反馈意见
    """
    chapter = db.query(Chapter).filter(
        Chapter.id == chapter_id,
        Chapter.novel_id == novel.id
    ).first()
    
    if not chapter:
        raise NotFoundException("章节")
    
    lock_key = f"{novel.id}_{chapter.chapter_number}"
    if lock_key in _generation_locks and _generation_locks[lock_key]:
        return ApiResponse.error(
            code="GEN_002",
            message="章节正在生成中，无法重新生成",
            status_code=409
        )
    
    task_record = AgentTaskRecord(
        task_id=f"regen_{novel.id}_{chapter.chapter_number}",
        novel_id=novel.id,
        chapter_id=chapter_id,
        task_type=TaskType.GENERATE_CHAPTER.value,
        status=TaskStatus.PENDING.value,
        parameters={
            "chapter_number": chapter.chapter_number,
            "feedback": feedback
        }
    )
    db.add(task_record)
    
    chapter.status = "generating"
    db.commit()
    
    _generation_locks[lock_key] = True
    
    background_tasks.add_task(
        _regenerate_chapter_task,
        novel.id, chapter_id, chapter.chapter_number, feedback, task_record.task_id
    )
    
    return ApiResponse.success({
        "task_id": task_record.task_id,
        "chapter_id": chapter_id,
        "status": "regenerating",
        "message": "章节重新生成任务已提交"
    })


async def _regenerate_chapter_task(
    novel_id: int,
    chapter_id: int,
    chapter_number: int,
    feedback: str,
    task_id: str
):
    """后台任务：重新生成章节"""
    from app.core.database import SessionLocal
    
    db = SessionLocal()
    lock_key = f"{novel_id}_{chapter_number}"
    
    try:
        task_record = db.query(AgentTaskRecord).filter(
            AgentTaskRecord.task_id == task_id
        ).first()
        
        if task_record:
            task_record.status = TaskStatus.IN_PROGRESS.value
            db.commit()
        
        service = ChapterGenerationService(db, novel_id)
        result = await _regenerate_with_retry(
            service, chapter_id, feedback, max_retries=3
        )
        
        if task_record:
            task_record.status = TaskStatus.COMPLETED.value if result["success"] else TaskStatus.FAILED.value
            task_record.result = result
            if not result["success"]:
                task_record.error = result.get("error", "Unknown error")
            db.commit()
        
    except Exception as e:
        logger.error(f"Chapter regeneration task {task_id} failed: {e}")
        try:
            task_record = db.query(AgentTaskRecord).filter(
                AgentTaskRecord.task_id == task_id
            ).first()
            if task_record:
                task_record.status = TaskStatus.FAILED.value
                task_record.error = str(e)
                db.commit()
        except Exception as db_error:
            logger.error(f"Failed to update task status: {db_error}")
    finally:
        _generation_locks[lock_key] = False
        db.close()


async def _regenerate_with_retry(
    service: ChapterGenerationService,
    chapter_id: int,
    feedback: str,
    max_retries: int = 3
) -> dict:
    """带重试的章节重新生成"""
    last_error = None
    for attempt in range(max_retries):
        try:
            result = await service.regenerate_chapter(
                chapter_id=chapter_id,
                feedback=feedback
            )
            if result["success"]:
                return result
            last_error = result.get("error", "Unknown error")
            logger.warning(f"Regeneration attempt {attempt + 1} failed: {last_error}")
        except Exception as e:
            last_error = str(e)
            logger.warning(f"Regeneration attempt {attempt + 1} raised error: {e}")
        
        if attempt < max_retries - 1:
            await asyncio.sleep(2.0 * (attempt + 1))
    
    return {"success": False, "error": f"Failed after {max_retries} retries: {last_error}"}


@router.get("/novels/{novel_id}/tasks")
def get_generation_tasks(
    novel: NovelOwner,
    status: str = None,
    page: int = 1,
    page_size: int = 20,
    db: Session = Depends(get_db)
):
    """
    获取生成任务列表
    
    - status: 任务状态筛选
    - page: 页码
    - page_size: 每页数量
    """
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
            "chapter_id": task.chapter_id,
            "status": task.status,
            "created_at": task.created_at.isoformat(),
            "completed_at": task.completed_at.isoformat() if task.completed_at else None,
            "error": task.error
        }
        for task in tasks
    ]
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.get("/tasks/{task_id}")
def get_task_status(
    task_id: str,
    db: Session = Depends(get_db)
):
    """
    获取任务状态
    """
    task = db.query(AgentTaskRecord).filter(AgentTaskRecord.task_id == task_id).first()
    
    if not task:
        raise NotFoundException("任务")
    
    return ApiResponse.success({
        "task_id": task.task_id,
        "task_type": task.task_type,
        "novel_id": task.novel_id,
        "chapter_id": task.chapter_id,
        "status": task.status,
        "parameters": task.parameters,
        "result": task.result,
        "error": task.error,
        "created_at": task.created_at.isoformat(),
        "updated_at": task.updated_at.isoformat() if task.updated_at else None,
        "completed_at": task.completed_at.isoformat() if task.completed_at else None
    })
