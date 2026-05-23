# Agent Loop 设计文档

## 概述

Agent Loop 是对话系统的编排核心——接收消息列表，调用 LLM 流式接口，解析 tool_calls 并执行工具，将结果追加回消息列表，循环直到 LLM 不再调用工具或达到上限。

与 Python `core/agent_loop.py` 逻辑等价，但取消了 WebSocket 推送和 asyncio 回调模型，改用 channel 事件流 + function 字段。

## 整体流程

```
app handler:

  1. sess := store.DB.First(&Session{}, "session_id = ?", id)
  2. apiMsgs := store.GetMessagesForAPI(sessionID, sess.ActiveVersion)
  3. toolDefs := registry.BuildOpenAIFunctions()     // 运行时拼接
  4. messages := append(toolDefs, apiMsgs...)          // toolDefs 在最前面

  5. agent.Run(ctx, RunOptions{
       SessionID:     sess.SessionID,
       NovelID:       sess.NovelID,
       Messages:      messages,
       Tools:         toolDefs,
       ActiveVersion: sess.ActiveVersion,
       Model:         sess.Model,
     })

  6. for event := range eventCh {
       wails.EventsEmit("agent:"+taskID, event)
       if event.Type == EventUsage {
         store.UpdateSessionUsage(sessionID, event.UsageJSON)
       }
     }
```

## Agent 结构

使用 function 字段而非单方法 interface——Go 标准库风格（参考 `http.HandlerFunc`、`context.CancelFunc`）：

```go
type Agent struct {
    llm          *llm.Client
    executeTool  func(ctx context.Context, name string, rawArgs json.RawMessage, tc ToolContext) *ToolResult
    buildDisplay func(ctx context.Context, name string, args map[string]any, phase DisplayPhase) *DisplayInfo
    persistMsg   func(ctx context.Context, msg *session.Message) error
    logger       *slog.Logger
}

type RunOptions struct {
    SessionID     string
    NovelID       int64
    Messages      []map[string]any
    Tools         []map[string]any
    ActiveVersion int
    Model         string
    MaxTurns      int               // 默认 50
    MaxContextTokens int            // 默认 800000
}
```

三个 function 字段的职责：

| 字段 | 职责 | 调用时机 |
|------|------|---------|
| `executeTool` | 执行 MCP 工具，返回结果 | tool_call_end 事件到达时 |
| `buildDisplay` | 生成展示文本（selected/executing/completed/failed） | 工具生命周期各阶段 |
| `persistMsg` | 消息即时持久化到 DB | 每拼一条消息立刻调用 |

## ToolContext 和 ToolResult

```go
type ToolContext struct {
    DB      *gorm.DB
    NovelID int64
    ToolID  string
}

type ToolResult struct {
    Success  bool
    Data     map[string]any
    Error    string
    ErrKind  string            // "system" 表示系统异常（DB/网络），"" 表示业务错误
    Metadata map[string]any
    Inject   []InjectMessage   // 工具返回的额外上下文消息
}

type InjectMessage struct {
    Role    string             // "user" | "system"
    Content string
}
```

## DisplayPhase

展示文本分三阶段（Python 同样）：

```go
type DisplayPhase int

const (
    PhaseSelected  DisplayPhase = iota  // tool_call_start，工具被选中
    PhaseExecuting                      // 参数解析完毕，开始执行
    PhaseCompleted                      // 执行成功
    PhaseFailed                         // 执行失败
)

type DisplayInfo struct {
    DisplayText  string
    ActivityKind string
    Metadata     map[string]any
}
```

## AgentEvent — 实时推送前端

```go
type AgentEventType int

const (
    EventThinking      AgentEventType = iota  // DeepSeek reasoning_content
    EventThinkingDone                          // 思考结束
    EventContent                               // 文本 chunk
    EventToolCall                              // 工具调用状态变化
    EventToolCallArgs                          // 工具参数流式（edit_chapter 实时预览）
    EventUsage                                 // token 用量
    EventDone                                  // 循环结束
    EventError                                 // 错误
)

type AgentEvent struct {
    Type      AgentEventType
    Data      string         // thinking/content 文本
    Phase     string         // selected/executing/completed/failed/loop_detected
    ToolName  string
    ToolID    string
    ToolArgs  map[string]any
    Result    *ToolResult
    Display   *DisplayInfo
    Usage     map[string]any
    FinalText string
    TurnCount int
    Error     error
}
```

## 核心循环

```
while turn < maxTurns:
    toolOutputs := []
    responseBuffer := ""
    thinkingBuffer := ""
    isThinking := false

    for event := range llm.ChatStream(messages, tools, model):

        // ---- 取消检查 ----
        select case <-ctx.Done():
            return partial response

        case = "thinking":
            isThinking = true
            thinkingBuffer += event.Data
            eventCh <- EventThinking

        case = "content":
            if isThinking: eventCh <- EventThinkingDone; isThinking = false
            responseBuffer += event.Data
            eventCh <- EventContent

        case = "tool_call_start":
            eventCh <- EventThinkingDone
            info := buildDisplay(name, {}, PhaseSelected)
            eventCh <- EventToolCall{Phase: "selected", Display: info}

        case = "tool_call_arguments":
            eventCh <- EventToolCallArgs{name, argsText}

        case = "tool_call_end":
            // Phase executing
            buildDisplay(name, args, PhaseExecuting)
            eventCh <- EventToolCall{Phase: "executing"}

            // 执行工具
            result := executeTool(name, rawArgs, ToolContext{DB, NovelID, toolID})

            // Phase completed/failed
            phase := "completed" / "failed"
            buildDisplay(name, args, phase)
            eventCh <- EventToolCall{Phase: phase, Result: result}

            // 失败计数：仅系统异常 (ErrKind="system") 计入，业务错误不计数
            if !result.Success && result.ErrKind == "system":
                failCnt[toolName]++
            else:
                failCnt[toolName] = 0
            if failCnt[toolName] >= 3:
                // 注入 system 警告，提醒 LLM 停用该工具
            // Inject 暂存

            toolOutputs += {name, toolID, args, result}

        case = "usage":
            eventCh <- EventUsage

    // ---- 流结束 ----

    if len(toolOutputs) > 0:
        // 1. 拼 assistant+tool_calls 消息 → persist + append messages
        // 2. 拼 tool 结果消息 ×N → persist + append messages
        // 3. 拼 inject 消息 ×N → persist + append messages
        // 4. 死循环检测 → 触发时注入 system 警告
        // 5. Token 预算检查 → 超限时注入 system 警告
        // 6. agentEventCh <- EventToolCall{Phase: "loop_detected"}（如果触发）
        turn++
    else:
        break

eventCh <- EventDone{FinalText, TurnCount}
```

## 消息持久化（agent 内部）

每 turn 工具调用完成后，逐条拼消息并立即 persist + 追加到内存 messages：

```
1. assistant 消息（带 tool_calls + reasoning_content）
   msg := session.Message{
       Role: "assistant", Content: responseBuffer,
       ExtraMetadata: {tool_calls, thinking_content},
       Version: activeVersion, ToAPI: true, ToFrontend: true,
   }
   persistMsg(msg)
   messages = append(messages, msg.ToAPIFormat())

2. tool 结果消息 ×N
   persistMsg(role="tool", content=resultJSON,
              extra_metadata={tool_call_id}, version=activeVersion)
   messages = append(...)

3. inject 消息 ×N（紧随对应 tool 后）
   persistMsg(role=inject.Role, content=inject.Content,
              version=activeVersion, to_frontend=false)
   messages = append(...)

4. 安全警告（如果触发）
   persistMsg(role="system", content=warning,
              version=activeVersion, to_frontend=false)
   messages = append(...)
```

## 安全机制

| 机制 | 逻辑 | 触发后动作 |
|------|------|-----------|
| 工具失败降级 | 同工具连续系统异常（ErrKind="system"）3 次 | persist system 警告（to_api=true, to_frontend=false）。业务错误（ErrKind=""）不计数——LLM 换个参数就能成功 |
| 死循环检测 | 最近 4 轮 ≤2 种模式 + 全是只读工具 + turn≥4 | persist system 警告 + push loop_detected 事件 |
| Token 预算 | tiktoken 逐消息计数 > maxContextTokens | persist system 警告 |
| 取消 | ctx.Done() 在每收到 SSE event 时检查 | 返回当前 partial 文本 |

只读工具集合：`search_story_memory`、`get_timeline`、`get_chapter_content`、`get_chapter_list`、`get_characters`、`get_locations`、`get_novel_info`、`get_creative_profile`、`get_story_arcs`、`get_story_state`、`get_reader_perspective`

## 与 Python 的差异

| | Python | Go |
|---|---|---|
| 通信 | `ws_manager.send_personal_message` | `eventCh <- AgentEvent` |
| 工具执行 | `tool_call_handler` 闭包 | `executeTool` function 字段 |
| 展示文本 | `display_handler` 闭包 | `buildDisplay` function 字段 |
| 消息持久化 | `on_message` 闭包 | `persistMsg` function 字段 |
| 用量持久化 | `on_usage` 闭包 | 调用方从 EventUsage 自行持久化 |
| 取消 | `asyncio.Event` | `context.Context` |
| 回调模型 | 5 个闭包参数 | 3 个 function 字段 + event channel |

### function 字段 vs interface

Python 的设计模式是闭包回调——在 `ws_chat.py` 中定义 `_handle_tool`、`_display`、`_on_message` 等闭包捕获 `registry`、`session`、`websocket`、`novel_id` 等上下文，然后传给 `run_agent_loop`。

Go 采用 function 字段而非单方法 interface：

- **相同的灵活性**：调用方同样用闭包捕获 DB、registry、session 等
- **零样本代码**：不需要声明 `type XxxExecutor interface { Execute(...) }` 再写 `type impl struct{}` 再 `func (i impl) Execute(...)`
- **Go 标准库先例**：`http.HandlerFunc`、`context.CancelFunc`、`io.ReaderFunc`（提案中）

当 function 字段需要多方法时（如 ToolRegistry 既有 List 又有 Execute），再抽 interface。单方法优先用 func。

## 与子 Agent 的关系

Python 中 `run_subagent` 工具复用了 `run_agent_loop`，传不同的 system prompt + 工具白名单 + max_turns：

```python
run_agent_loop(
    messages=sub_messages,
    tools=sub_tools,           # 白名单工具
    tool_call_handler=sub_handler,  # 白名单门控
    max_turns=20,
    # ...其他回调复用
)
```

Go 同样复用——创建 Agent 实例时注入不同的 `executeTool`（带白名单校验）、`buildDisplay`、`persistMsg`，其他逻辑完全相同。

## 排序规则

- **消息追加**：`created_at ASC`（时序不可逆）
- **工具执行结果**：按 LLM 返回顺序，不做重排
- **并发限制**：工具串行执行（跟随 LLM 的 tool_calls 顺序）

## 与其他模块的关系

| 模块 | 关系 |
|------|------|
| `llm` | Agent 持有 `*llm.Client`，调 `ChatStream` |
| `session` | 每消息即时 `persistMsg`，turn 结束 update usage |
| `mcp` | 工具定义从 registry 生成，执行通过 `executeTool` function |
| `context` | 调用方在进 loop 前组装好 messages，agent 不管 |
| `app` | app handler 创建 Agent 实例，注入 function 字段，消费 event channel |
