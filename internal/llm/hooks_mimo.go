package llm

import "strings"

// mimoBuildHeaders 替换标准 Authorization: Bearer <key> 为 Mimo 专用的 api-key 鉴权头。
func mimoBuildHeaders(base map[string]string) map[string]string {
	key := strings.TrimPrefix(base["Authorization"], "Bearer ")
	base["api-key"] = key
	delete(base, "Authorization")
	return base
}
