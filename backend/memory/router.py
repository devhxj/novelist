"""
记忆管理模块 - API路由
"""
import time
import logging
from typing import List, Optional
from fastapi import APIRouter, Query
from sqlalchemy import select

from core.response import ApiResponse
from core.database import DBSession
from core.auth import CurrentUserDep
from core.dependencies import NovelOwner
from core.exceptions import NotFoundException
from core.vector_store import vector_store, VectorStoreError
from novels.models import Novel
from chapters.models import Chapter
from .models import MemoryChunk
from .schemas import MemorySearchRequest, MemoryIndexRequest

router = APIRouter(prefix="/memory", tags=["memory"])
logger = logging.getLogger(__name__)
@router.post("/novels/{novel_id}/index")
async def index_novel_memory(
    novel: NovelOwner,
    db: DBSession
):
    """索引小说所有章节到向量存储"""
    logger.info(f"Indexing novel {novel.id}")
    
    try:
        result = await db.execute(
            select(Chapter).where(Chapter.novel_id == novel.id)
        )
        chapters = result.scalars().all()
        
        if not chapters:
            logger.info(f"Novel {novel.id} has no chapters to index")
            return ApiResponse.success(
                {"chapters_indexed": 0, "total_chunks": 0},
                message="没有章节需要索引"
            )
        
        total_chunks = 0
        for chapter in chapters:
            if not chapter.content:
                continue
            
            chunk_data = vector_store.build_chapter_chunks(
                chapter_id=chapter.id,
                chapter_number=chapter.chapter_number,
                chapter_title=chapter.title,
                content=chapter.content,
                summary=chapter.summary,
            )
            
            if chunk_data:
                vector_store.add_chunks(novel.id, chunk_data)
                total_chunks += len(chunk_data)
        
        logger.info(f"Novel {novel.id} indexed: {len(chapters)} chapters, {total_chunks} chunks")
        return ApiResponse.success(
            {
                "novel_id": novel.id,
                "chapters_indexed": len(chapters),
                "total_chunks": total_chunks
            },
            message="索引完成"
        )
        
    except VectorStoreError as e:
        logger.error(f"VectorStore error while indexing novel {novel.id}: {e}")
        return ApiResponse.error(
            code="MEMORY_001",
            message=f"索引失败: {str(e)}",
            status_code=500
        )


@router.post("/novels/{novel_id}/chapters/{chapter_id}/index")
async def index_chapter_memory(
    novel: NovelOwner,
    chapter_id: int,
    request: MemoryIndexRequest,
    db: DBSession
):
    """索引单个章节到向量存储"""
    logger.info(f"Indexing chapter {chapter_id} of novel {novel.id}")
    
    try:
        result = await db.execute(
            select(Chapter).where(
                Chapter.id == chapter_id,
                Chapter.novel_id == novel.id
            )
        )
        chapter = result.scalar_one_or_none()
        
        if chapter is None:
            raise NotFoundException("章节")
        
        if not chapter.content:
            logger.info(f"Chapter {chapter_id} has no content")
            return ApiResponse.success(
                {"chapter_id": chapter_id, "chunks_created": 0},
                message="章节内容为空"
            )
        
        vector_store.delete_chapter_chunks(novel.id, chapter_id)
        
        chunk_data = vector_store.build_chapter_chunks(
            chapter_id=chapter.id,
            chapter_number=chapter.chapter_number,
            chapter_title=chapter.title,
            content=chapter.content,
            summary=chapter.summary,
            chunk_size=request.chunk_size,
            overlap=request.overlap
        )
        
        if chunk_data:
            vector_store.add_chunks(novel.id, chunk_data)
        
        logger.info(f"Chapter {chapter_id} indexed: {len(chunk_data)} chunks")
        return ApiResponse.success(
            {
                "chapter_id": chapter_id,
                "chunks_created": len(chunk_data),
                "status": "completed"
            },
            message="章节索引完成"
        )
        
    except VectorStoreError as e:
        logger.error(f"VectorStore error while indexing chapter {chapter_id}: {e}")
        return ApiResponse.error(
            code="MEMORY_001",
            message=f"索引失败: {str(e)}",
            status_code=500
        )


@router.post("/novels/{novel_id}/search")
async def search_memory(
    novel: NovelOwner,
    request: MemorySearchRequest,
    db: DBSession
):
    """语义检索小说内容"""
    logger.info(f"Searching novel {novel.id}: '{request.query[:50]}...'")
    
    try:
        start_time = time.time()
        
        results = await vector_store.search(
            novel_id=novel.id,
            query=request.query,
            top_k=request.top_k,
            filters=request.filters
        )
        
        search_time = time.time() - start_time
        
        formatted_results = []
        for result in results:
            formatted_results.append({
                "id": result["id"],
                "type": result["metadata"].get("chunk_type", "content"),
                "content": result["content"],
                "chapter_id": result["metadata"].get("chapter_id"),
                "relevance_score": 1 - result["distance"],
                "metadata": result["metadata"]
            })
        
        logger.info(f"Search completed for novel {novel.id}: {len(formatted_results)} results in {search_time:.3f}s")
        return ApiResponse.success({
            "results": formatted_results,
            "total": len(formatted_results),
            "search_time": round(search_time, 3)
        })
        
    except VectorStoreError as e:
        logger.error(f"VectorStore error while searching novel {novel.id}: {e}")
        return ApiResponse.error(
            code="MEMORY_002",
            message=f"检索失败: {str(e)}",
            status_code=500
        )


@router.delete("/novels/{novel_id}/memory")
async def clear_novel_memory(
    novel: NovelOwner
):
    """清除小说的所有向量索引"""
    logger.info(f"Clearing memory for novel {novel.id}")
    
    try:
        success = vector_store.delete_collection(novel.id)
        
        if success:
            logger.info(f"Memory cleared for novel {novel.id}")
            return ApiResponse.success(message="记忆索引已清除")
        else:
            logger.info(f"No memory found for novel {novel.id}")
            return ApiResponse.success(message="没有找到需要清除的索引")
            
    except VectorStoreError as e:
        logger.error(f"VectorStore error while clearing memory for novel {novel.id}: {e}")
        return ApiResponse.error(
            code="MEMORY_003",
            message=f"清除失败: {str(e)}",
            status_code=500
        )
