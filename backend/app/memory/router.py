"""
记忆管理模块 - API路由
"""
import time
import logging
from typing import List, Optional, Dict, Any
from fastapi import APIRouter, Depends
from sqlalchemy.orm import Session

from app.core.database import get_db
from app.core.response import ApiResponse
from app.core.exceptions import NotFoundException
from app.core.auth import get_current_user
from app.core.dependencies import NovelOwner
from app.core.vector_store import vector_store, VectorStoreError
from app.auth.models import User
from app.novels.models import Novel
from app.chapters.models import Chapter
from .models import MemoryChunk
from .schemas import MemorySearchRequest, MemoryIndexRequest

router = APIRouter(prefix="/memory", tags=["memory"])
logger = logging.getLogger(__name__)


def split_text_into_chunks(text: str, chunk_size: int = 500, overlap: int = 50) -> List[str]:
    """将文本分割成重叠的块"""
    if not text:
        return []
    
    chunks = []
    start = 0
    while start < len(text):
        end = start + chunk_size
        chunk = text[start:end]
        if chunk.strip():
            chunks.append(chunk.strip())
        start = end - overlap
    
    return chunks


@router.post("/novels/{novel_id}/index")
def index_novel_memory(
    novel: NovelOwner,
    db: Session = Depends(get_db)
):
    """索引小说所有章节到向量存储"""
    logger.info(f"Indexing novel {novel.id}")
    
    try:
        chapters = db.query(Chapter).filter(Chapter.novel_id == novel.id).all()
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
            
            chunks = split_text_into_chunks(chapter.content)
            
            chunk_data = []
            for i, chunk_content in enumerate(chunks):
                chunk_data.append({
                    "id": f"{chapter.id}_{i}",
                    "content": chunk_content,
                    "chapter_id": chapter.id,
                    "chunk_type": "content",
                    "chunk_index": i,
                    "metadata": {
                        "chapter_number": chapter.chapter_number,
                        "chapter_title": chapter.title
                    }
                })
            
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
def index_chapter_memory(
    novel: NovelOwner,
    chapter_id: int,
    request: MemoryIndexRequest,
    db: Session = Depends(get_db)
):
    """索引单个章节到向量存储"""
    logger.info(f"Indexing chapter {chapter_id} of novel {novel.id}")
    
    try:
        chapter = db.query(Chapter).filter(
            Chapter.id == chapter_id,
            Chapter.novel_id == novel.id
        ).first()
        if chapter is None:
            raise NotFoundException("章节")
        
        if not chapter.content:
            logger.info(f"Chapter {chapter_id} has no content")
            return ApiResponse.success(
                {"chapter_id": chapter_id, "chunks_created": 0},
                message="章节内容为空"
            )
        
        vector_store.delete_chapter_chunks(novel.id, chapter_id)
        
        chunks = split_text_into_chunks(
            chapter.content, 
            chunk_size=request.chunk_size,
            overlap=request.overlap
        )
        
        chunk_data = []
        for i, chunk_content in enumerate(chunks):
            chunk_data.append({
                "id": f"{chapter.id}_{i}",
                "content": chunk_content,
                "chapter_id": chapter.id,
                "chunk_type": "content",
                "chunk_index": i,
                "metadata": {
                    "chapter_number": chapter.chapter_number,
                    "chapter_title": chapter.title
                }
            })
        
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
def search_memory(
    novel: NovelOwner,
    request: MemorySearchRequest,
    db: Session = Depends(get_db)
):
    """语义检索小说内容"""
    logger.info(f"Searching novel {novel.id}: '{request.query[:50]}...'")
    
    try:
        start_time = time.time()
        
        results = vector_store.search(
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
def clear_novel_memory(
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
