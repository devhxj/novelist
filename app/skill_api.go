package app

import (
	"fmt"
	"os"

	"novel/internal/config"
	"novel/internal/git"
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

// GetSkillInput 是 GetSkill 的入参。
type GetSkillInput struct {
	NovelID int64  `json:"novel_id"`
	Name    string `json:"name"`
}

// GetSkill 返回完整 skill（含正文 Content），用于前端 markdown 渲染。
func (a *App) GetSkill(input GetSkillInput) (*skill.Skill, error) {
	if a.skill == nil {
		return nil, fmt.Errorf("app: skill store 未初始化")
	}
	sk, ok := a.skill.Get(input.NovelID, input.Name)
	if !ok {
		return nil, fmt.Errorf("app: skill %q 不存在", input.Name)
	}
	return sk, nil
}

// ReloadUserSkills 重新扫描用户级 skill 目录。
func (a *App) ReloadUserSkills() error {
	if a.skill == nil {
		return fmt.Errorf("app: skill store 未初始化")
	}
	return a.skill.ReloadUser(config.UserSkillsDir())
}

// ReloadNovelSkillsInput 是 ReloadNovelSkills 的入参。
type ReloadNovelSkillsInput struct {
	NovelID int64 `json:"novel_id"`
}

// ReloadNovelSkills 重新扫描指定小说的 skill 目录。
func (a *App) ReloadNovelSkills(input ReloadNovelSkillsInput) error {
	if a.skill == nil {
		return fmt.Errorf("app: skill store 未初始化")
	}
	return a.skill.ReloadNovel(input.NovelID, config.NovelSkillsDir(input.NovelID))
}

// ── 写 / 删 ─────────────────────────────────────────────────

// SaveUserSkillInput 是 SaveUserSkill 的入参。
type SaveUserSkillInput struct {
	Content string `json:"content"` // 完整 markdown 原文
}

// SaveUserSkill 校验并写入用户级 skill 文件，然后热重载内存。
func (a *App) SaveUserSkill(input SaveUserSkillInput) error {
	if a.skill == nil {
		return fmt.Errorf("app: skill store 未初始化")
	}
	dir := config.UserSkillsDir()
	if err := a.saveSkill(dir, input.Content); err != nil {
		return err
	}
	return a.skill.ReloadUser(dir)
}

// SaveNovelSkillInput 是 SaveNovelSkill 的入参。
type SaveNovelSkillInput struct {
	NovelID int64  `json:"novel_id"`
	Content string `json:"content"`
}

// SaveNovelSkill 校验并写入小说级 skill 文件，然后热重载内存。
func (a *App) SaveNovelSkill(input SaveNovelSkillInput) error {
	if a.skill == nil {
		return fmt.Errorf("app: skill store 未初始化")
	}
	dir := config.NovelSkillsDir(input.NovelID)
	if err := a.saveSkill(dir, input.Content); err != nil {
		return err
	}
	return a.skill.ReloadNovel(input.NovelID, dir)
}

// saveSkill 校验 markdown 内容，安全写入到指定目录。
func (a *App) saveSkill(dir, content string) error {
	sk, err := skill.ParseBytes([]byte(content), "user")
	if err != nil {
		return fmt.Errorf("app: skill 格式无效: %w", err)
	}

	if err := os.MkdirAll(dir, 0700); err != nil {
		return fmt.Errorf("app: 创建目录失败: %w", err)
	}

	path, err := git.SafePath(dir, sk.Name+".md")
	if err != nil {
		return fmt.Errorf("app: 非法文件名: %w", err)
	}

	if err := os.WriteFile(path, []byte(content), 0600); err != nil {
		return fmt.Errorf("app: 写入文件失败: %w", err)
	}
	return nil
}

// DeleteUserSkillInput 是 DeleteUserSkill 的入参。
type DeleteUserSkillInput struct {
	Name string `json:"name"`
}

// DeleteUserSkill 删除用户级 skill 文件，然后热重载内存。
func (a *App) DeleteUserSkill(input DeleteUserSkillInput) error {
	if a.skill == nil {
		return fmt.Errorf("app: skill store 未初始化")
	}

	dir := config.UserSkillsDir()
	path, err := git.SafePath(dir, input.Name+".md")
	if err != nil {
		return fmt.Errorf("app: 非法文件名: %w", err)
	}

	if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("app: 删除文件失败: %w", err)
	}

	return a.skill.ReloadUser(dir)
}

// DeleteNovelSkillInput 是 DeleteNovelSkill 的入参。
type DeleteNovelSkillInput struct {
	NovelID int64  `json:"novel_id"`
	Name    string `json:"name"`
}

// DeleteNovelSkill 删除小说级 skill 文件，然后热重载内存。
func (a *App) DeleteNovelSkill(input DeleteNovelSkillInput) error {
	if a.skill == nil {
		return fmt.Errorf("app: skill store 未初始化")
	}

	dir := config.NovelSkillsDir(input.NovelID)
	path, err := git.SafePath(dir, input.Name+".md")
	if err != nil {
		return fmt.Errorf("app: 非法文件名: %w", err)
	}

	if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("app: 删除文件失败: %w", err)
	}

	return a.skill.ReloadNovel(input.NovelID, dir)
}
