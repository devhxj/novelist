# Session 设计文档

## 概述

Session 管理对话会话（sessions）和消息（messages），是整个对话系统的基础持久化层。

核心原则：**DB 存全量历史，只追加不删除；LLM context 和前端展示各自独立查询；Session 不持有 messages。**

## 与 Python 版本的差异

### 架构简化

Python 的 Session（Pydantic 内存对象）持有 `messages: list[Message]`，每次 save 全量 DELETE+INSERT（O(N²)），内存/DB/LLM context 三者纠缠不清。重构计划（`docs/refactor/`）设计了新方案但未执行完。

Go 直接落地重构后的架构：

| | Python 现状 | Go |
|---|---|---|
| 消息持久化 | DELETE ALL + INSERT ALL | append-only INSERT |
| Session 持有 messages | 是 | 否，消息走 DB 查询 |
| 可见性控制 | 无（前端暴露 system 消息） | to_api + to_frontend 独立控制 |
| version 机制 | 字段已加但未用 | 完整实现，压缩和回滚的基础 |
| 缓存层 | Redis | 无（本地 SQLite） |
| 多用户 | user_id FK | 无（单用户） |

### 砍掉的

| 字段 | 理由 |
|------|------|
| `user_id` | 单用户应用 |
| `edit_mode` | 运行时状态，不持久化，app handler 管 |
| `chapter_ids` | 运行时从上下文推导，不持久化 |
| `current_chapter_id` | 运行时追踪编辑位置，不持久化 |
| `message_count` | 消息不挂 Session 上，COUNT 查询即可 |
| `get_token_count()` | token 走 session.usage |
| `get_context_usage_ratio()` | 前端自己算 |

### 保留并强化

| 能力 | 说明 |
|------|------|
| to_api / to_frontend | 双可见性控制，不由 role 决定，四种角色都可有任意组合 |
| version + active_version | 压缩不删旧消息，切 active_version 即可回滚 |
| event_type | 标记特殊事件（compression/interrupt/error），前端据此渲染 |
| extra_metadata | JSON 扩展槽，存 tool_calls/thinking_content/tool_call_id/display_text 等 |
| ToAPIFormat() | 消息转为 OpenAI 兼容格式 |
| reasoning_effort | 控制 DeepSeek 推理深度，per-session 可切换 |

## 表结构

```
sessions
  session_id       TEXT PK    — UUID
  novel_id         INTEGER    — FK，索引
  title            TEXT       — 会话标题
  model            TEXT       — "deepseek-v4-pro"
  reasoning_effort TEXT       — "high" | "max" | ""
  summary          TEXT       — 最新压缩摘要
  pending_changes  TEXT       — JSON，待确认编辑变更
  extra_metadata   TEXT       — JSON，扩展槽
  active_version   INTEGER    — 当前活跃的上下文代数
  usage            TEXT       — JSON，最近 LLM token 用量
  created_at / updated_at

messages
  id              INTEGER PK AUTOINCREMENT
  session_id      TEXT    FK → sessions.session_id
  role            TEXT    — "system" | "user" | "assistant" | "tool"
  content         TEXT
  token_count     INTEGER
  extra_metadata  TEXT    — JSON：tool_calls / thinking_content / tool_call_id / display_text / source / agent_type 等
  version         INTEGER — 压缩代数，API 查询时 = session.active_version
  to_api          BOOL    — LLM context 是否需要此消息
  to_frontend     BOOL    — 前端是否需要渲染此消息
  event_type      TEXT    — "compression" | "interrupt" | "error" | ""
  created_at      TEXT
```

索引：
- `idx_sessions_novel (novel_id, updated_at DESC)` — 按小说列出会话
- messages 的 `session_id`、`to_api`、`to_frontend`、`version`、`created_at` 各建单列索引

## 三种查询路径

```
API 查询（LLM context 构建）:
  SELECT * FROM messages
  WHERE session_id=? AND to_api=true AND version=?
  ORDER BY created_at

前端查询（UI 渲染）:
  SELECT * FROM messages
  WHERE session_id=? AND to_frontend=true
  ORDER BY created_at

全量查询（审计/回退）:
  SELECT * FROM messages WHERE session_id=? ORDER BY created_at
```

## 消息角色与可见性

to_api 和 to_frontend 不由 role 决定，由写入方独立设置：

| 消息类型 | role | to_api | to_frontend | 写入时机 |
|----------|------|--------|-------------|---------|
| System1（base prompt） | system | true | false | 创建 session + 压缩时重建 |
| System2（novel context） | system | true | false | 创建 session + 压缩时重建 |
| 工具定义 | — | — | — | 不存 DB，运行时注入 |
| 用户消息 | user | true | true | 发送时立即 |
| 助手回复 | assistant | true | true | 流结束立即 |
| 工具调用结果 | tool | true | true | 执行完立即 |
| 系统提醒（<system-reminder>） | user | true | true/false | 需要时立即 |
| 压缩摘要 | system | true | false | 压缩完成后 |
| 压缩边界事件 | system | false | true | 压缩完成后 |
| 工具失败禁用 | system | true | false | 失败达阈值 |
| 死循环检测 | system | true | false | 检测到时 |
| Token 预算警告 | system | true | false | 超限时 |

## 消息立即持久化

agent loop 内每产生一条消息都即时 INSERT，不等 turn 结束：

1. 用户消息 → AppendMessage（进 loop 前已写入）
2. LLM 流返回 → 拼 assistant+tool_calls → AppendMessage
3. 工具执行完 → 拼 tool 结果 → AppendMessage
4. Inject 消息 → 紧随 tool 后 → AppendMessage
5. 安全机制触发 → system 警告 → AppendMessage
6. LLM 返回 usage → UpdateSessionUsage

## Version 与压缩

压缩时代数流转（详见 `docs/pending-message-storage-refactor.md`）：

```
压缩前（active_version=1）：
  所有消息 version=1

压缩触发：
  1. active_version → 2
  2. 重建 System1+System2 → INSERT version=2
  3. 摘要 system 消息 → INSERT version=2
  4. 边界事件 → INSERT version=2  (event_type="compression")
  5. 保留的近期消息 → UPDATE version=2
  6. 旧消息不动（version=1，自然被过滤）

压缩后：
  version=2：System1/2 + 保留消息 + 摘要 + 边界事件 ← API 查询
  version=1：旧历史 ← 保留但不可见

回滚：切 active_version 即可，一行 UPDATE
```

### KV-cache 友好

System1/System2 在压缩之间保持不变的 version，消息前缀固定，每 turn 只追加尾部，KV-cache 持续命中。压缩时一次性重建前缀，开始新的缓存周期。

## 排序规则

- **sessions 列表**：`updated_at DESC`
- **messages API 查询**：`created_at ASC`（时序不可变）
- **messages 前端查询**：`created_at ASC`

## Store 方法（后续实现）

```go
// Session
CreateSession(sessionID, novelID) → insert + 写入 System1+System2
GetSession(sessionID)
ListSessions(novelID, offset, limit)
UpdateSessionMeta(sessionID, title, model)
UpdateSessionUsage(sessionID, usageJSON)
BumpActiveVersion(sessionID) → active_version+1

// Message — 只追加，不更新不删除
AppendMessage(msg)

// 三条查询
GetMessagesForAPI(sessionID, version)
GetMessagesForFrontend(sessionID)
GetAllMessages(sessionID)

// 压缩用
UpdateMessageVersion(msgIDs, newVersion)
```

## 与其他模块的关系

| 模块 | 关系 |
|------|------|
| Novel | `novel_id` FK |
| Agent Loop | 每 turn 内每消息即时 AppendMessage，结束时 UpdateSessionUsage |
| Context Builder | 从 session store 取 API 消息，prepend 工具定义，应用消息数限制 |
| MCP 工具 | 子 agent 产生的消息写入同一 session，source/agent_type 标记在 extra_metadata |
| 压缩（context 包） | 触发时 BumpActiveVersion + 重建 System1/2 + 插入摘要 + UpdateMessageVersion |
