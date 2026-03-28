"""
上下文构建服务 - RAG核心逻辑
"""
import logging
import hashlib
from typing import List, Dict, Any, Optional
from datetime import datetime, timedelta
from sqlalchemy.orm import Session

from app.core.vector_store import vector_store, VectorStoreError
from app.novels.models import Novel
from app.chapters.models import Chapter
from app.characters.models import Character
from app.plot_events.models import PlotEvent

logger = logging.getLogger(__name__)


class ContextCache:
    """简单的内存缓存"""
    
    def __init__(self, ttl_seconds: int = 300):
        self._cache: Dict[str, Any] = {}
        self._timestamps: Dict[str, datetime] = {}
        self._ttl = ttl_seconds
    
    def _get_key(self, *args, **kwargs) -> str:
        """生成缓存键"""
        key_data = f"{args}_{sorted(kwargs.items())}"
        return hashlib.md5(key_data.encode()).hexdigest()
    
    def get(self, key: str) -> Optional[Any]:
        """获取缓存"""
        if key in self._cache:
            timestamp = self._timestamps.get(key)
            if timestamp and datetime.now() - timestamp < timedelta(seconds=self._ttl):
                logger.debug(f"Cache hit: {key[:8]}")
                return self._cache[key]
            else:
                del self._cache[key]
                del self._timestamps[key]
        return None
    
    def set(self, key: str, value: Any):
        """设置缓存"""
        self._cache[key] = value
        self._timestamps[key] = datetime.now()
        logger.debug(f"Cache set: {key[:8]}")
    
    def clear(self):
        """清空缓存"""
        self._cache.clear()
        self._timestamps.clear()
        logger.info("Cache cleared")


context_cache = ContextCache(ttl_seconds=300)


class ContextBuilder:
    """上下文构建器"""
    
    def __init__(self, db: Session, novel_id: int):
        self.db = db
        self.novel_id = novel_id
        self.novel = db.query(Novel).filter(Novel.id == novel_id).first()
    
    def build_writing_context(
        self,
        chapter_id: int,
        context_size: int = 3000,
        include_previous_chapters: bool = True,
        include_characters: bool = True,
        include_plot_events: bool = True
    ) -> Dict[str, Any]:
        """构建写作上下文"""
        cache_key = context_cache._get_key(
            "writing_context",
            novel_id=self.novel_id,
            chapter_id=chapter_id,
            context_size=context_size,
            include_previous=include_previous_chapters,
            include_chars=include_characters,
            include_plot=include_plot_events
        )
        
        cached = context_cache.get(cache_key)
        if cached:
            return cached
        
        logger.info(f"Building writing context for chapter {chapter_id}")
        
        context_parts = []
        chapter = self.db.query(Chapter).filter(Chapter.id == chapter_id).first()
        
        if not chapter:
            raise ValueError(f"Chapter {chapter_id} not found")
        
        context_parts.append(f"【小说标题】{self.novel.title}")
        if self.novel.description:
            context_parts.append(f"【小说简介】{self.novel.description}")
        
        previous_summary = None
        if include_previous_chapters:
            previous_summary = self._get_previous_chapters_summary(chapter.chapter_number)
            if previous_summary:
                context_parts.append(f"【前文摘要】\n{previous_summary}")
        
        if include_characters:
            characters_context = self._get_characters_context()
            if characters_context:
                context_parts.append(f"【角色信息】\n{characters_context}")
        
        if include_plot_events:
            plot_context = self._get_plot_events_context()
            if plot_context:
                context_parts.append(f"【情节线索】\n{plot_context}")
        
        full_context = "\n\n".join(context_parts)
        
        if len(full_context) > context_size:
            full_context = full_context[:context_size] + "..."
        
        characters = self._get_characters_list()
        plot_hints = self._get_plot_hints()
        
        logger.info(f"Context built: {len(full_context)} chars")
        
        result = {
            "chapter_id": chapter_id,
            "novel_id": self.novel_id,
            "context": full_context,
            "previous_summary": previous_summary,
            "characters": characters,
            "plot_hints": plot_hints,
            "context_length": len(full_context)
        }
        
        context_cache.set(cache_key, result)
        
        return result
    
    def search_relevant_context(
        self,
        query: str,
        top_k: int = 5,
        filters: Optional[Dict[str, Any]] = None
    ) -> List[Dict[str, Any]]:
        """搜索相关上下文"""
        cache_key = context_cache._get_key(
            "search",
            novel_id=self.novel_id,
            query=query,
            top_k=top_k,
            filters=str(filters)
        )
        
        cached = context_cache.get(cache_key)
        if cached:
            return cached
        
        logger.info(f"Searching context for query: '{query[:50]}...'")
        
        try:
            results = vector_store.search(
                novel_id=self.novel_id,
                query=query,
                top_k=top_k,
                filters=filters
            )
            
            formatted_results = []
            for result in results:
                formatted_results.append({
                    "chunk_id": result["id"],
                    "content": result["content"],
                    "source_type": result["metadata"].get("chunk_type", "content"),
                    "source_id": result["metadata"].get("chapter_id"),
                    "relevance_score": 1 - result["distance"],
                    "metadata": result["metadata"]
                })
            
            logger.info(f"Found {len(formatted_results)} relevant chunks")
            
            context_cache.set(cache_key, formatted_results)
            
            return formatted_results
            
        except VectorStoreError as e:
            logger.error(f"Vector search failed: {e}")
            return []
    
    def _get_previous_chapters_summary(self, current_chapter_num: int) -> Optional[str]:
        """获取前几章的摘要"""
        previous_chapters = self.db.query(Chapter).filter(
            Chapter.novel_id == self.novel_id,
            Chapter.chapter_number < current_chapter_num,
            Chapter.status == "completed"
        ).order_by(Chapter.chapter_number.desc()).limit(3).all()
        
        if not previous_chapters:
            return None
        
        summaries = []
        for ch in reversed(previous_chapters):
            if ch.summary:
                summaries.append(f"第{ch.chapter_number}章: {ch.summary}")
            elif ch.content:
                summaries.append(f"第{ch.chapter_number}章: {ch.content[:200]}...")
        
        return "\n".join(summaries) if summaries else None
    
    def _get_characters_context(self) -> Optional[str]:
        """获取角色上下文"""
        characters = self.db.query(Character).filter(
            Character.novel_id == self.novel_id
        ).all()
        
        if not characters:
            return None
        
        char_info = []
        for char in characters:
            info = f"- {char.name}"
            if char.personality:
                traits = char.personality.get("traits", [])
                if traits:
                    info += f" ({', '.join(traits)})"
            char_info.append(info)
        
        return "\n".join(char_info)
    
    def _get_plot_events_context(self) -> Optional[str]:
        """获取情节事件上下文"""
        events = self.db.query(PlotEvent).filter(
            PlotEvent.novel_id == self.novel_id
        ).order_by(PlotEvent.timeline).limit(10).all()
        
        if not events:
            return None
        
        event_info = []
        for event in events:
            info = f"- [{event.event_type or '事件'}] {event.description}"
            event_info.append(info)
        
        return "\n".join(event_info)
    
    def _get_characters_list(self) -> List[Dict[str, Any]]:
        """获取角色列表"""
        characters = self.db.query(Character).filter(
            Character.novel_id == self.novel_id
        ).all()
        
        return [
            {
                "id": char.id,
                "name": char.name,
                "personality": char.personality,
                "abilities": char.abilities
            }
            for char in characters
        ]
    
    def _get_plot_hints(self) -> List[Dict[str, Any]]:
        """获取情节提示"""
        events = self.db.query(PlotEvent).filter(
            PlotEvent.novel_id == self.novel_id
        ).order_by(PlotEvent.created_at.desc()).limit(5).all()
        
        return [
            {
                "id": event.id,
                "type": event.event_type,
                "description": event.description,
                "chapter_id": event.chapter_id
            }
            for event in events
        ]
