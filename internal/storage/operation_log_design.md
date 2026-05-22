# 操作日志与 Turn 级回退设计

## 动机

用户可撤销某一轮对话的全部改动，回到该 turn 还没发出时的状态。回退范围覆盖两类数据：

| 数据类型 | 回退方式 |
|---------|---------|
| 领域元数据（角色、时间线、弧线、地点、读者认知、偏好等） | operation_log 逆向执行 |
| 对话消息（messages） | turn_id 列直接 DELETE |

## 核心决策

### 为什么不用 SQLite Session Extension

`mattn/go-sqlite3` 的源码包含 session extension（`sqlite3session.h` 在 `sqlite3-binding.h` 中），但被 `#if defined(SQLITE_ENABLE_SESSION)` 守卫且默认不编译。要启用需要 fork 库并添加 CFLAGS + 手写 CGo 封装。维护成本高于收益，且锁定 SQLite 失去跨数据库能力。

### 为什么不用文件快照

SQLite 文件包含会话消息表，随使用增长可达几百 MB。为回退几十 KB 的元数据改动而复制整个文件，浪费巨大且回退粒度不符需求。

### 为什么选 GORM Callback

- 零侵入：所有 store 代码无需改动
- 不会漏记：所有走 GORM 的写操作必经 callback
- 通用：不绑定 SQLite，切换到 MySQL/PostgreSQL 时同样可用
- GORM Callback 接口是公开且稳定的，不担心升级

## turn_id 的生命周期

### 生成

agent loop 在每个 turn 开始时调用 `NextTurn`：

```sql
UPDATE sessions SET last_turn_id = last_turn_id + 1 WHERE session_id = ? RETURNING last_turn_id
```

一步原子递增 + 持久化。per-session 独立计数，从 1 开始。

### 传递

```go
ctx = storage.WithTurn(ctx, sessionID, turnID)
```

turn 信息通过 `context.Context` 传递，callback 从 `db.Statement.Context` 中提取。所有 store 方法已接受 `ctx context.Context`，无需签名变更。

### 永不回退

`last_turn_id` 只增不减。回退后历史 turn 编号不回收——下一个新 turn 继续递增。这保证了 turn_id 作为"曾经发生过的 turn"的唯一标识。

## operation_log 表结构

```sql
CREATE TABLE operation_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    turn_id     INTEGER NOT NULL,
    session_id  TEXT    NOT NULL,
    operation   TEXT    NOT NULL,  -- "create" | "update" | "delete"
    table_name  TEXT    NOT NULL,  -- "characters", "timeline_entries", etc.
    entity_id   TEXT    NOT NULL,  -- JSON 主键条件：{"id":5} 或 {"novel_id":1,"scope":"next"}
    old_values  TEXT,              -- JSON，create 时为 ""
    new_values  TEXT,              -- JSON，delete 时为 ""
    created_at  TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_oplog_rollback ON operation_log(session_id, turn_id, id);
```

### entity_id 设计

主键值序列化为 JSON map，key 使用 GORM 的列名（`f.DBName`），不是 Go 字段名。示例：
- 单主键：`{"id": 5}`
- 复合主键：`{"novel_id": 1, "scope": "next"}`

回退时反序列化为 `map[string]any` 直接用作 WHERE 条件，支持任意主键结构。

### old_values / new_values 格式

Go struct 经 `json.Marshal` 序列化的完整 JSON。key 是 JSON tag 名。回退时反序列化为 `map[string]any` 用于 GORM 的 `Updates(map)` 或 `Create(map)`。

**前置条件**：JSON tag 必须与 GORM column tag 一致，且 `json:"omitempty"` 标签会导致零值字段不序列化进 JSON，回退恢复时丢失原始零值。故**所有需回退的表字段不要使用 `omitempty`**(当前代码库满足此条件)。

## Callback 流程

### 普通 INSERT

```
BeforeCreate → PK 值含零（自增未分配）→ 跳过查旧行
AfterCreate  → 记录 operation="create"，old="", new=序列化行
回退时       → DELETE WHERE entity_id
```

### UPSERT（ON CONFLICT DO UPDATE）

```
BeforeCreate → PK 值全部非零 → SELECT 查旧行
              → 有旧行：InstanceSet("oplog:before_create_old", oldRow)
              → 无旧行：InstanceSet(nil) — 真正的新行
AfterCreate  → InstanceGet("oplog:before_create_old")
              → 有值：这是对已有行的更新 → 记录 operation="update"，old=旧行, new=新行
              → 无值：真正的 INSERT → 记录 operation="create"
```

关键：BeforeCreate 只对"所有 PK 值都非零"的实体查旧行。自增 ID 为 0 的普通 INSERT 跳过，避免无效 SELECT。

### UPDATE

```
BeforeUpdate → SELECT 旧行 → InstanceSet("oplog:update_old", oldRow)
AfterUpdate  → 取 oldRow，序列化新旧 → 记录 operation="update"
              → oldRow 为 nil（旧行不存在）→ 跳过不记录
              → old == new → 跳过不记录（零字段更新等无效操作）
回退时       → UPDATE SET old_values WHERE entity_id
```

### DELETE

```
BeforeDelete → SELECT 旧行 → InstanceSet("oplog:delete_old", oldRow)
AfterDelete  → 取 oldRow → 记录 operation="delete"，old=旧行, new=""
              → oldRow 为 nil → 跳过不记录
回退时       → INSERT old_values
```

## 回退执行

```go
RollbackTo(ctx, db, sessionID, targetTurn)
```

### 语义

撤销 `[targetTurn, last_turn_id]` 区间所有 turn 的变更，回到 targetTurn 还没发出的状态。

### 步骤（事务包裹）

1. `SELECT last_turn_id FROM sessions` → 得到当前最新 turn
2. `SELECT * FROM operation_log WHERE session_id=? AND turn_id >= targetTurn AND turn_id <= lastTurn ORDER BY id DESC`
3. 逐条逆向执行：
   - create → DELETE WHERE entity_id
   - update → UPDATE SET old_values（覆盖整行）
   - delete → INSERT old_values
4. `DELETE FROM messages WHERE session_id=? AND turn_id >= targetTurn AND turn_id <= lastTurn`
5. 清理已执行的 operation_log 记录

### 为何不支持"回退中间某一段"

回退某一段（如只回退 turn 15-18，保留 19-20）在语义上不成立。例如：turn 15 修改了角色 A 的 importance，turn 17 基于这个新值又修改了一次。只撤 turn 15 会导致 turn 17 的操作依赖的行状态不一致。

`RollbackTo` 只接受一个 targetTurn 参数，从当前最新退回到目标点，全退。

## 并发与事务

- 单用户本地应用，无并发 session 竞争
- `fetchOldRow` 使用 `db.Session(&gorm.Session{NewDB: true, SkipHooks: true})` —— NewDB 隔离 Statement 状态，SkipHooks 防止递归触发。底层连接/事务与当前 callback 共享
- 回退操作在单个事务内完成

## 边界情况与限制

### 批量更新不自动记录

`db.Model(&T{}).Where(...).Updates(map)` 这种批量更新不走 struct callback（`db.Statement.Schema` 为 nil），不会自动记录。当前所有 store 的写操作都是单行操作，无此情况。

**如果未来需要**：此类操作需显式调用 `storage.RegisterManualOp()`（尚未实现）。

### 裸 SQL 不自动记录

`db.Exec(...)` 完全不经过 GORM callback。当前 store 均无裸 SQL 写操作。

### 回退时可能发生的冲突

正常使用路径不会冲突。唯一可能冲突的场景：用户回退 turn 15 之前，手动修改了某个被 turn 15 改过的字段值（如通过外部工具直接改 SQLite）。此时 `old_values` 与当前 DB 中的值不一致，UPDATE 会覆盖——这正是回退的预期行为（恢复到 turn 14 的状态）。

### 跨 Session 并发修改

多个 Session 先后修改同一实体时，一个 Session 的回退可能影响另一个 Session 的数据。例如：

```
Session A, turn 3: INSERT 角色 X
Session B, turn 7: UPDATE 角色 X importance=5
Session A, turn 15: RollbackTo(1) → DELETE 角色 X
```

Session B 的修改悬空：角色 X 已被删除，或即使未删除其值也被 Session A 的回退覆盖。operation_log 是 per-session 的，无法感知跨 session 的修改。

**不修复**：单用户单小说场景下概率极低，解决需要实体级跨 session 引用追踪，成本远高于收益。

## 与 message 表的关系

message 不走 operation_log，而是通过 `turn_id` 列直接 DELETE。原因：
- message 是纯追加（INSERT only），从不修改
- 每 turn 几十条消息，走日志再逐条逆向是绕圈
- 直接 `DELETE WHERE session_id=? AND turn_id BETWEEN ? AND ?` 简单高效

## 与 session 表的关系

- `sessions.last_turn_id`：原子自增，只增不降，永不回收
- `operation_log` 和 `messages` 的 turn_id 都来源于此值
- session 删除 → 级联清理该 session 的 operation_log 和 messages
- 小说删除 → 级联清理该小说所有 session 的相关数据

## 变更摘要

| 文件 | 变更 |
|------|------|
| `internal/storage/operation_log.go` | 新建：TurnInfo、WithTurn、RegisterOplogHooks、RollbackTo 及所有辅助函数 |
| `internal/session/types.go` | Session 加 `LastTurnID`，Message 加 `TurnID` |
| `internal/session/store.go` | 加 `NextTurn` 方法 |
