"""
文本生成服务 - 统一的文本生成接口
支持多种生成模式：章节、对话、描写、大纲、摘要、角色档案
"""
import logging
from typing import Dict, Any, List, Optional
from enum import Enum
from dataclasses import dataclass

from app.core.llm_service import llm_service
from app.core.context_builder import ContextBuilder

logger = logging.getLogger(__name__)


class GenerationType(str, Enum):
    """生成类型"""
    CHAPTER = "chapter"
    DIALOGUE = "dialogue"
    DESCRIPTION = "description"
    OUTLINE = "outline"
    SUMMARY = "summary"
    CHARACTER_PROFILE = "character_profile"


@dataclass
class GenerationConfig:
    """生成配置"""
    generation_type: GenerationType = GenerationType.CHAPTER
    style: str = "narrative"
    target_length: int = 3000
    temperature: float = 0.8
    max_tokens: int = 4000


class TextGenerator:
    """文本生成服务"""
    
    def __init__(self, db, novel_id: int):
        self.db = db
        self.novel_id = novel_id
        self.context_builder = ContextBuilder(db, novel_id)
    
    async def generate(
        self,
        prompt: str,
        context: Dict[str, Any] = None,
        config: GenerationConfig = None
    ) -> Dict[str, Any]:
        """
        生成文本
        
        Args:
            prompt: 提示词
            context: 额外上下文
            config: 生成配置
            
        Returns:
            生成结果
        """
        if config is None:
            config = GenerationConfig()
        
        try:
            system_prompt = self._build_system_prompt(config)
            
            full_prompt = self._build_full_prompt(prompt, context, system_prompt)
            
            result = await llm_service.generate_text(
                prompt=full_prompt,
                system_prompt=system_prompt,
                temperature=config.temperature,
                max_tokens=config.max_tokens
            )
            
            return {
                "success": True,
                "content": result,
                "generation_type": config.generation_type.value,
                "style": config.style
            }
            
        except Exception as e:
            logger.error(f"Generation failed: {e}")
            return {
                "success": False,
                "error": str(e)
            }
    
    async def generate_chapter(
        self,
        chapter_number: int,
        target_length: int = 3000,
        style: str = "narrative",
        additional_context: Dict[str, Any] = None
    ) -> Dict[str, Any]:
        """
        生成章节
        
        Args:
            chapter_number: 章节号
            target_length: 目标字数
            style: 写作风格
            additional_context: 额外上下文
            
        Returns:
            生成结果
        """
        config = GenerationConfig(
            generation_type=GenerationType.CHAPTER,
            target_length=target_length,
            style=style
        )
        
        context = await self.context_builder.build_writing_context(
            chapter_number=chapter_number,
            context_size=3000,
            include_previous_chapters=True,
            include_characters=True,
            include_plot_events=True
        )
        
        if additional_context:
            context.update(additional_context)
        
        prompt = f"请生成第{chapter_number}章，目标字数约{target_length}字。"
        
        return await self.generate(prompt, context, config)
    
    async def polish_chapter(
        self,
        chapter_id: int,
        style: str = "narrative",
        feedback: str = None
    ) -> Dict[str, Any]:
        """
        润色已有章节
        
        Args:
            chapter_id: 章节ID
            style: 写作风格
            feedback: 润色反馈/要求
            
        Returns:
            润色结果
        """
        result = await self.db.execute(
            select(Chapter).where(Chapter.id == chapter_id)
        )
        chapter = result.scalar_one_or_none()
        
        if not chapter:
            return {"success": False, "error": "章节不存在"}
        
        config = GenerationConfig(
            generation_type=GenerationType.CHAPTER,
            style=style
        )
        
        context = await self.context_builder.build_writing_context(
            chapter_id=chapter_id,
            context_size=3000,
            include_previous_chapters=True,
            include_characters=True,
            include_plot_events=True
        )
        
        prompt = f"""请润色以下章节内容，保持原有的情节和人物设定，但提升文字质量和可读性。

原文内容：
{chapter.content}

润色要求：
- 写作风格: {style}
- 保持字数相近
- 提升文字流畅度
- 增强场景描写
- 优化对话表达
"""
        
        if feedback:
            prompt += f"\n额外要求: {feedback}"
        
        result = await self.generate(prompt, context, config)
        
        if result.get("success"):
            return {
                "success": True,
                "content": result.get("content"),
                "original_length": len(chapter.content or ""),
                "new_length": len(result.get("content", ""))
            }
        else:
            return result
    
    async def generate_dialogue(
        self,
        characters: List[str],
        context: str,
        style: str = "natural"
    ) -> Dict[str, Any]:
        """
        生成对话
        
        Args:
            characters: 参与对话的角色列表
            context: 对话场景背景
            style: 对话风格
            
        Returns:
            生成结果
        """
        config = GenerationConfig(
            generation_type=GenerationType.DIALOGUE,
            style=style
        )
        
        prompt = f"""请生成以下角色之间的对话：
角色: {', '.join(characters)}
场景: {context}
风格: {style}
"""
        
        return await self.generate(prompt, {}, config)
    
    async def generate_description(
        self,
        subject: str,
        style: str = "vivid"
    ) -> Dict[str, Any]:
        """
        生成描写
        
        Args:
            subject: 描写对象
            style: 描写风格
            
        Returns:
            生成结果
        """
        config = GenerationConfig(
            generation_type=GenerationType.DESCRIPTION,
            style=style
        )
        
        prompt = f"请生成一段关于'{subject}'的描写。风格: {style}"
        
        return await self.generate(prompt, {}, config)
    
    async def generate_outline(
        self,
        premise: str,
        genre: str,
        total_chapters: int = 20,
        style: str = "narrative"
    ) -> Dict[str, Any]:
        """
        生成大纲
        
        Args:
            premise: 故事前提
            genre: 类型
            total_chapters: 总章节数
            style: 写作风格
            
        Returns:
            生成结果
        """
        config = GenerationConfig(
            generation_type=GenerationType.OUTLINE,
            target_length=2000,
            style=style
        )
        
        prompt = f"""请为以下小说生成大纲。
故事前提: {premise}
类型: {genre}
预计章节数: {total_chapters}
请生成:
1. 故事主题
2. 主要情节线（开端、发展、高潮、结局）
3. 前10章的简要大纲
4. 主要角色设定建议
"""
        
        return await self.generate(prompt, {}, config)
    
    async def generate_summary(
        self,
        content: str,
        max_length: int = 500
    ) -> Dict[str, Any]:
        """
        生成摘要
        
        Args:
            content: 原文内容
            max_length: 最大长度
            
        Returns:
            生成结果
        """
        config = GenerationConfig(
            generation_type=GenerationType.SUMMARY,
            target_length=max_length,
            temperature=0.5
        )
        
        prompt = f"请为以下内容生成摘要，字数不超过{max_length}字。\n\n原文:\n{content[:3000]}"
        
        return await self.generate(prompt, {}, config)
    
    async def generate_character_profile(
        self,
        name: str,
        role: str,
        novel_context: str,
        style: str = "narrative"
    ) -> Dict[str, Any]:
        """
        生成角色档案
        
        Args:
            name: 角色名
            role: 角色定位
            novel_context: 小说背景
            style: 写作风格
            
        Returns:
            生成结果
        """
        config = GenerationConfig(
            generation_type=GenerationType.CHARACTER_PROFILE,
            style=style
        )
        
        prompt = f"""请为以下角色生成详细档案。
角色名: {name}
角色定位: {role}
小说背景: {novel_context}
请生成:
1. 外貌特征
2. 性格特点
3. 背景故事
4. 能力特长
5. 人际关系
"""
        
        return await self.generate(prompt, {}, config)
    
    def _build_system_prompt(self, config: GenerationConfig) -> str:
        """构建系统提示词"""
        base_prompts = {
            GenerationType.CHAPTER: "你是一位专业的小说作家，擅长创作引人入胜的故事。",
            GenerationType.DIALOGUE: "你是一位对话写作专家，擅长创作自然流畅的角色对话。",
            GenerationType.DESCRIPTION: "你是一位描写大师，擅长用生动的语言描绘场景和人物。",
            GenerationType.OUTLINE: "你是一位故事架构师，擅长设计完整的故事大纲。",
            GenerationType.SUMMARY: "你是一位摘要专家，擅长提炼核心内容。",
            GenerationType.CHARACTER_PROFILE: "你是一位角色设计专家，擅长创建立体的角色形象。"
        }
        
        style_hints = {
            "narrative": "使用叙述性语言，流畅自然。",
            "descriptive": "使用描写性语言，生动形象。",
            "dialogue": "使用对话形式，自然流畅。",
            "poetic": "使用诗意语言，优美动人。",
            "dramatic": "使用戏剧性语言，张力十足。",
            "natural": "使用自然语言，贴近生活。",
            "vivid": "使用生动语言，画面感强。 "
        }
        
        style_hint = style_hints.get(config.style, "")
        
        return f"{base_prompts.get(config.generation_type, '')} {style_hint}"
    
    def _build_full_prompt(
        self,
        user_prompt: str,
        context: Dict[str, Any],
        system_prompt: str
    ) -> str:
        """构建完整提示词"""
        if not context:
            return user_prompt
        
        context_parts = []
        
        if context.get("previous_summary"):
            context_parts.append(f"前文摘要:\n{context['previous_summary']}")
        
        if context.get("characters"):
            chars_info = "\n".join([
                f"- {c.get('name', '未知')}: {c.get('personality', '未知')}"
                for c in context["characters"]
            ])
            context_parts.append(f"角色信息:\n{chars_info}")
        
        if context.get("plot_hints"):
            context_parts.append(f"情节线索:\n{context['plot_hints']}")
        
        if context.get("relevant_memory"):
            context_parts.append(f"相关记忆:\n{context['relevant_memory']}")
        
        context_str = "\n\n".join(context_parts)
        
        return f"{context_str}\n\n{user_prompt}"
