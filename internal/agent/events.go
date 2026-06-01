package agent

import "time"

// AgentEventType 定义推送给前端的事件类型。
type AgentEventType int

const (
	EventThinking     AgentEventType = iota // DeepSeek reasoning_content 流式文本
	EventThinkingDone                       // 思考阶段结束
	EventContent                            // 正文流式文本
	EventToolCall                           // 工具调用状态变化
	EventUsage                              // 每次 LLM 调用的 token 用量
	EventError                              // 不可恢复错误
)

// AgentEvent 是 loop 推送给前端的单次事件，对标 Python WebSocket push dict。
// 仅包含前端展示所需字段，不引用任何内部类型。
type AgentEvent struct {
	TurnID       int            `json:"turn_id"`
	Seq          int            `json:"seq,omitempty"` // 单个 turn 内的事件序号，用于前端恢复流式顺序
	Type         AgentEventType `json:"type"`
	Data         string         `json:"data,omitempty"`          // thinking / content 文本 chunk
	ToolName     string         `json:"tool_name,omitempty"`     // EventToolCall 时
	ToolID       string         `json:"tool_id,omitempty"`       // EventToolCall 时
	Phase        string         `json:"phase,omitempty"`         // "selected" | "executing" | "completed" | "failed" | "cancelled" | "loop_detected"
	ToolArgs     map[string]any `json:"tool_args,omitempty"`     // 工具参数快照
	Success      bool           `json:"success,omitempty"`       // 工具执行结果摘要
	ErrMsg       string         `json:"error,omitempty"`         // 失败时的错误信息
	DisplayText  string         `json:"display_text,omitempty"`  // buildDisplay 产出的展示文本
	ActivityKind string         `json:"activity_kind,omitempty"` // 展示类别
	Metadata     map[string]any `json:"metadata,omitempty"`      // buildDisplay 产出的附加信息（如 sub_agent_type）
	Usage        map[string]any `json:"usage,omitempty"`         // token 用量详情（含 usage_ratio / detail）
	Timestamp    time.Time      `json:"timestamp"`               // 事件生成时间
}

// AgentLoopResult 是 Run() 的返回值。
type AgentLoopResult struct {
	FinalText       string
	ThinkingContent string
	TurnCount       int
}
