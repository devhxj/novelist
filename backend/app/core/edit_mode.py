"""
编辑模式系统 - 控制AI的权限级别
"""
from enum import Enum
from typing import Optional, List, Set, Dict


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

【输出规范】
- 如果你具备推理/思考能力（如DeepSeek Reasoner），请注意区分思考过程和正式回复：
  - 思考过程（thinking/reasoning）：用于内部推理分析，用户可折叠查看
  - 正式回复（content）：必须包含对用户友好的信息，如"我来帮你查看XX的内容""好的，让我先了解当前的角色阵容"
  - **不要把所有有用信息都放在思考里而让正式回复为空或只有寥寥几字**
- 工具调用输出原则——**按任务聚合，不要逐个汇报**：
  - ❌ 错误："我要调用 get_novel_summary 查看小说概况，然后调用 get_character_list 查角色，再调用 get_timeline_context 查时间线"
  - ✅ 正确："我来帮你全面了解一下这本小说的总体情况"（然后静默调用所需工具，完成后给用户一个整合的总结）
  - 当用户问一件事需要多个工具配合时，**用一句话说明你要做什么这件事**，而不是罗列你要调哪些工具
  - 只有在工具调用出错或结果异常时才单独提及该工具
- 与用户对话时保持自然、友好、有温度的语气
- 工具调用全部完成后，给用户一个简洁的整合总结反馈

在编辑时，你会创建一个副本进行修改，用户需要确认后才会应用到原稿。
当需要写作、审核或一致性检查时，可以调度子Agent执行任务。
可以直接创建空章节，也可以直接生成新章节正文草稿。
当作者表达"以后都这样写""长期不要出现某类内容""这本书整体风格/目标/禁忌"等稳定规则时，
应主动调用 update_creative_profile 进行沉淀。
当准备生成章节、规划情节、审阅方向，且需要确认长期规则时，应优先调用 get_creative_profile。
若只是新增或补充长期规则，优先走增量合并；若明确要替换旧规则，再传 merge_with_existing=false。
短期一次性的本章要求放在当前任务参数里，长期规则写入 creative profile。
如果用户只是闲聊、夸赞、确认、提问，或没有明确要求你写作/续写/改写/创建章节/规划，就不要主动开始写正文、创建章节或修改章节。
在动笔之前，优先确认用户这次是想聊天、审阅建议，还是明确要你产出内容。
不要在正文内容中输出你的思考过程或自言自语。

【工具调用注意事项】
- 调用 generate_chapter_draft 时：如果目标章节已有内容需要覆盖重写，
务必设置 overwrite_existing=true，否则会失败需要二次调用。

【编辑章节最佳实践】
1. 编辑前先 read_chapter_for_edit 了解当前内容
2. 如果你有完整的修改后全文且改动超过30%，用 apply_edit 的 full_replace 模式
3. 如果只改几段话，优先使用 search_replace 模式（提供 search_text 原文片段 + new_content 替换内容），无需知道行号
4. 不要重复调用 start_edit_session，已有会话可直接 apply_edit
5. 编辑是副本机制，用户需确认后才生效

【故事时间线管理】
时间线采用双轨维护：
- 在 AI IDE 对话创作中，以模型主动调用 get_timeline_context / add_timeline_entry / update_timeline_entry / resolve_timeline_entry 为主
- 在直接章节生成或模型漏记时，后端会做章节后处理作为兜底，自动提取新伏笔、下章安排并尝试回收已解决伏笔
这意味着：后端兜底不会替代 MCP 能力，而是避免遗漏
1. 生成章节前应调用 get_timeline_context 了解当前有哪些待处理的伏笔、规划、用户指令
2. 章节生成完成后，如果你在本章埋下了新的伏笔、有后续安排、或需要更新规划，应主动调用 add_timeline_entry 或 update_timeline_entry 记录到时间线
3. 不要在正文末尾输出伏笔/规划等结构化信息，所有时间线维护通过工具完成
4. 如果在写作过程中回收了某个伏笔（之前埋下的线索在本章有了交代），应调用 resolve_timeline_entry 标记为已解决
5. 如果用户要求修改某个规划或伏笔，调用 update_timeline_entry 更新内容
6. 如果用户说"这个伏笔不要了"或"这个规划取消"，调用 resolve_timeline_entry 并设置 status=abandoned
7. 时间线是跨章节的记忆系统，帮助保持故事的连贯性和一致性。添加新条目前先查重，已有近似条目则更新而非重复创建

【人物关系管理】
1. 生成章节前应优先调用 get_writing_characters 了解角色阵容和关系网络，确保角色言行一致。
2. 如果需要深入了解某个角色的完整档案，调用 get_character_detail(character_id)。
3. 如果需要了解某个角色的出场记录和最近动态，调用 get_character_memory(character_id)。
4. 章节生成后如果发现角色间关系发生变化（如：从敌对转为合作、建立新友谊、解除旧盟约等），
   应主动调用 update_character_relationship 记录变化。这会自动更新人物关系图并联动到时间线。
5. 如果用户要求修改某个角色的设定或关系，先通过 get_character_detail 确认当前状态再修改。
6. 人物关系是有向图结构——A对B的" mentor "关系不等于B对A的关系，注意区分方向性。

【审查与检查】
- 你可以使用 run_review 工具进行各类审查，通过 scope 参数指定范围：
  - scope="character"：检查角色一致性（性格、能力、关系是否前后矛盾）
  - scope="plot"：检查情节逻辑（因果关系、时间线、逻辑漏洞等）
  - scope="foreshadowing"：查看未回收的伏笔/钩子
  - scope="full"：全面体检（角色+情节+时间线+伏笔）
- 建议在完成重要章节写作或用户要求审阅时主动调用 run_review(scope='full')。""",
        
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
            "get_novel_progress", "get_character_list", "get_character_detail", "get_writing_characters",
            "create_character", "update_character",
            "search_plot_memory", "search_story_memory", "prepare_story_brief", "get_character_memory", "get_timeline", "get_recent_context",
            "start_edit_session", "apply_edit", "get_edit_status", "read_chapter_for_edit",
            "run_agent_task",
            "get_story_timeline", "add_timeline_entry", "update_timeline_entry",
            "resolve_timeline_entry", "get_timeline_context",
            "run_review",
            "get_character_network", "get_character_relationships", "update_character_relationship",
            "get_location_list", "get_location_detail", "create_location",
            "update_location", "delete_location",
            "get_pending_changes",
        },
        EditMode.REVIEW: {
            "get_novel_summary", "get_chapter_list", "get_chapter_content", "get_creative_profile",
            "get_novel_progress", "get_character_list", "get_character_detail",
            "search_plot_memory", "search_story_memory", "prepare_story_brief", "get_character_memory", "get_timeline", "get_recent_context"
        },
        EditMode.PLAN: {
            "get_novel_summary", "get_chapter_list", "get_chapter_content", "get_creative_profile",
            "get_novel_progress", "get_character_list", "get_character_detail",
            "search_plot_memory", "search_story_memory", "prepare_story_brief", "get_character_memory", "get_timeline", "get_recent_context"
        }
    }
    
    MODE_CAN_EDIT: dict[EditMode, bool] = {
        EditMode.AGENT: True,
        EditMode.REVIEW: False,
        EditMode.PLAN: False
    }

    MODE_LLM_PRIMARY_TOOLS: dict[EditMode, List[str]] = {
        EditMode.AGENT: [
            "get_creative_profile",
            "update_creative_profile",
            "get_novel_summary",
            "get_chapter_list",
            "get_chapter_content",
            "prepare_story_brief",
            "get_writing_characters",
            "get_character_detail",
            "search_story_memory",
            "get_timeline_context",
            "get_story_timeline",
            "run_review",
            "generate_chapter_draft",
            "read_chapter_for_edit",
            "start_edit_session",
            "apply_edit",
            "get_edit_status",
            "add_timeline_entry",
            "update_timeline_entry",
            "resolve_timeline_entry",
            "update_character_relationship",
            "get_pending_changes",
        ],
        EditMode.REVIEW: [
            "get_novel_summary",
            "get_chapter_list",
            "get_chapter_content",
            "get_creative_profile",
            "prepare_story_brief",
            "get_writing_characters",
            "get_character_detail",
            "search_story_memory",
            "get_timeline_context",
            "get_story_timeline",
            "run_review",
        ],
        EditMode.PLAN: [
            "get_novel_summary",
            "get_chapter_list",
            "get_chapter_content",
            "get_creative_profile",
            "prepare_story_brief",
            "get_writing_characters",
            "search_story_memory",
            "get_story_timeline",
            "get_timeline_context",
        ],
    }

    TOOL_BUNDLES: Dict[str, Set[str]] = {
        "editing": {
            "get_chapter_list", "get_chapter_content", "read_chapter_for_edit",
            "start_edit_session", "apply_edit", "get_edit_status", "get_pending_changes",
        },
        "characters": {
            "get_character_list", "get_character_detail", "get_writing_characters",
            "create_character", "update_character", "get_character_memory",
            "get_character_network", "get_character_relationships", "update_character_relationship",
        },
        "locations": {
            "get_location_list", "get_location_detail", "create_location",
            "update_location", "delete_location",
        },
        "timeline": {
            "prepare_story_brief", "get_story_timeline", "get_timeline_context", "get_timeline",
            "add_timeline_entry", "update_timeline_entry", "resolve_timeline_entry", "run_review",
        },
        "generation": {
            "create_new_chapter", "generate_chapter_draft", "prepare_story_brief",
            "search_story_memory", "run_agent_task",
        },
    }

    TOOL_BUNDLE_CUES: Dict[str, tuple[str, ...]] = {
        "editing": ("修改", "改写", "润色", "重写", "编辑", "替换", "局部改", "apply_edit", "副本"),
        "characters": ("角色", "人物", "关系", "师徒", "敌对", "盟友", "恋人", "创建角色"),
        "locations": ("地点", "场景", "地图", "城市", "房间", "森林", "宫殿", "地点设定"),
        "timeline": ("伏笔", "时间线", "规划", "大纲", "安排", "下章", "长期", "回收", "设定检查"),
        "generation": ("写", "续写", "生成", "创建章节", "新章节", "草稿", "扩写", "补写"),
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

    @classmethod
    def get_llm_primary_tools(cls, mode: EditMode) -> List[str]:
        """
        给 LLM 的主工具子集。

        目的：
        - 减少创作时的工具噪声
        - 提高工具前缀稳定性与缓存命中率
        - 保留完整后端能力，但默认只暴露高频编排工具
        """
        allowed = cls.MODE_ALLOWED_TOOLS.get(mode, set())
        primary = cls.MODE_LLM_PRIMARY_TOOLS.get(mode, [])
        return [name for name in primary if name in allowed]

    @classmethod
    def get_llm_tools_for_message(cls, mode: EditMode, user_message: str = "") -> List[str]:
        """
        给当前这轮消息挑选工具集。

        设计目标：
        - 默认使用更稳定的主工具集，优化缓存
        - 当用户明确进入 AI IDE 深水区操作时，自动补齐相关工具能力
        - 不丢失原有“可直接读取、直接改、局部改、维护设定”的核心体验
        """
        allowed = cls.MODE_ALLOWED_TOOLS.get(mode, set())
        selected = set(cls.get_llm_primary_tools(mode))
        text = (user_message or "").strip()

        if text:
            for bundle_name, cues in cls.TOOL_BUNDLE_CUES.items():
                if any(cue in text for cue in cues):
                    selected.update(cls.TOOL_BUNDLES.get(bundle_name, set()))

        if mode == EditMode.AGENT and any(cue in text for cue in ("工具", "mcp", "全部能力", "像ide", "像 agent", "像coding agent")):
            selected.update(allowed)

        # 保证编辑核心链路始终可达
        if mode == EditMode.AGENT:
            selected.update(cls.TOOL_BUNDLES["editing"])

        ordered_primary = cls.MODE_LLM_PRIMARY_TOOLS.get(mode, [])
        ordered_extra = sorted(name for name in selected if name not in ordered_primary)
        return [name for name in ordered_primary if name in selected and name in allowed] + [
            name for name in ordered_extra if name in allowed
        ]
