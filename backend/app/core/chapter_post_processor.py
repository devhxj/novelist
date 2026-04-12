"""
章节后处理流水线
在章节正文生成完成后执行：
1. 结尾完整性检测与补全
2. 结构化信息解析（未来规划、伏笔/钩子）
3. 时间线条目自动入库（Phase 2 接入）

说明：
- 这是“直接生成链路”的后端兜底，不取代 AI IDE 对话中模型主动调用时间线 MCP 工具
- 目标是避免模型漏记伏笔/计划，保持时间线长期可维护
"""
import re
import logging
import json
from typing import Optional, Dict, Any, List

from app.core.llm_service import llm_service

logger = logging.getLogger(__name__)

VALID_ENDING_CHARS = set("。！？…」』》\"'")
INCOMPLETE_PUNCTUATION = set("，、；：—·")


def is_ending_complete(text: str) -> bool:
    if not text or len(text.strip()) < 10:
        return False
    stripped = text.rstrip()
    last_char = stripped[-1] if stripped else ""
    if last_char in INCOMPLETE_PUNCTUATION:
        return False
    if last_char in VALID_ENDING_CHARS:
        last_paragraph = _get_last_paragraph(stripped)
        if len(last_paragraph) >= 10:
            return True
    return False


def _get_last_paragraph(text: str) -> str:
    paragraphs = text.rstrip().split("\n")
    for p in reversed(paragraphs):
        if p.strip():
            return p.strip()
    return ""


async def complete_ending(text: str, model: Optional[str] = None) -> str:
    prompt = (
        f"以下是一段小说正文的末尾，它似乎在句子中间被截断了。"
        f"请补全最后一句话，使其自然收尾。\n\n"
        f"原文末尾（约最后200字）：\n{text[-800:]}\n\n"
        f"要求：\n"
        f"- 只输出补全的部分，不要重复原文\n"
        f"- 保持原文风格和语气\n"
        f"- 以合适的标点符号结尾（句号/感叹号/问号）\n"
        f"- 补全内容控制在50-200字以内\n"
        f"直接输出补全文本，不要加任何前缀说明。"
    )
    try:
        completion = ""
        async for chunk in llm_service.generate_stream(
            prompt=prompt,
            system_prompt="你是一个专业的小说编辑助手，擅长自然地补全被截断的文本。",
            model=model,
            max_tokens=200,
        ):
            completion += chunk
        if completion.strip():
            return text + "\n" + completion.strip()
    except Exception as exc:
        logger.warning(f"Failed to complete ending: {exc}")
    return text


class ChapterPostProcessor:
    def __init__(self, db, novel_id: int):
        self.db = db
        self.novel_id = novel_id

    async def process(self, content: str, chapter_number: int, chapter_id: int,
                      model: Optional[str] = None) -> Dict[str, Any]:
        result: Dict[str, Any] = {
            "original_content": content,
            "final_content": content,
            "was_truncated": False,
            "ending_completed": False,
            "has_ending_marker": False,
            "structured_info": None,
            "timeline_entries_created": 0,
            "resolved_foreshadowing_ids": [],
        }
        processed = content
        if not is_ending_complete(processed):
            logger.info(f"Chapter {chapter_number} ending appears incomplete, attempting completion")
            processed = await complete_ending(processed, model)
            result["was_truncated"] = True
            result["ending_completed"] = processed != content
            result["final_content"] = processed
        marker_match = re.search(r'---【第\d+章完结】---', processed)
        structured_info = self._extract_structured_info(content)
        if not structured_info:
            structured_info = await self._analyze_structured_info_with_llm(
                content=processed,
                chapter_number=chapter_number,
                chapter_id=chapter_id
            )
        result["structured_info"] = structured_info
        if marker_match:
            result["has_ending_marker"] = True
            processed = re.sub(r'\n*---【第\d+章完结】---\n*', '', processed).strip()
            result["final_content"] = processed

        if structured_info:
            timeline_result = await self._sync_timeline_entries(
                chapter_number=chapter_number,
                chapter_id=chapter_id,
                chapter_content=processed,
                structured_info=structured_info,
            )
            result["timeline_entries_created"] = timeline_result["entries_created"]
            result["resolved_foreshadowing_ids"] = timeline_result["resolved_foreshadowing_ids"]
        return result

    def _extract_structured_info(self, content: str) -> Optional[Dict[str, Any]]:
        marker_pattern = r'---【第\d+章完结】---\s*\n?(.*?)(?=\n---|\Z)'
        match = re.search(marker_pattern, content, re.DOTALL)
        if not match:
            return None
        info_text = match.group(1).strip()
        parsed: Dict[str, Any] = {
            "foreshadowing_items": [],
            "next_chapter_plan": None,
            "near_term_plans": [],
            "long_term_direction": None,
        }
        foreshadowing_match = re.search(
            r'【本章埋下的伏笔[\/\\s]*钩子】.*?\n((?:-.*\n?)*)',
            info_text, re.DOTALL | re.IGNORECASE
        )
        if foreshadowing_match:
            for line in foreshadowing_match.group(1).split("\n"):
                line = line.strip().lstrip("- ").strip()
                if line:
                    parsed["foreshadowing_items"].append(line)
        next_chapter_match = re.search(
            r'【下章安排】.*?\n((?:-.*\n?)*)',
            info_text, re.DOTALL | re.IGNORECASE
        )
        if next_chapter_match:
            lines = [l.strip().lstrip("- ").strip() for l in next_chapter_match.group(1).split("\n") if l.strip()]
            if lines:
                parsed["next_chapter_plan"] = "\n".join(lines)
        near_term_match = re.search(
            r'【近期规划】.*?\n((?:-.*\n?)*)',
            info_text, re.DOTALL | re.IGNORECASE
        )
        if near_term_match:
            for line in near_term_match.group(1).split("\n"):
                line = line.strip().lstrip("- ").strip()
                if line:
                    parsed["near_term_plans"].append(line)
        long_term_match = re.search(
            r'【远期方向】[：:].*?\n(.*)',
            info_text, re.DOTALL | re.IGNORECASE
        )
        if long_term_match:
            direction = long_term_match.group(1).strip().rstrip("-").strip()
            if direction:
                parsed["long_term_direction"] = direction
        return parsed if any(parsed.values()) else None

    async def _analyze_structured_info_with_llm(
        self,
        *,
        content: str,
        chapter_number: int,
        chapter_id: int
    ) -> Optional[Dict[str, Any]]:
        try:
            unresolved_candidates = await self._get_unresolved_foreshadowing_candidates()
            candidate_lines = "\n".join(
                f"- id={item['id']} 标题={item['title']} 描述={item['description']}"
                for item in unresolved_candidates
            ) or "无"
            prompt = f"""请分析以下小说章节，并提取写作记忆信息。

目标章节：第{chapter_number}章

当前未解决伏笔候选：
{candidate_lines}

章节内容：
{content[:6000]}

请输出 JSON，格式如下：
{{
  "foreshadowing_items": ["本章新埋下的伏笔或钩子"],
  "resolved_foreshadowing_ids": [已经在本章得到明确交代的候选伏笔id],
  "next_chapter_plan": "下章最自然的推进方向",
  "near_term_plans": ["近2-4章的安排"],
  "long_term_direction": "更远期方向"
}}

规则：
1. 只有在本章已经明确回收、解释或兑现时，才能把伏笔 id 放进 resolved_foreshadowing_ids。
2. foreshadowing_items 只写真正新埋下且值得追踪的钩子，不要把普通悬念都算进去。
3. 只返回 JSON。"""
            raw = await llm_service.generate_text(
                prompt=prompt,
                system_prompt="你是长篇小说后处理分析器，只输出严格 JSON。",
                max_tokens=600
            )
            data = json.loads(raw)
            if not isinstance(data, dict):
                return None
            normalized = {
                "foreshadowing_items": [
                    str(item).strip()
                    for item in data.get("foreshadowing_items", [])
                    if str(item).strip()
                ],
                "resolved_foreshadowing_ids": [
                    int(item)
                    for item in data.get("resolved_foreshadowing_ids", [])
                    if str(item).isdigit()
                ],
                "next_chapter_plan": str(data.get("next_chapter_plan", "")).strip() or None,
                "near_term_plans": [
                    str(item).strip()
                    for item in data.get("near_term_plans", [])
                    if str(item).strip()
                ],
                "long_term_direction": str(data.get("long_term_direction", "")).strip() or None,
            }
            return normalized if any(normalized.values()) else None
        except Exception as exc:
            logger.warning(f"Failed to analyze chapter structured info: {exc}")
            return None

    async def _get_unresolved_foreshadowing_candidates(self) -> List[Dict[str, Any]]:
        try:
            from app.timeline.models import TimelineEntry, TimelineEntryCategory, TimelineEntryStatus
            from sqlalchemy import select

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
                .limit(8)
            )
            entries = list(result.scalars().all())
            return [
                {
                    "id": entry.id,
                    "title": entry.title,
                    "description": entry.description or "",
                }
                for entry in entries
            ]
        except Exception:
            return []

    async def _sync_timeline_entries(
        self,
        *,
        chapter_number: int,
        chapter_id: int,
        chapter_content: str,
        structured_info: Dict[str, Any]
    ) -> Dict[str, Any]:
        from app.timeline.service import TimelineService
        from app.timeline.schemas import TimelineEntryResolve

        service = TimelineService(self.db, self.novel_id)
        created_entries = await service.auto_extract_from_chapter(
            chapter_content=chapter_content,
            chapter_number=chapter_number,
            chapter_id=chapter_id,
            structured_info=structured_info,
        )

        resolved_ids: List[int] = []
        for entry_id in structured_info.get("resolved_foreshadowing_ids", []):
            try:
                resolved = await service.resolve_entry(
                    entry_id,
                    TimelineEntryResolve(
                        resolved_chapter_id=chapter_id,
                        resolution_notes=f"在第{chapter_number}章中已得到明确交代"
                    )
                )
                if resolved:
                    resolved_ids.append(entry_id)
            except Exception as exc:
                logger.warning(f"Failed to resolve foreshadowing entry {entry_id}: {exc}")

        return {
            "entries_created": len(created_entries),
            "resolved_foreshadowing_ids": resolved_ids,
        }
