"""
章节模块工具函数
"""


def _format_outline(outline: dict) -> str:
    """将大纲 JSON 格式化为 Markdown 文本"""
    lines = [
        f"## 第{outline.get('chapter_number', '?')}章：{outline.get('title', '未命名')}",
        "",
        f"**语调**：{outline.get('tone', '未指定')}　|　**预估字数**：{outline.get('estimated_words', '?')}",
        "",
        "### 场景",
    ]
    for i, scene in enumerate(outline.get("scenes", []), 1):
        lines.append(f"{i}. **{scene.get('name', '场景' + str(i))}**")
        lines.append(f"   {scene.get('description', '')}")
        lines.append(f"   > 目的：{scene.get('purpose', '')}")
        lines.append("")

    if outline.get("key_events"):
        lines.append("### 关键事件")
        for event in outline["key_events"]:
            lines.append(f"- {event}")
        lines.append("")

    if outline.get("focus_characters"):
        lines.append("### 重点角色")
        for fc in outline["focus_characters"]:
            if isinstance(fc, dict):
                lines.append(f"- **{fc.get('name', '?')}**：{fc.get('role_in_chapter', '')}")
            else:
                lines.append(f"- {fc}")
        lines.append("")

    if outline.get("foreshadowing_ops"):
        lines.append("### 伏笔操作")
        for op in outline["foreshadowing_ops"]:
            labels = {"plant": "埋下", "advance": "推进", "resolve": "回收"}
            label = labels.get(op.get("action", ""), op.get("action", ""))
            lines.append(f"- [{label}] {op.get('content', '')}")
        lines.append("")

    lines.append(f"**章末钩子**：{outline.get('chapter_hook', '无')}")
    return "\n".join(lines)
