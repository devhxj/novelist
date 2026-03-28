"""
写作Agent - 负责章节内容生成
"""
import logging
from typing import Dict, Any, Optional

from .base import BaseAgent, AgentTask, AgentResult, AgentRole, TaskType
from app.core.llm_service import llm_service

logger = logging.getLogger(__name__)


class WriterAgent(BaseAgent):
    """写作Agent - 负责章节内容生成"""
    
    SYSTEM_PROMPT = """你是一位专业的小说作家，擅长创作引人入胜的故事。
你的写作风格流畅自然，善于刻画人物性格，构建紧张的情节冲突。
请根据提供的上下文和要求，创作高质量的章节内容。"""
    
    def __init__(self, agent_id: str = "writer_001"):
        super().__init__(agent_id, AgentRole.WRITER)
        self.supported_tasks = {
            TaskType.GENERATE_CHAPTER,
            TaskType.PLAN_PLOT
        }
    
    def can_handle(self, task_type: TaskType) -> bool:
        return task_type in self.supported_tasks
    
    async def execute(self, task: AgentTask) -> AgentResult:
        """执行写作任务"""
        self.log_task_start(task)
        
        try:
            if task.task_type == TaskType.GENERATE_CHAPTER:
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
        context = task.context
        parameters = task.parameters
        
        chapter_number = parameters.get("chapter_number", 1)
        target_length = parameters.get("target_length", 3000)
        style = parameters.get("style", "narrative")
        
        previous_summary = context.get("previous_summary", "")
        characters = context.get("characters", [])
        plot_hints = context.get("plot_hints", [])
        
        prompt = self._build_writing_prompt(
            chapter_number=chapter_number,
            target_length=target_length,
            style=style,
            previous_summary=previous_summary,
            characters=characters,
            plot_hints=plot_hints
        )
        
        self.logger.info(f"Generating chapter {chapter_number} with LLM")
        
        try:
            generated_content = await llm_service.generate_text(
                prompt=prompt,
                system_prompt=self.SYSTEM_PROMPT,
                temperature=0.8,
                max_tokens=4096
            )
            
            return self.create_result(
                task=task,
                success=True,
                result={
                    "chapter_number": chapter_number,
                    "content": generated_content,
                    "word_count": len(generated_content),
                    "style": style
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
    
    def _build_writing_prompt(
        self,
        chapter_number: int,
        target_length: int,
        style: str,
        previous_summary: str,
        characters: list,
        plot_hints: list
    ) -> str:
        """构建写作提示"""
        prompt = f"""请创作小说的第{chapter_number}章。

写作要求：
- 目标字数：约{target_length}字
- 写作风格：{style}
- 保持与前文的一致性
- 注意角色性格的连贯性
- 情节要有张力和吸引力

"""
        
        if previous_summary:
            prompt += f"\n【前文摘要】\n{previous_summary}\n"
        
        if characters:
            prompt += "\n【相关角色】\n"
            for char in characters:
                prompt += f"- {char.get('name', '未知')}"
                if char.get('personality'):
                    traits = char['personality'].get('traits', [])
                    if traits:
                        prompt += f" (性格: {', '.join(traits)})"
                prompt += "\n"
        
        if plot_hints:
            prompt += "\n【情节提示】\n"
            for hint in plot_hints:
                prompt += f"- {hint.get('description', '')}\n"
        
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
