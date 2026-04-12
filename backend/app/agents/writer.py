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
请严格遵循任务要求、风格、语气和章节目标。
如果任务中给出明确写作指令、提纲、修订意见或重点场景，必须优先执行。"""
    
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
        model = parameters.get("model")
        writing_task = parameters.get("writing_task", "")
        tone = parameters.get("tone", "")
        outline = parameters.get("outline", "")
        author_intent = parameters.get("author_intent", "")
        scene_goal = parameters.get("scene_goal", "")
        must_keep = parameters.get("must_keep", [])
        must_avoid = parameters.get("must_avoid", [])
        revision = parameters.get("revision", False)
        issues = parameters.get("issues", [])
        
        previous_summary = context.get("previous_summary", "")
        characters = context.get("characters", [])
        plot_hints = context.get("plot_hints", [])
        story_outline = context.get("story_outline", {})
        active_plot_lines = context.get("active_plot_lines", [])
        upcoming_plot_nodes = context.get("upcoming_plot_nodes", [])
        due_plot_nodes = context.get("due_plot_nodes", [])
        timeline_entries = context.get("timeline_entries", [])
        priority_timeline_entries = context.get("priority_timeline_entries", [])
        unresolved_foreshadowings = context.get("unresolved_foreshadowings", [])
        due_foreshadowings = context.get("due_foreshadowings", [])
        retrieved_memory = context.get("retrieved_memory", [])
        prewrite_recommendations = context.get("prewrite_recommendations", [])
        chapter_mission = context.get("chapter_mission", {})
        story_brief = context.get("story_brief", "")
        current_arc_summary = context.get("current_arc_summary", "")
        author_preferences = context.get("author_preferences", {})
        
        prompt = self._build_writing_prompt(
            chapter_number=chapter_number,
            target_length=target_length,
            style=style,
            writing_task=writing_task,
            tone=tone,
            outline=outline,
            author_intent=author_intent,
            scene_goal=scene_goal,
            must_keep=must_keep,
            must_avoid=must_avoid,
            revision=revision,
            issues=issues,
            previous_summary=previous_summary,
            characters=characters,
            plot_hints=plot_hints,
            story_outline=story_outline,
            active_plot_lines=active_plot_lines,
            due_plot_nodes=due_plot_nodes,
            upcoming_plot_nodes=upcoming_plot_nodes,
            timeline_entries=timeline_entries,
            priority_timeline_entries=priority_timeline_entries,
            unresolved_foreshadowings=unresolved_foreshadowings,
            due_foreshadowings=due_foreshadowings,
            retrieved_memory=retrieved_memory,
            prewrite_recommendations=prewrite_recommendations,
            chapter_mission=chapter_mission,
            story_brief=story_brief,
            current_arc_summary=current_arc_summary,
            author_preferences=author_preferences
        )
        
        self.logger.info(f"Generating chapter {chapter_number} with LLM")
        
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
        writing_task: str,
        tone: str,
        outline: str,
        author_intent: str,
        scene_goal: str,
        must_keep: list,
        must_avoid: list,
        revision: bool,
        issues: list,
        previous_summary: str,
        characters: list,
        plot_hints: list,
        story_outline: dict,
        active_plot_lines: list,
        due_plot_nodes: list,
        upcoming_plot_nodes: list,
        timeline_entries: list,
        priority_timeline_entries: list,
        unresolved_foreshadowings: list,
        due_foreshadowings: list,
        retrieved_memory: list,
        prewrite_recommendations: list,
        chapter_mission: dict,
        story_brief: str,
        current_arc_summary: str,
        author_preferences: dict
    ) -> str:
        """构建写作提示"""
        prompt = f"""请创作小说的第{chapter_number}章。

写作要求：
- 目标字数：约{target_length}字
- 写作风格：{style}
- 保持与前文的一致性
- 注意角色性格的连贯性
- 情节要有张力和吸引力
- 优先满足作者明确表达的创作意图
- 输出正文，不要输出解释
"""
        if tone:
            prompt += f"- 语气要求：{tone}\n"
        if writing_task:
            prompt += f"- 核心任务：{writing_task}\n"
        if scene_goal:
            prompt += f"- 本场景目标：{scene_goal}\n"
        if author_intent:
            prompt += f"\n【作者意图】\n{author_intent}\n"
        if outline:
            prompt += f"\n【章节提纲】\n{outline}\n"
        if story_brief:
            prompt += (
                "\n【写前 StoryBrief】\n"
                "以下信息已经按 Plot / Timeline / Foreshadowing 区分整理，请先理解再下笔：\n"
                f"{story_brief}\n"
            )
        if must_keep:
            prompt += "\n【必须保留/实现】\n"
            for item in must_keep:
                prompt += f"- {item}\n"
        if must_avoid:
            prompt += "\n【明确避免】\n"
            for item in must_avoid:
                prompt += f"- {item}\n"
        if revision and issues:
            prompt += "\n【修订要求】\n"
            for issue in issues:
                prompt += f"- {issue}\n"
        
        if previous_summary:
            prompt += f"\n【前文摘要】\n{previous_summary}\n"

        if current_arc_summary:
            prompt += f"\n【当前卷/主线目标】\n{current_arc_summary}\n"

        if author_preferences:
            prompt += "\n【作者长期协作配置】\n"
            if author_preferences.get("author_intent"):
                prompt += f"- 长期意图：{author_preferences['author_intent']}\n"
            if author_preferences.get("preferred_tone") and not tone:
                prompt += f"- 默认语气：{author_preferences['preferred_tone']}\n"
            if author_preferences.get("scene_planning_notes"):
                prompt += f"- 章节规划备注：{author_preferences['scene_planning_notes']}\n"
            for item in author_preferences.get("long_term_goals", [])[:5]:
                prompt += f"- 长线目标：{item}\n"
            if author_preferences.get("must_keep"):
                prompt += "必须长期遵守：\n"
                for item in author_preferences.get("must_keep", [])[:8]:
                    prompt += f"- {item}\n"
            if author_preferences.get("must_avoid"):
                prompt += "长期明确避免：\n"
                for item in author_preferences.get("must_avoid", [])[:8]:
                    prompt += f"- {item}\n"

        if story_outline:
            outline_parts = []
            if story_outline.get("premise"):
                outline_parts.append(f"故事前提：{story_outline['premise']}")
            if story_outline.get("theme"):
                outline_parts.append(f"主题：{story_outline['theme']}")
            if story_outline.get("middle"):
                outline_parts.append(f"中段方向：{story_outline['middle']}")
            if story_outline.get("climax"):
                outline_parts.append(f"高潮目标：{story_outline['climax']}")
            if outline_parts:
                prompt += "\n【整体大纲】\n" + "\n".join(f"- {item}" for item in outline_parts) + "\n"
        
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

        if active_plot_lines:
            prompt += "\n【Plot｜当前活跃情节线】\n"
            for line in active_plot_lines[:5]:
                prompt += f"- {line.get('name', '')}: {line.get('description', '')}\n"

        if due_plot_nodes:
            prompt += "\n【Plot Nodes｜本章优先推进】\n"
            for node in due_plot_nodes[:5]:
                prompt += f"- {node.get('title', '')}: {node.get('description', '')}\n"

        if upcoming_plot_nodes:
            prompt += "\n【Plot Nodes｜后续可推进】\n"
            for node in upcoming_plot_nodes[:5]:
                prompt += f"- {node.get('title', '')}: {node.get('description', '')}\n"

        if priority_timeline_entries or timeline_entries:
            prompt += (
                "\n【Timeline｜章节安排与用户指令】\n"
                "注意：这里是近期安排、写作约束和里程碑，不等同于伏笔。\n"
            )
            for item in (priority_timeline_entries or timeline_entries)[:5]:
                target = f"（目标章:{item.get('target_chapter')}）" if item.get("target_chapter") else ""
                prompt += f"- [{item.get('category', '')}] {item.get('title', '')}{target}: {item.get('description', '')}\n"

        if unresolved_foreshadowings:
            prompt += (
                "\n【Foreshadowing｜未解决伏笔】\n"
                "注意：伏笔是等待未来回收的钩子，不等同于整体 Plot 规划。\n"
            )
            for item in unresolved_foreshadowings[:5]:
                prompt += f"- {item.get('title', '')}: {item.get('description', '')}\n"

        if due_foreshadowings:
            prompt += "\n【Foreshadowing｜本章建议优先处理】\n"
            for item in due_foreshadowings[:5]:
                prompt += f"- {item.get('title', '')}: {item.get('description', '')}\n"

        if chapter_mission:
            prompt += "\n【本章任务分配】\n"
            if chapter_mission.get("must_resolve_foreshadowing_ids"):
                prompt += "- 优先考虑回收至少一个已到期伏笔。\n"
            if chapter_mission.get("should_advance_plot_node_ids"):
                prompt += "- 本章必须实质推进当前 Plot 节点，不能只做气氛铺垫。\n"
            if chapter_mission.get("must_respect_timeline_ids"):
                prompt += "- 本章要落实近期 Timeline 安排或用户指令。\n"
            if chapter_mission.get("should_introduce_new_foreshadowing"):
                prompt += "- 若剧情自然允许，可埋一个与主线直接相关的新伏笔。\n"

        if prewrite_recommendations:
            prompt += "\n【写前检查清单】\n"
            for item in prewrite_recommendations[:5]:
                prompt += f"- {item}\n"

        if retrieved_memory:
            prompt += "\n【检索到的前文记忆片段】\n"
            for item in retrieved_memory[:5]:
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
