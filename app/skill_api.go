package app

import (
	"fmt"
	"strings"

	"novel/internal/skill"
)

// ListSkillsInput 是 ListSkills 的入参。
type ListSkillsInput struct {
	NovelID int64 `json:"novel_id"`
}

// ListSkills 返回所有可用 skill 的元数据（同名覆盖：novel > user > builtin）。
func (a *App) ListSkills(input ListSkillsInput) []skill.SkillMeta {
	if a.skill == nil {
		return nil
	}
	return a.skill.ListMeta(input.NovelID)
}

// ExtractStyleInput 是 ExtractStyle 的入参。
type ExtractStyleInput struct {
	NovelID         int64  `json:"novel_id"`
	Sample          string `json:"sample"`
	ProviderName    string `json:"provider_name"`
	ModelID         string `json:"model_id"`
	ReasoningEffort string `json:"reasoning_effort"`
}

// ExtractStyleResult 是 ExtractStyle 的返回值。
type ExtractStyleResult struct {
	Name        string `json:"name"`
	Description string `json:"description"`
	RawContent  string `json:"raw_content"`
	FilePath    string `json:"file_path"`
}

// ExtractStyle 分析样本文字的写作风格，调用 LLM 生成仿写 skill，返回预览结果。
// 不自动保存——前端让用户确认后再调 SaveContent 写入。
func (a *App) ExtractStyle(input ExtractStyleInput) (*ExtractStyleResult, error) {
	if a.llmClient == nil {
		return nil, fmt.Errorf("LLM 客户端未初始化")
	}

	sk, err := skill.Extract(a.ctx, a.llmClient, input.Sample, input.ProviderName, input.ModelID, input.ReasoningEffort)
	if err != nil {
		return nil, fmt.Errorf("提取风格失败: %w", err)
	}

	safeName := strings.Map(func(r rune) rune {
		if r == '/' || r == '\\' || r == ':' {
			return -1
		}
		return r
	}, sk.Name)

	return &ExtractStyleResult{
		Name:        sk.Name,
		Description: sk.Description,
		RawContent:  sk.RawContent,
		FilePath:    fmt.Sprintf("skills/%s.md", safeName),
	}, nil
}
