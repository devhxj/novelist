"""
RAG检索模块 - API路由
"""
import logging
from fastapi import APIRouter, Depends, Query
from sqlalchemy.orm import Session

from app.core.database import get_db
from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException, UnauthorizedException
from app.core.auth import get_current_user
from app.core.context_builder import ContextBuilder
from app.auth.models import User
from app.novels.models import Novel
from .models import RAGContext
from .schemas import (
    RAGQueryRequest,
    RAGContextResponse,
    RAGContextChunk,
    WritingContextRequest,
    WritingContextResponse
)

router = APIRouter(prefix="/rag", tags=["rag"])
logger = logging.getLogger(__name__)


def check_novel_ownership(db: Session, novel_id: int, user_id: int) -> Novel:
    """检查小说所有权"""
    novel = db.query(Novel).filter(Novel.id == novel_id).first()
    if novel is None:
        raise NotFoundException("小说")
    if novel.author_id != user_id:
        raise UnauthorizedException("无权访问此小说")
    return novel


@router.post("/novels/{novel_id}/search")
def search_context(
    novel_id: int,
    request: RAGQueryRequest,
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    RAG语义检索
    
    - query: 检索查询
    - context_type: 上下文类型 (writing/character/plot)
    - top_k: 返回结果数量
    - include_chapters: 限定章节范围
    """
    logger.info(f"User {current_user.id} searching novel {novel_id}")
    check_novel_ownership(db, novel_id, current_user.id)
    
    try:
        builder = ContextBuilder(db, novel_id)
        
        filters = {}
        if request.include_chapters:
            filters["chapter_ids"] = request.include_chapters
        
        results = builder.search_relevant_context(
            query=request.query,
            top_k=request.top_k,
            filters=filters if filters else None
        )
        
        chunks = [
            RAGContextChunk(
                chunk_id=r["chunk_id"],
                content=r["content"],
                source_type=r["source_type"],
                source_id=r["source_id"],
                relevance_score=r["relevance_score"],
                metadata=r["metadata"]
            )
            for r in results
        ]
        
        context_content = "\n\n---\n\n".join([c.content for c in chunks])
        
        rag_context = RAGContext(
            novel_id=novel_id,
            context_type=request.context_type,
            query=request.query,
            context_content=context_content,
            source_chunks=[c.dict() for c in chunks]
        )
        db.add(rag_context)
        db.commit()
        db.refresh(rag_context)
        
        logger.info(f"RAG search completed: {len(chunks)} chunks found")
        
        return ApiResponse.success({
            "context_id": rag_context.id,
            "novel_id": novel_id,
            "context_type": request.context_type,
            "query": request.query,
            "context_content": context_content,
            "chunks": [c.dict() for c in chunks],
            "total_chunks": len(chunks),
            "created_at": rag_context.created_at.isoformat()
        })
        
    except Exception as e:
        logger.error(f"RAG search failed: {e}")
        return ApiResponse.error(
            code="RAG_001",
            message=f"检索失败: {str(e)}",
            status_code=500
        )


@router.post("/novels/{novel_id}/writing-context")
def get_writing_context(
    novel_id: int,
    request: WritingContextRequest,
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    获取写作上下文
    
    - chapter_id: 章节ID
    - context_size: 上下文大小限制
    - include_previous_chapters: 包含前文摘要
    - include_characters: 包含角色信息
    - include_plot_events: 包含情节线索
    """
    logger.info(f"User {current_user.id} getting writing context for chapter {request.chapter_id}")
    check_novel_ownership(db, novel_id, current_user.id)
    
    try:
        builder = ContextBuilder(db, novel_id)
        
        context_data = builder.build_writing_context(
            chapter_id=request.chapter_id,
            context_size=request.context_size,
            include_previous_chapters=request.include_previous_chapters,
            include_characters=request.include_characters,
            include_plot_events=request.include_plot_events
        )
        
        logger.info(f"Writing context built: {context_data['context_length']} chars")
        
        return ApiResponse.success(context_data)
        
    except ValueError as e:
        logger.error(f"Writing context error: {e}")
        return ApiResponse.error(
            code="RAG_002",
            message=str(e),
            status_code=404
        )
    except Exception as e:
        logger.error(f"Writing context failed: {e}")
        return ApiResponse.error(
            code="RAG_003",
            message=f"构建上下文失败: {str(e)}",
            status_code=500
        )


@router.get("/novels/{novel_id}/contexts")
def get_context_history(
    novel_id: int,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    context_type: str = None,
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """
    获取上下文历史
    
    - page: 页码
    - page_size: 每页数量 (1-100)
    - context_type: 上下文类型筛选
    """
    check_novel_ownership(db, novel_id, current_user.id)
    
    query = db.query(RAGContext).filter(RAGContext.novel_id == novel_id)
    
    if context_type:
        query = query.filter(RAGContext.context_type == context_type)
    
    total = query.count()
    contexts = query.order_by(RAGContext.created_at.desc()).offset((page - 1) * page_size).limit(page_size).all()
    
    items = [
        {
            "id": ctx.id,
            "context_type": ctx.context_type,
            "query": ctx.query,
            "context_length": len(ctx.context_content) if ctx.context_content else 0,
            "created_at": ctx.created_at.isoformat()
        }
        for ctx in contexts
    ]
    
    return ApiResponse.paginated(items, total, page, page_size)


@router.get("/contexts/{context_id}")
def get_context_detail(
    context_id: int,
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user)
):
    """获取上下文详情"""
    context = db.query(RAGContext).filter(RAGContext.id == context_id).first()
    
    if not context:
        raise NotFoundException("上下文")
    
    check_novel_ownership(db, context.novel_id, current_user.id)
    
    return ApiResponse.success({
        "id": context.id,
        "novel_id": context.novel_id,
        "chapter_id": context.chapter_id,
        "context_type": context.context_type,
        "query": context.query,
        "context_content": context.context_content,
        "source_chunks": context.source_chunks,
        "relevance_score": context.relevance_score,
        "created_at": context.created_at.isoformat()
    })
