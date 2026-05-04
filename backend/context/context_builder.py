"""
上下文构建服务 - RAG核心逻辑
支持分层缓存（Layered Caching）与静态前缀对齐
"""
import asyncio
import logging
import hashlib
from typing import List, Dict, Any, Optional
from datetime import datetime, timedelta
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from rag.vector_store import vector_store, VectorStoreError
from chat.session_manager import NovelContext
from novels.models import Novel, NovelCreativeProfile, NovelStoryState, ReaderPerspective
from locations.models import Location
from chapters.models import Chapter
from characters.models import Character
from timeline.models import TimelineEntry

logger = logging.getLogger(__name__)


class ContextCache:
    """分层内存缓存"""

    def __init__(self, ttl_seconds: int = 300):
        self._cache: dict[str, Any] = {}
        self._timestamps: dict[str, datetime] = {}
        self._novel_keys: dict[int, set[str]] = {}
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
    
    def set(self, key: str, value: Any, novel_id: int | None = None):
        """设置缓存"""
        self._cache[key] = value
        self._timestamps[key] = datetime.now()
        if novel_id is not None:
            if novel_id not in self._novel_keys:
                self._novel_keys[novel_id] = set()
            self._novel_keys[novel_id].add(key)
        logger.debug(f"Cache set: {key[:8]}")
    
    def clear(self):
        """清空缓存"""
        self._cache.clear()
        self._timestamps.clear()
        logger.info("Cache cleared")

    def invalidate_novel(self, novel_id: int):
        """清除指定小说的所有缓存条目"""
        keys = self._novel_keys.pop(novel_id, set())
        for key in keys:
            self._cache.pop(key, None)
            self._timestamps.pop(key, None)
        if keys:
            logger.info(f"Cache invalidated for novel {novel_id}: {len(keys)} entries removed")


context_cache = ContextCache(ttl_seconds=300)


class ContextLayer:
    """上下文层级枚举"""
    STATIC = "static"
    STABLE = "stable"
    SLIDING = "sliding"
    DYNAMIC = "dynamic"


class ContextBuilder:
    """上下文构建器 - 支持分层缓存优化"""
    
    LAYER_CONFIG = {
        ContextLayer.STATIC: {
            "tag": "novel_profile",
            "priority": 1,
            "description": "小说标题、简介（几乎永不改变）"
        },
        ContextLayer.STABLE: {
            "tag": "character_network",
            "priority": 2,
            "description": "角色信息、人物关系网络（变动频率低）"
        },
        ContextLayer.SLIDING: {
            "tag": "previous_chapters",
            "priority": 3,
            "description": "前文摘要（随章节滚动，相对稳定）"
        },
        ContextLayer.DYNAMIC: {
            "tag": "current_plot",
            "priority": 4,
            "description": "情节线索、故事时间线（最易变动）"
        }
    }
    
    def __init__(self, db: AsyncSession, novel_id: int):
        self.db = db
        self.novel_id = novel_id
        self.novel = None
    
    async def _init_novel(self):
        """初始化小说对象"""
        if self.novel is None:
            result = await self.db.execute(
                select(Novel).where(Novel.id == self.novel_id)
            )
            self.novel = result.scalar_one_or_none()
    
    def _generate_layer_cache_key(
        self,
        layer: str,
        chapter_number: Optional[int] = None,
        context_size: int = 3000,
        **extra_params
    ) -> str:
        """生成分层缓存键"""
        base_key_data = {
            "novel_id": self.novel_id,
            "layer": layer,
            "chapter_number": chapter_number or 0,
            "context_size": context_size,
            **{k: v for k, v in sorted(extra_params.items())}
        }
        key_str = "_".join(f"{k}={v}" for k, v in sorted(base_key_data.items()))
        return f"{layer}_{hashlib.md5(key_str.encode()).hexdigest()}"
    
    async def _fetch_layer_static(self) -> Optional[str]:
        """
        Layer 1 (Static): 小说标题、简介
        变化频率：极低（仅当用户手动修改时）
        缓存策略：长缓存（30分钟）
        """
        await self._init_novel()
        
        cache_key = self._generate_layer_cache_key(ContextLayer.STATIC)
        cached = context_cache.get(cache_key)
        if cached:
            return cached
        
        parts = []
        tag_config = self.LAYER_CONFIG[ContextLayer.STATIC]
        
        if self.novel:
            parts.append(f"<{tag_config['tag']}>")
            parts.append(f"【小说标题】{self.novel.title}")
            if self.novel.description:
                parts.append(f"【小说简介】{self.novel.description}")
            parts.append(f"</{tag_config['tag']}>")
        
        result = "\n".join(parts) if parts else None
        
        if result:
            context_cache.set(cache_key, result, novel_id=self.novel_id)
            
        return result
    
    async def _fetch_layer_stable(self) -> Optional[str]:
        """
        Layer 2 (Stable): 角色信息、人物关系网络
        变化频率：低（角色增删改时变化）
        缓存策略：中等缓存（10分钟）
        注意：结果按ID/名称排序以保证缓存键稳定
        """
        cache_key = self._generate_layer_cache_key(ContextLayer.STABLE)
        cached = context_cache.get(cache_key)
        if cached:
            return cached
        
        tag_config = self.LAYER_CONFIG[ContextLayer.STABLE]
        parts = [f"<{tag_config['tag']}>"]
        
        characters_context = await self._get_characters_context_sorted()
        if characters_context:
            parts.append(characters_context)
        
        relation_summary = await self._get_relation_network_context_sorted()
        if relation_summary:
            parts.append(relation_summary)
        
        parts.append(f"</{tag_config['tag']}>")
        result = "\n".join(parts)
        
        context_cache.set(cache_key, result, novel_id=self.novel_id)
        
        return result
    
    async def _fetch_layer_sliding(
        self,
        target_chapter_number: Optional[int] = None
    ) -> Optional[str]:
        """
        Layer 3 (Sliding): 前文摘要
        变化频率：中（随章节推进滚动）
        缓存策略：短缓存（5分钟）
        """
        if not target_chapter_number:
            return None
            
        cache_key = self._generate_layer_cache_key(
            ContextLayer.SLIDING,
            chapter_number=target_chapter_number
        )
        cached = context_cache.get(cache_key)
        if cached:
            return cached
        
        tag_config = self.LAYER_CONFIG[ContextLayer.SLIDING]
        parts = [f"<{tag_config['tag']}>"]
        
        previous_summary = await self._get_previous_chapters_summary(target_chapter_number)
        if previous_summary:
            parts.append(previous_summary)
        
        parts.append(f"</{tag_config['tag']}>")
        result = "\n".join(parts)
        
        context_cache.set(cache_key, result, novel_id=self.novel_id)
        
        return result
    
    async def _fetch_layer_dynamic(
        self,
        target_chapter_number: Optional[int] = None
    ) -> Optional[str]:
        """
        Layer 4 (Dynamic): 情节线索、故事时间线
        变化频率：高（每次创作都可能更新）
        缓存策略：极短缓存（2分钟）或不缓存
        """
        if not target_chapter_number:
            target_chapter_number = 9999
            
        tag_config = self.LAYER_CONFIG[ContextLayer.DYNAMIC]
        parts = [f"<{tag_config['tag']}>"]
        
        timeline_context = await self._get_timeline_context(target_chapter_number)
        if timeline_context:
            parts.append(timeline_context)
        
        parts.append(f"</{tag_config['tag']}>")
        result = "\n".join(parts)
        
        return result
    
    async def build_writing_context(
        self,
        chapter_number: Optional[int] = None,
        chapter_id: Optional[int] = None,
        context_size: int = 3000,
        include_previous_chapters: bool = True,
        include_characters: bool = True,
    ) -> Dict[str, Any]:
        """
        构建写作上下文 - 分层缓存优化版

        层级构造顺序（由静到动）：
        1. Layer 1 (Static): 小说标题、简介
        2. Layer 2 (Stable): 角色信息、人物关系网络
        3. Layer 3 (Sliding): 前文摘要
        4. Layer 4 (Dynamic): 故事时间线

        Args:
            chapter_number: 章节号（二选一）
            chapter_id: 章节ID（二选一）
            context_size: 上下文大小
            include_previous_chapters: 是否包含前文摘要
            include_characters: 是否包含角色信息
        """
        await self._init_novel()
        
        chapter = None
        if chapter_number:
            result = await self.db.execute(
                select(Chapter).where(
                    Chapter.novel_id == self.novel_id,
                    Chapter.chapter_number == chapter_number
                )
            )
            chapter = result.scalar_one_or_none()
        elif chapter_id:
            result = await self.db.execute(
                select(Chapter).where(Chapter.id == chapter_id)
            )
            chapter = result.scalar_one_or_none()
        
        target_chapter_number = chapter.chapter_number if chapter else chapter_number
        
        logger.info(f"Building layered context for chapter {target_chapter_number}")
        
        layer_tasks = {}
        
        layer_tasks["static"] = self._fetch_layer_static()
        
        if include_characters:
            layer_tasks["stable"] = self._fetch_layer_stable()
        
        if include_previous_chapters and target_chapter_number:
            layer_tasks["sliding"] = self._fetch_layer_sliding(target_chapter_number)

        layer_tasks["dynamic"] = self._fetch_layer_dynamic(target_chapter_number)
        
        layer_results = await asyncio.gather(*layer_tasks.values(), return_exceptions=True)
        
        layer_contents = {}
        for layer_name, result in zip(layer_tasks.keys(), layer_results):
            if isinstance(result, Exception):
                logger.warning(f"Layer {layer_name} fetch failed: {result}")
                layer_contents[layer_name] = ""
            elif result:
                layer_contents[layer_name] = result
            else:
                layer_contents[layer_name] = ""
        
        ordered_layers = [
            ContextLayer.STATIC,
            ContextLayer.STABLE,
            ContextLayer.SLIDING,
            ContextLayer.DYNAMIC
        ]
        
        all_parts = []
        for layer in ordered_layers:
            content = layer_contents.get(layer, "")
            if content:
                all_parts.append(content)
        
        full_context = "\n\n".join(all_parts)
        
        if len(full_context) > context_size:
            full_context = self._smart_truncate_by_layers(
                all_parts, 
                ordered_layers,
                context_size
            )
        
        characters = await self._get_characters_list()
        plot_hints = await self._get_plot_hints()
        
        logger.info(f"Context built: {len(full_context)} chars (layered)")
        
        result_data = {
            "chapter_id": chapter.id if chapter else None,
            "novel_id": self.novel_id,
            "context": full_context,
            "previous_summary": layer_contents.get(ContextLayer.SLIDING, "").replace("<previous_chapters>", "").replace("</previous_chapters>", "").strip() or None,
            "characters": characters,
            "plot_hints": plot_hints,
            "context_length": len(full_context),
            "layers_used": [l for l in ordered_layers if layer_contents.get(l)]
        }
        
        return result_data
    def _smart_truncate_by_layers(
        self,
        layer_parts: List[str],
        layer_order: List[str],
        max_size: int
    ) -> str:
        """
        按层智能截断：优先保留高层级（Static/Stable），舍弃低层级（Dynamic）
        确保XML标签完整性
        """
        total_len = sum(len(p) for p in layer_parts)
        
        if total_len <= max_size:
            return "\n\n".join(layer_parts)
        
        priority_map = {layer: idx for idx, layer in enumerate(layer_order)}
        
        indexed_parts = []
        for i, part in enumerate(layer_parts):
            layer_name = layer_order[i] if i < len(layer_order) else "dynamic"
            priority = priority_map.get(layer_name, 99)
            indexed_parts.append((priority, i, part))
        
        indexed_parts.sort(key=lambda x: x[0])
        
        retained_parts = []
        current_size = 0
        
        for priority, original_idx, part in indexed_parts:
            part_len = len(part)
            
            if current_size + part_len <= max_size:
                retained_parts.append((original_idx, part))
                current_size += part_len
            elif current_size < max_size * 0.9:
                remaining_space = max_size - current_size
                truncated_part = part[:remaining_space]
                
                open_tag_pos = truncated_part.rfind("<")
                if open_tag_pos > 0:
                    tag_content = truncated_part[open_tag_pos:]
                    if not tag_content.rstrip().endswith(">") or tag_content.strip().startswith("</"):
                        truncated_part = truncated_part[:open_tag_pos].rstrip()
                
                last_close_tag = truncated_part.rfind("</")
                if last_close_tag > 0:
                    tag_end = truncated_part.find(">", last_close_tag)
                    if tag_end > 0:
                        truncated_part = truncated_part[:tag_end + 1]
                    else:
                        truncated_part = truncated_part[:last_close_tag].rstrip()
                
                if truncated_part.strip():
                    retained_parts.append((original_idx, truncated_part))
                    current_size += len(truncated_part)
                break
            else:
                break
        
        retained_parts.sort(key=lambda x: x[0])
        
        final_parts = [part for _, part in retained_parts]
        
        return "\n\n".join(final_parts) + "\n\n[...部分动态内容已省略以控制长度...]"
    
    async def search_relevant_context(
        self,
        query: str,
        top_k: int = 5,
        filters: Optional[Dict[str, Any]] = None,
        min_relevance_score: float = 0.5
    ) -> List[Dict[str, Any]]:
        """
        搜索相关上下文 - 优化版
        
        改进点：
        1. 相关性阈值过滤：丢弃低质量结果
        2. 多样性重排序（MMR）：避免重复内容
        3. 智能截断：控制总长度
        4. 质量监控：记录检索质量指标
        """
        await self._init_novel()
        
        cache_key = context_cache._get_key(
            "search_v2",
            novel_id=self.novel_id,
            query=query,
            top_k=top_k,
            filters=str(filters),
            min_score=min_relevance_score
        )
        
        cached = context_cache.get(cache_key)
        if cached:
            return cached
        
        logger.info(f"🔍 RAG搜索: query='{query[:50]}...', top_k={top_k}, threshold={min_relevance_score}")
        
        try:
            results = await vector_store.search(
                novel_id=self.novel_id,
                query=query,
                top_k=top_k * 2,  
                filters=filters
            )
            
            filtered_results = []
            for result in results:
                distance = result["distance"]
                if distance >= 0:
                    relevance_score = 1.0 / (1.0 + distance)
                else:
                    relevance_score = 1.0
                
                if relevance_score < min_relevance_score:
                    logger.debug(f"丢弃低质量结果 (score={relevance_score:.3f}<{min_relevance_score}): {result['content'][:50]}...")
                    continue
                
                formatted = {
                    "chunk_id": result["id"],
                    "content": result["content"],
                    "source_type": result["metadata"].get("chunk_type", "content"),
                    "source_id": result["metadata"].get("chapter_id"),
                    "relevance_score": round(relevance_score, 4),
                    "metadata": result["metadata"]
                }
                filtered_results.append(formatted)
            
            diverse_results = self._mmr_rerank(filtered_results, final_k=top_k)
            
            total_chars = sum(len(r["content"]) for r in diverse_results)
            
            logger.info(
                f"✅ RAG完成: 原始={len(results)} → 过滤后={len(filtered_results)} "
                f"→ 重排后={len(diverse_results)}, 总字符={total_chars}, "
                f"最高分={diverse_results[0]['relevance_score']:.3f}" if diverse_results else "❌ 无有效结果"
            )
            
            context_cache.set(cache_key, diverse_results, novel_id=self.novel_id)
            
            return diverse_results
            
        except VectorStoreError as e:
            logger.error(f"❌ Vector search failed: {e}")
            return []
    
    def _mmr_rerank(self, results: List[Dict], final_k: int, lambda_param: float = 0.7) -> List[Dict]:
        """
        Maximal Marginal Relevance (MMR) 重排序算法
        
        目标：
        - 保持高相关性
        - 增加结果多样性（避免重复内容）
        
        参数：
        - lambda_param: 相关性 vs 多样性的权衡（0.7表示70%权重给相关性）
        """
        if len(results) <= final_k:
            return results
        
        import numpy as np
        
        selected = []
        remaining = list(range(len(results)))
        
        best_idx = max(remaining, key=lambda i: results[i]["relevance_score"])
        selected.append(best_idx)
        remaining.remove(best_idx)
        
        while len(selected) < final_k and remaining:
            mmr_scores = []
            for idx in remaining:
                
                max_similarity_to_selected = 0
                for sel_idx in selected:
                    similarity = self._text_similarity(
                        results[idx]["content"],
                        results[sel_idx]["content"]
                    )
                    max_similarity_to_selected = max(max_similarity_to_selected, similarity)
                
                relevance = results[idx]["relevance_score"]
                diversity_penalty = max_similarity_to_selected
                
                mmr_score = lambda_param * relevance - (1 - lambda_param) * diversity_penalty
                mmr_scores.append((idx, mmr_score))
            
            next_best = max(mmr_scores, key=lambda x: x[1])
            selected.append(next_best[0])
            remaining.remove(next_best[0])
        
        return [results[idx] for idx in selected]
    
    def _text_similarity(self, text1: str, text2: str) -> float:
        """计算两个文本的简单相似度（基于词重叠）"""
        words1 = set(text1.lower().split())
        words2 = set(text2.lower().split())
        
        if not words1 or not words2:
            return 0.0
        
        intersection = words1 & words2
        union = words1 | words2
        
        jaccard = len(intersection) / len(union) if union else 0.0
        
        return jaccard
    
    async def _get_previous_chapters_summary(self, current_chapter_num: int) -> Optional[str]:
        """获取前几章的摘要"""
        result = await self.db.execute(
            select(Chapter).where(
                Chapter.novel_id == self.novel_id,
                Chapter.chapter_number < current_chapter_num,
                Chapter.status == "completed"
            ).order_by(Chapter.chapter_number.desc()).limit(3)
        )
        previous_chapters = result.scalars().all()
        
        if not previous_chapters:
            return None
        
        summaries = []
        for ch in reversed(previous_chapters):
            if ch.summary:
                summaries.append(f"第{ch.chapter_number}章: {ch.summary}")
            elif ch.content:
                summaries.append(f"第{ch.chapter_number}章: {ch.content[:200]}...")
        
        return "\n".join(summaries) if summaries else None

    async def _get_characters_context(self) -> Optional[str]:
        """获取角色上下文（原始版本，保持兼容性）"""
        result = await self.db.execute(
            select(Character).where(Character.novel_id == self.novel_id)
        )
        characters = result.scalars().all()
        
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
    
    async def _get_characters_context_sorted(self) -> Optional[str]:
        """
        获取角色上下文（排序版本）
        按名称排序以确保缓存键稳定
        """
        result = await self.db.execute(
            select(Character).where(Character.novel_id == self.novel_id)
        )
        characters = result.scalars().all()
        
        if not characters:
            return None
        
        sorted_characters = sorted(characters, key=lambda c: c.name.lower())
        
        char_info = []
        for char in sorted_characters:
            info = f"- {char.name}"
            if char.personality:
                traits = char.personality.get("traits", [])
                if traits:
                    info += f" ({', '.join(traits)})"
            char_info.append(info)
        
        return "\n".join(char_info)
    async def _get_characters_list(self) -> List[Dict[str, Any]]:
        """获取角色列表"""
        result = await self.db.execute(
            select(Character).where(Character.novel_id == self.novel_id)
        )
        characters = result.scalars().all()
        
        return [
            {
                "id": char.id,
                "name": char.name,
                "personality": char.personality,
                "abilities": char.abilities
            }
            for char in characters
        ]
    
    async def _get_plot_hints(self) -> List[Dict[str, Any]]:
        """获取情节提示（从 TimelineEntry 的活跃条目中提取）"""
        result = await self.db.execute(
            select(TimelineEntry).where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.status.in_(["pending", "active"])
            ).order_by(TimelineEntry.importance.desc(), TimelineEntry.id.desc()).limit(5)
        )
        entries = result.scalars().all()

        return [
            {
                "id": entry.id,
                "type": entry.category,
                "description": entry.title + (f": {entry.description}" if entry.description else ""),
                "chapter_id": entry.source_chapter_id
            }
            for entry in entries
        ]
    
    async def _get_timeline_context(self, current_chapter: Optional[int] = None) -> Optional[str]:
        try:
            from timeline.service import TimelineService
            service = TimelineService(self.db, self.novel_id)
            target_chapter = current_chapter or 9999
            entries, summary_text = await service.get_context_for_generation(
                current_chapter=target_chapter, max_entries=12
            )
            if not entries:
                return None
            return summary_text
        except Exception as exc:
            logger.warning(f"Timeline context injection failed (non-fatal): {exc}")
            return None
    
    async def _get_relation_network_context(self) -> Optional[str]:
        """获取人物关系概要（原始版本，保持兼容性）"""
        try:
            from characters.service import CharacterService

            char_svc = CharacterService(self.db, self.novel_id)
            network = await char_svc.get_network()
            if not network.get("edges"):
                return None

            relation_lines = []
            for edge in network["edges"][:15]:
                relation_lines.append(
                    f"- {edge['source_name']} → {edge['target_name']} "
                    f"({edge['type']}, 强度{edge['intensity']})"
                )

            return "当前人物关系网络：\n" + "\n".join(relation_lines)
        except Exception as exc:
            logger.warning(f"Relation network context injection failed (non-fatal): {exc}")
            return None
    
    async def _get_relation_network_context_sorted(self) -> Optional[str]:
        """
        获取人物关系概要（排序版本）
        按source_name和target_name排序以确保缓存键稳定
        """
        try:
            from characters.service import CharacterService

            char_svc = CharacterService(self.db, self.novel_id)
            network = await char_svc.get_network()
            if not network.get("edges"):
                return None

            sorted_edges = sorted(
                network["edges"][:15],
                key=lambda e: (e.get('source_name', '').lower(), e.get('target_name', '').lower())
            )

            relation_lines = []
            for edge in sorted_edges:
                relation_lines.append(
                    f"- {edge['source_name']} → {edge['target_name']} "
                    f"({edge['type']}, 强度{edge['intensity']})"
                )

            return "当前人物关系网络：\n" + "\n".join(relation_lines)
        except Exception as exc:
            logger.warning(f"Relation network context injection failed (non-fatal): {exc}")
            return None


# --- 辅助函数（从 ws_chat.py 提取）---

def _format_creative_profile_for_prompt(profile: NovelCreativeProfile) -> str:
    llm_brief = (profile.extra_metadata or {}).get("llm_brief")
    if llm_brief:
        return str(llm_brief).strip()
    parts: List[str] = []
    if profile.premise:
        parts.append(f"- 故事前提：{profile.premise}")
    if profile.theme:
        parts.append(f"- 主题：{profile.theme}")
    if profile.beginning:
        parts.append(f"- 开头：{profile.beginning}")
    if profile.middle:
        parts.append(f"- 中段：{profile.middle}")
    if profile.climax:
        parts.append(f"- 高潮：{profile.climax}")
    if profile.ending:
        parts.append(f"- 结尾：{profile.ending}")
    if profile.author_intent:
        parts.append(f"- 长期作者意图：{profile.author_intent}")
    if profile.preferred_tone:
        parts.append(f"- 默认语气：{profile.preferred_tone}")
    if profile.scene_planning_notes:
        parts.append(f"- 规划备注：{profile.scene_planning_notes}")
    for item in (profile.long_term_goals or [])[:5]:
        parts.append(f"- 长线目标：{item}")
    for item in (profile.must_keep or [])[:8]:
        parts.append(f"- 必须长期保留：{item}")
    for item in (profile.must_avoid or [])[:8]:
        parts.append(f"- 必须长期避免：{item}")
    return "\n".join(parts)


async def _build_novel_context_snapshot(db, novel_id: int) -> str:
    """构建小说上下文快照（system2），对话开始时注入一次，压缩时才重新生成"""
    sections: List[str] = []

    # 1. 故事状态文档
    state_result = await db.execute(
        select(NovelStoryState).where(NovelStoryState.novel_id == novel_id)
    )
    story_state = state_result.scalar_one_or_none()
    if story_state and story_state.content.strip():
        sections.append(f"## 故事状态\n{story_state.content.strip()}")

    # 2. 读者认知
    rp_result = await db.execute(
        select(ReaderPerspective)
        .where(
            ReaderPerspective.novel_id == novel_id,
            ReaderPerspective.revealed_chapter.is_(None),
        )
        .order_by(ReaderPerspective.type, ReaderPerspective.planted_chapter)
    )
    entries = rp_result.scalars().all()
    known = [e for e in entries if e.type == "known"]
    suspenses = [e for e in entries if e.type == "suspense"]
    misconceptions = [e for e in entries if e.type == "misconception"]

    if known or suspenses or misconceptions:
        rp_lines = ["## 读者认知"]
        if known:
            rp_lines.append("### 已知信息")
            for e in known:
                rp_lines.append(f"- {e.content} [第{e.planted_chapter}章起]")
        if suspenses:
            rp_lines.append("### 活跃悬念")
            for e in suspenses:
                ref = f"（第{e.planted_chapter}章种下"
                if e.last_mentioned_chapter:
                    ref += f"，最近提及：第{e.last_mentioned_chapter}章"
                ref += "）"
                rp_lines.append(f"- {e.content}{ref}")
        if misconceptions:
            rp_lines.append("### 读者误知")
            for e in misconceptions:
                truth = f" → 实际：{e.related_truth}" if e.related_truth else ""
                rp_lines.append(f"- {e.content}{truth}")
        sections.append("\n".join(rp_lines))

    # 3. 角色索引
    char_result = await db.execute(
        select(Character.id, Character.name, Character.personality)
        .where(Character.novel_id == novel_id)
        .limit(30)
    )
    characters = char_result.all()
    if characters:
        char_lines = ["## 角色索引"]
        for cid, name, personality in characters:
            brief = ""
            if personality and isinstance(personality, dict):
                brief = personality.get("brief") or personality.get("summary") or ""
                if not brief:
                    traits = personality.get("traits") or personality.get("性格") or []
                    if isinstance(traits, list) and traits:
                        brief = "、".join(str(t) for t in traits[:3])
            if brief:
                char_lines.append(f"- {name}：{brief}")
            else:
                char_lines.append(f"- {name}")
        sections.append("\n".join(char_lines))

    # 4. 世界设定概要（地点）
    loc_result = await db.execute(
        select(Location.name, Location.location_type, Location.description)
        .where(Location.novel_id == novel_id)
        .limit(20)
    )
    locations = loc_result.all()
    if locations:
        loc_lines = ["## 世界设定"]
        for name, loc_type, desc in locations:
            brief = (desc or "")[:80]
            if brief:
                loc_lines.append(f"- {name}（{loc_type}）：{brief}")
            else:
                loc_lines.append(f"- {name}（{loc_type}）")
        sections.append("\n".join(loc_lines))

    if not sections:
        return ""

    return (
        "【小说上下文快照 — 对话开始时生成，仅作参考。如你在对话中做了修改，以后边的工具调用结果为准。】\n\n"
        + "\n\n".join(sections)
    )


async def _build_novel_context(db, novel_id: int) -> NovelContext:
    """构建 NovelContext 对象"""

    result = await db.execute(select(Novel).where(Novel.id == novel_id))
    novel = result.scalar_one_or_none()

    if not novel:
        return NovelContext()

    return NovelContext(
        title=novel.title or "",
        description=novel.description or "",
        genre=novel.genre or ""
    )
