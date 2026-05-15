# 上下文压缩 & 存储重构 — 设计文档

## 当前存储架构（问题分析）

### 模型四层映射（过度复杂）

```
API/WS 层:      JSON 协议（前后端通信）
内存运行时:      Session (Pydantic) → Message[] (Pydantic)
翻译层:         SessionStorage（手动逐字段转换）
DB ORM 层:      ChatSession (SQLAlchemy) + ChatMessage (SQLAlchemy)
```

### 问题 1：Message 重复定义

| Pydantic `Message` | ORM `ChatMessage` |
|---|---|
| `role: MessageRole` (enum) | `role: str` |
| `content: str` | `content: str` |
| `timestamp: datetime` | `created_at: datetime` |
| `token_count: int` | `token_count: int` |
| `importance: float` (0-1) | `importance: int` (0-100) |
| `metadata: dict` | `extra_metadata: dict(JSON)` |

两个类几乎相同，但 `SessionStorage._db_to_session()` 手动逐字段翻译 60+ 行。加新字段（version/to_api/to_frontend）需改三处：ORM 列、Pydantic 字段、翻译函数映射。

**改进**：Pydantic `Message` 加 `model_config = ConfigDict(from_attributes=True)`（Pydantic v2），直接从 ORM 对象解析。不再手动翻译。`Message.model_validate(orm_msg)` 即可。

### 问题 2：DB 没有存全量历史

`SessionStorage._save_to_db()` 实现：

```python
DELETE FROM chat_messages WHERE session_id = ?;  -- 全删
INSERT INTO chat_messages ...;                    -- 重插所有
```

每次保存都是全量覆写。一旦压缩重建 `session.messages`，旧消息从 DB 物理删除，历史永久丢失。DB 存的不是"全量历史"，而是"当前 LLM context 的快照"。

### 问题 3：Session 既是真相源又是运行时

`Session` (Pydantic) 在内存持有 `messages: list[Message]`，`_save_to_db` 直接把内存状态刷到 DB。不存在"DB 存全量、内存动态构建 LLM context"的分离。内存就是 DB 的镜像，LLM context 就是 DB 的镜像。三者纠缠不清。

### 问题 4：SessionManager 和 SessionStorage 职责模糊 + 服务层过度拆分

`SessionManager` 有 `save_session`/`load_session`/`add_message` 等，但大部分是 `SessionStorage` 的薄包装。`SessionStorage` 374 行核心工作只有三件事：Redis 缓存（~50 行）、DB 读写+字段翻译（~150 行，最复杂）、用户会话索引（~30 行）。改完后消息是单条 INSERT 追加，不需要批量覆写，不需要手动字段翻译（from_attributes），存储逻辑缩到 100 行以内，没必要独立一个类——合并到 `SessionManager` 直接调 async session 即可。压缩逻辑（`ContextCompressor`）嵌在 `session_manager.py` 里，跟 session 管理耦合，应拆到独立的 `context/compression.py`。

### 问题 5：写入性能 O(N²)

每次追加一条消息触发 N+1 条 SQL（DELETE N + INSERT N）。200 条消息就是 200 次 DELETE + 200 次 INSERT。增长到千条级别不可接受。

### 问题 6：前端不应拿到 system 消息（及其他不应展示的消息）

当前 `session_loaded` 把 `recent_messages` 全量推给前端，System1/2、压缩摘要、工具定义等 system 角色消息全暴露给用户。未来还可能需要注入 user 角色的消息给 LLM 但不给用户看到（如自动补充偏好提示）。所以 `to_frontend` 跟 role 无关——四种角色都可能有 `to_frontend=false` 的消息。

---

## 新存储方案

### 核心思路

- **DB 存全量历史**：只追加、不删除（压缩不删旧消息）
- **LLM context 动态构建**：从 DB 加载时按规则过滤/替换，构建当前运行的 messages
- **前端拿到的只是展示层消息**：system 消息不透出

### 字段设计

`chat_messages` 表新增字段：

```sql
ALTER TABLE chat_messages ADD COLUMN
  version      INT NOT NULL DEFAULT 1,      -- 属于第几代上下文构建
  to_api       BOOL NOT NULL DEFAULT TRUE,   -- LLM context 是否需要此消息
  to_frontend  BOOL NOT NULL DEFAULT TRUE,   -- 前端是否需要渲染此消息
  event_type   VARCHAR(32) NULL;             -- 事件标记: compression / interrupt / error / null(普通消息)
```

`chat_sessions` 表新增：

```sql
ALTER TABLE chat_sessions ADD COLUMN
  active_version INT NOT NULL DEFAULT 1;     -- 当前活跃的版本号
```

### 查询路径

```sql
-- LLM context: 只查当前版本 + 需要发给 API 的消息
SELECT * FROM chat_messages
WHERE session_id = ? AND to_api = TRUE AND version = ?
ORDER BY created_at;

-- 前端渲染: 所有需要展示的消息（含事件标记）
SELECT * FROM chat_messages
WHERE session_id = ? AND to_frontend = TRUE
ORDER BY created_at;
```

索引：
```sql
INDEX idx_api (session_id, to_api, version, created_at)
INDEX idx_frontend (session_id, to_frontend, created_at)
```

### 四种消息类型

| | to_api=true | to_api=false |
|---|---|---|
| **to_frontend=true** | 普通对话（user/assistant 消息） | 事件标记（压缩完成、中止、断连…） |
| **to_frontend=false** | 系统注入（压缩摘要等 LLM 需要看到的消息，不限 role） | 暂无（预留） |

`to_frontend=false` 不限于 system 角色。未来可能注入 user 角色的消息给 LLM 但不给用户看到（如自动补充的偏好提示），也设 `to_frontend=false`。

### version 方案优势

- 旧消息不需要反激活：天然被 `WHERE version = active_version` 过滤
- 压缩只 UPDATE 保留消息的 version（一条 SQL），不删任何数据
- 天然支持 version 级别的回滚：切 `active_version` 即可回到某次压缩前的完整状态。不支持同一 version 内的消息级撤销（那属于另一个 feature）
- 其他事件标记（打断、断连等）同机制：`to_api=false, to_frontend=true` + `event_type` 区分

### 系统注入不存 DB

System1（base prompt）、System2（novel context）、工具定义等运行时动态注入的内容**不存 DB**，仅存在于内存构建 LLM context 阶段 prepend。这样前端永远看不到，也不需要 `to_frontend=false` 来控制。

### 写入开销对比

| 操作 | 当前 | 新方案 |
|---|---|---|
| 追加一条消息 | DELETE N + INSERT N (O(N²)) | INSERT 1 (O(1)) |
| 压缩 | 不存在 | UPDATE ~15 条保留消息 + INSERT 2 条（摘要 + 边界标记） |

### 旧数据迁移

现有数据没有新字段，加列时设默认值：
- `to_api = TRUE, to_frontend = TRUE, version = 1`
- session 加 `active_version = 1`
- 一次 ALTER TABLE + 回填，不影响现有功能

---

## 压缩方案

### 设计原则

- LLM 负责压缩，不需要后端计算重要性评分
- 全量上下文发给 LLM 做摘要（前缀缓存命中，成本低效果最好）
- 绝对禁止截断——LLM 看到完整历史才能准确摘要
- 断点续传：压缩后 LLM 必须知道自己干到了哪里、接下来做什么
- User 消息尽量保留（核心要求和偏好），但按条数限制（最近 10-20 条）
- 旧 assistant/tool 消息全部进入摘要

### 触发方式（两种，统一逻辑）

| 触发 | 条件 | 前端表现 |
|---|---|---|
| 自动压缩 | turn 内 `usage_ratio >= 80%` | ContextRing 鎏金动画 + 对话流中展示"正在压缩..."（无分割线，无头像） |
| 手动压缩 | 用户点击 ContextRing 里的压缩按钮 | 分割线 + "我将开始压缩历史对话" + 鎏金动画 → 完成后显示"对话历史已压缩" |

手动和自动走同一套后端逻辑，区别仅在前端展示和触发来源。

### 压缩流程（agent_loop 内）

```
循环开头检测 usage_ratio >= 80%
  → 在 messages 末尾追加 system 消息（压缩提示词，5 section 模板）
  → 调用 generate_stream（无工具，纯文本流式），LLM 基于完整上下文生成结构化摘要
  → 捕获摘要文本
  → 从 messages 中移除刚才追加的压缩提示词
  → 重建 messages（应用消息保留规则）
  → 发送 WebSocket compression_done 事件，携带 usage 数据
  → 循环自然继续（LLM 读摘要 → 调 MCP 工具刷新状态 → 接着干活）
```

### 压缩提示词（5 section，侧重不同）

```
═══ 上下文压缩摘要 ═══

## 已完成的事项
（每个一句话，不再重复。最多 15 条，从最近的开始保留）

## 进行中（断点）
（最详细：当前做什么、做到哪一步、下一步是什么。这是最重要的部分）

## 用户偏好和要求
（从 user 消息提炼，核心的写作风格、约束、反复强调的事项）

## 关键决策和设定变更
（已确认的情节走向、角色设定、世界观规则、命名等）

## 待办事项
（已计划但未开始的任务清单）

---
请先调用相关 MCP 工具（get_chapter_detail、get_characters、get_story_state 等）刷新当前上下文，确认状态后再继续工作。
```

压缩提示词作为 system 消息追加到 messages 末尾。压缩完成后该条 system 消息被移除，摘要以 system 角色注入到消息列表末尾。虽然中途插入 system 不符合 OpenAI 协议规范，但 DeepSeek 实测可行。

### 消息重建规则

压缩后 messages 重建：

- **必保留**：System1（base prompt）、System2（novel context + 工具定义）—— 运行时 prepend
- **保留最近**：最近 10-20 条 user 消息 + 最近 4-6 条对话（维持连贯性）
- **替换为摘要**：其余一切（旧 assistant、旧 tool、旧 system 注入）
- **摘要位置**：末尾（作为最后一条 system 消息，确保 LLM 在下一轮读到）

### 摘要的多层叠加

多次压缩后 messages 里不会堆积多个摘要块。每次压缩重建时，旧的摘要 system 消息也被替换，永远只有最新一条摘要。

### _running_tokens 重建

压缩后 messages 被重建，agent_loop 维护的 `_running_tokens`（tiktoken 计数）必须重新计算：遍历重建后的全量 messages，用 tiktoken 重新 tokenize 初始化。否则后续 usage 事件的 detail 是脏数据。

压缩调用本身也是一次 LLM 调用，也会返回 usage，但那是压缩过程的用量。压缩完成后 `_running_tokens` 重新初始化时没有 API 锚点缩放——detail 是纯 tiktoken 计数。下一轮正常 LLM 调用返回 usage → 触发 `on_usage` → 等比缩放回来。这个过渡状态极短（一个 turn 内），不影响。

### 压缩失败降级

如果压缩 LLM 调用失败（超时、API 报错），Keep 原 messages，记录日志。下一轮循环如果 usage 仍然 >= 80%，再次尝试压缩。不做截断兜底。

### 压缩取消

用户取消任务时 `asyncio.CancelledError` 传播到压缩调用，捕获后 messages 保持压缩前状态。取消后循环结束，下次新 turn 进入时检查 usage → 达到阈值自然触发压缩（天然保证）。

### DB 视角

压缩完成后：

1. 插入一条压缩摘要 system 消息：`to_api=true, to_frontend=false, version=active_version`
2. 插入一条边界标记消息：`to_api=false, to_frontend=true, event_type='compression', version=active_version`
3. `active_version += 1`
4. 保留的近期消息 UPDATE `version = active_version`
5. 旧消息不动（version 低于 active_version，自动被 API 查询过滤）
6. 旧消息的 to_api 仍为 true——如果未来需要回滚到旧版本，切 active_version 即可

---

## 文件职责

### 模块重组：sessions 模块独立

当前 Session/Message 等模型和 SessionManager 在 `chat/` 模块下，`sessions/` 只有一个 `session_storage.py`，名不副实。重构后：

```
backend/sessions/
  models.py           ← ORM: ChatSession, ChatMessage（从 chat/models.py 移入）
  schema.py           ← Pydantic: Session, Message, NovelContext, ChapterContext（从 chat/session_manager.py 移入）
  manager.py          ← SessionManager + 内建存储逻辑（吸收 session_storage.py）
  router.py           ← REST API（保留，更新端点）
```

`chat/` 模块只保留 WebSocket 聊天相关（ws_chat.py、edit_mode.py、diff_engine.py 等）。

### 简化后的类职责

| 类 | 位置 | 职责 |
|---|---|---|
| `ChatSession` / `ChatMessage` (ORM) | `sessions/models.py` | DB 表定义，加新字段（version/to_api/to_frontend/event_type） |
| `Message` / `Session` / `NovelContext` / `ChapterContext` (Pydantic) | `sessions/schema.py` | 内存模型，`model_config = ConfigDict(from_attributes=True)` |
| `SessionManager` | `sessions/manager.py` | 创建/加载 session、保存消息（单条 INSERT）、双查询（api/frontend）、Redis 缓存、构建 LLM context、吸收原 SessionStorage 的所有逻辑 |
| `CompressionService` | `context/compression.py`（新建）| 压缩提示词、调 LLM 生成摘要、消息重建、重建 _running_tokens |
| `SessionStorage` | **删除** | 代码合并到 SessionManager |

### 第一步：存储改造

| 文件 | 改动 |
|---|---|
| `backend/chat/models.py` | `ChatSession`/`ChatMessage` 移出到 `sessions/models.py` |
| `backend/sessions/models.py` | 新建：ORM 模型 + 加新字段（version/to_api/to_frontend/event_type） |
| `backend/sessions/schema.py` | 新建：Pydantic 模型（Session/Message/NovelContext/ChapterContext），`model_config = ConfigDict(from_attributes=True)` |
| `backend/sessions/manager.py` | 新建：SessionManager + 内建存储逻辑（吸收 session_storage.py） |
| `backend/chat/session_manager.py` | 删除 ContextCompressor；Pydantic 模型移出；SessionManager 移出 |
| `backend/sessions/session_storage.py` | 删除，逻辑合并到 manager.py |

### 第二步：压缩实现

| 文件 | 改动 |
|---|---|
| `backend/context/compression.py` | 新建：压缩提示词常量、构建压缩请求、解析摘要、消息重建逻辑 |
| `backend/core/agent_loop.py` | 循环开头调用 `CompressionService.maybe_compress()` |
| `backend/chat/ws_chat.py` | 更新 import（session 相关从 sessions 模块导入）；处理 `{type: "compress"}` 手动压缩消息；推送 compression 事件 |

### 第三步：前端

| 文件 | 改动 |
|---|---|
| `frontend/src/services/wsEditorService.ts` | 加 compression 事件类型 |
| `frontend/src/components/common/ContextRing.tsx` | 加手动压缩按钮（悬停 Popover 内） |
| `frontend/src/pages/editor/EditorPage.tsx` | 处理压缩事件：分割线渲染、鎏金动画、turn 状态 |

---

## 决策记录

1. **context_window 用十进制**：1M = 1,000,000，不是 1,048,576（LLM 行业统一十进制计量 token）
2. **detail 等比缩放到 API total_tokens**：用 tiktoken 算各角色占比，乘以 API 的 total_tokens 作为锚，确保分项加总 = 总量。tiktoken 用 `o200k_base`（GPT-4o，中文远优于 cl100k_base）
3. **压缩摘要放末尾**：确保 LLM 下一轮读到。虽然 system 角色插中间不符合 OpenAI 协议，但 DeepSeek 实测可行
4. **压缩摘要作为 assistant 角色 vs system 角色**：最终选择 system，语义更接近"系统给的上下文信息"
5. **手动压缩走 turn**：和自动压缩用同一套后端逻辑。手动多了分割线 + 状态文案，自动只显示"正在压缩..."（无分割线无头像）+ ContextRing 鎏金动画
6. **前端 UX 参考**：分割线 + "我将开始压缩历史对话" → 鎏金动画 → "对话历史已压缩"。不伪造 LLM 消息，"开始压缩"是前端自己塞的
7. **version 方案优于 active flag 方案**：旧消息不需显式反激活、天然支持 version 级回滚、历史留痕完整
8. **全量平铺存储 + version 区分**：所有消息在一张表按时间排序，靠 version 和 to_api/to_frontend 控制可见性，不做分段表
9. **未来 MCP todo 工具**：压缩摘要的"待办事项"section 保持结构化，以后可喂给 todo 工具管理
10. **system 注入不存 DB**：System1/System2/工具定义运行时动态 prepend，不落入持久化，不给前端看到
11. **Message 用 Pydantic v2 ConfigDict**：`model_config = ConfigDict(from_attributes=True)`，不用旧版 `class Config`
12. **SessionStorage 合并到 SessionManager**：新方案下存储逻辑极简（单条 INSERT + 双查询），不需要独立服务类
13. **压缩独立类**：`CompressionService` 放在 `context/compression.py`，不依赖 SessionManager
14. **to_frontend 跟 role 无关**：任意角色都可能有不可见消息，前端可见性由 `to_frontend` 字段独立控制
15. **回滚粒度**：支持 version 级别的完整回滚（切 active_version），不支持同一 version 内的消息级撤销
16. **sessions 模块独立**：Session/Message 模型和 SessionManager 从 `chat/` 移入 `sessions/`，`chat/` 只保留 WebSocket 聊天
17. **ORM 和 Pydantic 分文件**：`sessions/models.py`（ORM）+ `sessions/schema.py`（Pydantic），不混在一起
