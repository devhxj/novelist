"""
提示词模板管理 - 系统提示词和用户提示词分离
支持多种生成类型的提示词模板
"""
from typing import Dict, Any, Optional, List
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
    DEEPSEEK_CHAT = "deepseek-chat"
    DEEPSEEK_REASONER = "deepseek-reasoner"


@dataclass
class PromptTemplate:
    """提示词模板"""
    system_prompt: str
    default_user_prompt: str
    context_template: str


SYSTEM_PROMPTS: Dict[str, str] = {
    GenerationType.CHAPTER: """你是一位专业的小说作家。

核心要求：
1. 严格遵循既有设定、世界观与角色性格，不得随意改动
2. 情节推进要有因果、节奏与张力
3. 语言自然流畅，细节与场景兼具
4. 不输出与正文无关的解释或评论

输出目标：
- 产出可直接作为章节内容的正文""",

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

STYLE_HINTS: Dict[str, str] = {
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
    style: Optional[str] = None
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
    user_prompt: Optional[str] = None,
    chapter_outline: Optional[str] = None,
    key_events: Optional[List[str]] = None,
    focus_characters: Optional[List[str]] = None
) -> str:
    """
    构建章节生成提示词
    
    Args:
        chapter_number: 章节号
        target_length: 目标字数
        style: 写作风格
        context: 上下文信息
        user_prompt: 用户自定义提示词
        chapter_outline: 章节大纲
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
    
    if context:
        parts.append(f"\n【上下文信息】\n{context}")
    
    if chapter_outline:
        parts.append(f"\n【章节大纲】\n{chapter_outline}")
    
    if key_events:
        events_str = "\n".join(f"- {event}" for event in key_events)
        parts.append(f"\n【关键事件】\n{events_str}")
    
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
    characters: List[str],
    context: str,
    style: str,
    user_prompt: Optional[str] = None
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
    user_prompt: Optional[str] = None
) -> str:
    """构建描写生成提示词"""
    if user_prompt:
        return f"【创作要求】\n{user_prompt}\n\n描写对象：{subject}\n描写风格：{style}"
    return f"请生成一段关于「{subject}」的描写。\n描写风格：{style}"


def build_outline_prompt(
    premise: str,
    genre: str,
    total_chapters: int,
    user_prompt: Optional[str] = None
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
    user_prompt: Optional[str] = None
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
    user_prompt: Optional[str] = None
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


def get_available_models() -> List[Dict[str, str]]:
    """获取可用的LLM模型列表"""
    return [
        {
            "value": LLMModel.DEEPSEEK_CHAT.value,
            "label": "DeepSeek Chat",
            "description": "通用对话模型，适合日常对话和文本生成"
        },
        {
            "value": LLMModel.DEEPSEEK_REASONER.value,
            "label": "DeepSeek Reasoner",
            "description": "推理增强模型，适合复杂推理和创意写作"
        }
    ]


def get_available_styles() -> List[Dict[str, str]]:
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
