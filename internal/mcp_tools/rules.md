# MCP 工具开发规范

## 1. 文件结构

- 一个领域一个文件：`xxx_tools.go`
- 公共工具函数放 `utils.go`
- 注册函数必须是独立顶层函数：`func RegisterXxxTools(r *Registry)`
- 文件末尾放 Register 函数

## 2. 工具实现模板

```go
type XxxTool struct{}

func (t *XxxTool) Name() string        { return "xxx" }
func (t *XxxTool) Description() string { return "工具描述，给 LLM 看，说明适用场景和参数用法" }
func (t *XxxTool) Category() ToolCategory { return CategoryNovelManagement }

func (t *XxxTool) JSONSchema() json.RawMessage {
    return SchemaOf(XxxArgs{})
}

func (t *XxxTool) ExposeToLLM() bool { return true }

func (t *XxxTool) Execute(ctx context.Context, args map[string]any, tc ToolContext) (*ToolResult, error) {
    b, _ := json.Marshal(args)
    var a XxxArgs
    if err := json.Unmarshal(b, &a); err != nil {
        return &ToolResult{Success: false, Error: "参数校验失败: " + err.Error()}, nil
    }
    return &ToolResult{Success: true, Data: ...}, nil
}
```

Args 结构体用 `jsonschema` tag 定义 JSON Schema：

```go
type XxxArgs struct {
    Name string `json:"name" jsonschema:"required,description=名称"`
    Type string `json:"type" jsonschema:"description=类型"`
}
```

## 3. 错误处理

| 类型 | 做法 |
|------|------|
| 业务错误（参数不合法、资源不存在） | `return &ToolResult{Success: false, Error: "中文消息"}, nil` |
| 意外异常（DB、网络） | 不 catch，让 `Registry.Execute()` 兜底，设 `ErrKind: "system"` |

工具不要用 `recover()` 包裹 `Execute()` 方法体。

## 4. 工具白名单

- 主 agent 和子 agent 各自持有 `AllowedTools []string`，定义在 agent 配置中
- `Registry.OpenAI(allowed map[string]bool)` 生成 LLM 可见的工具列表
- `Registry.Execute(allowed map[string]bool)` 执行时双重校验——工具不存在 vs 工具不可用
- `allowed=nil` 表示不限制
- 使用 `AllowSet([]string{"a","b"})` 将 []string 转为 map[string]bool，在 agent 配置 init 中调用一次

## 5. Category

| Category | 适用工具 |
|----------|---------|
| `novel_management` | 小说/章节/角色/地点 CRUD |
| `writing_assistant` | 创作辅助（大纲、弧线、时间线、编辑、子Agent） |
| `memory_retrieval` | 检索查询（记忆、角色记忆、时间线、弧线、读者视角） |
| `consistency_check` | 一致性审查 |

仅作组织用途，不驱动不同行为。

## 6. 注册

- `ExposeToLLM()` 默认 `true`
- 新增工具后在 `registry.go` 调用 Register 函数

## 7.格式化
- 返回的消息能格式化成md的就格式化，不要直接返回原始json。方便llm理解
- 图结构可以返回邻接表，而不是原始的点和边。