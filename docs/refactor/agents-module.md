# Agents 模块重构方案

## 背景

当前 `agents/` 模块的核心问题：架构过度设计但能力极其单薄。`SubAgentSpec`、`AgentTask` 链、depth 追踪等抽象层叠，但底层只是 `generate_text()` 和 `generate_json()` 两个单次 LLM 调用。子 agent 没有独立的上下文窗口和工具调用循环。

## 目标

子 agent 拥有独立的 LLM 会话，能自主进行多轮工具调用和思考，类似 Claude Code 的 Explore Agent。主 agent 通过 `run_subagent` 工具发起子 agent，下达任务后等待结构化结果返回。

## 核心思路

将 `ws_chat.py` 中的 `_run_chat_with_tools`（~700 行）拆解为两部分：

1. **通用 Agent 循环**（~100 行）：LLM 流式调用 → 工具执行 → 消息追加 → 护栏检查 → 循环
2. **主 chat 外围逻辑**（~600 行）：消息组装、权限检查、参数清洗、缓存、inject、session 保存

主 chat 和子 agent 共用同一个循环核心。

### 循环边界

**在循环内**（主 chat 和子 agent 完全一致）：
- LLM 流式调用 + 事件解析
- 流式事件推送 WebSocket（`websocket` 参数必传，所有场景都流式）
- 工具执行（`registry.execute()`）
- assistant 消息拼接（含 tool_calls 元数据，LLM 协议要求）
- TOOL 角色消息组装（LLM 协议要求）
- messages 迭代追加（每轮把 assistant + tool 消息追加到消息列表）
- token 预算检查
- 死循环检测

**在循环外**（调用方处理）：
- 初始 messages 构建（主 chat：session 历史 + RAG + 创作画像 + 小说快照；子 agent：system prompt + 任务描述）
- 权限检查（主 chat：`EditModeConfig.can_use_tool()`；子 agent：`SubAgentSpec.allowed_tools` 白名单）
- 参数清洗（布尔值归一化、自动补 chapter_id 等启发式逻辑）
- 工具结果缓存
- inject 消息处理
- 结束后持久化（主 chat：`session_manager.save()`；子 agent：写历史表）
- `_build_tool_call_presentation()` 前端展示格式化

### 前端：主 agent 与子 agent 推送区分

所有 WS 推送事件已有的字段：

| 场景 | task_id | parent_task_id |
|------|---------|----------------|
| 主 agent 调 run_subagent | `chat_{session_id}_{ts}` | — |
| 子 agent thinking/content/tool_call | `subagent_{session_id}_{agent_type}` | `chat_{session_id}_{ts}` |
| 子 agent 结束，主 agent 继续 | `chat_{session_id}_{ts}` | — |

前端根据 `parent_task_id` 将子 agent 的事件嵌套渲染在主 agent 的 `run_subagent` 工具调用卡片内。子 agent 事件结构本身与主 agent 完全相同（thinking_chunk → tool_call → content_chunk），不需要新的前端渲染逻辑。

### 子 Agent 配置

`SubAgentSpec` 精简为纯配置——子 agent 需要什么上下文自己调工具获取，不再预先注入：

```python
@dataclass
class SubAgentSpec:
    agent_type: str           # "memory" | "review"
    display_name: str         # "记忆探索" | "章节审核"
    system_prompt: str        # 角色定义
    allowed_tools: list[str]  # 工具白名单
```

`context_provider.py` 删除——LLM 有工具循环后，自主决定查什么。

## 实现步骤

### 第 1 步：提取通用 Agent 循环

从 `_run_chat_with_tools` 抽取出 `run_agent_loop`，循环本身只做：

```
while turns < max_turns:
    LLM 流式调用(messages, tools) → 推 thinking/content/tool_call 事件到 WS
    没有 tool_call → 循环结束，返回最终文本
    有 tool_call → registry.execute() → 追加 assistant + tool 消息到 messages → 继续
```

循环接收初始 messages 和 tools，通过 `websocket` 推送流式事件，执行工具并追加协议消息，检查护栏，循环结束返回最终文本。

外围逻辑（权限检查、参数清洗、缓存、inject、持久化）由调用方在外层处理。

### 第 2 步：改造主 chat 使用提取后的循环

`_run_chat_with_tools` 改为调用 `run_agent_loop`。消息准备（RAG、创作画像、session 历史）在外层构建初始 messages，循环结束后外层保存 session。验证主 chat 功能不退化。

### 第 3 步：改进 MCP 工具以适配子 Agent

上一轮重构（mcp-module）解决的是工具**结构**问题：Args 模型、错误处理、缓存失效、代码规范。本轮改进关注工具**语义**——让工具更适合自主 Agent 调用。

**通用改进**：
- 工具描述优化：从"给人看的简短说明"升级为"给 LLM Agent 看的详细说明"，包含何时调用、参数含义、返回值结构、调用建议
- 返回结构标准化：所有读取类工具返回统一的数据结构，方便 Agent 理解和后续工具调用串联

**Memory Agent 专用**：
- `search_story_memory`：增强语义搜索的覆盖面，支持按类型（伏笔/角色/情节）过滤
- `get_timeline`：context 模式增加"未完成条目优先"排序，让 Agent 快速定位待处理事项
- `get_characters`：增加关联查询（某角色参与的情节、时间线、弧线）
- `get_chapters`：增加摘要返回，支持按范围查询（如"最近 N 章"）

**Review Agent 专用**：
- `get_chapter_content`：返回章节完整正文 + 元数据（字数、状态、前后章节号）
- `check_consistency`：现有工具已覆盖，补充输出格式规范
- 审核 Agent 可能需要对比多章内容，参数设计上支持批量查询

**后续可考虑新增**：
- `get_novel_overview`：一次性返回小说全局视图（总章节数、角色数、活跃弧线摘要、未回收伏笔计数），作为 Agent 首次探索的入口

### 第 4 步：实现 Memory Agent

在通用循环基础上，配置 Memory Agent：

- **system_prompt**：定义为"小说探索分析师"，职责是深入探索小说内容，自由调用工具获取信息，最终输出结构化探索报告
- **allowed_tools**：改进后的只读 MCP 工具子集（get_chapters、get_characters、get_timeline、search_story_memory、get_story_arcs、get_reader_perspective、get_story_state 等）
- **消息初始化**：system prompt → 主 agent 下达的任务（user message）
- **WebSocket 推送**：与主 chat 同一 WebSocket，通过 `parent_task_id` 区分嵌套关系
- **结果返回**：循环结束后的最终文本直接返回给主 agent（LLM 消费自然语言比结构化 JSON 更自然）
- **历史持久化**：子 agent 完整消息历史存入 `AgentTaskRecord` 表

### 第 5 步：改造 run_subagent 工具

现有的 `run_subagent` 工具（`editing_tools.py`）改为调用 `run_agent_loop`，替代单次 `WriterAgent.execute()`。通过 `agent_type` 查找对应的 `SubAgentSpec`，获取 system_prompt 和 allowed_tools，启动子 agent 循环。

### 第 6 步：清理旧 agents 模块

**保留**：

| 保留 | 用途 |
|------|------|
| `SubAgentSpec`（精简后） | 子 agent 能力声明：agent_type、display_name、system_prompt、allowed_tools |
| `registry.py` | 装饰器注册子 agent 类型 |
| `models.py`（AgentTaskRecord） | 子 agent 历史持久化 |

**删除**：

| 删除 | 理由 |
|------|------|
| `BaseAgent` + `AgentRole`/`TaskType`/`TaskStatus` 枚举 | 单次调用的继承体系，Agent 循环替代 |
| `AgentTask` + `AgentResult` | 任务链数据结构，不再需要 |
| `CoordinatorAgent` | 纯函数分派器，主循环已替代 |
| `WriterAgent` | 单次 generate_text()，Agent 循环 + 工具替代 |
| `ReviewerAgent` | 单次 generate_json()，且 DB 操作绕过 Service 层 |
| `context.py`（WritingContext） | 25 字段的数据类，子 agent 自己调工具获取上下文 |
| `context_provider.py` | LLM 自主调工具，不需要预先注入上下文 |
| `SubAgentReport` | 结构化报告，子 agent 直接返回文本由主 agent LLM 消费 |
| `factory.py` | 创建 Coordinator 注册 Writer/Reviewer 的工厂 |

### 第 7 步：实现 Review Agent

在 Memory Agent 验证通过后，配置 Review Agent：

- **system_prompt**：定义为"小说编辑审核员"，负责审核章节质量、一致性检查
- **allowed_tools**：章节读取 + 角色读取 + 时间线读取 + 一致性检查工具（只读）
- **典型任务**：「审核第 N 章的角色一致性和情节连贯性」
- **返回**：审核报告（问题列表 + 评分 + 改进建议）

## 新旧架构对比

```
旧：主 agent → run_subagent → AgentTask → CoordinatorAgent → WriterAgent.execute()
                                                            → llm_service.generate_text() 单次调用
                                                            → 返回文本

新：主 agent → run_subagent → run_agent_loop(websocket, messages, tools)
                                 → LLM 流式调用
                                 → 子 agent 决定调工具 → registry.execute()
                                 → 子 agent 继续思考 → 循环
                                 → 最终输出探索报告
```

## 验证

1. 主 chat 对话功能正常，工具调用和流式输出不退化
2. 主 agent 调用 run_subagent(agent_type="memory")，前端通过 `parent_task_id` 将子 agent 事件嵌套在主 agent 工具卡片内
3. Memory Agent 能自主探索小说内容，多轮调用工具后返回结构化报告
4. 子 agent 的消息历史持久化到数据库，可查询
