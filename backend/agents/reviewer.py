"""
审核Agent - 负责内容审核和一致性检查

支持两层审核：
1. 规则快速初筛（零成本、毫秒级）
2. LLM 语义深审（有成本、秒级）- 可通过 use_llm_review 参数控制
"""
import json
import logging
from datetime import datetime, timezone
from typing import Any

from sqlalchemy import select

from .base import BaseAgent, AgentTask, AgentResult, AgentRole, TaskType, SubAgentSpec
from .registry import register_agent
from core.database import AsyncSessionLocal
from consistency.service import ConsistencyChecker
from timeline.models import TimelineEntry, TimelineEntryStatus
from timeline.schemas import (
    TimelineEntryCreate,
    TimelineEntryCategory as TimelineEntryCategorySchema,
    TimeHorizon as TimeHorizonSchema,
)
from chapters.models import Chapter

logger = logging.getLogger(__name__)

REVIEW_SPEC = SubAgentSpec(
    task_type="review",
    display_name="审核专家",
    description="全量审核章节质量，包含规则初筛、LLM语义深审、角色/情节/时间线一致性检查、伏笔管理",
    system_prompt="你是一位严格的小说编辑审核员，负责全面检查章节质量、角色一致性、情节连贯性和伏笔管理。",
    required_context_keys=["chapter_content", "chapter_info"],
    optional_context_keys=[
        "characters",
        "previous_summary",
        "consistency_result",
        "active_story_arcs",
        "unresolved_foreshadowings",
    ],
    allowed_tools=[
        "get_chapter_content",
        "get_characters",
        "get_timeline",
    ],
    allowed_resources=[
        "novel://{novel_id}/summary",
        "chapter://{chapter_id}",
        "novel://{novel_id}/characters",
    ],
    requires_chapter_id=True,
    result_description="返回审核评分、问题列表、一致性报告和改进建议",
)


@register_agent("review", REVIEW_SPEC)
class ReviewerAgent(BaseAgent):
    """审核Agent - 负责内容审核和一致性检查"""

    def __init__(self, agent_id: str = "reviewer_001"):
        super().__init__(agent_id, AgentRole.REVIEWER)
        self.supported_tasks = {
            TaskType.REVIEW_CHAPTER,
            TaskType.CHECK_CONSISTENCY,
            TaskType.MANAGE_FORESHADOWING
        }

    def can_handle(self, task_type: TaskType) -> bool:
        return task_type in self.supported_tasks

    async def execute(self, task: AgentTask) -> AgentResult:
        """执行审核任务"""
        self.log_task_start(task)

        try:
            if task.task_type == TaskType.REVIEW_CHAPTER:
                result = await self._review_chapter(task)
            elif task.task_type == TaskType.CHECK_CONSISTENCY:
                result = await self._check_consistency(task)
            elif task.task_type == TaskType.MANAGE_FORESHADOWING:
                result = await self._manage_foreshadowing(task)
            else:
                result = self.create_result(
                    task=task,
                    success=False,
                    error=f"Unsupported task type: {task.task_type}"
                )

            self.log_task_complete(result)
            return result

        except Exception as e:
            self.logger.error(f"Error in review task: {e}")
            return self.create_result(
                task=task,
                success=False,
                error=str(e)
            )

    async def _review_chapter(self, task: AgentTask) -> AgentResult:
        """审核章节内容：规则初筛 + LLM 语义深审"""
        context = task.context
        content = context.get("chapter_content", "") or task.parameters.get("content", "")
        use_llm_review = task.parameters.get("use_llm_review", True)

        rule_issues, rule_suggestions = self._rule_based_review(content, context)

        llm_result = None
        if use_llm_review and content.strip():
            llm_result = await self._llm_review(content, context, task)

        all_issues = list(rule_issues)
        all_suggestions = list(rule_suggestions)

        if llm_result:
            all_issues.extend(llm_result.get("issues", []))
            overall = llm_result.get("overall_comment", "")
            if overall:
                all_suggestions.append(overall)

        passed_rule = len([i for i in rule_issues if i.get("severity") == "error"]) == 0
        passed_llm = llm_result.get("passed", True) if llm_result else True
        passed = passed_rule and passed_llm

        rule_score = max(0, 100 - len([i for i in rule_issues if i.get("severity") == "warning"]) * 10 - len([i for i in rule_issues if i.get("severity") == "info"]) * 3)
        llm_scores = llm_result.get("scores", {}) if llm_result else {}
        avg_llm_score = sum(llm_scores.values()) / len(llm_scores) * 10 if llm_scores else rule_score
        final_score = int((rule_score + avg_llm_score) / 2) if llm_scores else rule_score

        result_data = {
            "content_length": len(content),
            "issues_found": len(all_issues),
            "issues": all_issues,
            "passed": passed,
            "approved": passed,
            "score": final_score,
            "rule_score": rule_score,
            "llm_scores": llm_scores,
            "review_method": "rule+llm" if llm_result else "rule_only",
        }

        return self.create_result(
            task=task,
            success=passed,
            result=result_data,
            suggestions=[s for s in all_suggestions if s],
            next_actions=[] if passed else [
                {
                    "type": "create_task",
                    "task_type": TaskType.GENERATE_CHAPTER.value,
                    "chapter_id": task.chapter_id,
                    "parameters": {
                        "revision": True,
                        "issues": all_issues
                    }
                }
            ]
        )

    def _rule_based_review(self, content: str, context: dict) -> tuple[list[dict], list[str]]:
        """规则快速初筛（零成本、毫秒级）"""
        issues = []
        suggestions = []
        paragraphs = [p.strip() for p in content.split("\n\n") if p.strip()]
        lines = [line.strip() for line in content.splitlines() if line.strip()]

        if len(content) < 500:
            issues.append({"type": "length", "severity": "warning", "message": "章节内容过短，建议扩充"})

        if len(paragraphs) < 3:
            issues.append({"type": "structure_sparse", "severity": "warning", "message": "章节段落过少，结构显得单薄"})

        if len(content) > 0 and len(set(content)) / max(len(content), 1) < 0.18:
            issues.append({"type": "repetition", "severity": "warning", "message": "文本重复度偏高，建议压缩重复表达"})

        dialogue_lines = [line for line in lines if any(mark in line for mark in [""", """, "\"", "："])]
        if len(dialogue_lines) == 0 and len(content) > 1200:
            suggestions.append("可适度加入对话，增强场景表现和阅读节奏")

        if paragraphs and any(len(p) > 500 for p in paragraphs):
            suggestions.append("部分段落过长，建议拆分长段以提升可读性")

        return issues, suggestions

    async def _llm_review(self, content: str, context: dict, task: AgentTask) -> dict | None:
        """LLM 语义深审（有成本、秒级）"""
        try:
            from core.llm_service import llm_service
            from context.prompt_templates import REVIEW_SYSTEM_PROMPT, build_review_prompt

            chapter_number = task.parameters.get("chapter_number") or context.get("chapter_number")
            characters = context.get("characters", [])
            previous_summary = context.get("previous_summary", "")
            unresolved_foreshadowings = context.get("unresolved_foreshadowings", [])
            active_story_arcs = context.get("active_story_arcs", [])

            prompt = build_review_prompt(
                content=content[:6000],
                chapter_number=chapter_number,
                characters=characters,
                previous_summary=previous_summary,
                unresolved_foreshadowings=unresolved_foreshadowings,
                active_story_arcs=active_story_arcs,
            )

            return await llm_service.generate_json(
                prompt=prompt,
                system_prompt=REVIEW_SYSTEM_PROMPT,
                temperature=0.3,
                max_tokens=50000,
            )

        except Exception as e:
            self.logger.warning(f"LLM review failed, falling back to rule-only: {e}")
            return None

    @staticmethod
    def _parse_llm_review_response(response: str) -> dict:
        """解析 LLM 审核的 JSON 响应"""
        try:
            start = response.find("{")
            end = response.rfind("}") + 1
            if start >= 0 and end > start:
                return json.loads(response[start:end])
        except json.JSONDecodeError:
            pass
        return {"passed": True, "issues": [], "scores": {}, "overall_comment": ""}

    async def _check_consistency(self, task: AgentTask) -> AgentResult:
        """检查一致性"""
        chapter_id = task.chapter_id
        parameters = task.parameters
        precomputed = task.context.get("consistency_result") or {}
        check_types = parameters.get("check_types", ["character", "plot", "timeline"])

        if precomputed:
            consistency_issues = precomputed.get("issues", [])
            summary = precomputed.get("summary", {})
        else:
            consistency_issues = []
            if "character" in check_types:
                consistency_issues.extend(await self._check_character_consistency(task))
            if "plot" in check_types:
                consistency_issues.extend(await self._check_plot_consistency(task))
            summary = {
                "total_issues": len(consistency_issues)
            }

        passed = not any(issue.get("severity") == "error" for issue in consistency_issues)

        return self.create_result(
            task=task,
            success=passed,
            result={
                "chapter_id": chapter_id,
                "consistency_issues": consistency_issues,
                "checks_performed": check_types,
                "passed": passed,
                "issues": consistency_issues,
                "summary": summary
            }
        )

    async def _manage_foreshadowing(self, task: AgentTask) -> AgentResult:
        """管理伏笔"""
        parameters = task.parameters
        action = parameters.get("action", "list")

        if action == "list":
            foreshadowing = await self._list_foreshadowing(task)
            success = not isinstance(foreshadowing, dict) or not foreshadowing.get("error")
            return self.create_result(
                task=task,
                success=success,
                result={
                    "action": "list",
                    "foreshadowing": foreshadowing
                },
                error=foreshadowing.get("error") if isinstance(foreshadowing, dict) else None
            )
        elif action == "create":
            new_fs = await self._create_foreshadowing(task)
            success = not new_fs.get("error")
            return self.create_result(
                task=task,
                success=success,
                result={
                    "action": "create",
                    "foreshadowing": new_fs
                },
                error=new_fs.get("error")
            )
        elif action == "resolve":
            resolved = await self._resolve_foreshadowing(task)
            success = not resolved.get("error")
            return self.create_result(
                task=task,
                success=success,
                result={
                    "action": "resolve",
                    "foreshadowing": resolved
                },
                error=resolved.get("error")
            )
        else:
            return self.create_result(
                task=task,
                success=False,
                error=f"Unknown foreshadowing action: {action}"
            )

    async def _check_character_consistency(self, task: AgentTask) -> list[dict[str, Any]]:
        """检查角色一致性"""
        async with AsyncSessionLocal() as db:
            checker = ConsistencyChecker(db, task.novel_id)
            chapters = await checker._get_chapters([task.chapter_id] if task.chapter_id else None)
            issues = await checker.check_character_consistency(chapters)
            return [issue.model_dump() for issue in issues]

    async def _check_plot_consistency(self, task: AgentTask) -> list[dict[str, Any]]:
        """检查情节一致性"""
        async with AsyncSessionLocal() as db:
            checker = ConsistencyChecker(db, task.novel_id)
            chapters = await checker._get_chapters([task.chapter_id] if task.chapter_id else None)
            issues = await checker.check_plot_consistency(chapters)
            return [issue.model_dump() for issue in issues]

    async def _list_foreshadowing(self, task: AgentTask) -> list[dict[str, Any]] | dict[str, Any]:
        """列出伏笔（通过时间线系统查询）"""
        parameters = task.parameters
        status = parameters.get("status")
        min_importance = parameters.get("min_importance")
        limit = parameters.get("limit", 20)

        async with AsyncSessionLocal() as db:
            query = select(TimelineEntry).where(
                TimelineEntry.novel_id == task.novel_id,
                TimelineEntry.category == "foreshadowing",
            )
            if status:
                query = query.where(TimelineEntry.status == status)
            if min_importance is not None:
                query = query.where(TimelineEntry.importance >= min_importance)

            query = query.order_by(TimelineEntry.importance.desc(), TimelineEntry.created_at.desc()).limit(limit)
            result = await db.execute(query)
            items = result.scalars().all()

            return [
                {
                    "id": entry.id,
                    "title": entry.title,
                    "description": entry.description,
                    "status": entry.status,
                    "category": entry.category,
                    "importance": entry.importance,
                    "source_chapter_id": entry.source_chapter_id,
                    "resolved_chapter_id": entry.resolved_chapter_id,
                    "detail_json": entry.detail_json,
                    "metadata": entry.extra_metadata,
                    "created_at": entry.created_at.isoformat() if entry.created_at else None,
                    "resolved_at": entry.resolved_at.isoformat() if entry.resolved_at else None
                }
                for entry in items
            ]

    async def _create_foreshadowing(self, task: AgentTask) -> dict[str, Any]:
        """创建伏笔（写入时间线系统）"""
        parameters = task.parameters
        title = parameters.get("title")
        if not title:
            return {"error": "缺少 title，无法创建伏笔"}

        source_chapter_id = parameters.get("created_chapter_id", task.chapter_id)
        async with AsyncSessionLocal() as db:
            if source_chapter_id:
                chapter_result = await db.execute(select(Chapter).where(Chapter.id == source_chapter_id))
                chapter = chapter_result.scalar_one_or_none()
                if not chapter or chapter.novel_id != task.novel_id:
                    return {"error": "source_chapter_id 无效或不属于当前小说"}

            entry_data = TimelineEntryCreate(
                category=TimelineEntryCategorySchema.FORESHADOWING,
                title=title,
                description=parameters.get("description"),
                detail_json={
                    "foreshadowing_type": parameters.get("foreshadowing_type", "other"),
                    "expected_resolution": "",
                },
                target_chapter=None,
                time_horizon=TimeHorizonSchema.UNDEFINED,
                importance=parameters.get("importance", 3),
                source="ai_generated",
                source_chapter_id=source_chapter_id,
            )
            entry = TimelineEntry(
                novel_id=task.novel_id,
                category=entry_data.category.value,
                title=entry_data.title,
                description=entry_data.description,
                detail_json=entry_data.detail_json,
                target_chapter=entry_data.target_chapter,
                time_horizon=entry_data.time_horizon.value if entry_data.time_horizon else None,
                importance=entry_data.importance,
                source=entry_data.source,
                source_chapter_id=entry_data.source_chapter_id,
            )
            db.add(entry)
            await db.commit()
            await db.refresh(entry)

            return {
                "id": entry.id,
                "title": entry.title,
                "status": entry.status,
                "category": entry.category,
                "importance": entry.importance,
                "source_chapter_id": entry.source_chapter_id
            }

    async def _resolve_foreshadowing(self, task: AgentTask) -> dict[str, Any]:
        """解决伏笔（通过时间线系统更新状态）"""
        parameters = task.parameters
        entry_id = parameters.get("foreshadowing_id")
        if not entry_id:
            return {"error": "缺少 foreshadowing_id，无法解决伏笔"}

        resolved_chapter_id = parameters.get("resolved_chapter_id", task.chapter_id)
        async with AsyncSessionLocal() as db:
            result = await db.execute(
                select(TimelineEntry).where(
                    TimelineEntry.id == entry_id,
                    TimelineEntry.novel_id == task.novel_id
                )
            )
            entry = result.scalar_one_or_none()
            if not entry:
                return {"error": "时间线条目不存在"}

            if resolved_chapter_id:
                chapter_result = await db.execute(select(Chapter).where(Chapter.id == resolved_chapter_id))
                chapter = chapter_result.scalar_one_or_none()
                if not chapter or chapter.novel_id != task.novel_id:
                    return {"error": "resolved_chapter_id 无效或不属于当前小说"}

            entry.status = TimelineEntryStatus.RESOLVED.value
            entry.resolved_chapter_id = resolved_chapter_id
            if not entry.detail_json:
                entry.detail_json = {}
            entry.detail_json["resolution_notes"] = parameters.get("resolution_notes")
            entry.resolved_at = datetime.now(timezone.utc)
            await db.commit()
            await db.refresh(entry)

            resolved_at = entry.resolved_at
            return {
                "id": entry.id,
                "title": entry.title,
                "status": entry.status,
                "resolved_chapter_id": entry.resolved_chapter_id,
                "resolution_notes": entry.detail_json.get("resolution_notes") if entry.detail_json else None,
                "resolved_at": resolved_at.isoformat() if resolved_at else None
            }
