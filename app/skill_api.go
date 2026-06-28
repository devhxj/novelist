package app

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"novel/internal/config"
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

// DeleteSkillInput 是 DeleteSkill 的入参。
type DeleteSkillInput struct {
	NovelID int64  `json:"novel_id"`
	Name    string `json:"name"`
	Source  string `json:"source"` // "novel" | "user"
}

// DeleteSkill 删除用户级或小说级技能文件。内置技能不可删除。
func (a *App) DeleteSkill(input DeleteSkillInput) error {
	if a.skill == nil {
		return fmt.Errorf("skill store 未初始化")
	}
	if input.Name == "" {
		return fmt.Errorf("技能名称不能为空")
	}

	source := input.Source
	if source != "novel" && source != "user" {
		return fmt.Errorf("只能删除用户级或小说级技能")
	}

	var filePath string
	switch source {
	case "novel":
		if input.NovelID <= 0 {
			return fmt.Errorf("小说 ID 无效")
		}
		filePath = filepath.Join(config.NovelSkillsDir(input.NovelID), input.Name+".md")
	case "user":
		filePath = filepath.Join(config.UserSkillsDir(), input.Name+".md")
	}

	if err := os.Remove(filePath); err != nil {
		if os.IsNotExist(err) {
			return fmt.Errorf("技能文件不存在: %s", input.Name)
		}
		return fmt.Errorf("删除技能文件失败: %w", err)
	}

	// 重新加载对应层级
	switch source {
	case "novel":
		if err := a.skill.ReloadNovel(input.NovelID, config.NovelSkillsDir(input.NovelID)); err != nil {
			a.logger.Warn("删除技能后重新加载小说级技能失败", "name", input.Name, "err", err)
		}
	case "user":
		if err := a.skill.ReloadUser(config.UserSkillsDir()); err != nil {
			a.logger.Warn("删除技能后重新加载用户级技能失败", "name", input.Name, "err", err)
		}
	}

	return nil
}
