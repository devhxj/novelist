# 章节创作工作流改进方案

## 一、当前问题

1. **单次生成**：整章由一次 LLM 调用完成，没有边写边思考的过程，质量不稳定
2. **上下文管理粗糙**：按层截断到固定字数，不是按相关性选择性注入
3. **缺少大纲环节**：直接从上下文跳到完整章节，没有结构规划
4. **模式选择冗余**：Agent/Plan/Review 三种模式与子 Agent 功能重叠，增加复杂度
5. **LLM 自主收集上下文不可靠**：依赖 LLM 自己调 MCP 工具查信息，容易遗漏

---

## 二、上下文注入架构

**对话开始时就默认注入核心上下文，不等用户开口。**

用户打开创作工作台 → 系统自动注入：

```
[system] 系统指令 + 创作偏好 + 写作规则（固定）

[system 消息] 自动注入：
  · 故事状态文档（CLAUDE.md 风格，轻量 markdown）
  · 读者认知（结构化数据格式化注入）
  · 角色索引表（所有角色一行概要）
  · 最近 2-3 章全文
  · 下一章待办（时间线中 pending 的任务、要回收的伏笔、要推进的节点）
```

好处：
- AI 一上来就知道"故事现在在哪、接下来该做什么"
- 用户说"继续写"时不用再等 AI 收集上下文
- 即使用户只是讨论剧情，这些上下文也有帮助

---

## 三、章节创作流程

### 混合架构：自由对话 + LangGraph 工作流

普通聊天继续走 ws_chat.py 自由对话模式（工具循环）。识别到"创建新章节"意图时，触发 LangGraph 章节创作工作流。

```
ws_chat.py 自由对话（工具循环）
    ↓ 意图识别：用户要创建新章节
    ↓ LLM 调用 create_chapter_workflow MCP 工具
    ↓
LangGraph 工作流启动：
  节点1: 构建上下文（故事状态 + 读者认知 + 角色 + 最近章节）
  节点2: 生成大纲（结构化：场景节拍、角色、伏笔操作、章末钩子）
  节点3: interrupt() → 通过 WebSocket 发大纲给用户，等待审批
  节点4: 用户批准 → 精确补注入（角色档案、地点设定、伏笔原文）
  节点5: 写正文（流式输出到 WebSocket）
  节点6: 后处理（并行执行）:
    ├── 更新故事状态文档
    ├── 更新读者认知
    ├── 更新时间线
    ├── 生成章节摘要
    ├── 向量记忆入库
    └── Review（一致性 + 角色 + 逻辑检查）
    ↓
工具返回结果，回到 ws_chat.py 自由对话
```

### 关键设计

- **create_chapter_workflow 作为 MCP 工具**：被 LLM 在工具循环中调用，内部启动 LangGraph
- **interrupt() 做用户审批**：LangGraph 原生支持 human-in-the-loop，暂停等待用户通过 WebSocket 响应
- **后处理并行**：LangGraph 支持并行节点，Review 和数据更新同时执行，不额外增加时间
- **后处理有工具权限**：后处理节点的 LLM 也能调 MCP 工具（查角色、查时间线），不是"盲更新"
- **批量创建**：识别到"写15-20章"时，生成联合大纲（多章整体规划），一次审批后按章执行工作流

---

## 四、其他意图的处理

| 用户意图 | 处理方式 |
|---|---|
| 创建新章节 | 触发 LangGraph 章节创作工作流 |
| 批量创建（如"写15-20章"） | 联合大纲（多章整体规划），一次审批后按章执行工作流 |
| 修改/扩写/缩写 | LLM 直接调 `edit_chapter`，已有上下文足够 |
| 讨论剧情/角色 | LLM 直接对话，按需调工具查详细信息 |
| 查看信息 | LLM 调查询工具 |

不需要用户选模式，系统根据意图自动判断。

---

## 五、上下文分层与消息结构

### 当前实现的问题

1. **system prompt 本身就不稳定** — conditional_reminders 根据每条用户消息的意图检测动态拼入 system message，导致连第一条消息每轮都不同，后续全部 cache miss
2. **两个注入点冲突** — `build_context_prompt`（小说/章节概况）在 `get_messages_for_api` 里注入，但工具循环里 `include_context=False`，重建 messages 时丢失
3. **RAG 结果绑在用户消息上** — `extra_context_for_user` 追加在用户消息末尾，语义不对且每轮重建时只在最后一条 user 消息里

### 目标消息结构

```
=== 章节内完全不变（单轮工具循环缓存锚点）===
[system]  系统指令 + 创作偏好 + 写作规则

=== 章节级变化（每章更新一次，章节内单轮工具循环中不变）===
[system]  故事状态文档（markdown）+ 读者认知（数据库格式化）+ 角色索引
[system]  最近 2-3 章摘要/原文
[system]  下一章待办（时间线 pending 项）

=== 对话历史（追加式，前面的不变）===
[user] / [assistant] / [tool]  历史消息

=== 当前轮末尾 ===
[user]  用户消息 + 条件提醒（意图检测结果放这里，不放 system）
```

### 关键改动

1. **分离静态和动态 system 内容** — 系统指令单独一条 system message，永远不变；故事状态等内容单独一条，章节级更新；conditional_reminders 从 system 里移出，放到用户消息末尾或独立的 user role 消息
2. **统一注入点** — 所有上下文（故事状态、读者认知、角色索引、最近章节）通过统一的机制注入，不在多个地方分别拼接
3. **工具循环中保持前缀稳定** — 重建 full_messages 时只追加新的 assistant/tool 消息，不重拼前缀

### 缓存收益

- **跨章节**：不追求，每章更新一次上下文必然 miss
- **单轮工具循环**：真正的收益。一轮对话 5-10 次工具调用，system prompt + 章节上下文几千 token 每次都命中
- **coding agent 95% 命中率的启示**：不是因为没状态，而是因为变化只发生在末尾追加。我们通过分离稳定/动态内容，也能在单轮内实现同样的效果

---

## 六、故事状态文档（CLAUDE.md 风格）

### 设计理念

参考 CLAUDE.md 的设计：简单、轻量、人可读可改、不追求精确但有比没有强太多。

- **不是数据库**，就是一个 markdown 文件
- **不是数据的源头**，原文和向量记忆才是真相。状态文档只是帮 AI 快速导航
- **有错也没关系**，原文和向量检索能兜底
- **维护成本极低** — AI 写完章节后顺手改几行，作者也可以直接编辑

### 格式示例

```markdown
# 第15章后 故事状态

## 当前进展
林风通过外门考核，即将进入内门。与苏婉关系暧昧但互相有隐瞒。

## 角色动态
- 林风：炼气四层，开始怀疑苏婉身份
- 苏婉：天机阁圣女伪装中，对林风动了真感情

## 开着的悬念
- 印记来源（第8章埋下，未解释）
- 苏婉真实身份（怀疑线索在积累中）
```

### 与时间线的区别

- 时间线 = 待办清单，面向未来（"接下来要做什么"）
- 故事状态文档 = 当前快照，面向现在（"故事现在是什么情况"）
- 两者互补：时间线管计划，状态文档管现状

### 更新机制

每章写完后，AI 顺手更新这个文件（类似修改 CLAUDE.md）：
1. 更新"当前进展"段落
2. 更新"角色动态"中有变化的角色
3. 在"开着的悬念"里添加新悬念、标记已回收的
4. 成本极低，不需要 LLM 额外调用——就在写后自动执行里顺手完成

---

## 六·二、读者认知系统（结构化，独立于故事状态文档）

### 为什么独立于故事状态文档

故事状态文档记录**客观事实**（叙事性、模糊），读者认知记录**信息差**（二元、精确）。两者性质不同：
- "林风对苏婉的感情" → 叙事性的，适合写在故事状态文档里
- "读者是否知道苏婉是天机阁圣女" → yes/no，适合结构化存储

### 数据库设计（单表，type 字段区分类型）

```sql
CREATE TABLE reader_perspective (
    id INT PRIMARY KEY AUTO_INCREMENT,
    novel_id INT NOT NULL,
    type VARCHAR(20) NOT NULL,            -- known / suspense / misconception
    content TEXT NOT NULL,                -- 已知内容 / 悬念描述 / 读者误知
    related_truth TEXT,                   -- 仅 misconception：真实情况
    planted_chapter INT NOT NULL,         -- 种下的章节
    revealed_chapter INT,                 -- 揭露/回收的章节
    last_mentioned_chapter INT,           -- 最近提及章节
    status VARCHAR(20) DEFAULT 'active',  -- active / resolved / revealed
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_novel_type (novel_id, type),
    INDEX idx_novel_status (novel_id, status)
);
```

一张表，`type` 区分三种条目：
- `known`：读者已知信息（content + planted_chapter）
- `suspense`：活跃悬念（content + last_mentioned_chapter）
- `misconception`：读者误知（content=误知, related_truth=真相）

### 注入时格式化

查询当前 novel 的活跃数据，格式化成文本注入 context：

```
## 读者认知

### 已知信息
- 林风是青云宗外门弟子 [第1章起]
- 林风体内有神秘印记，来源不明 [第8章起]
- 苏婉身份不简单，具体未知 [第12章起，视角切换暗示]

### 活跃悬念
- 印记是什么？（第8章种下，最近提及：第14章）
- 苏婉接近林风的真实目的？（第12章种下，持续铺垫中）

### 读者误知
- 读者以为林风资质平庸 → 实际被封印（计划：内门试炼篇揭露）
```

### MCP 工具设计（读写分离，与现有工具风格一致）

```
get_reader_perspective(novel_id)                    → 查询当前读者认知（格式化文本）
add_reader_perspective_entry(novel_id, type, ...)   → 添加条目（known/suspense/misconception）
update_reader_perspective_entry(id, ...)            → 更新条目（状态、提及频率等）
```

与现有工具风格一致：
- `get_characters` / `create_character` / `update_character`
- `get_timeline` / `add_timeline_entry` / `update_timeline_entry`
- `get_reader_perspective` / `add_reader_perspective_entry` / `update_reader_perspective_entry`

总计 +5 个 MCP 工具（故事状态 2 + 读者认知 3），总数从 24 到 29。

### 与向量记忆的配合

跨章节伏笔回收时（第10章埋下、第200章回收）：
1. 读者认知系统确认悬念状态（是否还在等、最近铺垫密度）
2. 向量记忆检索埋下时的具体语境和措辞
3. 两者结合，写出有呼应感的回收

---

## 七、架构调整

### 已完成
- **去掉** Agent/Plan/Review 三种手动模式，默认 AGENT

### 待实施
- **Writer Agent → LangGraph 工作流**：章节创作不再走子 Agent，而是走 LangGraph 固定流程
- **保留 Review Agent**：复用到 LangGraph 后处理的并行节点中，做一致性 + 角色 + 逻辑检查
- **复用现有 LangGraph 代码**：`workflows/langgraph_workflow.py` 已有 StateGraph、MemorySaver 等基础设施，重构而非重写
- **保留** ws_chat.py 工具循环：用于自由对话、讨论、查询、编辑等非章节创作场景
- **混合模式**：ws_chat.py 负责日常对话，章节创作时触发 LangGraph 工作流

---

## 八、缓存策略

- **跨章节不追求缓存命中** — 每章更新上下文必然 miss，把精力放在上下文质量和数据可靠性上
- **单轮工具循环是核心收益** — 5-10 次工具调用中，稳定的 system prompt + 章节上下文每次都命中
- **关键是分离稳定和动态内容** — system prompt 绝对不能混入每条消息变化的 conditional_reminders
- 最近章节用小窗口（2-3章），靠故事状态文档覆盖历史信息
- 200K 窗口下不做激进压缩，先跑通再优化

---

## 九、实施路径

### 第一批：基础设施（先跑通，用户手动触发写作时已有更好的上下文）

**1. 消息结构重构**
- conditional_reminders 从 system message 移到用户消息末尾
- 统一注入点（故事状态、读者认知、角色索引通过同一机制注入）
- 工具循环中保持前缀稳定（重建 messages 时不重拼 system prompt）
- **改动文件**：`ws_chat.py`、`context_builder.py`

**2. 故事状态文档（CLAUDE.md 风格）**
- 数据库加 `novel_story_state` 表（存 markdown 文本，每本小说一条）
- 加 MCP 工具 `get_story_state` / `update_story_state`
- 写后自动执行里让 AI 顺手更新
- 注入时格式化到 system message
- **改动文件**：新增 migration、`story_state_tools.py`、`ws_chat.py`

**3. 读者认知系统（结构化）**
- 数据库建 `reader_perspective` 单表（type 字段区分 known/suspense/misconception）
- 加 MCP 工具：`get_reader_perspective` + `add_reader_perspective_entry` + `update_reader_perspective_entry`
- 注入时按 type 分组格式化
- system prompt 加指令：告诉 AI 何时更新读者认知
- **改动文件**：新增 migration、`reader_perspective_tools.py`、`ws_chat.py`

**4. 上下文自动注入**
- 对话开始时自动构建并注入上下文（不等用户开口）
- 上下文预算管理：根据模型窗口大小动态调整注入内容量
- **改动文件**：`ws_chat.py`、`context_builder.py`

### 第二批：体验升级（跑通第一批后再做）

**5. LangGraph 章节创作工作流**
- 重构 `workflows/langgraph_workflow.py`（复用现有 StateGraph / MemorySaver 基础设施）
- 图结构：上下文构建 → 大纲生成 → interrupt 审批 → 补注入 → 写作 → 并行后处理
- create_chapter_workflow 作为 MCP 工具，ws_chat.py 意图识别后调用
- 后处理并行节点：状态更新 + 读者认知 + 时间线 + 摘要 + 向量记忆 + Review（复用 ReviewAgent）
- **改动文件**：重构 `workflows/langgraph_workflow.py`、`ws_chat.py`

**6. 前端适配**
- 大纲审批 UI（展示大纲、允许修改、批准按钮）
- 工作流进度展示（当前处于哪个节点）
- **改动文件**：`EditorPage.tsx`
