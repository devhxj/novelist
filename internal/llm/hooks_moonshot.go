package llm

import "strings"

// moonshotBuildRequest 适配 Kimi 开放平台与标准 OpenAI 格式的差异：
// - temperature 等参数 K2 系列有固定值，传入其他值会报错，需移除
// - Kimi 不使用 reasoning_effort，通过 thinking.type 控制思考
// - k2.6/k2.5 不传 thinking → 服务端默认开启，无需干涉
// - kimi-k2.7-code 始终思考，不应传入 thinking 参数
func moonshotBuildRequest(payload map[string]any) map[string]any {
	delete(payload, "temperature")
	delete(payload, "reasoning_effort")

	model, _ := payload["model"].(string)
	if strings.HasPrefix(model, "kimi-k2.7-code") {
		delete(payload, "thinking")
	}

	return payload
}
