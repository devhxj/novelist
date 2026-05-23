package mcp_tools

import (
	"context"
	"encoding/json"
	"log/slog"
	"time"

	"github.com/go-playground/validator/v10"
	"github.com/invopop/jsonschema"
	"gorm.io/gorm"

	"novel/internal/session"
)

// ── 接口 ──────────────────────────────────────────────

// Tool 是每个 MCP 工具实现的接口。
type Tool interface {
	Name() string
	Description() string
	Category() ToolCategory
	JSONSchema() json.RawMessage
	ExposeToLLM() bool
	NewArgs() any                            // 返回零值 args 指针，供 Registry 反序列化 + 校验用
	Execute(ctx context.Context, args any, tc ToolContext) (*ToolResult, error)
}

// ── 上下文 ────────────────────────────────────────────

// ToolContext 是工具执行时注入的上下文，包含 DB、会话、回调等。
// 简单 CRUD 工具只用 DB + NovelID，子 agent 工具使用全部字段。
type ToolContext struct {
	DB      *gorm.DB
	NovelID int64
	ToolID  string
	Emit    func(event ToolEvent)
	Session *SessionContext
	//子agent使用
	PersistMsg   func(ctx context.Context, msg *session.Message) error
	BuildDisplay func(ctx context.Context, name string, args map[string]any, phase DisplayPhase) *DisplayInfo
	ParentTaskID string
}

// SessionContext 是工具执行时的会话快照。
type SessionContext struct {
	SessionID        string
	CurrentChapterID int64
	ActiveVersion    int
}

// ── 结果 ──────────────────────────────────────────────

// ToolResult 是工具执行结果。
type ToolResult struct {
	Success  bool            `json:"success"`
	Data     map[string]any  `json:"data,omitempty"`
	Error    string          `json:"error,omitempty"`
	ErrKind  string          `json:"err_kind,omitempty"` // "" = 业务错误，"system" = 系统错误
	Metadata map[string]any  `json:"metadata,omitempty"`
	Inject   []InjectMessage `json:"inject,omitempty"`
}

// InjectMessage 由工具返回，agent loop 会后追加到对话流。固定 to_api=true, to_frontend=false。
type InjectMessage struct {
	Role    string `json:"role"` // "user" | "system"
	Content string `json:"content"`
}

// ── 分类 ──────────────────────────────────────────────

// ToolCategory 是工具分类，仅作组织用途。
type ToolCategory string

const (
	CategoryNovelManagement  ToolCategory = "novel_management"
	CategoryMemoryRetrieval  ToolCategory = "memory_retrieval"
	CategoryConsistencyCheck ToolCategory = "consistency_check"
	CategoryWritingAssistant ToolCategory = "writing_assistant"
)

// ── 事件 ──────────────────────────────────────────────

// ToolEvent 是工具执行中发出的实时事件。
type ToolEvent struct {
	Type string
	Data map[string]any
}

// ── 展示 ──────────────────────────────────────────────

// DisplayPhase 表示工具展示所处的阶段。
type DisplayPhase int

const (
	PhaseSelected  DisplayPhase = iota // 工具被选中
	PhaseExecuting                     // 正在执行
	PhaseCompleted                     // 执行成功
	PhaseFailed                        // 执行失败
	PhaseCancelled                     // 用户取消
)

// DisplayInfo 是某个阶段的前端展示信息。
type DisplayInfo struct {
	DisplayText  string
	ActivityKind string
	Metadata     map[string]any
}

// ── 注册表 ────────────────────────────────────────────

// Registry 是 MCP 工具注册表，无全局单例，由 main.go 创建后注入。
type Registry struct {
	tools    map[string]Tool
	logger   *slog.Logger
	validate *validator.Validate
}

// NewRegistry 创建新的工具注册表。
func NewRegistry(logger *slog.Logger) *Registry {
	return &Registry{
		tools:    make(map[string]Tool),
		logger:   logger,
		validate: validator.New(),
	}
}

// Register 注册工具。同名工具后注册的覆盖先注册的。
func (r *Registry) Register(t Tool) {
	r.tools[t.Name()] = t
}

// Get 按名获取工具。
func (r *Registry) Get(name string) (Tool, bool) {
	t, ok := r.tools[name]
	return t, ok
}

// List 返回全部已注册工具。
func (r *Registry) List() []Tool {
	tools := make([]Tool, 0, len(r.tools))
	for _, t := range r.tools {
		tools = append(tools, t)
	}
	return tools
}

// ListByCategory 按 category 分组返回全部工具信息，供管理端展示。
func (r *Registry) ListByCategory() map[string][]ToolInfo {
	result := make(map[string][]ToolInfo)
	for _, t := range r.tools {
		cat := string(t.Category())
		result[cat] = append(result[cat], ToolInfo{
			Name:        t.Name(),
			Description: t.Description(),
			Category:    cat,
		})
	}
	return result
}

// ToolInfo 是工具的公开元信息。
type ToolInfo struct {
	Name        string `json:"name"`
	Description string `json:"description"`
	Category    string `json:"category"`
}

// OpenAI 生成 OpenAI Function Calling 格式的工具列表。
// allowed=nil 表示全部 expose_to_llm=true 的工具；非 nil 只取白名单内的。
func (r *Registry) OpenAI(allowed map[string]bool) []map[string]any {
	var list []map[string]any
	for _, t := range r.tools {
		if !t.ExposeToLLM() || !allow(allowed, t.Name()) {
			continue
		}
		list = append(list, map[string]any{
			"type": "function",
			"function": map[string]any{
				"name":        t.Name(),
				"description": t.Description(),
				"parameters":  t.JSONSchema(),
			},
		})
	}
	return list
}

// Execute 查表 → 白名单校验 → 反序列化 + validate → 调 Tool.Execute → 兜底 panic + 计时。
// allowed=nil 表示不限制；非 nil 只放行白名单内的工具。
func (r *Registry) Execute(ctx context.Context, name string, rawArgs json.RawMessage, tc ToolContext, allowed map[string]bool) (*ToolResult, error) {
	t, ok := r.Get(name)
	if !ok {
		return &ToolResult{Success: false, Error: "工具不存在: " + name}, nil
	}
	if !allow(allowed, name) {
		return &ToolResult{Success: false, Error: "工具禁止使用: " + name}, nil
	}

	// 反序列化 + 校验
	args := t.NewArgs()
	if err := json.Unmarshal(rawArgs, args); err != nil {
		return &ToolResult{Success: false, Error: "参数格式不正确: " + err.Error()}, nil
	}
	if err := r.validate.Struct(args); err != nil {
		return &ToolResult{Success: false, Error: "参数校验失败: " + err.Error()}, nil
	}

	t0 := time.Now()
	var result *ToolResult
	var err error
	func() {
		defer func() {
			if p := recover(); p != nil {
				result = &ToolResult{Success: false, Error: "服务器内部错误，请稍后重试", ErrKind: "system"}
				err = nil
			}
		}()
		result, err = t.Execute(ctx, args, tc)
	}()

	if result != nil {
		r.logger.Info("mcp tool executed", "tool", name, "elapsed_ms", time.Since(t0).Milliseconds(), "success", result.Success)
	}
	return result, err
}

// AllowSet 将 []string 白名单转为 map[string]bool，供 Registry 方法使用。nil → nil。
func AllowSet(allowed []string) map[string]bool {
	if allowed == nil {
		return nil
	}
	set := make(map[string]bool, len(allowed))
	for _, n := range allowed {
		set[n] = true
	}
	return set
}

// allow 校验工具名是否在白名单中。nil set = 不限制。
func allow(set map[string]bool, name string) bool {
	return set == nil || set[name]
}

// ── JSON Schema 生成 ──────────────────────────────────

// schemaReflector 是全局配置，RequiredFromJSONSchemaTags=true 配合 jsonschema tag 的 required 选项。
// ExpandedStruct 让输出内联不含 $ref/$defs。
var schemaReflector = &jsonschema.Reflector{
	RequiredFromJSONSchemaTags: true,
	ExpandedStruct:             true,
}

// SchemaOf 从带 jsonschema tag 的 struct 生成 OpenAI function calling 兼容的 JSON Schema。
// 内部用 github.com/invopop/jsonschema 生成，然后白名单只取 type/properties/required。
func SchemaOf(v any) json.RawMessage {
	s := schemaReflector.Reflect(v)

	// 先 marshal 再 unmarshal 为 map，只挑顶层三个 key
	b, _ := json.Marshal(s)
	var full map[string]any
	json.Unmarshal(b, &full)

	clean := map[string]any{"type": full["type"]}
	if props, ok := full["properties"]; ok {
		clean["properties"] = props
	}
	if req, ok := full["required"]; ok {
		clean["required"] = req
	}

	raw, _ := json.Marshal(clean)
	return raw
}
