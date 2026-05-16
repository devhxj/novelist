# 章节编辑协作体验重构计划

## 现状问题

### 1. 用户自己编辑也要走副本

WS 编辑（`ws_chat._handle_apply_edit`）强制要求 `edit_session_id`，永远写入 `working_content`。用户独自编辑没有 AI 参与，却也要点"接受"才生效，多此一举。

**预期行为：**
- 纯用户编辑 → 直接写 `chapter.content`
- AI 参与或有协作者 → 走副本 `working_content`

### 2. 向量索引更新滞后

`schedule_memory_update` 只在「接受编辑」和「REST 直接编辑」时触发。`edit_chapter`、WS 编辑、REST 协作编辑都不会触发。

**影响：** AI 刚写完或用户刚编辑完的内容，`search_story_memory` 搜不到，直到点"接受"。而写作流程中 LLM 经常先写再搜（比如子 Agent 审阅），导致漏内容。

**预期：** 任何 `apply_change()` 后都触发（10 秒防抖已就绪）。

### 3. 章节摘要生成不可靠

摘要生成路径 `generate_chapter_summary` — 异步后台、fire-and-forget、只取前 4000 字、仅在接受编辑时触发。REST 直接编辑、章节创建都不生成摘要。

| 路径 | 当前有摘要 | 问题 |
|---|---|---|
| AI full_replace | ❌ (等 accept) | 异步，取前 4000 字 |
| AI 小改动 | ❌ | 没问题 |
| 用户 WS 编辑 | ❌ (等 accept) | 异步 |
| 用户 REST 协作编辑 | ❌ (等 accept) | 异步 |
| 用户 REST 直接编辑 | ❌ | 遗漏 |
| 接受编辑 | ✅ | 异步 LLM 调外部 API，和写作 LLM 不是同一个 |

计划复用editchapter工具的提醒，顺带让主agent维护一下summary
**预期：** LLM 写完全章后自己生成摘要，调用工具写入。去掉外部 LLM 调用的异步摘要生成。

### 4. AI 编辑体验差 — 副本 + 流式机制

当前 `edit_chapter` 的实现存在几个体验问题：

**流式输出期间副本不可读：** AI 写作时通过 `content_chunk` 流式推送给前端展示，但 `working_content` 是在 tool call 全部结束后才写入的。流式过程中副本是空的，此时 `get_chapter_content`、`search_story_memory` 等工具看不到正在写的内容。子 Agent 审阅需要等到流式全部完成才能读到。

**副本机制笨重：** EditSession 的创建/应用/接受流程过于复杂。每次 `edit_chapter` 都要创建或复用 session，用户需要手动接受，中间状态不透明。

**前端体验：** 流式输出时编辑器显示的是 SSE 增量文本，不是副本内容。流式结束后才同步到编辑器，中间有视觉割裂。

**改进方向：**
- 流式输出的同时逐步写入 `working_content`（每个 chunk 结束后累加），而非等 tool call 结束一次性写入
- 或者反过来：取消流式输出，等 tool call 全部结束后一次性更新 `working_content`，编辑器直接读副本展示
- 简化副本生命周期：去掉手动接受步骤，AI full_replace 直接写正文

### 5. 编辑路径混乱

内容写入涉及多个入口，行为不一致：

| # | 路径 | 写入目标 | 索引更新 | 摘要 |
|---|---|---|---|---|
| 1 | AI full_replace | working_content | ❌ | ❌ |
| 2 | AI 小改动 | working_content | ❌ | ❌ |
| 3 | 用户 WS 编辑 | working_content | ❌ | ❌ |
| 4 | 用户 REST 协作编辑 | working_content | ❌ | ❌ |
| 5 | 用户 REST 直接编辑 | chapter.content | ✅ | ❌ |
| 6 | 接受编辑 | chapter.content | ✅ | ✅ (异步) |

## 改造方案

### A. 区分编辑模式

- **独占模式**（用户独自）：直接写 `chapter.content`，无副本
- **协作模式**（AI 参与或多人）：走副本 `working_content`，需要接受

### B. 统一索引更新

- `memory_updater._do_update` 优先读 `working_content`
- 所有 `apply_change()` 之后调 `schedule_memory_update`
- 直接写 `chapter.content` 的路径保留现有逻辑

### C. LLM 自维护摘要

- 新增 `set_chapter_summary` MCP 工具，写 `chapter.summary` 字段
- `edit_chapter` 的 maintenance reminder 加一行"生成本章摘要并保存"
- 删除 `chapters/summary.py`（外部 LLM 异步生成）
- 删除 `editor/service._post_accept_refresh` 中的摘要生成部分

### D. AI 结构化待办列表

AI 在写作流程中会产生多项维护任务（更新角色、更新时间线、生成摘要、审核等），当前通过纯文本提醒散落在对话中，LLM 容易遗漏，用户也无法追踪完成状态。

**目标：**
- LLM 生成结构化 todo list 推送给前端
- 前端渲染为可折叠节点列表，每项关联具体的 MCP 工具调用
- 工具执行完成后对应项自动勾选
- 类似 IDE 侧边栏的任务面板

## 涉及文件

| 文件 | 改动 |
|---|---|
| `mcp_tools/editing_tools.py` | 加 `schedule_memory_update`；reminder 加摘要行 |
| `mcp_tools/` 新建 | `set_chapter_summary` 工具 |
| `rag/memory_updater.py` | `_do_update` 优先读 working_content |
| `chat/ws_chat.py` | `_handle_apply_edit` 加 `schedule_memory_update` |
| `chapters/router.py` | 协作分支加 `schedule_memory_update` |
| `chapters/summary.py` | 删除 |
| `editor/service.py` | `_post_accept_refresh` 删摘要生成 |
| `mcp_tools/registry.py` | 注册新工具 |
| `mcp_tools/subagent_tools.py` | 不暴露给子 Agent |
| `chat/ws_utils.py` | 显示名称映射 |
