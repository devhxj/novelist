"""
编辑模式系统 - 控制AI的权限级别
"""
from enum import Enum
from typing import Optional, List, Set


class EditMode(str, Enum):
    """编辑模式"""
    AGENT = "agent"
    REVIEW = "review"
    PLAN = "plan"


class EditModeConfig:
    """编辑模式配置"""
    
    MODE_DESCRIPTIONS = {
        EditMode.AGENT: "智能助手模式：AI可以读取和编辑小说内容，帮助您进行创作和修改。",
        EditMode.REVIEW: "审阅模式：AI只能读取小说内容，提供审阅意见，不能进行任何修改。",
        EditMode.PLAN: "规划模式：AI只能读取小说内容并创建写作大纲/规划，不能修改原稿。"
    }
    
    MODE_SYSTEM_PROMPTS = {
        EditMode.AGENT: """你是一个专业的小说创作助手。你可以：
1. 读取小说的所有内容（章节、角色、情节等）
2. 编辑和修改小说内容
3. 帮助用户进行创作、润色、修改

在编辑时，你会创建一个副本进行修改，用户需要确认后才会应用到原稿。
当需要写作、审核或一致性检查时，可以调度子Agent执行任务。
可以直接创建空章节，也可以直接生成新章节正文草稿。
当作者表达“以后都这样写”“长期不要出现某类内容”“这本书整体风格/目标/禁忌”等稳定规则时，
应主动调用 update_creative_profile 进行沉淀。
当准备生成章节、规划情节、审阅方向，且需要确认长期规则时，应优先调用 get_creative_profile。
若只是新增或补充长期规则，优先走增量合并；若明确要替换旧规则，再传 merge_with_existing=false。
短期一次性的本章要求放在当前任务参数里，长期规则写入 creative profile。
如果用户只是闲聊、夸赞、确认、提问，或没有明确要求你写作/续写/改写/创建章节/规划，就不要主动开始写正文、创建章节或修改章节。
在动笔之前，优先确认用户这次是想聊天、审阅建议，还是明确要你产出内容。
不要在正文内容中输出你的思考过程或自言自语。""",
        
        EditMode.REVIEW: """你是一个专业的小说审阅助手。你可以：
1. 读取小说的所有内容
2. 提供审阅意见、改进建议
3. 指出问题、分析情节、评价人物

注意：你**不能**修改任何小说内容，只能提供审阅意见。""",
        
        EditMode.PLAN: """你是一个专业的小说规划助手。你可以：
1. 读取小说的所有内容
2. 创建写作大纲、情节规划
3. 设计章节结构、人物发展路线

注意：你**不能**修改原稿内容，只能创建规划和大纲。你的输出应该是一个结构化的规划文档。"""
    }
    
    MODE_ALLOWED_TOOLS: dict[EditMode, Set[str]] = {
        EditMode.AGENT: {
            "get_novel_summary", "get_chapter_list", "get_chapter_content", "create_new_chapter", "generate_chapter_draft",
            "get_creative_profile", "update_creative_profile",
            "get_novel_progress", "get_character_list", "get_character_detail",
            "search_plot_memory", "get_character_memory", "get_timeline", "get_recent_context",
            "start_edit_session", "apply_edit", "get_edit_status", "read_chapter_for_edit",
            "run_agent_task"
        },
        EditMode.REVIEW: {
            "get_novel_summary", "get_chapter_list", "get_chapter_content", "get_creative_profile",
            "get_novel_progress", "get_character_list", "get_character_detail",
            "search_plot_memory", "get_character_memory", "get_timeline", "get_recent_context"
        },
        EditMode.PLAN: {
            "get_novel_summary", "get_chapter_list", "get_chapter_content", "get_creative_profile",
            "get_novel_progress", "get_character_list", "get_character_detail",
            "search_plot_memory", "get_character_memory", "get_timeline", "get_recent_context"
        }
    }
    
    MODE_CAN_EDIT: dict[EditMode, bool] = {
        EditMode.AGENT: True,
        EditMode.REVIEW: False,
        EditMode.PLAN: False
    }
    
    @classmethod
    def can_use_tool(cls, mode: EditMode, tool_name: str) -> bool:
        """检查指定模式下是否可以使用某个工具"""
        allowed = cls.MODE_ALLOWED_TOOLS.get(mode, set())
        return tool_name in allowed
    
    @classmethod
    def can_edit(cls, mode: EditMode) -> bool:
        """检查指定模式下是否可以编辑"""
        return cls.MODE_CAN_EDIT.get(mode, False)
    
    @classmethod
    def get_system_prompt(cls, mode: EditMode) -> str:
        """获取指定模式的系统提示词"""
        return cls.MODE_SYSTEM_PROMPTS.get(mode, cls.MODE_SYSTEM_PROMPTS[EditMode.AGENT])
    
    @classmethod
    def get_description(cls, mode: EditMode) -> str:
        """获取指定模式的描述"""
        return cls.MODE_DESCRIPTIONS.get(mode, "")
    
    @classmethod
    def filter_tools(cls, mode: EditMode, all_tools: List[str]) -> List[str]:
        """过滤出当前模式允许使用的工具"""
        allowed = cls.MODE_ALLOWED_TOOLS.get(mode, set())
        return [t for t in all_tools if t in allowed]
