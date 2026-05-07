"""
提示词模板管理 - 系统提示词和用户提示词分离
支持多种生成类型的提示词模板
"""
from enum import Enum
from dataclasses import dataclass


class GenerationType(str, Enum):
    """生成类型"""
    CHAPTER = "chapter"
    DIALOGUE = "dialogue"
    DESCRIPTION = "description"
    OUTLINE = "outline"
    SUMMARY = "summary"
    CHARACTER_PROFILE = "character_profile"


class LLMModel(str, Enum):
    """LLM模型"""
    DEEPSEEK_V4_FLASH = "deepseek-v4-flash"
    DEEPSEEK_V4_PRO = "deepseek-v4-pro"


@dataclass
class PromptTemplate:
    """提示词模板"""
    system_prompt: str
    default_user_prompt: str
    context_template: str


SYSTEM_PROMPTS: dict[str, str] = {
    GenerationType.CHAPTER: """你是一位专业的小说作家。

核心要求：
1. 严格遵循既有设定、世界观与角色性格，不得随意改动
2. 情节推进要有因果、节奏与张力
3. 语言自然流畅，细节与场景兼具
4. 不输出与正文无关的解释或评论

输出目标：
- 产出可直接作为章节内容的正文

【章节输出格式要求 — 必须严格遵守】
请在正文结束后，严格按以下格式输出完结标记：

---【第X章完结】---

注意：
1. "第X章"中的X请替换为实际章节号
2. 完结标记必须完整，不可在标记中间截断
3. 正文结束后直接输出完结标记即可，不需要输出其他结构化信息
4. 如果本章埋下了伏笔、有规划安排、或角色关系发生变化，请在输出完结标记后通过工具（add_timeline_entry / update_timeline_entry / update_character_relationship）记录到时间线中，不要在正文中列出""",

    GenerationType.DIALOGUE: """你是一位对话写作专家。

核心要求：
1. 角色语言风格鲜明且稳定
2. 对话推动情节与人物关系发展
3. 节奏自然，避免啰嗦
4. 不输出与对话无关的解释""",

    GenerationType.DESCRIPTION: """你是一位描写大师。

核心要求：
1. 五感描写充分、细节具体
2. 语言有画面感且节奏稳定
3. 不脱离既有设定与语境""",

    GenerationType.OUTLINE: """你是一位故事架构师。

核心要求：
1. 主线清晰，支线有目的
2. 节奏合理，高潮设置明确
3. 伏笔与回收逻辑自洽
4. 输出结构化大纲""",

    GenerationType.SUMMARY: """你是一位摘要专家。

核心要求：
1. 提炼主旨与关键事件
2. 客观简洁，不做评论
3. 保留关键设定与角色关系""",

    GenerationType.CHARACTER_PROFILE: """你是一位角色设计专家。

核心要求：
1. 外貌、性格、动机一致
2. 优缺点并存且有成长空间
3. 与现有设定不冲突
4. 输出清晰结构"""
}

STYLE_HINTS: dict[str, str] = {
    "narrative": "使用叙述性语言，流畅自然，注重情节推进。",
    "descriptive": "使用描写性语言，生动形象，注重场景和细节。",
    "dialogue": "使用对话形式，自然流畅，注重人物性格展现。",
    "poetic": "使用诗意语言，优美动人，注重意境和氛围。",
    "dramatic": "使用戏剧性语言，张力十足，注重冲突和转折。",
    "natural": "使用自然语言，贴近生活，注重真实感。",
    "vivid": "使用生动语言，画面感强，注重细节刻画。"
}


def get_system_prompt(
    generation_type: str,
    style: str | None = None
) -> str:
    """
    获取系统提示词
    
    Args:
        generation_type: 生成类型
        style: 写作风格
        
    Returns:
        系统提示词
    """
    base_prompt = SYSTEM_PROMPTS.get(
        generation_type, 
        "你是一位专业的文本生成助手。"
    )
    
    if style and style in STYLE_HINTS:
        base_prompt += f"\n\n写作风格要求：{STYLE_HINTS[style]}"
    
    return base_prompt


def build_chapter_prompt(
    chapter_number: int,
    target_length: int,
    style: str,
    context: str,
    user_prompt: str | None = None,
    author_intent: str | None = None,
    scene_goal: str | None = None,
    chapter_outline: str | None = None,
    tone: str | None = None,
    must_keep: list[str] | None = None,
    must_avoid: list[str] | None = None,
    key_events: list[str] | None = None,
    focus_characters: list[str] | None = None
) -> str:
    """
    构建章节生成提示词
    
    Args:
        chapter_number: 章节号
        target_length: 目标字数
        style: 写作风格
        context: 上下文信息
        user_prompt: 用户自定义提示词
        author_intent: 作者明确创作意图
        scene_goal: 本章或本场景目标
        chapter_outline: 章节大纲
        must_keep: 必须保留要点
        must_avoid: 必须避免要点
        key_events: 关键事件
        focus_characters: 重点角色
        
    Returns:
        完整的用户提示词
    """
    parts = []
    
    if user_prompt:
        parts.append(f"【创作要求】\n{user_prompt}")
    else:
        parts.append(f"请创作小说的第{chapter_number}章，目标字数约{target_length}字。")

    if author_intent:
        parts.append(f"\n【作者意图】\n{author_intent}")

    if scene_goal:
        parts.append(f"\n【场景目标】\n{scene_goal}")

    if context:
        parts.append(f"\n【上下文信息】\n{context}")
    
    if chapter_outline:
        parts.append(f"\n【章节大纲】\n{chapter_outline}")
    
    if tone:
        parts.append(f"\n【语调氛围】\n{tone}")
    
    if key_events:
        events_str = "\n".join(f"- {event}" for event in key_events)
        parts.append(f"\n【关键事件】\n{events_str}")

    if must_keep:
        keep_str = "\n".join(f"- {item}" for item in must_keep)
        parts.append(f"\n【必须保留/实现】\n{keep_str}")

    if must_avoid:
        avoid_str = "\n".join(f"- {item}" for item in must_avoid)
        parts.append(f"\n【明确避免】\n{avoid_str}")
    
    if focus_characters:
        chars_str = "、".join(focus_characters)
        parts.append(f"\n【重点角色】\n{chars_str}")
    
    parts.append(f"\n【写作要求】")
    parts.append(f"- 目标字数：约{target_length}字")
    parts.append(f"- 写作风格：{style}")
    parts.append("- 保持情节连贯性")
    parts.append("- 角色行为符合设定")
    parts.append("- 注重细节描写")
    
    return "\n".join(parts)


def build_dialogue_prompt(
    characters: list[str],
    context: str,
    style: str,
    user_prompt: str | None = None
) -> str:
    """构建对话生成提示词"""
    parts = []
    
    if user_prompt:
        parts.append(f"【创作要求】\n{user_prompt}")
    
    parts.append(f"请生成以下角色之间的对话：")
    parts.append(f"- 参与角色：{', '.join(characters)}")
    parts.append(f"- 场景背景：{context}")
    parts.append(f"- 对话风格：{style}")
    
    return "\n".join(parts)


def build_description_prompt(
    subject: str,
    style: str,
    user_prompt: str | None = None
) -> str:
    """构建描写生成提示词"""
    if user_prompt:
        return f"【创作要求】\n{user_prompt}\n\n描写对象：{subject}\n描写风格：{style}"
    return f"请生成一段关于「{subject}」的描写。\n描写风格：{style}"


def build_outline_prompt(
    premise: str,
    genre: str,
    total_chapters: int,
    user_prompt: str | None = None
) -> str:
    """构建大纲生成提示词"""
    parts = []
    
    if user_prompt:
        parts.append(f"【创作要求】\n{user_prompt}")
    
    parts.append("请为以下小说生成完整的故事大纲：")
    parts.append(f"- 故事前提：{premise}")
    parts.append(f"- 小说类型：{genre}")
    parts.append(f"- 预计章节数：{total_chapters}")
    parts.append("\n请生成：")
    parts.append("1. 故事主题")
    parts.append("2. 主要情节线（开端、发展、高潮、结局）")
    parts.append("3. 前10章的简要大纲")
    parts.append("4. 主要角色设定建议")
    
    return "\n".join(parts)


def build_summary_prompt(
    content: str,
    max_length: int,
    user_prompt: str | None = None
) -> str:
    """构建摘要生成提示词"""
    parts = []
    
    if user_prompt:
        parts.append(f"【摘要要求】\n{user_prompt}")
    else:
        parts.append(f"请为以下内容生成摘要，字数不超过{max_length}字。")
    
    parts.append(f"\n原文内容：\n{content[:3000]}")
    
    return "\n".join(parts)


def build_character_profile_prompt(
    name: str,
    role: str,
    novel_context: str,
    user_prompt: str | None = None
) -> str:
    """构建角色档案生成提示词"""
    parts = []
    
    if user_prompt:
        parts.append(f"【创作要求】\n{user_prompt}")
    
    parts.append("请为以下角色生成详细档案：")
    parts.append(f"- 角色名：{name}")
    parts.append(f"- 角色定位：{role}")
    parts.append(f"- 小说背景：{novel_context}")
    parts.append("\n请生成：")
    parts.append("1. 外貌特征")
    parts.append("2. 性格特点")
    parts.append("3. 背景故事")
    parts.append("4. 能力特长")
    parts.append("5. 人际关系")
    
    return "\n".join(parts)


def get_available_models() -> list[dict[str, str]]:
    """获取可用的LLM模型列表"""
    return [
        {
            "value": LLMModel.DEEPSEEK_V4_FLASH.value,
            "label": "DeepSeek V4 Flash",
            "description": "官方推荐的高速低成本模型，支持 1M 上下文和工具调用"
        },
        {
            "value": LLMModel.DEEPSEEK_V4_PRO.value,
            "label": "DeepSeek V4 Pro",
            "description": "官方旗舰模型，适合复杂推理、Agent 编排和高难写作任务"
        },
    ]


def get_available_styles() -> list[dict[str, str]]:
    """获取可用的写作风格列表"""
    return [
        {"value": k, "label": _style_label(k), "description": v}
        for k, v in STYLE_HINTS.items()
    ]


def _style_label(style: str) -> str:
    """获取风格标签"""
    labels = {
        "narrative": "叙述性",
        "descriptive": "描写性",
        "dialogue": "对话式",
        "poetic": "诗意",
        "dramatic": "戏剧性",
        "natural": "自然",
        "vivid": "生动"
    }
    return labels.get(style, style)


REVIEW_SYSTEM_PROMPT = """你是一位严格的小说审稿编辑。请审核以下章节内容。

审核维度与评分标准（每项 1-10 分）：
1. 逻辑连贯性：前后文是否矛盾，因果是否合理
2. 角色一致性：角色行为是否符合已建立的人设，对话风格是否一致
3. 情节推进：本章是否实质推进了故事，还是原地踏步
4. 伏笔管理：是否合理处理了到期伏笔，是否自然地埋下新伏笔
5. 文笔质量：节奏、描写、对话的自然度

请严格以 JSON 格式输出审核结果，不要输出其他内容：
{
  "scores": {"logic": 8, "character": 7, "plot": 6, "foreshadowing": 5, "writing": 8},
  "issues": [
    {"dimension": "character", "severity": "warning", "description": "问题描述", "suggestion": "修改建议"}
  ],
  "passed": true,
  "overall_comment": "整体评价"
}

评分规则：
- passed=true：所有维度 >= 6 分且无 severity=error 的问题
- severity 级别：error（必须修改）、warning（建议修改）、info（可选优化）
- issues 中每个问题必须包含 dimension、severity、description、suggestion 四个字段"""


def build_review_prompt(
    content: str,
    chapter_number: int | None = None,
    characters: list[dict] | None = None,
    previous_summary: str | None = None,
    unresolved_foreshadowings: list[dict] | None = None,
    active_story_arcs: list[dict] | None = None,
) -> str:
    """构建审核提示词"""
    parts = []
    if chapter_number:
        parts.append(f"章节号：第{chapter_number}章")
    if previous_summary:
        parts.append(f"\n【前文摘要】\n{previous_summary[:500]}")
    if characters:
        char_names = [c.get("name", "") for c in characters if c.get("name")]
        if char_names:
            parts.append(f"\n【本章应出场的角色】\n{', '.join(char_names)}")
    if unresolved_foreshadowings:
        parts.append("\n【未解决的伏笔】")
        for fs in unresolved_foreshadowings[:5]:
            parts.append(f"- {fs.get('title', '')}: {fs.get('description', '')[:100]}")
    if active_story_arcs:
        parts.append("\n【叙事弧线】")
        for pl in active_story_arcs[:5]:
            parts.append(f"- {pl.get('name', '')}: {pl.get('description', '')[:100]}")
    parts.append(f"\n【待审核的章节内容】\n{content}")
    return "\n".join(parts)


# === 章节创作大纲 prompt ===

CHAPTER_OUTLINE_SYSTEM_PROMPT = """你是一位资深的故事架构师。

## 你的任务
根据提供的上下文，为指定章节编写结构化大纲。输出始终为数组，一章就是数组中的一个元素。

## 每章大纲结构
- title: 章节标题
- scenes: 3-6 个场景，每个含 name/description/purpose
- key_events: 本章核心转折点
- focus_characters: 重点角色及其本章作用
- foreshadowing_ops: 伏笔操作（plant 埋下 / advance 推进 / resolve 回收）
- tone: 本章氛围
- chapter_hook: 章末钩子
- estimated_words: 预估字数

## 输出格式
严格输出 JSON，不要包含任何其他内容：
{"chapters": [{
  "chapter_number": 15,
  "title": "章节标题",
  "scenes": [
    {"name": "场景名", "description": "场景描述（50字内）", "purpose": "叙事目的"}
  ],
  "key_events": ["事件1", "事件2"],
  "focus_characters": [
    {"name": "角色名", "role_in_chapter": "本章作用"}
  ],
  "foreshadowing_ops": [
    {"action": "plant", "content": "伏笔描述"}
  ],
  "tone": "紧张/温馨/悲伤/热血/悬疑/...",
  "chapter_hook": "章末钩子描述",
  "estimated_words": 3000
}]}"""


def build_chapter_outline_user_prompt(
    chapter_numbers: list[int],
    instruction: str,
    layer2_context: str,
    author_intent: str | None = None,
) -> str:
    """构建大纲生成的用户提示词，支持单章和多章"""
    if len(chapter_numbers) == 1:
        ch_desc = f"第{chapter_numbers[0]}章"
    else:
        ch_desc = f"第{chapter_numbers[0]}章至第{chapter_numbers[-1]}章（共{len(chapter_numbers)}章）"

    parts = [
        f"请为{ch_desc}编写结构化大纲。",
        f"\n【创作指令】\n{instruction}",
    ]
    if author_intent:
        parts.append(f"\n【作者意图】\n{author_intent}")
    parts.append(f"\n【参考上下文】\n{layer2_context}")
    parts.append(f"\n请输出{ch_desc}的结构化大纲 JSON 数组。")
    return "\n".join(parts)
