# 子 Agent 压缩设计

## 现状

主 Agent 和子 Agent 共用同一个 `Run()` 循环，token 预算超 80% 均触发压缩。两者走不同的压缩路径。

### 主 Agent 压缩 (`Compress`)

- 上下文权威来源：DB（`GetMessagesForAPI` 按 `Session.ActiveVersion` 查询）
- 流程：生成摘要 → 重建 System1/2/3 → 开事务 bump `ActiveVersion` → 全量重写 DB（System1/2/3、reminder、summary、retained 消息副本、边界标记）→ 从 DB 回读新版本消息 → 更新 `opts.Messages`
- `ToAPI=true`，后续 LLM 调用可见

### 子 Agent 压缩 (`compressInMemory`)

- 上下文权威来源：内存（`opts.Messages`）
- 流程：生成摘要 → 保留头部 system 消息（System1/System3，不动）→ 内存重建 `opts.Messages = [sys1, sys3, reminder, summary, ...retained]` → 写边界标记到 DB（唯一 DB 写入，`ToFrontend=true`）→ 更新 runningTokens → `SubAgentVersion++`
- 不 bump `Session.ActiveVersion`，不重写 retained，reminder/summary 不写 DB
- `ToAPI=false`，天然隔离，不会污染主 Agent 上下文
- 子 Agent 结束 → 内存消息随栈帧销毁

### 共用部分

`generateSummary()` 方法：追加 `compressionPrompt` → LLM 生成摘要 → `retainMessages()` 筛选保留消息。主/子复用。

### 关键差异

| | 主 Agent | 子 Agent |
|---|---|---|
| 触发条件 | token ≥ 80% | 同 |
| System1 | 重建（MainAgent） | 原样保留 |
| System2 | 重建 | 无 |
| System3 | 重建 | 原样保留 |
| DB 写入 | 全量 | 仅边界标记 |
| 版本 | bump `Session.ActiveVersion` | `SubAgentVersion++`（内存） |
| 上下文加载 | DB 回读 | 内存直接赋值 |
| ToAPI | true | false |

### 子 Agent 初始化

`RunSubAgent` 构建初始消息：`[System1, System3, user instruction]`。`ActiveVersion` 从父级传入。

---

## 未来：并行子 Agent

当前设计对并行友好——每个子 Agent 的压缩状态完全封装在自己的 `RunOptions` 里（`opts.Messages`、`SubAgentVersion`、`runningTokens`），互不干扰。

并行化前需解决以下独立于压缩的问题：

### EventSeq 数据竞争

```go
*eventSeq++  // 非原子操作
```

需改为 `atomic.AddInt32`，或每个子 Agent 独立序列号，前端按 `SubTaskID` 排序。

### 取消粒度

```go
a.cancels map[string]context.CancelFunc  // key = sessionID
```

一个 session 只有一个 cancel。并行子 Agent 需支持单独取消，key 改为 `sessionID + subTaskID`。

### 主 Agent version bump 时序

同步模型下，主 Agent 阻塞等子 Agent 返回，不会出现中途压缩 bump version。并行后主 Agent 可能在子 Agent 执行期间压缩。当前 `appendMsg` 用 `opts.ActiveVersion` 写入，子 Agent 持有的旧版本号可能与实际时间线不对应。`GetMessagesForFrontend` 不按 version 过滤，不影响前端展示。若未来需要精确的时间线排序，可考虑子 Agent 启动时快照 version，或引入独立的消息序号。
