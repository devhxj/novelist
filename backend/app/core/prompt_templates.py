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
    GenerationType.CHAPTER: """你是一位专业的小说作家，拥有丰富的创作经验。

你的能力：
- 精通各种文学体裁和写作风格
- 善于塑造立体丰满的人物形象
- 擅长构建扣人心弦的情节
- 注重细节描写和氛围营造

写作原则：
1. 保持人物性格的一致性和发展性
2. 情节要符合逻辑，有因果关联
3. 对话要自然，符合角色身份
4. 描写要生动，有画面感
5. 节奏要张弛有度，引人入胜""",

    GenerationType.DIALOGUE: """你是一位对话写作专家，擅长创作自然流畅、富有张力的角色对话。

你的能力：
- 深谙人物性格与语言风格的关系
- 善于通过对话展现人物内心
- 擅长设计对话中的冲突与转折
- 注重对话的节奏和韵律

对话原则：
1. 每个角色有独特的说话方式
2. 对话要推动情节发展
3. 潜台词要丰富
4. 避免冗长和无意义的对话""",

    GenerationType.DESCRIPTION: """你是一位描写大师，擅长用生动的语言描绘场景、人物和氛围。

你的能力：
- 善于运用五感描写
- 擅长营造氛围和意境
- 注重细节的精准刻画
- 懂得留白与想象的平衡

描写原则：
1. 具体而生动，避免空洞
2. 多角度、多层次展现
3. 情景交融，物我合一
4. 语言优美，节奏感强""",

    GenerationType.OUTLINE: """你是一位故事架构师，擅长设计完整、引人入胜的故事大纲。

你的能力：
- 精通各种叙事结构和情节模式
- 善于设计冲突和转折
- 擅长埋设伏笔和呼应
- 注重故事的完整性和节奏感

大纲原则：
1. 结构清晰，层次分明
2. 主线明确，支线丰富
3. 高潮迭起，张弛有度
4. 结局合理，回味悠长""",

    GenerationType.SUMMARY: """你是一位摘要专家，擅长提炼文本的核心内容。

你的能力：
- 快速把握文本主旨
- 精准提取关键信息
- 善于概括和归纳
- 注重语言的简洁准确

摘要原则：
1. 抓住核心，舍弃细节
2. 保持客观，不加评论
3. 语言简洁，逻辑清晰
4. 信息完整，重点突出""",

    GenerationType.CHARACTER_PROFILE: """你是一位角色设计专家，擅长创建立体、丰满的人物形象。

你的能力：
- 深谙人物塑造的心理学原理
- 善于设计人物的外在特征和内在性格
- 擅长构建人物关系网络
- 注重人物的成长和变化

角色设计原则：
1. 外貌与性格相符
2. 优点与缺点并存
3. 背景故事要合理
4. 人物要有独特性"""
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
