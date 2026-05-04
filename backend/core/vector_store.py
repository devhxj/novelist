"""
向量存储服务 - ChromaDB集成
"""
# pyright: reportArgumentType=false, reportOptionalMemberAccess=false, reportOptionalSubscript=false
import os
import logging
import asyncio
import gc
from typing import List, Dict, Any, Optional

from core.exceptions import SystemError

os.environ.setdefault("HF_HUB_OFFLINE", "0")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "0")
os.environ.setdefault("HF_ENDPOINT", os.getenv("HF_ENDPOINT", "https://hf-mirror.com"))
os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")

import chromadb
from chromadb.utils import embedding_functions

logger = logging.getLogger(__name__)


class VectorStoreConfig:
    """向量存储配置"""
    CHROMA_PERSIST_DIR: str = os.getenv("CHROMA_PERSIST_DIR", "./data/chroma")
    EMBEDDING_MODEL: str = os.getenv("EMBEDDING_MODEL", "shibing624/text2vec-base-chinese")
    OPENAI_API_KEY: str | None = os.getenv("OPENAI_API_KEY")
    USE_OPENAI_EMBEDDING: bool = os.getenv("USE_OPENAI_EMBEDDING", "false").lower() == "true"
    
    @classmethod
    def validate(cls):
        """验证配置"""
        if cls.USE_OPENAI_EMBEDDING and not cls.OPENAI_API_KEY:
            raise ValueError("USE_OPENAI_EMBEDDING is true but OPENAI_API_KEY is not set")


class VectorStoreError(SystemError):
    def __init__(self, message: str = "向量存储操作失败"):
        super().__init__(message, code="VECTOR_STORE_ERROR", status_code=500)


class VectorStore:
    """向量存储服务"""
    
    _instance = None
    
    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
            cls._instance._initialized = False
            cls._instance._embedding_function = None
        return cls._instance
    
    def __init__(self):
        if self._initialized:
            return
        
        try:
            self.persist_directory = VectorStoreConfig.CHROMA_PERSIST_DIR
            os.makedirs(self.persist_directory, exist_ok=True)
            logger.info(f"ChromaDB persist directory: {self.persist_directory}")
            
            self.client = chromadb.PersistentClient(path=self.persist_directory)
            
            # 启动时立即加载 embedding 模型，不懒加载
            self._load_embedding_model()
            
            self._initialized = True
            logger.info("VectorStore initialized (embedding model loaded at startup)")
            
        except Exception as e:
            logger.error(f"Failed to initialize VectorStore: {e}")
            raise VectorStoreError(f"VectorStore initialization failed: {e}")
    
    @property
    def embedding_function(self):
        """获取 embedding 模型（已在启动时加载）"""
        return self._embedding_function

    def _get_embedding_signature(self) -> str:
        if VectorStoreConfig.USE_OPENAI_EMBEDDING:
            return f"openai:{VectorStoreConfig.EMBEDDING_MODEL}"
        return f"sentence-transformer:{VectorStoreConfig.EMBEDDING_MODEL}"

    def _create_collection(self, novel_id: int):
        collection_name = f"novel_{novel_id}"
        return self.client.get_or_create_collection(
            name=collection_name,
            embedding_function=self.embedding_function,
            metadata={
                "novel_id": novel_id,
                "embedding_model": VectorStoreConfig.EMBEDDING_MODEL,
                "embedding_signature": self._get_embedding_signature(),
            }
        )

    def _recreate_collection(self, novel_id: int):
        collection_name = f"novel_{novel_id}"
        logger.warning(
            "Recreating Chroma collection %s because the embedding configuration changed or became incompatible",
            collection_name,
        )
        try:
            self.client.delete_collection(collection_name)
        except Exception as exc:
            logger.warning("Failed to delete stale collection %s: %s", collection_name, exc)
        return self._create_collection(novel_id)
    
    def _load_embedding_model(self):
        """加载embedding模型"""
        try:
            if VectorStoreConfig.USE_OPENAI_EMBEDDING:
                logger.info(f"Using OpenAI embedding model: {VectorStoreConfig.EMBEDDING_MODEL}")
                self._embedding_function = embedding_functions.OpenAIEmbeddingFunction(
                    api_key=VectorStoreConfig.OPENAI_API_KEY,
                    model_name=VectorStoreConfig.EMBEDDING_MODEL
                )
            else:
                logger.info(f"Loading SentenceTransformer model: {VectorStoreConfig.EMBEDDING_MODEL}")
                self._embedding_function = embedding_functions.SentenceTransformerEmbeddingFunction(
                    model_name=VectorStoreConfig.EMBEDDING_MODEL
                )
            logger.info("Embedding model loaded successfully")
        except Exception as e:
            logger.error(f"Failed to load embedding model: {e}")
            raise VectorStoreError(f"Embedding model load failed: {e}")
    
    def get_or_create_collection(self, novel_id: int):
        """获取或创建小说的向量集合"""
        try:
            collection_name = f"novel_{novel_id}"
            collection = self._create_collection(novel_id)
            metadata = collection.metadata or {}
            stored_signature = metadata.get("embedding_signature")
            current_signature = self._get_embedding_signature()
            if stored_signature and stored_signature != current_signature:
                collection = self._recreate_collection(novel_id)
            logger.debug(f"Got/created collection: {collection_name}")
            return collection
        except Exception as e:
            logger.error(f"Failed to get/create collection for novel {novel_id}: {e}")
            raise VectorStoreError(f"Collection operation failed: {e}")

    def _is_dimension_mismatch(self, error: Exception) -> bool:
        message = str(error)
        return "Embedding dimension" in message and "collection dimensionality" in message
    
    def add_chunks(
        self, 
        novel_id: int, 
        chunks: List[Dict[str, Any]]
    ) -> int:
        """添加内容块到向量存储"""
        try:
            collection = self.get_or_create_collection(novel_id)
            
            ids = [f"chunk_{chunk['id']}" for chunk in chunks]
            documents = [chunk['content'] for chunk in chunks]
            metadatas = [
                {
                    "chapter_id": chunk.get('chapter_id'),
                    "chunk_type": chunk.get('chunk_type', 'content'),
                    "chunk_index": chunk.get('chunk_index', 0),
                    **chunk.get('metadata', {})
                }
                for chunk in chunks
            ]
            
            collection.add(
                ids=ids,
                documents=documents,
                metadatas=metadatas
            )
            
            logger.info(f"Added {len(chunks)} chunks to novel {novel_id}")
            return len(chunks)
            
        except Exception as e:
            if self._is_dimension_mismatch(e):
                collection = self._recreate_collection(novel_id)
                ids = [f"chunk_{chunk['id']}" for chunk in chunks]
                documents = [chunk['content'] for chunk in chunks]
                metadatas = [
                    {
                        "chapter_id": chunk.get('chapter_id'),
                        "chunk_type": chunk.get('chunk_type', 'content'),
                        "chunk_index": chunk.get('chunk_index', 0),
                        **chunk.get('metadata', {})
                    }
                    for chunk in chunks
                ]
                collection.add(ids=ids, documents=documents, metadatas=metadatas)
                logger.info("Rebuilt collection for novel %s after dimension mismatch and added %s chunks", novel_id, len(chunks))
                return len(chunks)
            logger.error(f"Failed to add chunks to novel {novel_id}: {e}")
            raise VectorStoreError(f"Add chunks failed: {e}")
    
    async def search(
        self, 
        novel_id: int, 
        query: str, 
        top_k: int = 10,
        filters: Optional[Dict[str, Any]] = None
    ) -> List[Dict[str, Any]]:
        """语义检索（异步）"""
        try:
            collection = self.get_or_create_collection(novel_id)
            
            where = None
            if filters:
                conditions = []
                if filters.get("chapter_ids"):
                    conditions.append({"chapter_id": {"$in": filters["chapter_ids"]}})
                if filters.get("chunk_types"):
                    conditions.append({"chunk_type": {"$in": filters["chunk_types"]}})
                if conditions:
                    where = {"$and": conditions} if len(conditions) > 1 else conditions[0]
            
            def _sync_query():
                return collection.query(
                    query_texts=[query],
                    n_results=top_k,
                    where=where,
                    include=["documents", "metadatas", "distances"]
                )
            
            results = await asyncio.to_thread(_sync_query)
            
            formatted_results = []
            if results['ids'] and results['ids'][0]:
                for i, doc_id in enumerate(results['ids'][0]):
                    formatted_results.append({
                        "id": doc_id,
                        "content": results['documents'][0][i],
                        "metadata": results['metadatas'][0][i] if results['metadatas'] else {},
                        "distance": results['distances'][0][i] if results['distances'] else 0
                    })
            
            logger.info(f"Search completed for novel {novel_id}, found {len(formatted_results)} results")
            return formatted_results
            
        except Exception as e:
            if self._is_dimension_mismatch(e):
                self._recreate_collection(novel_id)
                logger.warning("Reset collection for novel %s after search-time dimension mismatch; returning empty results", novel_id)
                return []
            logger.error(f"Search failed for novel {novel_id}: {e}")
            raise VectorStoreError(f"Search failed: {e}")
    
    def delete_chapter_chunks(self, novel_id: int, chapter_id: int) -> int:
        """删除章节的所有内容块"""
        try:
            collection = self.get_or_create_collection(novel_id)
            
            results = collection.get(
                where={"chapter_id": chapter_id}
            )
            
            deleted_count = 0
            if results['ids']:
                collection.delete(ids=results['ids'])
                deleted_count = len(results['ids'])
            
            logger.info(f"Deleted {deleted_count} chunks for chapter {chapter_id} in novel {novel_id}")
            return deleted_count
            
        except Exception as e:
            if self._is_dimension_mismatch(e):
                self._recreate_collection(novel_id)
                logger.warning("Reset collection for novel %s after delete-time dimension mismatch", novel_id)
                return 0
            logger.error(f"Failed to delete chapter chunks: {e}")
            raise VectorStoreError(f"Delete chapter chunks failed: {e}")
    
    def delete_collection(self, novel_id: int) -> bool:
        """删除小说的整个向量集合"""
        try:
            collection_name = f"novel_{novel_id}"
            self.client.delete_collection(collection_name)
            logger.info(f"Deleted collection: {collection_name}")
            return True
        except Exception as e:
            logger.warning(f"Failed to delete collection for novel {novel_id}: {e}")
            return False
    
    def split_text(self, text: str, chunk_size: int = 500, overlap: int = 50) -> list[str]:
        """将文本分割成重叠的块，优先在段落/句子边界处切分"""
        if not text:
            return []

        paragraphs = text.split("\n")
        if not paragraphs:
            return []

        chunks: list[str] = []
        current_chunk = ""

        for para in paragraphs:
            para = para.strip()
            if not para:
                if current_chunk:
                    current_chunk += "\n"
                continue

            if len(current_chunk) + len(para) + 1 <= chunk_size:
                if current_chunk:
                    current_chunk += "\n" + para
                else:
                    current_chunk = para
            else:
                if current_chunk:
                    chunks.append(current_chunk)

                if len(para) <= chunk_size:
                    current_chunk = para
                else:
                    sentences = self._split_sentences(para)
                    current_chunk = ""
                    for sentence in sentences:
                        if len(current_chunk) + len(sentence) + 1 <= chunk_size:
                            if current_chunk:
                                current_chunk += sentence
                            else:
                                current_chunk = sentence
                        else:
                            if current_chunk:
                                chunks.append(current_chunk)
                            current_chunk = sentence

        if current_chunk:
            chunks.append(current_chunk)

        return chunks

    @staticmethod
    def _split_sentences(text: str) -> list[str]:
        """Split text into sentences, supporting Chinese punctuation."""
        import re
        parts = re.split(r'([。！？；\.\!\?;])', text)
        sentences: list[str] = []
        buffer = ""
        for part in parts:
            buffer += part
            if re.match(r'[。！？；\.\!\?;]', part):
                sentences.append(buffer)
                buffer = ""
        if buffer:
            sentences.append(buffer)
        return [s for s in sentences if s.strip()]

    def build_chapter_chunks(
        self,
        *,
        chapter_id: int,
        chapter_number: Optional[int],
        chapter_title: Optional[str],
        content: str,
        summary: Optional[str] = None,
        extra_metadata: Optional[Dict[str, Any]] = None,
        chunk_size: int = 500,
        overlap: int = 50
    ) -> List[Dict[str, Any]]:
        """
        为章节构建更适合小说检索的多层记忆块。

        包含：
        - summary: 高密度剧情摘要，适合回忆“发生了什么”
        - chapter_brief: 章节标题 + 摘要/开头，适合模糊记忆搜索
        - content: 原始正文切块，适合精确回溯细节
        """
        clean_content = (content or "").strip()
        summary_text = (summary or "").strip()
        title = (chapter_title or f"第{chapter_number}章").strip()
        base_metadata = {
            "chapter_number": chapter_number,
            "chapter_title": title,
            **(extra_metadata or {})
        }

        chunks: List[Dict[str, Any]] = []
        if summary_text:
            chunks.append({
                "id": f"{chapter_id}_summary",
                "content": summary_text,
                "chapter_id": chapter_id,
                "chunk_type": "summary",
                "chunk_index": 0,
                "metadata": base_metadata,
            })

        chapter_brief_parts = [title]
        if summary_text:
            chapter_brief_parts.append(summary_text)
        if clean_content:
            chapter_brief_parts.append(clean_content[:280])
        chapter_brief = "\n".join(part for part in chapter_brief_parts if part).strip()
        if chapter_brief:
            chunks.append({
                "id": f"{chapter_id}_brief",
                "content": chapter_brief,
                "chapter_id": chapter_id,
                "chunk_type": "chapter_brief",
                "chunk_index": 0,
                "metadata": base_metadata,
            })

        for i, chunk in enumerate(self.split_text(clean_content, chunk_size=chunk_size, overlap=overlap)):
            chunks.append({
                "id": f"{chapter_id}_{i}",
                "content": chunk,
                "chapter_id": chapter_id,
                "chunk_type": "content",
                "chunk_index": i,
                "metadata": base_metadata,
            })

        return chunks

    def close(self):
        """尽量释放向量存储和 embedding 相关资源"""
        try:
            embedding_function = getattr(self, "_embedding_function", None)
            model = getattr(embedding_function, "_model", None) if embedding_function else None

            for obj in (model, embedding_function, getattr(self, "client", None)):
                if not obj:
                    continue
                for method_name in ("close", "shutdown", "stop", "stop_multi_process_pool"):
                    method = getattr(obj, method_name, None)
                    if callable(method):
                        try:
                            method()
                        except TypeError:
                            try:
                                method(None)
                            except Exception:
                                pass
                        except Exception:
                            pass

            self._embedding_function = None
            if hasattr(self, "client"):
                self.client = None

            gc.collect()
            logger.info("VectorStore resources released")
        except Exception as e:
            logger.warning(f"Failed to fully release VectorStore resources: {e}")


vector_store = VectorStore()
