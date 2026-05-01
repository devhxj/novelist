"""
一致性检查服务 - 检查角色、情节、时间线一致性
"""
import logging
import uuid
import time
import json
import asyncio
from typing import Dict, Any, List, Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from sqlalchemy.orm import selectinload
from datetime import datetime, timezone

from app.novels.models import Novel
from app.chapters.models import Chapter
from app.characters.models import Character
from app.timeline.models import TimelineEntry, TimelineEntryCategory, TimelineEntryStatus
from app.consistency.schemas import ConsistencyIssue
from app.core.llm_service import LLMService

logger = logging.getLogger(__name__)


class ConsistencyChecker:
    """一致性检查服务"""
    
    def __init__(self, db: AsyncSession, novel_id: int):
        self.db = db
        self.novel_id = novel_id
        self.novel = None
        self.llm_service = LLMService()
    
    async def _init_novel(self):
        """初始化小说对象"""
        if self.novel is None:
            result = await self.db.execute(
                select(Novel).where(Novel.id == self.novel_id)
            )
            self.novel = result.scalar_one_or_none()
    
    async def _call_llm_with_retry(
        self,
        prompt: str,
        max_retries: int = 3,
        delay: float = 1.0
    ) -> Optional[Dict[str, Any]]:
        """
        带重试机制的LLM调用
        
        Args:
            prompt: 提示词
            max_retries: 最大重试次数
            delay: 重试延迟
            
        Returns:
            解析后的JSON数据，失败返回None
        """
        for attempt in range(max_retries):
            try:
                result = await self.llm_service.generate_text(prompt)
                
                try:
                    data = json.loads(result)
                    return data
                except json.JSONDecodeError:
                    logger.warning(f"Attempt {attempt + 1}: Failed to parse JSON from LLM response")
                    if attempt < max_retries - 1:
                        await asyncio.sleep(delay * (attempt + 1))
                    continue
                    
            except Exception as e:
                logger.error(f"Attempt {attempt + 1}: LLM call failed: {e}")
                if attempt < max_retries - 1:
                    await asyncio.sleep(delay * (attempt + 1))
                continue
        
        logger.error(f"LLM call failed after {max_retries} retries")
        return None
    
    async def check_all(
        self,
        chapter_ids: Optional[List[int]] = None,
        check_types: Optional[List[str]] = None
    ) -> Dict[str, Any]:
        """
        执行全部一致性检查
        
        Args:
            chapter_ids: 指定检查的章节ID列表
            check_types: 检查类型列表 ['character', 'plot', 'timeline', 'foreshadowing']
            
        Returns:
            检查结果
        """
        await self._init_novel()
        
        start_time = time.time()
        check_id = f"check_{uuid.uuid4().hex[:12]}"
        
        if check_types is None:
            check_types = ["character", "plot", "timeline", "foreshadowing"]
        
        all_issues: List[ConsistencyIssue] = []
        
        chapters = await self._get_chapters(chapter_ids)
        
        if "character" in check_types:
            character_issues = await self.check_character_consistency(chapters)
            all_issues.extend(character_issues)
        
        if "plot" in check_types:
            plot_issues = await self.check_plot_consistency(chapters)
            all_issues.extend(plot_issues)
        
        if "foreshadowing" in check_types:
            foreshadowing_issues = await self.check_foreshadowing_status()
            all_issues.extend(foreshadowing_issues)
        
        check_time = time.time() - start_time
        
        summary = self._generate_summary(all_issues)
        
        return {
            "check_id": check_id,
            "novel_id": self.novel_id,
            "status": "completed",
            "issues": [issue.model_dump() for issue in all_issues],
            "summary": summary,
            "check_time": round(check_time, 2)
        }

    def _issue(
        self,
        issue_type: str,
        severity: str,
        description: str,
        chapter_id: Optional[Any] = None,
        chapter_number: Optional[Any] = None,
        details: Optional[Dict[str, Any]] = None,
        suggestion: Optional[str] = None
    ) -> Optional[ConsistencyIssue]:
        """统一构造一致性问题，避免脏数据导致 Pydantic 实例化失败"""
        payload = {
            "issue_type": str(issue_type or "").strip() or "unknown",
            "severity": str(severity or "").strip() or "info",
            "chapter_id": self._coerce_optional_int(chapter_id),
            "chapter_number": self._coerce_optional_int(chapter_number),
            "description": str(description or "").strip(),
            "details": details or None,
            "suggestion": str(suggestion).strip() if suggestion is not None else None,
        }
        if not payload["description"]:
            logger.warning("Skip empty consistency issue payload: %s", payload)
            return None
        try:
            return ConsistencyIssue.model_validate(payload)
        except Exception as e:
            logger.warning("Failed to build ConsistencyIssue: %s, payload=%s", e, payload)
            return None

    def _coerce_optional_int(self, value: Optional[Any]) -> Optional[int]:
        if value is None or value == "":
            return None
        try:
            return int(value)
        except (TypeError, ValueError):
            return None
    
    async def _get_chapters(self, chapter_ids: Optional[List[int]] = None) -> List[Chapter]:
        """获取要检查的章节"""
        query = select(Chapter).where(
            Chapter.novel_id == self.novel_id,
            Chapter.status == "completed"
        )
        
        if chapter_ids:
            query = query.where(Chapter.id.in_(chapter_ids))
        
        query = query.order_by(Chapter.chapter_number)
        result = await self.db.execute(query)
        return result.scalars().all()
    
    async def check_character_consistency(self, chapters: List[Chapter]) -> List[ConsistencyIssue]:
        """
        检查角色一致性
        
        检查内容:
        - 角色性格前后是否一致
        - 角色能力是否突然变化
        - 角色关系是否矛盾
        """
        issues: List[ConsistencyIssue] = []
        
        if not chapters:
            return issues
        
        result = await self.db.execute(
            select(Character).where(Character.novel_id == self.novel_id)
        )
        characters = result.scalars().all()
        
        if not characters:
            return issues
        
        character_info = "\n".join([
            f"- {char.name}: 性格={char.personality}, 能力={char.abilities}"
            for char in characters
        ])
        
        chapter_contents = []
        for chapter in chapters[-5:]:
            if chapter.content:
                chapter_contents.append(f"第{chapter.chapter_number}章:\n{chapter.content[:2000]}")
        
        if not chapter_contents:
            return issues
        
        prompt = f"""请检查以下章节内容中角色的一致性问题。

角色设定:
{character_info}

章节内容:
{chr(10).join(chapter_contents)}

请检查:
1. 角色性格是否前后矛盾
2. 角色能力是否突然变化且无解释
3. 角色关系是否有矛盾

以JSON格式返回问题列表，格式如下:
{{
    "issues": [
        {{
            "chapter_number": 章节号,
            "character_name": "角色名",
            "issue_type": "personality/ability/relationship",
            "description": "问题描述",
            "suggestion": "修改建议"
        }}
    ]
}}

如果没有问题，返回 {{"issues": []}}
只返回JSON，不要其他内容。"""

        try:
            data = await self._call_llm_with_retry(prompt)
            
            if data:
                for item in data.get("issues", []):
                    issue = self._issue(
                        issue_type="character",
                        severity="warning",
                        chapter_number=item.get("chapter_number"),
                        description=f"角色'{item.get('character_name')}'一致性问题: {item.get('description')}",
                        details={
                            "character_name": item.get("character_name"),
                            "issue_type": item.get("issue_type")
                        },
                        suggestion=item.get("suggestion")
                    )
                    if issue:
                        issues.append(issue)
                
        except Exception as e:
            logger.error(f"Character consistency check failed: {e}")
        
        return issues
    
    async def check_plot_consistency(self, chapters: List[Chapter]) -> List[ConsistencyIssue]:
        """
        检查情节一致性

        检查内容:
        - 情节发展是否合理
        - 是否有逻辑漏洞
        - 事件因果关系是否清晰
        """
        issues: List[ConsistencyIssue] = []

        if len(chapters) < 2:
            return issues

        recent_chapters = chapters[-3:]
        chapter_contents = []
        for chapter in recent_chapters:
            if chapter.content:
                chapter_contents.append(f"第{chapter.chapter_number}章:\n{chapter.content[:1500]}")

        if not chapter_contents:
            return issues

        prompt = f"""请检查以下章节内容的情节一致性问题。

近期章节内容:
{chr(10).join(chapter_contents)}

请检查:
1. 情节发展是否有逻辑漏洞
2. 事件因果关系是否合理
3. 是否有前后矛盾的情节

以JSON格式返回问题列表，格式如下:
{{
    "issues": [
        {{
            "chapter_number": 章节号,
            "issue_type": "logic/contradiction/causality",
            "description": "问题描述",
            "suggestion": "修改建议"
        }}
    ]
}}

如果没有问题，返回 {{"issues": []}}
只返回JSON，不要其他内容。"""

        try:
            data = await self._call_llm_with_retry(prompt)

            if data:
                for item in data.get("issues", []):
                    issue = self._issue(
                        issue_type="plot",
                        severity="warning",
                        chapter_number=item.get("chapter_number"),
                        description=f"情节一致性问题: {item.get('description')}",
                        details={
                            "issue_type": item.get("issue_type")
                        },
                        suggestion=item.get("suggestion")
                    )
                    if issue:
                        issues.append(issue)

        except Exception as e:
            logger.error(f"Plot consistency check failed: {e}")

        return issues

    async def check_foreshadowing_status(self) -> List[ConsistencyIssue]:
        """
        检查伏笔状态（通过时间线系统查询）

        检查内容:
        - 未解决的伏笔
        - 长期未填的坑
        """
        issues: List[ConsistencyIssue] = []

        result = await self.db.execute(
            select(TimelineEntry).where(
                TimelineEntry.novel_id == self.novel_id,
                TimelineEntry.category == TimelineEntryCategory.FORESHADOWING.value,
                TimelineEntry.status.in_([
                    TimelineEntryStatus.PENDING.value,
                    TimelineEntryStatus.ACTIVE.value,
                ])
            )
        )
        unresolved_entries = result.scalars().all()

        for entry in unresolved_entries:
            days_pending = (datetime.now(timezone.utc) - entry.created_at.replace(tzinfo=timezone.utc)).days if entry.created_at else 0

            severity = "info"
            if entry.importance >= 4 and days_pending > 60:
                severity = "error"
            elif entry.importance >= 4 and days_pending > 30:
                severity = "warning"

            issue = self._issue(
                issue_type="foreshadowing",
                severity=severity,
                chapter_id=entry.source_chapter_id,
                description=f"未解决的伏笔/钩子: {entry.title}",
                details={
                    "timeline_entry_id": entry.id,
                    "importance": entry.importance,
                    "days_pending": days_pending,
                    "description": entry.description
                },
                suggestion=f"建议在后续章节中解决此伏笔（可通过 get_timeline_context 或 update_timeline_entry(status=resolved) 处理）" if entry.importance >= 3 else None
            )
            if issue:
                issues.append(issue)

        return issues
    
    def _generate_summary(self, issues: List[ConsistencyIssue]) -> Dict[str, Any]:
        """生成检查摘要"""
        summary = {
            "total_issues": len(issues),
            "by_severity": {
                "error": 0,
                "warning": 0,
                "info": 0
            },
            "by_type": {
                "character": 0,
                "plot": 0,
                "timeline": 0,
                "foreshadowing": 0
            }
        }
        
        for issue in issues:
            summary["by_severity"][issue.severity] = summary["by_severity"].get(issue.severity, 0) + 1
            summary["by_type"][issue.issue_type] = summary["by_type"].get(issue.issue_type, 0) + 1
        
        return summary
