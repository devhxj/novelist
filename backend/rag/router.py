"""
RAG检索模块 - API路由
"""
import logging
from fastapi import APIRouter, Query
from sqlalchemy import select, func

from core.response import ApiResponse
from core.database import DBSession
from core.auth import CurrentUserDep
from core.dependencies import NovelOwner
from core.exceptions import NotFoundException, UnauthorizedException
from core.context_builder import ContextBuilder
from novels.models import Novel
from .models import RAGContext
from .schemas import (
    RAGQueryRequest,
    RAGContextChunk,
    WritingContextRequest
)

router = APIRouter(prefix="/rag", tags=["rag"])
logger = logging.getLogger(__name__)


@router.post("/novels/{novel_id}/search")
async def search_context(
    novel: NovelOwner,
    request: RAGQueryRequest,
    db: DBSession
):
    """
    RAG语义检索
    
    - query: 检索查询
    - context_type: 上下文类型 (writing/character/plot)
    - top_k: 返回结果数量
    - include_chapters: 限定章节范围
    """
    logger.info(f"Searching novel {novel.id}")
    
    try:
        builder = ContextBuilder(db, novel.id)
        
        filters = {}
        if request.include_chapters:
            filters["chapter_ids"] = request.include_chapters
        
        results = await builder.search_relevant_context(
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
            novel_id=novel.id,
            context_type=request.context_type,
            query=request.query,
            context_content=context_content,
            source_chunks=[c.dict() for c in chunks]
        )
        db.add(rag_context)
        await db.commit()
        await db.refresh(rag_context)
        
        logger.info(f"RAG search completed: {len(chunks)} chunks found")
        
        return ApiResponse.success({
            "context_id": rag_context.id,
            "novel_id": novel.id,
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
async def get_writing_context(
    novel: NovelOwner,
    request: WritingContextRequest,
    db: DBSession
):
    """
    获取写作上下文
    
    - chapter_id: 章节ID
    - context_size: 上下文大小限制
    - include_previous_chapters: 包含前文摘要
    - include_characters: 包含角色信息
    """
    logger.info(f"Getting writing context for chapter {request.chapter_id}")

    try:
        builder = ContextBuilder(db, novel.id)

        context_data = await builder.build_writing_context(
            chapter_id=request.chapter_id,
            context_size=request.context_size,
            include_previous_chapters=request.include_previous_chapters,
            include_characters=request.include_characters,
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
async def get_context_history(
    novel: NovelOwner,
    db: DBSession,
    page: int = Query(1, ge=1),
    page_size: int = Query(20, ge=1, le=100),
    context_type: str = None
):
    """
    获取上下文历史
    
    - page: 页码
    - page_size: 每页数量 (1-100)
    - context_type: 上下文类型筛选
    """
    query = select(RAGContext).where(RAGContext.novel_id == novel.id)
    
    if context_type:
        query = query.where(RAGContext.context_type == context_type)
    
    count_query = select(func.count()).select_from(query.subquery())
    total_result = await db.execute(count_query)
    total = total_result.scalar()
    
    query = query.order_by(RAGContext.created_at.desc()).offset((page - 1) * page_size).limit(page_size)
    result = await db.execute(query)
    contexts = result.scalars().all()
    
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
async def get_context_detail(
    context_id: int,
    db: DBSession,
    current_user: CurrentUserDep
):
    """获取上下文详情"""
    result = await db.execute(
        select(RAGContext).where(RAGContext.id == context_id)
    )
    context = result.scalar_one_or_none()
    
    if not context:
        raise NotFoundException("上下文")
    
    result = await db.execute(
        select(Novel).where(Novel.id == context.novel_id)
    )
    novel = result.scalar_one_or_none()
    
    if not novel or novel.author_id != current_user.id:
        raise UnauthorizedException("无权访问此上下文")
    
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
