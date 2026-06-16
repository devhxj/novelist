# 踩坑记录

## 1. Wails v2: context.Context 与方法绑定

**症状：** Chat() 调用报 `reflect: Call using zero Value argument`

**根因：** Wails v2 不会自动剥离 `context.Context` 参数。生成的 TypeScript binding 包含了它，前端传 `null` 过去 Go reflection 解析不了。这是 Wails v3 才支持的特性，v2 官方明确表示不会 backport。

**修复：** 去掉 `Chat()` 的 `ctx` 参数，函数内从 `a.ctx` 派生：

```go
func (a *App) Chat(input ChatInput) (*ChatResult, error) {
    ctx, cancel := context.WithCancel(a.ctx)  // a.ctx 来自 OnStartup
    defer cancel()
}
```

---

## 2. GORM: bool 零值跳过 + DB 默认值冲突

**症状：** 所有 tool 消息的 `to_frontend` 自动变成 `true`，工具结果泄漏到前端

**根因：** GORM v2 INSERT 时会跳过零值字段。`false` 是 bool 的零值，GORM 直接跳过，不写入 SQL。DB 列有 `DEFAULT 1`，跳过后 DB 填入 `1`，变成了 `true`。

```go
ToFrontend bool `gorm:"default:1"`  // 错：GORM 跳过 false，DB 填 1
```

**修复：** DB 默认值与 Go 零值对齐：

```go
ToFrontend bool `gorm:"default:0"`  // 对：GORM 跳过 false，DB 填 0
```

---

## 3. Go 切片传值 + append 失效

**症状：** LLM 每轮看到的上下文只有初始消息，工具结果和历史对话全部丢失，死循环调用工具

**根因：** Go slice header（指针+长度+容量）是值类型。`appendMsg` 里的 `append` 修改的是副本，外层 `opts.Messages` 的 slice header 没变。下一轮循环 `ChatStream(ctx, opts.Messages)` 拿到的始终是初始消息。

```go
// 错：传值，append 改了副本
func (a *Agent) appendMsg(..., opts RunOptions, ...) {
    opts.Messages = append(opts.Messages, msg)
}

// 对：传指针
func (a *Agent) appendMsg(..., opts *RunOptions, ...) {
    opts.Messages = append(opts.Messages, msg)
}
```

---

## 4. json.RawMessage 裸 JSON 破坏 API 协议

**症状：** DeepSeek 返回 400 `"invalid type: map, expected a string"`

**根因：** OpenAI/DeepSeek 协议规定 `tool_calls[n].function.arguments` 必须是 JSON 字符串（`"{}"`），不是 JSON 对象（`{}`）。`json.RawMessage` 实现了 `json.Marshaler`，序列化时直接输出裸字节不带引号。存到 `extra_metadata` 里变成 `arguments: {}`，回传时 DeepSeek 校验拦截。

```go
// 错：裸 JSON 对象，协议要的是字符串
"arguments": json.RawMessage(o.rawArgs),   // → arguments: {}

// 对：Go 字符串序列化后自动加引号
"arguments": string(o.rawArgs),            // → arguments: "{}"
```

---

## 5. validator 标签顺序错误

**症状：** 所有嵌入 PageArgs 的工具校验失败，LLM 空转重试

**根因：** go-playground/validator 从左到右执行。`validate:"min=1,omitempty"` 先跑 `min=1`，`Page=0` 直接失败，轮不到 `omitempty` 跳过。

```go
Page int `validate:"min=1,omitempty"`  // 错
Page int `validate:"omitempty,min=1"`  // 对
```

共 4 处（PageArgs ×2 + Importance ×2）。

---

## 6. invopop/jsonschema: 切片元素 $ref 被 SchemaOf 丢弃 + tag 中英文逗号截断

**症状：** LLM 调用嵌套数组工具时猜错字段名。例如 `create_arc_node` 能看见 `arc_nodes` 但看不见内部有 `story_arc_id`、`title` 等字段，只能靠枚举尝试。

**根因：** 两个独立的 bug 叠加：

**Bug A — `ExpandedStruct` 对切片不生效。** `invopop/jsonschema` 的 `ExpandedStruct: true` 只内联直接嵌套的 struct 字段，`[]T` 的 items 仍然用 `$ref` 指向 `$defs`。而 `SchemaOf` 的 clean 逻辑只保留 `type`/`properties`/`required`，把 `$defs` 丢了。LLM 看到 `"$ref": "#/$defs/CreateArcNodeItem"` 但永远找不到定义。

**Bug B — tag parser 用英文逗号分隔 option。** jsonschema tag 的值按 `,` 切分为 `key=value` option。description 里包含英文逗号（如 `["剑术","隐身"]`）时，`,` 被当作新 option，后面的内容全部丢失，描述截断。

**修复 A：** `SchemaOf` 构建 clean map 时保留 `$defs`，再用递归函数 `inlineDefs` + `resolveRefs` 把 `$ref` 替换为内联定义，消除跨节点跳转。

```go
// SchemaOf 现在走：full → clean（含 $defs）→ inlineDefs（替换所有 $ref）→ 输出
func inlineDefs(schema map[string]any) map[string]any {
    defs, _ := schema["$defs"].(map[string]any)
    delete(schema, "$defs")
    return resolveRefs(schema, defs).(map[string]any)
}
```

**修复 B：** 描述中的 JSON 示例改用中文逗号 `，` 替代英文 `,`，避免触发 tag parser 的分隔逻辑。这些描述是给 LLM 看的文本，不是真 JSON。

**测试：** `internal/mcp_tools/schema_test.go` — 单元测试覆盖单层/嵌套切片内联，集成测试遍历 registry 全部 31 个工具验证无 `$ref`/`$defs`。

**教训：** 所有带英文逗号的 jsonschema description 都会被截断。以后写 tag 看到 JSON 示例就用中文标点。

---

## 7. SQLite 写事务内访问同一个 `*sql.DB` 导致死锁

**症状：** 已有 session 的对话正常，但新建 session 第一条消息发出去后无响应，日志在 `next turn` 之后中断。随后所有 DB 查询全部阻塞。

**根因：** `Chat()` 的事务内调用 `writeSystemMessages`，后者通过 `a.db` 执行 `System3` 读查询。但 `a.db` 和 `a.session.DB` 是同一个 `*sql.DB` 实例，SQLite 驱动默认连接池大小只有 1。写事务占着唯一连接，`a.db` 的读请求永远等不到连接——死锁。

```go
// Chat() 事务中：
a.session.DB.Transaction(func(tx *gorm.DB) error {
    a.writeSystemMessages(tx, ...)  // 内部：System3(a.db, ...) ← 卡死！
})

// a.db == a.session.DB → 同一连接池 → 唯一的连接被 tx 占着
```

**为什么 MySQL/PG 不会：** 它们是客户端-服务器架构 + MVCC，写事务不阻塞读，连接池有足够多连接。SQLite 是嵌入式单文件、单写者，默认连接池=1，事务内任何对同一 `*sql.DB` 的访问都会互相阻塞。

**修复：** `writeSystemMessages` 内用 `tx` 替代 `a.db` 执行 `System3` 查询。`tx` 就是当前写事务的连接，SQLite 允许在同一连接上读写。

```go
// 修复前：
sys3, err := agentcfg.System3(a.db, novelID)  // 死锁

// 修复后：
sys3, err := agentcfg.System3(tx, novelID)     // 同一条连接，不阻塞
```

**教训：** SQLite 事务内的一切 DB 操作必须走 `tx`，任何对原始 `*sql.DB` 的访问都可能死锁。
