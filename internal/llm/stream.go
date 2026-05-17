package llm

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strings"
)

// Client 是 LLM 流式调用的传输层。
// 接收 messages 和 tools（OpenAI 规范格式），拼装 payload 并管理 SSE 传输。
type Client struct {
	provider Provider
	http     *http.Client
	logger   *slog.Logger
}

// NewClient 创建 LLM 客户端。
func NewClient(p Provider, log *slog.Logger) *Client {
	return &Client{
		provider: p,
		http: &http.Client{
			Timeout: 0, // 流式请求不设超时，由 ctx 控制
		},
		logger: log,
	}
}

// DefaultDeepSeek 返回 DeepSeek 默认 Provider 配置。
func DefaultDeepSeek(apiKey string) Provider {
	return Provider{
		Name:    "DeepSeek",
		ChatURL: "https://api.deepseek.com/v1/chat/completions",
		APIKey:  apiKey,
		Models: []ModelInfo{
			{
				ID:              "deepseek-v4-flash",
				Name:            "DeepSeek V4 Flash",
				ContextWindow:   1000000,
				MaxOutputTokens: 384000,
				ReasoningEffort: "high",
				SupportsVision:  false,
			},
			{
				ID:              "deepseek-v4-pro",
				Name:            "DeepSeek V4 Pro",
				ContextWindow:   1000000,
				MaxOutputTokens: 384000,
				ReasoningEffort: "high",
				SupportsVision:  false,
			},
		},
		BuildRequest: nil, // 默认 OpenAI 兼容格式，无需额外改造
	}
}

// ChatStream 发起流式对话，返回 SSE 事件 channel。
//
// messages / tools 由调用方按 OpenAI Function Calling 格式组装。
// Client 负责拼装完整 payload 并注入流式参数和钩子处理。
//
// ctx 取消时底层 HTTP 连接被中止，channel 随之关闭。
func (c *Client) ChatStream(
	ctx context.Context,
	messages []map[string]any,
	tools []map[string]any,
	model string,
	opts *CallOptions,
) <-chan StreamEvent {
	ch := make(chan StreamEvent, 8)

	go func() {
		defer close(ch)

		payload := c.buildPayload(messages, tools, model, opts)

		body, err := json.Marshal(payload)
		if err != nil {
			ch <- StreamEvent{Type: EventError, Error: fmt.Errorf("序列化请求体失败: %w", err)}
			return
		}

		req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.provider.ChatURL, bytes.NewReader(body))
		if err != nil {
			ch <- StreamEvent{Type: EventError, Error: fmt.Errorf("创建 HTTP 请求失败: %w", err)}
			return
		}

		// 组装请求头
		headers := map[string]string{
			"Content-Type":  "application/json",
			"Authorization": "Bearer " + c.provider.APIKey,
		}
		if c.provider.BuildHeaders != nil {
			headers = c.provider.BuildHeaders(headers)
		}
		for k, v := range headers {
			req.Header.Set(k, v)
		}

		resp, err := c.http.Do(req)
		if err != nil {
			ch <- StreamEvent{Type: EventError, Error: &APIError{
				StatusCode: 0,
				Message:    fmt.Sprintf("请求失败: %s", err),
				Retryable:  true,
			}}
			return
		}
		defer resp.Body.Close()

		// HTTP 错误
		if resp.StatusCode >= 400 {
			errBody, _ := io.ReadAll(resp.Body)
			msg := parseDefaultError(errBody).Error()
			if c.provider.ParseError != nil {
				msg = c.provider.ParseError(errBody).Error()
			}
			ch <- StreamEvent{Type: EventError, Error: &APIError{
				StatusCode: resp.StatusCode,
				Message:    msg,
				Retryable:  statusRetryable(resp.StatusCode),
			}}
			return
		}

		// SSE 逐行解析
		c.parseSSE(ch, resp.Body)
	}()

	return ch
}

// buildPayload 组装 LLM API 请求体。
func (c *Client) buildPayload(
	messages []map[string]any,
	tools []map[string]any,
	model string,
	opts *CallOptions,
) map[string]any {
	payload := map[string]any{
		"model":    model,
		"messages": messages,
		"stream":   true,
		"stream_options": map[string]any{"include_usage": true},
	}

	if len(tools) > 0 {
		payload["tools"] = tools
	}

	// 从 ModelInfo 取模型默认值
	var info *ModelInfo
	for i := range c.provider.Models {
		if c.provider.Models[i].ID == model {
			info = &c.provider.Models[i]
			break
		}
	}

	temperature := 0.7
	maxTokens := 4096
	if opts != nil && opts.Temperature != nil {
		temperature = *opts.Temperature
	}
	if opts != nil && opts.MaxTokens != nil {
		maxTokens = *opts.MaxTokens
	} else if info != nil && info.MaxOutputTokens > 0 {
		maxTokens = info.MaxOutputTokens
	}
	payload["temperature"] = temperature
	payload["max_tokens"] = maxTokens

	// 推理/思考参数
	reasoningEffort := ""
	thinkingEnabled := false
	if opts != nil && opts.ReasoningEffort != nil {
		reasoningEffort = *opts.ReasoningEffort
	} else if info != nil {
		reasoningEffort = info.ReasoningEffort
	}
	if opts != nil && opts.ThinkingEnabled != nil {
		thinkingEnabled = *opts.ThinkingEnabled
	} else if info != nil && info.ReasoningEffort != "" {
		thinkingEnabled = true
	}

	if thinkingEnabled && reasoningEffort != "" {
		payload["thinking"] = map[string]string{"type": "enabled"}
		payload["reasoning_effort"] = reasoningEffort
	}

	// Provider 钩子改造
	if c.provider.BuildRequest != nil {
		payload = c.provider.BuildRequest(payload)
	}

	return payload
}

// parseSSE 解析 SSE 流，产出 StreamEvent。
func (c *Client) parseSSE(ch chan<- StreamEvent, body io.Reader) {
	scanner := bufio.NewScanner(body)
	scanner.Buffer(make([]byte, 0, 64*1024), 2*1024*1024) // 行最长 2MB

	// 工具调用累积缓冲区：按 index 对齐
	type accToolCall struct {
		id        string
		name      string
		arguments strings.Builder
	}
	accumulated := make([]accToolCall, 0, 4)

	for scanner.Scan() {
		line := scanner.Text()

		// SSE data 行
		const prefix = "data: "
		if !strings.HasPrefix(line, prefix) {
			continue
		}
		data := line[len(prefix):]

		// 流结束标记
		if data == "[DONE]" {
			break
		}

		// 解析 JSON chunk
		var chunk map[string]any
		if err := json.Unmarshal([]byte(data), &chunk); err != nil {
			c.logger.Warn("SSE JSON 解析失败", "line", data, "error", err)
			continue
		}

		// 提取 usage（可能出现在最后一个 chunk）
		if usage, ok := chunk["usage"].(map[string]any); ok && usage != nil {
			ch <- StreamEvent{Type: EventUsage, Usage: usage}
		}

		// 提取 choices
		choices, ok := chunk["choices"].([]any)
		if !ok || len(choices) == 0 {
			continue
		}
		choice, ok := choices[0].(map[string]any)
		if !ok {
			continue
		}
		delta, ok := choice["delta"].(map[string]any)
		if !ok {
			continue
		}

		// reasoning_content → EventThinking
		if reasoning, ok := delta["reasoning_content"].(string); ok && reasoning != "" {
			ch <- StreamEvent{Type: EventThinking, Data: reasoning}
		}

		// content → EventContent
		if content, ok := delta["content"].(string); ok && content != "" {
			ch <- StreamEvent{Type: EventContent, Data: content}
		}

		// tool_calls delta → 按 index 累积
		toolCalls, ok := delta["tool_calls"].([]any)
		if !ok {
			continue
		}
		for _, tcRaw := range toolCalls {
			tc, _ := tcRaw.(map[string]any)
			if tc == nil {
				continue
			}
			idx := int(tc["index"].(float64))

			// 扩展累积缓冲区
			for len(accumulated) <= idx {
				accumulated = append(accumulated, accToolCall{})
			}
			acc := &accumulated[idx]

			// ID（首次出现）
			if id, ok := tc["id"].(string); ok && id != "" && acc.id == "" {
				acc.id = id
				ch <- StreamEvent{
					Type:  EventToolCallStart,
					Delta: &ToolCallDelta{ToolID: id},
				}
			}

			// function 子对象
			fn, ok := tc["function"].(map[string]any)
			if !ok {
				continue
			}

			// 名称（首次出现）
			if name, ok := fn["name"].(string); ok && name != "" && acc.name == "" {
				acc.name = name
				ch <- StreamEvent{
					Type:  EventToolCallStart,
					Delta: &ToolCallDelta{ToolName: name, ToolID: acc.id},
				}
			}

			// arguments 增量追加
			if args, ok := fn["arguments"].(string); ok && args != "" {
				acc.arguments.WriteString(args)
				ch <- StreamEvent{
					Type: EventToolCallArguments,
					Delta: &ToolCallDelta{
						ToolName:      acc.name,
						ToolID:        acc.id,
						ArgumentsText: acc.arguments.String(),
					},
				}
			}
		}
	}

	if err := scanner.Err(); err != nil {
		ch <- StreamEvent{Type: EventError, Error: &APIError{
			StatusCode: 0,
			Message:    fmt.Sprintf("SSE 流读取异常: %s", err),
			Retryable:  true,
		}}
	}

	// 流结束后，parse 所有完整工具调用
	for i := range accumulated {
		acc := &accumulated[i]
		if acc.name == "" || acc.arguments.Len() == 0 {
			continue
		}
		var args map[string]any
		if err := json.Unmarshal([]byte(acc.arguments.String()), &args); err != nil {
			c.logger.Warn("工具参数 JSON 解析失败", "tool", acc.name, "raw", acc.arguments.String(), "error", err)
			continue
		}
		ch <- StreamEvent{
			Type: EventToolCallEnd,
			Delta: &ToolCallDelta{
				ToolName:      acc.name,
				ToolID:        acc.id,
				ArgumentsText: acc.arguments.String(),
				ArgumentsJSON: args,
			},
		}
	}
}

// statusRetryable 判断 HTTP 状态码对应的错误是否可重试。
// 429（限流）、408（超时）、5xx（服务端错误）可重试。
func statusRetryable(code int) bool {
	if code == 429 || code == 408 {
		return true
	}
	if code >= 500 && code < 600 {
		return true
	}
	return false
}

// parseDefaultError 按 OpenAI 标准格式解析错误响应体。
func parseDefaultError(body []byte) error {
	var resp struct {
		Error struct {
			Message string `json:"message"`
		} `json:"error"`
	}
	if err := json.Unmarshal(body, &resp); err != nil || resp.Error.Message == "" {
		return fmt.Errorf("请求失败: %s", string(body))
	}
	return fmt.Errorf("%s", resp.Error.Message)
}
