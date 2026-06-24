package llm

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// DiscoverModels 调用 /models 端点自动发现可用模型列表。
// 从 chatURL 推导 modelsURL（去掉 /chat/completions，拼接 /models），
// 解析标准 OpenAI 格式及 Kimi 等扩展字段。返回的 ModelInfo 中未获取到的字段留零值。
func DiscoverModels(ctx context.Context, chatURL, apiKey string) ([]ModelInfo, error) {
	chatURL = normalizeURL(chatURL)
	baseURL := strings.TrimSuffix(chatURL, "/chat/completions")
	modelsURL := baseURL + "/models"

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, modelsURL, nil)
	if err != nil {
		return nil, fmt.Errorf("创建请求失败: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+apiKey)

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("请求失败: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 400 {
		errBody, _ := io.ReadAll(io.LimitReader(resp.Body, 1024))
		return nil, httpError(resp.StatusCode, errBody)
	}

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, fmt.Errorf("读取响应失败: %w", err)
	}
	if looksLikeHTML(body) {
		return nil, fmt.Errorf("该端点不支持自动发现（服务端返回了网页而非 JSON）")
	}

	var result struct {
		Data []struct {
			ID                string `json:"id"`
			ContextLength     int    `json:"context_length"`
			SupportsImageIn   *bool  `json:"supports_image_in"`
			SupportsVideoIn   *bool  `json:"supports_video_in"`
			SupportsReasoning *bool  `json:"supports_reasoning"`
		} `json:"data"`
	}
	if err := json.Unmarshal(body, &result); err != nil {
		return nil, fmt.Errorf("解析模型列表失败（该端点可能不支持 /models）: %w", err)
	}

	models := make([]ModelInfo, 0, len(result.Data))
	for _, item := range result.Data {
		if item.ID == "" {
			continue
		}
		m := ModelInfo{
			ID:   item.ID,
			Name: modelIDToName(item.ID),
		}
		if item.ContextLength > 0 {
			m.ContextWindow = item.ContextLength
		}
		if item.SupportsReasoning != nil {
			m.SupportsThinking = *item.SupportsReasoning
		}
		if item.SupportsImageIn != nil {
			m.SupportsVision = *item.SupportsImageIn
		} else if item.SupportsVideoIn != nil && *item.SupportsVideoIn {
			m.SupportsVision = true
		}
		models = append(models, m)
	}

	return models, nil
}

func looksLikeHTML(body []byte) bool {
	trimmed := bytes.TrimSpace(body)
	if len(trimmed) == 0 {
		return false
	}
	return trimmed[0] != '{' && trimmed[0] != '['
}

func httpError(code int, body []byte) error {
	switch code {
	case 401:
		return fmt.Errorf("API Key 无效或未配置 (401)")
	case 403:
		if looksLikeHTML(body) {
			return fmt.Errorf("服务端拒绝访问，可能被防火墙拦截，该端点不支持自动发现 (403)")
		}
		return fmt.Errorf("无权访问该端点 (403)")
	case 404:
		return fmt.Errorf("该端点不支持 /models 自动发现 (404)")
	case 429:
		return fmt.Errorf("请求过于频繁，请稍后重试 (429)")
	default:
		msg := string(body)
		if looksLikeHTML(body) {
			msg = "服务端返回了网页，该端点可能不支持自动发现"
		}
		return fmt.Errorf("[%d] %s", code, msg)
	}
}

// modelIDToName 将模型 ID 转为显示名称：首字母大写，- 替换为空格。
func modelIDToName(id string) string {
	s := strings.ReplaceAll(id, "-", " ")
	if len(s) == 0 {
		return s
	}
	return strings.ToUpper(s[:1]) + s[1:]
}
