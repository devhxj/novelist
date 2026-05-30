package llm

// UserLLMConfig 是用户 LLM 配置的持久化格式（加密 JSON）。
// 一个 provider 一个 key，内置模型随代码自动更新，自定义模型需完整填写信息。
type UserLLMConfig struct {
	Providers []Provider `json:"providers"`
	Default   string     `json:"default"` // "deepseek/deepseek-v4-pro"
}

// AvailableModel 是前端下拉列表的模型选项。
type AvailableModel struct {
	Key             string   // "deepseek/deepseek-v4-pro"
	ProviderName    string   // "DeepSeek"
	ModelName       string   // "DeepSeek V4 Pro"
	ContextWindow   int
	MaxOutputTokens int
	ReasoningLevels []string
	SupportsVision  bool
}

// Merge 合并内置模板和用户配置，返回组装完成的 Provider map。
// 内置模型全量加入（随代码版本），用户自定义模型去重追加。只产出用户配置过的 Provider。
func Merge(builtin map[string]Provider, user *UserLLMConfig) map[string]Provider {
	result := make(map[string]Provider, len(user.Providers))

	for _, up := range user.Providers {
		bp, isBuiltin := builtin[up.Name]

		p := Provider{Name: up.Name, ChatURL: up.ChatURL, APIKey: up.APIKey}

		if isBuiltin {
			if p.ChatURL == "" {
				p.ChatURL = bp.ChatURL
			}
			p.BuildRequest = bp.BuildRequest
			p.BuildHeaders = bp.BuildHeaders
			p.ParseError = bp.ParseError

			// 内置模型全量加入（随代码版本自动更新）
			p.Models = append([]ModelInfo{}, bp.Models...)
		}

		// 用户自定义模型去重追加
		for _, um := range up.Models {
			if !modelExists(p.Models, um.ID) {
				p.Models = append(p.Models, um)
			}
		}

		result[up.Name] = p
	}

	return result
}

// Models 从 Provider map 中提取前端下拉列表。
func Models(providers map[string]Provider) []AvailableModel {
	var list []AvailableModel
	for name, p := range providers {
		for _, m := range p.Models {
			list = append(list, AvailableModel{
				Key:             name + "/" + m.ID,
				ProviderName:    p.Name,
				ModelName:       m.Name,
				ContextWindow:   m.ContextWindow,
				MaxOutputTokens: m.MaxOutputTokens,
				ReasoningLevels: m.ReasoningLevels,
				SupportsVision:  m.SupportsVision,
			})
		}
	}
	return list
}

func modelExists(models []ModelInfo, id string) bool {
	for i := range models {
		if models[i].ID == id {
			return true
		}
	}
	return false
}
