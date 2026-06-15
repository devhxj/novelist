package app

import (
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
