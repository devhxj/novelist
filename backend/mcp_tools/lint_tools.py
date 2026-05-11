"""
章节文本机械检查工具 — 纯规则引擎，不调 LLM
供 Review 子 Agent 使用，检查 LLM 不擅长的行级文本质量问题
"""
from __future__ import annotations

import re
from collections import Counter
from typing import Any

from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from chapters.models import Chapter
from editor.models import EditSession, EditSessionStatus


class LintChapterArgs(BaseModel):
    chapter_id: int = Field(description="目标章节ID")
    scope: str = Field(default="full", description="检查范围：full=全部, style=句式结构, repetition=重复检测")


class LintChapterTool(BaseMCPTool):
    """章节文本机械检查 — 重复词、过长句、异常段落等"""

    name = "lint_chapter"
    description = (
        "对指定章节进行机械性文本质量检查（纯规则引擎，不调LLM）。"
        "\n检查内容："
        "\n- 高频重复词/短语"
        "\n- 过长句（>80字）"
        "\n- 异常段落（>800字单段）"
        "\n- 连续句子同开头"
        "\n返回结构化问题清单，供审阅Agent在报告中引用。"
    )
    category = MCPToolCategory.CONSISTENCY_CHECK
    args_schema = LintChapterArgs

    async def _execute(
        self,
        args: LintChapterArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        # 获取章节内容（优先副本）
        result = await db.execute(select(Chapter).where(Chapter.id == args.chapter_id, Chapter.novel_id == novel_id))
        chapter = result.scalar_one_or_none()
        if not chapter:
            return MCPToolResult(success=False, error=f"章节不存在: {args.chapter_id}")

        edit_result = await db.execute(
            select(EditSession).where(
                EditSession.chapter_id == chapter.id,
                EditSession.status == EditSessionStatus.PENDING,
            ).order_by(EditSession.created_at.desc()).limit(1)
        )
        edit_session = edit_result.scalar_one_or_none()
        content = edit_session.working_content if edit_session else chapter.content

        if not content or not content.strip():
            return MCPToolResult(success=False, error="章节内容为空")

        issues: list[dict[str, Any]] = []

        if args.scope in ("full", "style"):
            issues.extend(_check_long_sentences(content))
            issues.extend(_check_long_paragraphs(content))
            issues.extend(_check_sentence_openings(content))

        if args.scope in ("full", "repetition"):
            issues.extend(_check_repeated_phrases(content))

        # 统计信息
        sentences = _split_sentences(content)
        stats = {
            "total_chars": len(content.replace("\n", "")),
            "total_paragraphs": len([p for p in content.split("\n\n") if p.strip()]),
            "sentence_count": len(sentences),
            "avg_sentence_length": sum(len(s) for s in sentences) // max(len(sentences), 1),
            "long_sentence_count": sum(1 for s in sentences if len(s) > 80),
        }

        return MCPToolResult(
            success=True,
            data={
                "chapter_id": args.chapter_id,
                "chapter_number": chapter.chapter_number,
                "title": chapter.title,
                "total_issues": len(issues),
                "issues": issues,
                "stats": stats,
            },
            metadata={"tool": self.name, "chapter_id": args.chapter_id},
        )


# ---------------------------------------------------------------------------
# 检查函数
# ---------------------------------------------------------------------------

def _split_sentences(text: str) -> list[str]:
    """按中文标点分句"""
    return [s.strip() for s in re.split(r"[。！？；\n]+", text) if s.strip() and len(s.strip()) > 1]


def _split_paragraphs(text: str) -> list[tuple[int, str]]:
    """按空行分段，返回 (段落号, 段落文本) 列表"""
    paras = [p.strip() for p in text.split("\n\n")]
    return [(i + 1, p) for i, p in enumerate(paras) if p]


def _check_long_sentences(content: str) -> list[dict[str, Any]]:
    """检查过长句（>80字）"""
    issues: list[dict[str, Any]] = []
    sentences = _split_sentences(content)
    for s in sentences:
        if len(s) > 80:
            issues.append({
                "category": "long_sentence",
                "severity": "info",
                "description": f"句子过长（{len(s)}字），建议断句",
                "context": s[:60] + "..." if len(s) > 60 else s,
            })
    return issues


def _check_long_paragraphs(content: str) -> list[dict[str, Any]]:
    """检查异常段落（>800字）"""
    issues: list[dict[str, Any]] = []
    for pnum, ptext in _split_paragraphs(content):
        if len(ptext) > 800:
            issues.append({
                "category": "long_paragraph",
                "severity": "info",
                "description": f"第{pnum}段过长（{len(ptext)}字），建议拆分",
                "context": ptext[:60] + "...",
            })
    return issues


def _check_sentence_openings(content: str) -> list[dict[str, Any]]:
    """检查连续句子同开头"""
    issues: list[dict[str, Any]] = []
    sentences = _split_sentences(content)
    consecutive = 1
    for i in range(1, len(sentences)):
        prev_start = sentences[i - 1][:2]
        curr_start = sentences[i][:2]
        if prev_start == curr_start and len(prev_start) >= 1:
            consecutive += 1
        else:
            if consecutive >= 3:
                issues.append({
                    "category": "repeated_opening",
                    "severity": "warning",
                    "description": f"连续{consecutive}句以\"{prev_start}\"开头",
                })
            consecutive = 1
    if consecutive >= 3:
        issues.append({
            "category": "repeated_opening",
            "severity": "warning",
            "description": f"连续{consecutive}句以\"{sentences[-1][:2]}\"开头",
        })
    return issues


def _check_repeated_phrases(content: str) -> list[dict[str, Any]]:
    """检查高频重复短语"""
    issues: list[dict[str, Any]] = []
    # 用标点/换行切分语段，提取 2-4 字短语统计
    segments = [s.strip() for s in re.split(r"[。！？；，、\n]", content) if len(s.strip()) >= 4]
    phrase_counter: Counter = Counter()

    for seg in segments:
        for n in (3, 4):
            for i in range(len(seg) - n + 1):
                phrase_counter[seg[i:i + n]] += 1

    total_chars = len(content.replace("\n", ""))
    threshold = max(3, total_chars // 500)  # 每500字允许出现一次

    for phrase, count in phrase_counter.most_common(30):
        if count >= threshold and len(phrase.strip()) >= 2:
            # 找到短语在文中的大致位置
            pos = content.find(phrase)
            ctx_start = max(0, pos - 10)
            ctx_end = min(len(content), pos + len(phrase) + 20)
            issues.append({
                "category": "repetition",
                "severity": "warning" if count >= threshold * 2 else "info",
                "description": f"\"{phrase}\" 出现 {count} 次",
                "context": "..." + content[ctx_start:ctx_end].replace("\n", " ") + "...",
            })

    return issues


# ---------------------------------------------------------------------------
# 注册
# ---------------------------------------------------------------------------


def register_lint_tools(registry: MCPToolRegistry) -> None:
    registry.register(LintChapterTool())
