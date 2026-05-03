"""
编辑模式系统 - 统一使用 AGENT 模式
"""
from enum import Enum
from typing import List, Set, Dict


class EditMode(str, Enum):
    """编辑模式（仅保留 AGENT）"""
    AGENT = "agent"


AGENT_SYSTEM_PROMPT = """你是一个专业的小说创作助手。你可以：
1. 读取小说的所有内容（章节、角色、情节等）
2. 编辑和修改小说内容
3. 帮助用户进行创作、润色、修改

【输出规范】
- 如果你具备推理/思考能力（如DeepSeek Reasoner），请注意区分思考过程和正式回复：
  - 思考过程（thinking/reasoning）：用于内部推理分析，用户可折叠查看
  - 正式回复（content）：必须包含对用户友好的信息，如"我来帮你查看XX的内容""好的，让我先了解当前的角色阵容"
  - **不要把所有有用信息都放在思考里而让正式回复为空或只有寥寥几字**
- 工具调用输出原则——**按任务聚合，不要逐个汇报**：
  - ❌ 错误："我要调用 get_novel_info 查看小说概况，然后调用 get_characters 查角色，再调用 get_timeline 查时间线"
  - ✅ 正确："我来帮你全面了解一下这本小说的总体情况"（然后静默调用所需工具，完成后给用户一个整合的总结）
  - 当用户问一件事需要多个工具配合时，**用一句话说明你要做什么这件事**，而不是罗列你要调哪些工具
  - 只有在工具调用出错或结果异常时才单独提及该工具
- 与用户对话时保持自然、友好、有温度的语气
- 工具调用全部完成后，给用户一个简洁的整合总结反馈

当需要写作、审核或一致性检查时，可以调度子Agent执行任务。
可以直接创建空章节，也可以用 edit_chapter 直接写出或修改章节正文。
当作者表达"以后都这样写""长期不要出现某类内容""这本书整体风格/目标/禁忌"等稳定规则时，
应主动调用 update_creative_profile 进行沉淀。
当准备生成章节、规划情节、审阅方向，且需要确认长期规则时，应优先调用 get_creative_profile。
若只是新增或补充长期规则，优先走增量合并；若明确要替换旧规则，再传 merge_with_existing=false。
短期一次性的本章要求放在当前任务参数里，长期规则写入 creative profile。
如果用户没有明确的创作或编辑意图（如只是在确认、反馈、提问或简单交流），就不要主动开始写正文、创建章节或修改章节。
在动笔之前，优先判断用户这次是否明确要求产出内容；如果只是了解、查看或讨论，保持对话即可。
不要在正文内容中输出你的思考过程或自言自语。

【编辑章节最佳实践】
1. 编辑前先 get_chapter_content(include_lines=true) 了解当前内容和行号
2. 如果你有完整的修改后全文，用 change_type=full_replace（默认）
3. 如果只改几段话，优先用 search_replace 模式（提供 search_text + new_content）
4. 如果你知道精确行号范围，用 line_range_replace 模式

【故事时间线管理】
时间线采用双轨维护：
- 在 AI IDE 对话创作中，以模型主动调用 get_timeline / add_timeline_entry / update_timeline_entry 为主
- 在直接章节生成或模型漏记时，后端会做章节后处理作为兜底，自动提取新伏笔、下章安排并尝试回收已解决伏笔
这意味着：后端兜底不会替代 MCP 能力，而是避免遗漏
1. 生成章节前应调用 get_timeline(mode="context") 了解当前有哪些待处理的伏笔、规划、用户指令
2. 章节生成完成后，如果你在本章埋下了新的伏笔、有后续安排、或需要更新规划，应主动调用 add_timeline_entry 或 update_timeline_entry 记录到时间线
3. 不要在正文末尾输出伏笔/规划等结构化信息，所有时间线维护通过工具完成
4. 如果在写作过程中回收了某个伏笔（之前埋下的线索在本章有了交代），应调用 update_timeline_entry 设置 status=resolved
5. 如果用户要求修改某个规划或伏笔，调用 update_timeline_entry 更新内容
6. 如果用户说"这个伏笔不要了"或"这个规划取消"，调用 update_timeline_entry 并设置 status=abandoned
7. 时间线是跨章节的记忆系统，帮助保持故事的连贯性和一致性。添加新条目前先查重，已有近似条目则更新而非重复创建
8. **时间线状态维护**：每章写作完成后，检查时间线中是否有状态不合理的条目（例如：明显已在前几章回收的伏笔仍是 pending、已完成章节的规划未标记 completed、已过期的安排未更新），主动调用 update_timeline_entry 修正状态

【人物关系管理】
1. 生成章节前应优先调用 get_characters(mode="list") 了解角色阵容和关系网络，确保角色言行一致。
2. 如果需要深入了解某个角色的完整档案，调用 get_characters(mode="detail", character_id=...)。
3. 如果需要了解某个角色的出场记录和最近动态，调用 get_characters(mode="detail", character_id=..., include_memory=true)。
4. 章节生成后如果发现角色间关系发生变化（如：从敌对转为合作、建立新友谊、解除旧盟约等），
   应主动调用 update_character_relationship 记录变化。这会自动更新人物关系图并联动到时间线。
5. 如果用户要求修改某个角色的设定或关系，先通过 get_characters(mode="detail") 确认当前状态再修改。
6. 人物关系是有向图结构——A对B的" mentor "关系不等于B对A的关系，注意区分方向性。

【审查与检查】
- 你可以使用 run_review 工具进行各类审查，通过 scope 参数指定范围：
  - scope="character"：检查角色一致性（性格、能力、关系是否前后矛盾）
  - scope="plot"：检查情节逻辑（因果关系、时间线、逻辑漏洞等）
  - scope="foreshadowing"：查看未回收的伏笔/钩子
  - scope="full"：全面体检（角色+情节+时间线+伏笔）
- 建议在完成重要章节写作或用户要求审阅时主动调用 run_review(scope='full')。

【故事状态文档维护】
故事状态文档是 CLAUDE.md 风格的轻量 markdown，帮 AI 快速了解"故事现在是什么情况"。
使用 get_story_state 读取，update_story_state 更新。每章写完后应顺手更新。

更新时必须保持以下结构（可按需增删子项，但大框架不变）：

## 当前进展
用 2-3 句话概括故事进行到哪了、主角当前处境。

## 角色动态
列出本章有状态变化的角色，每人一行：名字 + 当前状态/处境/情绪变化。
只列有变化的，没变化的不要重复。

## 开着的悬念
列出当前未回收的伏笔和悬念，每条包含：简述 + 埋设章节。
已回收的从列表中移除或标记 [已回收]。

不需要写得面面俱到，重点是帮下一次创作快速进入状态。"""


class EditModeConfig:
    """编辑模式配置（统一 AGENT 模式）"""

    MODE_DESCRIPTIONS = {
        EditMode.AGENT: "智能助手模式：AI可以读取和编辑小说内容，帮助您进行创作和修改。",
    }

    MODE_SYSTEM_PROMPTS = {
        EditMode.AGENT: AGENT_SYSTEM_PROMPT,
    }

    MODE_ALLOWED_TOOLS: dict[EditMode, Set[str]] = {
        EditMode.AGENT: {
            "get_novel_info", "get_chapter_list", "get_chapter_content", "create_new_chapter",
            "get_creative_profile", "update_creative_profile",
            "get_characters", "create_character", "update_character",
            "search_story_memory",
            "edit_chapter",
            "run_subagent",
            "get_timeline", "add_timeline_entry", "update_timeline_entry",
            "run_review",
            "update_character_relationship",
            "get_locations", "create_location", "update_location", "delete_location",
            "get_story_arcs", "add_story_arc", "update_story_arc",
            "get_story_state", "update_story_state",
            "get_reader_perspective", "add_reader_perspective_entry", "update_reader_perspective_entry",
        },
    }

    MODE_CAN_EDIT: dict[EditMode, bool] = {
        EditMode.AGENT: True,
    }

    MODE_LLM_PRIMARY_TOOLS: dict[EditMode, List[str]] = {
        EditMode.AGENT: [
            "get_creative_profile",
            "update_creative_profile",
            "get_novel_info",
            "get_chapter_list",
            "get_chapter_content",
            "get_characters",
            "search_story_memory",
            "get_timeline",
            "run_review",
            "edit_chapter",
            "add_timeline_entry",
            "update_timeline_entry",
            "update_character_relationship",
            "get_story_state", "update_story_state",
            "get_reader_perspective", "add_reader_perspective_entry", "update_reader_perspective_entry",
            "run_subagent",
        ],
    }

    TOOL_BUNDLES: Dict[str, Set[str]] = {
        "editing": {
            "get_chapter_list", "get_chapter_content",
            "edit_chapter",
        },
        "characters": {
            "get_characters",
            "create_character", "update_character",
            "update_character_relationship",
        },
        "locations": {
            "get_locations", "create_location",
            "update_location", "delete_location",
        },
        "timeline": {
            "get_timeline",
            "add_timeline_entry", "update_timeline_entry", "run_review",
        },
        "generation": {
            "create_new_chapter", "edit_chapter",
            "search_story_memory", "run_subagent",
        },
    }

    TOOL_BUNDLE_CUES: Dict[str, tuple[str, ...]] = {
        "editing": ("修改", "改写", "润色", "重写", "编辑", "替换", "局部改", "edit_chapter", "副本"),
        "characters": ("角色", "人物", "关系", "师徒", "敌对", "盟友", "恋人", "创建角色"),
        "locations": ("地点", "场景", "地图", "城市", "房间", "森林", "宫殿", "地点设定"),
        "timeline": ("伏笔", "时间线", "规划", "大纲", "安排", "下章", "长期", "回收", "设定检查"),
        "generation": ("写", "续写", "生成", "创建章节", "新章节", "扩写", "补写"),
    }

    @classmethod
    def can_use_tool(cls, mode: EditMode, tool_name: str) -> bool:
        """AGENT 模式允许所有工具"""
        return True

    @classmethod
    def can_edit(cls, mode: EditMode) -> bool:
        """AGENT 模式可编辑"""
        return True

    @classmethod
    def get_system_prompt(cls, mode: EditMode = EditMode.AGENT) -> str:
        """获取系统提示词"""
        return AGENT_SYSTEM_PROMPT

    @classmethod
    def get_description(cls, mode: EditMode = EditMode.AGENT) -> str:
        """获取模式描述"""
        return cls.MODE_DESCRIPTIONS.get(mode, "")

    @classmethod
    def filter_tools(cls, mode: EditMode, all_tools: List[str]) -> List[str]:
        """AGENT 模式允许所有工具"""
        return list(all_tools)

    @classmethod
    def get_llm_primary_tools(cls, mode: EditMode = EditMode.AGENT) -> List[str]:
        """
        给 LLM 的主工具子集。

        目的：
        - 减少创作时的工具噪声
        - 提高工具前缀稳定性与缓存命中率
        - 保留完整后端能力，但默认只暴露高频编排工具
        """
        return list(cls.MODE_LLM_PRIMARY_TOOLS.get(EditMode.AGENT, []))

    @classmethod
    def get_llm_tools_for_message(cls, mode: EditMode = EditMode.AGENT, user_message: str = "") -> List[str]:
        """
        给当前这轮消息挑选工具集。

        设计目标：
        - 默认使用更稳定的主工具集，优化缓存
        - 当用户明确进入 AI IDE 深水区操作时，自动补齐相关工具能力
        - 不丢失原有"可直接读取、直接改、局部改、维护设定"的核心体验
        """
        allowed = cls.MODE_ALLOWED_TOOLS.get(EditMode.AGENT, set())
        selected = set(cls.get_llm_primary_tools())
        text = (user_message or "").strip()

        if text:
            for bundle_name, cues in cls.TOOL_BUNDLE_CUES.items():
                if any(cue in text for cue in cues):
                    selected.update(cls.TOOL_BUNDLES.get(bundle_name, set()))

        if any(cue in text for cue in ("工具", "mcp", "全部能力", "像ide", "像 agent", "像coding agent")):
            selected.update(allowed)

        # 保证编辑核心链路始终可达
        selected.update(cls.TOOL_BUNDLES["editing"])

        ordered_primary = cls.MODE_LLM_PRIMARY_TOOLS.get(EditMode.AGENT, [])
        ordered_extra = sorted(name for name in selected if name not in ordered_primary)
        return [name for name in ordered_primary if name in selected and name in allowed] + [
            name for name in ordered_extra if name in allowed
        ]
