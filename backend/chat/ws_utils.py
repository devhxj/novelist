"""
WebSocket 工具函数 - 从 ws_chat.py 提取
工具调用展示、错误处理、JSON 解析等纯工具函数
"""
import logging
from typing import Any

from sqlalchemy import select

from core.exceptions import BusinessError, SystemError
from core.llm_service import LLMServiceError
from chapters.models import Chapter

logger = logging.getLogger(__name__)


def _friendly_error_message(exc: Exception) -> str:
    if isinstance(exc, BusinessError):
        return exc.message
    if isinstance(exc, LLMServiceError):
        logger.error(f"LLM error (message passed to client): {exc.message}")
        return exc.message
    if isinstance(exc, SystemError):
        logger.error(f"System error (hidden from client): {exc.message}")
        return "服务器异常，请稍后重试。"
    logger.error(f"Internal error (hidden from client): {exc}", exc_info=True)
    return "服务器异常，请稍后重试。"



def _decode_partial_json_string(raw: str) -> str:
    result: list[str] = []
    i = 0
    while i < len(raw):
        ch = raw[i]
        if ch != "\\":
            result.append(ch)
            i += 1
            continue
        if i + 1 >= len(raw):
            break
        nxt = raw[i + 1]
        mapping = {"n": "\n", "r": "\r", "t": "\t", '"': '"', "\\": "\\", "/": "/"}
        if nxt in mapping:
            result.append(mapping[nxt])
            i += 2
            continue
        if nxt == "u":
            hex_part = raw[i + 2:i + 6]
            if len(hex_part) == 4 and all(c in "0123456789abcdefABCDEF" for c in hex_part):
                result.append(chr(int(hex_part, 16)))
                i += 6
                continue
            break
        result.append(nxt)
        i += 2
    return "".join(result)


def _extract_partial_argument_string(arguments_text: str, key: str) -> str | None:
    marker = f'"{key}"'
    key_idx = arguments_text.find(marker)
    if key_idx < 0:
        return None
    colon_idx = arguments_text.find(":", key_idx + len(marker))
    if colon_idx < 0:
        return None
    quote_idx = arguments_text.find('"', colon_idx + 1)
    if quote_idx < 0:
        return None

    buf: list[str] = []
    escape = False
    i = quote_idx + 1
    while i < len(arguments_text):
        ch = arguments_text[i]
        if escape:
            buf.append("\\" + ch)
            escape = False
            i += 1
            continue
        if ch == "\\":
            escape = True
            i += 1
            continue
        if ch == '"':
            break
        buf.append(ch)
        i += 1
    return _decode_partial_json_string("".join(buf))


async def _lookup_chapter_brief(
    db,
    novel_id: int,
    chapter_id: int | None = None,
    chapter_number: int | None = None
) -> dict[str, Any]:
    stmt = None
    if chapter_id:
        stmt = select(Chapter).where(Chapter.id == chapter_id)
    elif chapter_number:
        stmt = select(Chapter).where(
            Chapter.novel_id == novel_id,
            Chapter.chapter_number == chapter_number
        )
    if stmt is None:
        return {}

    result = await db.execute(stmt)
    chapter = result.scalar_one_or_none()
    if not chapter:
        return {}
    return {
        "chapter_id": chapter.id,
        "chapter_number": chapter.chapter_number,
        "chapter_title": chapter.title or f"第{chapter.chapter_number}章"
    }


_TOOL_SYNC_NAMES = {
    "get_chapter_list": "查看章节目录",
    "get_chapter_content": "读取章节正文",
    "edit_chapter": "编辑章节内容",
    "create_new_chapter": "创建新章节",
    "get_creative_profile": "查看创作规则",
    "update_creative_profile": "设置创作规则",
    "search_story_memory": "搜索故事记忆",
    "get_timeline": "查看故事时间线",
    "add_timeline_entry": "记录追踪条目",
    "update_timeline_entry": "更新追踪条目",
    "get_locations": "查看地点信息",
    "create_location": "创建新地点",
    "update_location": "更新地点设定",
    "delete_location": "删除地点",
    "run_review": "执行审查",
    "get_novel_info": "查看小说信息",
    "get_characters": "查看角色信息",
    "create_character": "创建新角色",
    "update_character": "更新角色设定",
    "update_character_relationship": "更新人物关系",
    "run_subagent": "调度AI子任务",
    "create_outline": "章节大纲审批",
    "get_reader_perspective": "查看读者视角",
    "add_reader_perspective_entry": "添加读者视角",
    "update_reader_perspective_entry": "更新读者视角",
    "get_story_arcs": "查看故事弧线",
    "add_story_arc": "创建故事弧线",
    "update_story_arc": "更新故事弧线",
    "get_story_state": "查看故事状态",
    "update_story_state": "更新故事状态",
}


def _sync_tool_display_name(tool_name: str) -> str:
    return _TOOL_SYNC_NAMES.get(tool_name, "处理创作任务")


_TOOL_SYNC_KINDS = {
    "get_chapter_list": "browse",
    "get_chapter_content": "view",
    "edit_chapter": "write",
    "create_new_chapter": "create",
    "get_creative_profile": "memory",
    "update_creative_profile": "memory",
    "search_story_memory": "memory",
    "get_timeline": "view",
    "add_timeline_entry": "write",
    "update_timeline_entry": "edit",
    "get_locations": "view",
    "create_location": "create",
    "update_location": "edit",
    "delete_location": "delete",
    "run_review": "review",
    "get_novel_info": "view",
    "get_characters": "view",
    "create_character": "create",
    "update_character": "edit",
    "update_character_relationship": "edit",
    "run_subagent": "plan",
    "create_outline": "plan",
    "get_reader_perspective": "view",
    "add_reader_perspective_entry": "write",
    "update_reader_perspective_entry": "edit",
    "get_story_arcs": "view",
    "add_story_arc": "create",
    "update_story_arc": "edit",
    "get_story_state": "view",
    "update_story_state": "edit",
}


async def _build_tool_call_presentation(
    db,
    novel_id: int,
    tool_name: str,
    arguments: dict[str, Any] | None = None,
    result_payload: Any | None = None,
    status: str = "executing"
) -> dict[str, Any]:
    arguments = arguments or {}

    if not isinstance(result_payload, dict):
        result_payload = {}

    data_payload = result_payload.get("data") or {}
    if not isinstance(data_payload, dict):
        data_payload = {}

    chapter_id = arguments.get("chapter_id") or data_payload.get("chapter_id")
    if not chapter_id and tool_name in {"create_new_chapter", "edit_chapter"}:
        chapter_id = data_payload.get("chapter_id") or data_payload.get("id")
    chapter_number = arguments.get("chapter_number") or data_payload.get("chapter_number")
    chapter_title = arguments.get("title") or data_payload.get("title")

    chapter_brief = {}
    if chapter_id or chapter_number:
        chapter_brief = await _lookup_chapter_brief(
            db,
            novel_id,
            chapter_id=chapter_id,
            chapter_number=chapter_number
        )
        chapter_id = chapter_brief.get("chapter_id", chapter_id)
        chapter_number = chapter_brief.get("chapter_number", chapter_number)
        chapter_title = chapter_brief.get("chapter_title", chapter_title)

    chapter_label = None
    if chapter_number and chapter_title:
        chapter_label = f"第{chapter_number}章 {chapter_title}"
    elif chapter_number:
        chapter_label = f"第{chapter_number}章"
    elif chapter_title:
        chapter_label = str(chapter_title)

    base_text = tool_name
    activity_kind = "general"

    _TOOL_BASE_NAMES = {
        "get_chapter_list": ("查看章节目录", "browse"),
        "get_chapter_content": (f"查看 {chapter_label}" if chapter_label else "读取章节正文", "view"),
        "edit_chapter": (f"编辑 {chapter_label}" if chapter_label else "编辑章节内容", "write"),
        "create_new_chapter": (f"创建 {chapter_label}" if chapter_label else "创建新章节", "create"),
        "get_creative_profile": ("查看创作规则", "memory"),
        "update_creative_profile": ("设置创作规则", "memory"),
        "search_story_memory": ("搜索故事记忆", "memory"),
        "get_timeline": ("查看故事时间线", "view"),
        "add_timeline_entry": ("记录追踪条目", "write"),
        "update_timeline_entry": ("更新追踪条目", "edit"),
        "run_review": ("执行审查", "review"),
        "get_locations": ("查看地点信息", "view"),
        "create_location": ("创建新地点", "create"),
        "update_location": ("更新地点设定", "edit"),
        "delete_location": ("删除地点", "delete"),
        "get_novel_info": ("查看小说信息", "view"),
        "get_characters": ("查看角色信息", "view"),
        "create_character": ("创建新角色", "create"),
        "update_character": ("更新角色设定", "edit"),
        "update_character_relationship": ("更新人物关系", "edit"),
        "create_outline": ("章节大纲审批", "plan"),
        "run_subagent": ("调度AI子任务", "plan"),
    }

    if tool_name in _TOOL_BASE_NAMES:
        base_text, activity_kind = _TOOL_BASE_NAMES[tool_name]
    elif tool_name in _TOOL_SYNC_NAMES:
        base_text = _TOOL_SYNC_NAMES[tool_name]
        activity_kind = _TOOL_SYNC_KINDS.get(tool_name, "general")
    elif tool_name == "run_subagent":
        task_type = arguments.get("task_type")
        if task_type == "review":
            base_text, activity_kind = "检查剧情一致性", "review"
        elif task_type == "write_chapter":
            base_text, activity_kind = "调度写作子任务", "write"

    is_active = status == "executing"
    display_text = f"正在{base_text}" if is_active else base_text

    return {
        "display_text": display_text,
        "activity_kind": activity_kind,
        "chapter_id": chapter_id,
        "chapter_number": chapter_number,
        "chapter_title": chapter_title
    }
