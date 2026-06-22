package llm

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"time"
)

// TestConnectionInput 是连通性测试的参数。
type TestConnectionInput struct {
	ProviderName string `json:"provider_name"`
	ChatURL      string `json:"chat_url"`
	APIKey       string `json:"api_key"`
	ModelID      string `json:"model_id"`
}

// TestConnection 发送最小化请求验证 provider 连通性。返回 error 表示失败。
func TestConnection(ctx context.Context, builtin map[string]Provider, input TestConnectionInput) error {
	chatURL := input.ChatURL
	buildHeaders := func(base map[string]string) map[string]string { return base }
	var buildRequest func(map[string]any) map[string]any

	if bp, ok := builtin[input.ProviderName]; ok {
		if chatURL == "" {
			chatURL = bp.ChatURL
		}
		if bp.BuildHeaders != nil {
			buildHeaders = bp.BuildHeaders
		}
		if bp.BuildRequest != nil {
			buildRequest = bp.BuildRequest
		}
	}

	chatURL = normalizeURL(chatURL)

	payload := map[string]any{
		"model": input.ModelID,
		"messages": []map[string]any{
			{"role": "user", "content": "hi"},
		},
		"max_tokens": 1,
	}
	if buildRequest != nil {
		payload = buildRequest(payload)
	}

	body, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("序列化请求失败: %w", err)
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, chatURL, bytes.NewReader(body))
	if err != nil {
		return fmt.Errorf("创建 HTTP 请求失败: %w", err)
	}

	headers := buildHeaders(map[string]string{
		"Content-Type":  "application/json",
		"Authorization": "Bearer " + input.APIKey,
	})
	for k, v := range headers {
		req.Header.Set(k, v)
	}

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return fmt.Errorf("请求失败: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 400 {
		errBody := make([]byte, 1024)
		n, _ := resp.Body.Read(errBody)
		return fmt.Errorf("[%d] %s", resp.StatusCode, string(errBody[:n]))
	}

	return nil
}
