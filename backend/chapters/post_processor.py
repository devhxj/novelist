"""
章节后处理流水线
在章节正文生成完成后执行：
1. 结尾完整性检测与补全
2. 完结标记剥离

说明：
- 伏笔/规划等结构化信息已由 AI 通过 MCP 工具（add_timeline_entry 等）实时维护
- 本模块不再重复做 LLM 分析
"""
import re
import logging
from typing import Optional, Dict, Any

from core.llm_service import llm_service

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
        }
        processed = content
        if not is_ending_complete(processed):
            logger.info(f"Chapter {chapter_number} ending appears incomplete, attempting completion")
            processed = await complete_ending(processed, model)
            result["was_truncated"] = True
            result["ending_completed"] = processed != content
            result["final_content"] = processed
        marker_match = re.search(r'---【第\d+章完结】---', processed)
        if marker_match:
            result["has_ending_marker"] = True
            processed = re.sub(r'\n*---【第\d+章完结】---\n*', '', processed).strip()
            result["final_content"] = processed
        return result
