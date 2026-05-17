package llm

import "fmt"

// Provider 定义一个大模型供应商的完整配置。
// 默认行为走 OpenAI 兼容格式，钩子函数用于处理供应商差异。
type Provider struct {
	Name         string                                          // 供应商名称，如 "DeepSeek"
	ChatURL      string                                          // 聊天补全端点，如 "https://api.deepseek.com/v1/chat/completions"
	APIKey       string                                          // API 密钥
	Models       []ModelInfo                                     // 可用模型列表
	BuildRequest func(payload map[string]any) map[string]any     // 发送前改造请求体，nil 则原样发送
	BuildHeaders func(base map[string]string) map[string]string  // 发送前改造请求头，nil 则使用默认 Bearer 鉴权
	ParseError   func(body []byte) error                         // 解析非标准错误响应体，nil 则使用默认 OpenAI 格式解析
}

// ModelInfo 描述一个具体模型的元信息。
type ModelInfo struct {
	ID              string // 模型标识，如 "deepseek-v4-pro"
	Name            string // 显示名称，如 "DeepSeek V4 Pro"
	ContextWindow   int    // 上下文窗口大小（token 数）
	MaxOutputTokens int    // 最大输出 token 数
	ReasoningEffort string // 思考程度，"high" / "max"，非推理模型留空
	SupportsVision  bool   // 是否支持图片输入
}

// APIError 表示 LLM API 的调用错误，包含可重试标记。
type APIError struct {
	StatusCode int
	Message    string
	Retryable  bool
}

func (e *APIError) Error() string {
	return fmt.Sprintf("[%d] %s", e.StatusCode, e.Message)
}

// StreamEventType 流式事件的类型。
type StreamEventType int

const (
	EventThinking          StreamEventType = iota // 模型推理/思考内容（DeepSeek reasoning_content）
	EventContent                                  // 普通文本内容
	EventToolCallStart                            // 工具调用开始（携带名称和 ID）
	EventToolCallArguments                        // 工具参数增量（arguments 累计字符串）
	EventToolCallEnd                              // 工具调用完成（arguments 已 parse 为 JSON）
	EventUsage                                    // 本次调用 token 用量
	EventError                                    // 错误（HTTP 错误或网络异常）
)

// StreamEvent 流式对话过程中产出的单个事件。
// Data 承载 thinking/content 文本，Delta 承载工具调用信息，Usage 承载 token 统计。
// 错误事件通过 Error 字段传递。
type StreamEvent struct {
	Type  StreamEventType
	Data  string         // EventThinking / EventContent 的文本数据
	Delta *ToolCallDelta // EventToolCallStart / EventToolCallArguments / EventToolCallEnd
	Usage map[string]any // EventUsage 时，原样透传 API 返回的 usage 对象
	Error error          // EventError 时的错误信息
}

// ToolCallDelta 描述一次工具调用的增量信息。
// 工具调用通过 SSE 流分多次下发：先来 ID 和名称，再逐片来 arguments 字符串。
// 客户端内部维护按 index 的累积缓冲区，流结束后一次性 json.Unmarshal。
type ToolCallDelta struct {
	ToolName      string         // EventToolCallStart 时填充
	ToolID        string         // EventToolCallStart 时填充
	ArgumentsText string         // EventToolCallArguments 时，累积后的完整 JSON 字符串
	ArgumentsJSON map[string]any // EventToolCallEnd 时，json.Unmarshal 后的完整参数
}
