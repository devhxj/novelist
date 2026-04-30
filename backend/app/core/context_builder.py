"""
上下文构建服务 - RAG核心逻辑
支持分层缓存（Layered Caching）与静态前缀对齐
"""
import asyncio
import logging
import hashlib
from typing import List, Dict, Any, Optional, Tuple
from datetime import datetime, timedelta
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select, case

from app.core.vector_store import vector_store, VectorStoreError
from app.novels.models import Novel, NovelCreativeProfile
from app.chapters.models import Chapter
from app.characters.models import Character
from app.plot_events.models import PlotEvent
from app.planning.models import PlotOutline, PlotLine, PlotNode, PlotNodeStatus
from app.timeline.models import TimelineEntry, TimelineEntryCategory, TimelineEntryStatus

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
        
        plot_context = await self._get_plot_events_context_sorted()
        if plot_context:
            parts.append(plot_context)
        
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
        include_plot_events: bool = True
    ) -> Dict[str, Any]:
        """
        构建写作上下文 - 分层缓存优化版
        
        层级构造顺序（由静到动）：
        1. Layer 1 (Static): 小说标题、简介
        2. Layer 2 (Stable): 角色信息、人物关系网络
        3. Layer 3 (Sliding): 前文摘要
        4. Layer 4 (Dynamic): 情节线索、故事时间线
        
        Args:
            chapter_number: 章节号（二选一）
            chapter_id: 章节ID（二选一）
            context_size: 上下文大小
            include_previous_chapters: 是否包含前文摘要
            include_characters: 是否包含角色信息
            include_plot_events: 是否包含情节线索
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
        
        if include_plot_events:
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

    async def build_story_brief(
        self,
        chapter_number: int,
        *,
        context_size: int = 3600,
        additional_context: Optional[Dict[str, Any]] = None,
        retrieval_top_k: int = 3
    ) -> Dict[str, Any]:
        """
        构建统一的写前 StoryBrief。

        目标：
        1. 明确区分 Plot / Timeline / Foreshadowing 三个层次
        2. 为创作前提供“先理解再下笔”的结构化 brief
        3. 让缓存稳定块尽可能固定，只把动态检索结果放在末尾
        """
        additional_context = additional_context or {}
        layered_context = await self.build_writing_context(
            chapter_number=chapter_number,
            context_size=context_size,
            include_previous_chapters=True,
            include_characters=True,
            include_plot_events=True
        )

        await self._init_novel()

        outline = await self._get_outline_summary()
        creative_profile = await self._get_creative_profile_summary()
        active_plot_lines = await self._get_active_plot_lines(chapter_number)
        due_plot_nodes, upcoming_plot_nodes = await self._get_plot_nodes_for_brief(chapter_number)
        timeline_context = await self._get_timeline_entries_for_brief(chapter_number)
        foreshadowing_context = await self._get_foreshadowing_entries_for_brief(chapter_number)
        recent_chapters = await self._get_recent_chapter_cards(chapter_number, limit=5)
        retrieval_queries = self._build_memory_queries(
            chapter_number=chapter_number,
            due_plot_nodes=due_plot_nodes,
            timeline_entries=timeline_context["entries"],
            foreshadowing_entries=foreshadowing_context["entries"],
            additional_context=additional_context
        )
        retrieved_memory = await self._retrieve_memory_cards(retrieval_queries, top_k=retrieval_top_k)

        chapter_mission = {
            "must_resolve_foreshadowing_ids": [
                item["id"] for item in foreshadowing_context["due_entries"][:2]
            ],
            "should_advance_plot_node_ids": [
                item["id"] for item in due_plot_nodes[:3]
            ],
            "must_respect_timeline_ids": [
                item["id"] for item in timeline_context["priority_entries"][:3]
            ],
            "should_introduce_new_foreshadowing": bool(
                not foreshadowing_context["due_entries"]
                and chapter_number >= 3
                and active_plot_lines
            ),
        }

        recommendations = self._build_prewrite_recommendations(
            chapter_number=chapter_number,
            due_plot_nodes=due_plot_nodes,
            timeline_entries=timeline_context["priority_entries"],
            foreshadowing_entries=foreshadowing_context["due_entries"],
            chapter_mission=chapter_mission
        )

        brief_text = self._format_story_brief_text(
            chapter_number=chapter_number,
            layered_context=layered_context,
            outline=outline,
            creative_profile=creative_profile,
            active_plot_lines=active_plot_lines,
            due_plot_nodes=due_plot_nodes,
            upcoming_plot_nodes=upcoming_plot_nodes,
            timeline_context=timeline_context,
            foreshadowing_context=foreshadowing_context,
            recent_chapters=recent_chapters,
            retrieval_cards=retrieved_memory,
            recommendations=recommendations,
            additional_context=additional_context
        )

        return {
            "chapter_number": chapter_number,
            "novel_id": self.novel_id,
            "brief_text": brief_text,
            "outline": outline,
            "creative_profile": creative_profile,
            "recent_chapters": recent_chapters,
            "active_plot_lines": active_plot_lines,
            "due_plot_nodes": due_plot_nodes,
            "upcoming_plot_nodes": upcoming_plot_nodes,
            "timeline_entries": timeline_context["entries"],
            "priority_timeline_entries": timeline_context["priority_entries"],
            "foreshadowing_entries": foreshadowing_context["entries"],
            "due_foreshadowing_entries": foreshadowing_context["due_entries"],
            "retrieved_memory": retrieved_memory,
            "retrieval_queries": retrieval_queries,
            "chapter_mission": chapter_mission,
            "prewrite_recommendations": recommendations,
            "layered_context": layered_context,
        }
    
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

    async def _get_recent_chapter_cards(
        self,
        current_chapter_num: int,
        limit: int = 5
    ) -> List[Dict[str, Any]]:
        result = await self.db.execute(
            select(Chapter)
            .where(
                Chapter.novel_id == self.novel_id,
                Chapter.chapter_number < current_chapter_num,
                Chapter.status == "completed"
            )
            .order_by(Chapter.chapter_number.desc())
            .limit(limit)
        )
        chapters = list(result.scalars().all())
        cards: List[Dict[str, Any]] = []
        for chapter in reversed(chapters):
            cards.append({
                "id": chapter.id,
                "chapter_number": chapter.chapter_number,
                "title": chapter.title,
                "summary": chapter.summary or (chapter.content or "")[:180],
            })
        return cards

    async def _get_outline_summary(self) -> Dict[str, Any]:
        result = await self.db.execute(
            select(PlotOutline).where(PlotOutline.novel_id == self.novel_id)
        )
        outline = result.scalar_one_or_none()
        if not outline:
            return {}
        return {
            "premise": outline.premise,
            "theme": outline.theme,
            "beginning": outline.beginning,
            "middle": outline.middle,
            "climax": outline.climax,
            "ending": outline.ending,
            "current_chapter": outline.current_chapter,
            "total_chapters": outline.total_chapters,
        }

    async def _get_creative_profile_summary(self) -> Dict[str, Any]:
        result = await self.db.execute(
            select(NovelCreativeProfile).where(NovelCreativeProfile.novel_id == self.novel_id)
        )
        profile = result.scalar_one_or_none()
        if not profile:
            return {}
        extra_metadata = profile.extra_metadata or {}
        return {
            "author_intent": profile.author_intent,
            "preferred_tone": profile.preferred_tone,
            "scene_planning_notes": profile.scene_planning_notes,
            "must_keep": profile.must_keep or [],
            "must_avoid": profile.must_avoid or [],
            "long_term_goals": profile.long_term_goals or [],
            "llm_brief": extra_metadata.get("llm_brief", ""),
        }

    async def _get_active_plot_lines(self, chapter_number: int) -> List[Dict[str, Any]]:
        result = await self.db.execute(
            select(PlotLine)
            .where(PlotLine.novel_id == self.novel_id, PlotLine.status == "active")
            .order_by(PlotLine.importance.desc(), PlotLine.updated_at.desc())
            .limit(6)
        )
        lines = list(result.scalars().all())
        return [
            {
                "id": line.id,
                "name": line.name,
                "description": line.description,
                "line_type": line.line_type,
                "importance": line.importance,
                "start_chapter": line.start_chapter,
                "end_chapter": line.end_chapter,
                "is_current_window": (
                    (line.start_chapter is None or line.start_chapter <= chapter_number)
                    and (line.end_chapter is None or line.end_chapter >= chapter_number)
                )
            }
            for line in lines
        ]

    async def _get_plot_nodes_for_brief(
        self,
        chapter_number: int
    ) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]]]:
        result = await self.db.execute(
            select(PlotNode)
            .where(
                PlotNode.novel_id == self.novel_id,
                PlotNode.status.in_([
                    PlotNodeStatus.PLANNED.value,
                    PlotNodeStatus.IN_PROGRESS.value
                ])
            )
            .order_by(case((PlotNode.chapter_number.is_(None), 1), else_=0), PlotNode.chapter_number.asc(), PlotNode.sequence.asc())
            .limit(12)
        )
        nodes = list(result.scalars().all())
        due_nodes: List[Dict[str, Any]] = []
        upcoming_nodes: List[Dict[str, Any]] = []
        for node in nodes:
            item = {
                "id": node.id,
                "title": node.title,
                "description": node.description,
                "chapter_number": node.chapter_number,
                "status": node.status,
                "notes": node.notes,
                "priority_hint": "current"
                if node.chapter_number is not None and node.chapter_number <= chapter_number
                else "next"
            }
            if node.chapter_number is not None and node.chapter_number <= chapter_number + 1:
                due_nodes.append(item)
            else:
                upcoming_nodes.append(item)
        return due_nodes[:5], upcoming_nodes[:5]

    async def _get_timeline_entries_for_brief(self, chapter_number: int) -> Dict[str, Any]:
        result = await self.db.execute(
            select(TimelineEntry)
            .where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.category.in_([
                    TimelineEntryCategory.CHAPTER_PLAN.value,
                    TimelineEntryCategory.USER_DIRECTIVE.value,
                    TimelineEntryCategory.PLOT_NODE.value,
                ]),
                TimelineEntry.status.in_([
                    TimelineEntryStatus.PENDING.value,
                    TimelineEntryStatus.ACTIVE.value,
                    TimelineEntryStatus.DEFERRED.value,
                ])
            )
            .order_by(case((TimelineEntry.target_chapter.is_(None), 1), else_=0), TimelineEntry.target_chapter.asc(), TimelineEntry.importance.desc())
            .limit(12)
        )
        entries = list(result.scalars().all())
        serialized = []
        priority_entries = []
        for entry in entries:
            item = {
                "id": entry.id,
                "category": entry.category,
                "title": entry.title,
                "description": entry.description,
                "target_chapter": entry.target_chapter,
                "importance": entry.importance,
                "status": entry.status,
                "time_horizon": entry.time_horizon,
            }
            serialized.append(item)
            if entry.target_chapter is None or entry.target_chapter <= chapter_number + 1:
                priority_entries.append(item)
        return {
            "entries": serialized,
            "priority_entries": priority_entries[:6],
        }

    async def _get_foreshadowing_entries_for_brief(self, chapter_number: int) -> Dict[str, Any]:
        result = await self.db.execute(
            select(TimelineEntry)
            .where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value,
                TimelineEntry.status.in_([
                    TimelineEntryStatus.PENDING.value,
                    TimelineEntryStatus.ACTIVE.value,
                    TimelineEntryStatus.DEFERRED.value,
                ])
            )
            .order_by(TimelineEntry.importance.desc(), TimelineEntry.created_at.asc())
            .limit(12)
        )
        entries = list(result.scalars().all())
        source_chapter_ids = [
            entry.source_chapter_id for entry in entries
            if entry.source_chapter_id is not None
        ]
        source_chapter_map: Dict[int, int] = {}
        if source_chapter_ids:
            chapter_result = await self.db.execute(
                select(Chapter.id, Chapter.chapter_number)
                .where(Chapter.id.in_(source_chapter_ids))
            )
            source_chapter_map = {
                int(chapter_id): int(chapter_number)
                for chapter_id, chapter_number in chapter_result.all()
                if chapter_id is not None and chapter_number is not None
            }
        serialized = []
        due_entries = []
        for entry in entries:
            source_chapter_num = source_chapter_map.get(entry.source_chapter_id or 0)
            item = {
                "id": entry.id,
                "title": entry.title,
                "description": entry.description,
                "importance": entry.importance,
                "status": entry.status,
                "target_chapter": entry.target_chapter,
                "source_chapter_number": source_chapter_num,
            }
            serialized.append(item)
            overdue_by_age = (
                source_chapter_num is not None
                and chapter_number - source_chapter_num >= 3
            )
            due_by_target = entry.target_chapter is not None and entry.target_chapter <= chapter_number + 1
            if overdue_by_age or due_by_target:
                due_entries.append(item)
        return {
            "entries": serialized,
            "due_entries": due_entries[:6],
        }

    def _build_memory_queries(
        self,
        *,
        chapter_number: int,
        due_plot_nodes: List[Dict[str, Any]],
        timeline_entries: List[Dict[str, Any]],
        foreshadowing_entries: List[Dict[str, Any]],
        additional_context: Dict[str, Any]
    ) -> List[str]:
        queries: List[str] = []
        explicit_query = str(additional_context.get("memory_query", "")).strip()
        if explicit_query:
            queries.append(explicit_query)
        for node in due_plot_nodes[:2]:
            title = str(node.get("title", "")).strip()
            if title:
                queries.append(f"{title} 前文铺垫与相关场景")
        for item in foreshadowing_entries[:2]:
            title = str(item.get("title", "")).strip()
            if title:
                queries.append(f"{title} 埋设与回收线索")
        for item in timeline_entries[:1]:
            title = str(item.get("title", "")).strip()
            if title:
                queries.append(f"{title} 相关章节内容")

        unique_queries: List[str] = []
        seen: set[str] = set()
        for query in queries:
            normalized = query.strip()
            if normalized and normalized not in seen:
                unique_queries.append(normalized)
                seen.add(normalized)
        if not unique_queries:
            unique_queries.append(f"第{chapter_number}章最近关键事件")
        return unique_queries[:4]

    async def _retrieve_memory_cards(
        self,
        queries: List[str],
        top_k: int = 3
    ) -> List[Dict[str, Any]]:
        cards: List[Dict[str, Any]] = []
        seen_ids: set[str] = set()
        for query in queries:
            hits = await self.search_relevant_context(query=query, top_k=top_k, min_relevance_score=0.35)
            for hit in hits:
                chunk_id = str(hit.get("chunk_id", ""))
                if not chunk_id or chunk_id in seen_ids:
                    continue
                seen_ids.add(chunk_id)
                cards.append({
                    "query": query,
                    "chunk_id": chunk_id,
                    "content": hit.get("content", ""),
                    "source_type": hit.get("source_type", "content"),
                    "source_id": hit.get("source_id"),
                    "relevance_score": hit.get("relevance_score", 0),
                })
        return cards[:8]

    def _build_prewrite_recommendations(
        self,
        *,
        chapter_number: int,
        due_plot_nodes: List[Dict[str, Any]],
        timeline_entries: List[Dict[str, Any]],
        foreshadowing_entries: List[Dict[str, Any]],
        chapter_mission: Dict[str, Any]
    ) -> List[str]:
        recommendations: List[str] = []
        if foreshadowing_entries:
            top = foreshadowing_entries[0]
            recommendations.append(
                f"优先判断本章是否该回收伏笔“{top['title']}”，避免长期悬置。"
            )
        if due_plot_nodes:
            top = due_plot_nodes[0]
            recommendations.append(
                f"本章至少推进一个 Plot 节点，首选“{top['title']}”。"
            )
        if timeline_entries:
            top = timeline_entries[0]
            recommendations.append(
                f"本章要遵守 Timeline 安排“{top['title']}”，它属于章节安排/用户指令，不等同于伏笔。"
            )
        if chapter_mission.get("should_introduce_new_foreshadowing"):
            recommendations.append(
                "若本章没有自然的旧伏笔回收点，可顺势埋下一个与当前主线直接相关的新伏笔。"
            )
        if not recommendations:
            recommendations.append(
                f"第{chapter_number}章以稳态推进为主：承接前文、推进当前弧线，并避免无目的扩写。"
            )
        return recommendations[:5]

    def _format_story_brief_text(
        self,
        *,
        chapter_number: int,
        layered_context: Dict[str, Any],
        outline: Dict[str, Any],
        creative_profile: Dict[str, Any],
        active_plot_lines: List[Dict[str, Any]],
        due_plot_nodes: List[Dict[str, Any]],
        upcoming_plot_nodes: List[Dict[str, Any]],
        timeline_context: Dict[str, Any],
        foreshadowing_context: Dict[str, Any],
        recent_chapters: List[Dict[str, Any]],
        retrieval_cards: List[Dict[str, Any]],
        recommendations: List[str],
        additional_context: Dict[str, Any]
    ) -> str:
        parts: List[str] = [f"【StoryBrief｜第{chapter_number}章写前认知】"]

        if creative_profile:
            parts.append("\n【长期创作规则】")
            llm_brief = creative_profile.get("llm_brief")
            if llm_brief:
                parts.append(llm_brief)
            else:
                if creative_profile.get("author_intent"):
                    parts.append(f"- 本书意图：{creative_profile['author_intent']}")
                if creative_profile.get("preferred_tone"):
                    parts.append(f"- 默认语气：{creative_profile['preferred_tone']}")
                for item in creative_profile.get("must_keep", [])[:6]:
                    parts.append(f"- 必须保留：{item}")
                for item in creative_profile.get("must_avoid", [])[:6]:
                    parts.append(f"- 必须避免：{item}")

        if outline:
            parts.append("\n【整体方向】")
            for label, key in (
                ("故事前提", "premise"),
                ("主题", "theme"),
                ("中段方向", "middle"),
                ("高潮目标", "climax"),
                ("结局方向", "ending"),
            ):
                value = str(outline.get(key, "") or "").strip()
                if value:
                    parts.append(f"- {label}：{value}")

        if recent_chapters:
            parts.append("\n【最近章节记忆】")
            for chapter in recent_chapters[-3:]:
                parts.append(
                    f"- 第{chapter['chapter_number']}章：{str(chapter.get('summary', '')).strip()[:120]}"
                )

        if active_plot_lines:
            parts.append("\n【Plot｜宏观情节骨架】")
            parts.append("说明：Plot 是故事结构主线/支线，要回答“本章推进哪条线、推进到哪一步”。")
            for item in active_plot_lines[:4]:
                parts.append(
                    f"- {item['name']}（{item['line_type']}，重要度{item['importance']}）：{str(item.get('description', '')).strip()[:100]}"
                )
        if due_plot_nodes:
            parts.append("\n【Plot Nodes｜本章应优先推进】")
            for item in due_plot_nodes[:4]:
                chapter_hint = f" 预定章节:{item['chapter_number']}" if item.get("chapter_number") else ""
                parts.append(
                    f"- {item['title']}（状态:{item['status']}{chapter_hint}）：{str(item.get('description', '')).strip()[:100]}"
                )
        elif upcoming_plot_nodes:
            parts.append("\n【Plot Nodes｜后续可推进】")
            for item in upcoming_plot_nodes[:3]:
                parts.append(
                    f"- {item['title']}：{str(item.get('description', '')).strip()[:100]}"
                )

        if timeline_context["priority_entries"] or timeline_context["entries"]:
            parts.append("\n【Timeline｜章节安排/用户指令/里程碑】")
            parts.append("说明：Timeline 是安排和约束，不等同于伏笔；它回答“本章近期必须记得做什么”。")
            for item in (timeline_context["priority_entries"] or timeline_context["entries"])[:4]:
                target = f"，目标章:{item['target_chapter']}" if item.get("target_chapter") else ""
                parts.append(
                    f"- [{item['category']}] {item['title']}（重要度{item['importance']}{target}）：{str(item.get('description', '')).strip()[:100]}"
                )

        if foreshadowing_context["entries"]:
            parts.append("\n【Foreshadowing｜伏笔与回收】")
            parts.append("说明：Foreshadowing 是已埋下但尚未回收的钩子，回答“这章要不要收一条，或顺势再埋一条”。")
            for item in foreshadowing_context["entries"][:5]:
                due_flag = "，建议本章处理" if any(d["id"] == item["id"] for d in foreshadowing_context["due_entries"]) else ""
                source = f"，来源第{item['source_chapter_number']}章" if item.get("source_chapter_number") else ""
                parts.append(
                    f"- {item['title']}（重要度{item['importance']}{source}{due_flag}）：{str(item.get('description', '')).strip()[:100]}"
                )

        if retrieval_cards:
            parts.append("\n【按需检索到的记忆片段】")
            for item in retrieval_cards[:5]:
                parts.append(
                    f"- 查询“{item['query']}”命中[{item['source_type']}]：{str(item['content']).strip()[:120]}"
                )

        if layered_context.get("characters"):
            parts.append("\n【角色速览】")
            for char in layered_context["characters"][:6]:
                name = char.get("name", "未知")
                personality = char.get("personality") or {}
                traits = personality.get("traits", []) if isinstance(personality, dict) else []
                trait_text = f"（{', '.join(map(str, traits[:3]))}）" if traits else ""
                parts.append(f"- {name}{trait_text}")

        if recommendations:
            parts.append("\n【写前检查清单】")
            for item in recommendations:
                parts.append(f"- {item}")

        user_mission = str(additional_context.get("user_prompt", "") or "").strip()
        if user_mission:
            parts.append("\n【本轮用户明确要求】")
            parts.append(user_mission)

        return "\n".join(parts)
    
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
    
    async def _get_plot_events_context(self) -> Optional[str]:
        """获取情节事件上下文（原始版本）"""
        result = await self.db.execute(
            select(PlotEvent).where(
                PlotEvent.novel_id == self.novel_id
            ).order_by(PlotEvent.timeline).limit(10)
        )
        events = result.scalars().all()
        
        if not events:
            return None
        
        event_info = []
        for event in events:
            info = f"- [{event.event_type or '事件'}] {event.description}"
            event_info.append(info)
        
        return "\n".join(event_info)
    
    async def _get_plot_events_context_sorted(self) -> Optional[str]:
        """
        获取情节事件上下文（排序版本）
        按timeline和ID排序以确保缓存键稳定
        """
        result = await self.db.execute(
            select(PlotEvent).where(
                PlotEvent.novel_id == self.novel_id
            ).order_by(PlotEvent.timeline.asc(), PlotEvent.id.asc()).limit(10)
        )
        events = result.scalars().all()
        
        if not events:
            return None
        
        event_info = []
        for event in events:
            info = f"- [{event.event_type or '事件'}] {event.description}"
            event_info.append(info)
        
        return "\n".join(event_info)
    
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
        """获取情节提示"""
        result = await self.db.execute(
            select(PlotEvent).where(
                PlotEvent.novel_id == self.novel_id
            ).order_by(PlotEvent.created_at.desc()).limit(5)
        )
        events = result.scalars().all()
        
        return [
            {
                "id": event.id,
                "type": event.event_type,
                "description": event.description,
                "chapter_id": event.chapter_id
            }
            for event in events
        ]
    
    async def _get_timeline_context(self, current_chapter: Optional[int] = None) -> Optional[str]:
        try:
            from app.timeline.service import TimelineService
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
            from app.characters.service import CharacterService

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
            from app.characters.service import CharacterService

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
