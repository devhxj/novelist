"""
写作Agent - 负责章节内容生成
"""
import logging
from typing import Dict, Any, Optional

from .base import BaseAgent, AgentTask, AgentResult, AgentRole, TaskType, SubAgentSpec
from .registry import register_agent
from core.llm_service import llm_service
from core.text_utils import count_words

logger = logging.getLogger(__name__)

WRITER_SPEC = SubAgentSpec(
    task_type="write_chapter",
    display_name="写作专家",
    description="写作/续写章节内容，支持指定风格、长度和写作指令",
    system_prompt="你是一位专业的小说作家，擅长创作引人入胜的故事。\n你的写作风格流畅自然，善于刻画人物性格，构建紧张的情节冲突。\n请严格遵循任务要求、风格、语气和章节目标。\n如果任务中给出明确写作指令、提纲、修订意见或重点场景，必须优先执行。",
    required_context_keys=["chapter_info"],
    optional_context_keys=["characters", "previous_summary", "layered_context"],
    requires_chapter_id=True,
    result_description="返回生成的章节内容、字数和编辑会话ID",
)


@register_agent("write_chapter", WRITER_SPEC)
class WriterAgent(BaseAgent):
    """写作Agent - 负责章节内容生成"""
    
    SYSTEM_PROMPT = WRITER_SPEC.system_prompt
    
    def __init__(self, agent_id: str = "writer_001"):
        super().__init__(agent_id, AgentRole.WRITER)
        self.supported_tasks = {
            TaskType.GENERATE_CHAPTER,
            TaskType.WRITE_CHAPTER,
            TaskType.PLAN_PLOT
        }
    
    def can_handle(self, task_type: TaskType) -> bool:
        return task_type in self.supported_tasks
    
    async def execute(self, task: AgentTask) -> AgentResult:
        """执行写作任务"""
        self.log_task_start(task)
        
        try:
            if task.task_type in (TaskType.GENERATE_CHAPTER, TaskType.WRITE_CHAPTER):
                result = await self._generate_chapter(task)
            elif task.task_type == TaskType.PLAN_PLOT:
                result = await self._plan_plot(task)
            else:
                result = self.create_result(
                    task=task,
                    success=False,
                    error=f"Unsupported task type: {task.task_type}"
                )
            
            self.log_task_complete(result)
            return result
            
        except Exception as e:
            self.logger.error(f"Error in writing task: {e}")
            return self.create_result(
                task=task,
                success=False,
                error=str(e)
            )
    
    async def _generate_chapter(self, task: AgentTask) -> AgentResult:
        """生成章节内容"""
        from .context import WritingContext

        wc = WritingContext.from_task(task)
        model = task.parameters.get("model")
        prompt = self._build_writing_prompt(wc)

        self.logger.info(f"Generating chapter {wc.chapter_number} with LLM")

        try:
            generated_content = await llm_service.generate_text(
                prompt=prompt,
                system_prompt=self.SYSTEM_PROMPT,
                model=model,
                temperature=0.8,
                max_tokens=4096
            )

            return self.create_result(
                task=task,
                success=True,
                result={
                    "chapter_number": wc.chapter_number,
                    "content": generated_content,
                    "word_count": count_words(generated_content),
                    "style": wc.style
                },
                suggestions=[
                    "建议提交给审核Agent进行内容审核",
                    "检查角色一致性",
                    "验证情节连贯性"
                ],
                next_actions=[
                    {
                        "type": "create_task",
                        "task_type": TaskType.REVIEW_CHAPTER.value,
                        "chapter_id": task.chapter_id,
                        "parameters": {
                            "content": generated_content
                        }
                    }
                ]
            )

        except Exception as e:
            self.logger.error(f"LLM generation failed: {e}")
            return self.create_result(
                task=task,
                success=False,
                error=f"内容生成失败: {str(e)}"
            )
    
    async def _plan_plot(self, task: AgentTask) -> AgentResult:
        """规划情节"""
        parameters = task.parameters
        context = task.context
        
        plot_direction = parameters.get("direction", "continue")
        current_state = context.get("current_state", {})
        
        prompt = self._build_plot_planning_prompt(
            direction=plot_direction,
            current_state=current_state
        )
        
        try:
            plot_plan = await llm_service.generate_text(
                prompt=prompt,
                system_prompt=self.SYSTEM_PROMPT,
                temperature=0.7,
                max_tokens=2048
            )
            
            return self.create_result(
                task=task,
                success=True,
                result={
                    "plot_plan": plot_plan,
                    "direction": plot_direction
                }
            )
            
        except Exception as e:
            self.logger.error(f"Plot planning failed: {e}")
            return self.create_result(
                task=task,
                success=False,
                error=f"情节规划失败: {str(e)}"
            )
    
    def _build_writing_prompt(self, wc: "WritingContext") -> str:
        """构建写作提示"""
        prompt = f"""请创作小说的第{wc.chapter_number}章。

写作要求：
- 目标字数：约{wc.target_length}字
- 写作风格：{wc.style}
- 保持与前文的一致性
- 注意角色性格的连贯性
- 情节要有张力和吸引力
- 优先满足作者明确表达的创作意图
- 输出正文，不要输出解释
"""
        if wc.tone:
            prompt += f"- 语气要求：{wc.tone}\n"
        if wc.writing_task:
            prompt += f"- 核心任务：{wc.writing_task}\n"
        if wc.scene_goal:
            prompt += f"- 本场景目标：{wc.scene_goal}\n"
        if wc.author_intent:
            prompt += f"\n【作者意图】\n{wc.author_intent}\n"
        if wc.outline:
            prompt += f"\n【章节提纲】\n{wc.outline}\n"
        if wc.must_keep:
            prompt += "\n【必须保留/实现】\n"
            for item in wc.must_keep:
                prompt += f"- {item}\n"
        if wc.must_avoid:
            prompt += "\n【明确避免】\n"
            for item in wc.must_avoid:
                prompt += f"- {item}\n"
        if wc.revision and wc.issues:
            prompt += "\n【修订要求】\n"
            for issue in wc.issues:
                prompt += f"- {issue}\n"
        if wc.feedback:
            prompt += f"\n【审核反馈】\n{wc.feedback}\n"

        if wc.previous_summary:
            prompt += f"\n【前文摘要】\n{wc.previous_summary}\n"

        if wc.author_preferences:
            prompt += "\n【作者长期协作配置】\n"
            if wc.author_preferences.get("author_intent"):
                prompt += f"- 长期意图：{wc.author_preferences['author_intent']}\n"
            if wc.author_preferences.get("preferred_tone") and not wc.tone:
                prompt += f"- 默认语气：{wc.author_preferences['preferred_tone']}\n"
            if wc.author_preferences.get("scene_planning_notes"):
                prompt += f"- 章节规划备注：{wc.author_preferences['scene_planning_notes']}\n"
            for item in wc.author_preferences.get("long_term_goals", [])[:5]:
                prompt += f"- 长线目标：{item}\n"
            if wc.author_preferences.get("must_keep"):
                prompt += "必须长期遵守：\n"
                for item in wc.author_preferences.get("must_keep", [])[:8]:
                    prompt += f"- {item}\n"
            if wc.author_preferences.get("must_avoid"):
                prompt += "长期明确避免：\n"
                for item in wc.author_preferences.get("must_avoid", [])[:8]:
                    prompt += f"- {item}\n"

        if wc.story_outline:
            outline_parts = []
            if wc.story_outline.get("premise"):
                outline_parts.append(f"故事前提：{wc.story_outline['premise']}")
            if wc.story_outline.get("theme"):
                outline_parts.append(f"主题：{wc.story_outline['theme']}")
            if wc.story_outline.get("middle"):
                outline_parts.append(f"中段方向：{wc.story_outline['middle']}")
            if wc.story_outline.get("climax"):
                outline_parts.append(f"高潮目标：{wc.story_outline['climax']}")
            if outline_parts:
                prompt += "\n【整体大纲】\n" + "\n".join(f"- {item}" for item in outline_parts) + "\n"

        if wc.characters:
            prompt += "\n【相关角色】\n"
            for char in wc.characters:
                prompt += f"- {char.get('name', '未知')}"
                if char.get('personality'):
                    traits = char['personality'].get('traits', [])
                    if traits:
                        prompt += f" (性格: {', '.join(traits)})"
                prompt += "\n"

        if wc.plot_hints:
            prompt += "\n【情节提示】\n"
            for hint in wc.plot_hints:
                prompt += f"- {hint.get('description', '')}\n"

        if wc.active_story_arcs:
            prompt += "\n【Story Arcs｜叙事弧线】\n"
            for arc in wc.active_story_arcs[:5]:
                prompt += f"- {arc.get('name', '')}: {arc.get('description', '')}\n"

        if wc.due_plot_nodes:
            prompt += "\n【Plot Nodes｜本章优先推进】\n"
            for node in wc.due_plot_nodes[:5]:
                prompt += f"- {node.get('title', '')}: {node.get('description', '')}\n"

        if wc.upcoming_plot_nodes:
            prompt += "\n【Plot Nodes｜后续可推进】\n"
            for node in wc.upcoming_plot_nodes[:5]:
                prompt += f"- {node.get('title', '')}: {node.get('description', '')}\n"

        if wc.priority_timeline_entries or wc.timeline_entries:
            prompt += (
                "\n【Timeline｜章节安排与用户指令】\n"
                "注意：这里是近期安排、写作约束和里程碑，不等同于伏笔。\n"
            )
            for item in (wc.priority_timeline_entries or wc.timeline_entries)[:5]:
                target = f"（目标章:{item.get('target_chapter')}）" if item.get("target_chapter") else ""
                prompt += f"- [{item.get('category', '')}] {item.get('title', '')}{target}: {item.get('description', '')}\n"

        if wc.unresolved_foreshadowings:
            prompt += (
                "\n【Foreshadowing｜未解决伏笔】\n"
                "注意：伏笔是等待未来回收的钩子，不等同于整体 Plot 规划。\n"
            )
            for item in wc.unresolved_foreshadowings[:5]:
                prompt += f"- {item.get('title', '')}: {item.get('description', '')}\n"

        if wc.due_foreshadowings:
            prompt += "\n【Foreshadowing｜本章建议优先处理】\n"
            for item in wc.due_foreshadowings[:5]:
                prompt += f"- {item.get('title', '')}: {item.get('description', '')}\n"

        if wc.retrieved_memory:
            prompt += "\n【检索到的前文记忆片段】\n"
            for item in wc.retrieved_memory[:5]:
                prompt += (
                    f"- [{item.get('source_type', 'content')}] "
                    f"{str(item.get('content', ''))[:180]}\n"
                )

        prompt += "\n请开始创作本章内容："

        return prompt
    
    def _build_plot_planning_prompt(self, direction: str, current_state: dict) -> str:
        """构建情节规划提示"""
        return f"""作为情节规划师，请根据当前状态规划后续情节发展。

发展方向：{direction}
当前状态：{current_state}

请提供详细的情节规划方案，包括：
1. 主要情节线索
2. 角色发展
3. 冲突设置
4. 伏笔安排"""
