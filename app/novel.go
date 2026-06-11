package app

import (
	"fmt"

	"novel/internal/config"
	"novel/internal/git"
	"novel/internal/novel"
)

// ── 小说 ──────────────────────────────────────────────────

// SetActiveNovelInput 是切换当前小说的入参。
type SetActiveNovelInput struct {
	NovelID int64 `json:"novel_id"`
}

// ── 小说操作 ──────────────────────────────────────────────

// GetNovels 返回小说列表。
func (a *App) GetNovels() ([]novel.Novel, error) {
	result, err := a.novel.List(a.ctx, novel.ListNovelsOptions{})
	if err != nil {
		return nil, err
	}
	return result.Items, nil
}

// CreateNovelInput 是创建小说的入参。
type CreateNovelInput struct {
	Title       string `json:"title"`
	Description string `json:"description,omitempty"`
}

// CreateNovel 创建一部新小说。
func (a *App) CreateNovel(input CreateNovelInput) (*novel.Novel, error) {
	n := novel.Novel{
		Title:       input.Title,
		Description: input.Description,
	}
	if err := a.novel.DB.WithContext(a.ctx).Create(&n).Error; err != nil {
		return nil, fmt.Errorf("failed to create novel: %w", err)
	}

	n.DirPath = config.NovelDirPath(n.ID)
	if _, err := git.New(n.ID); err != nil {
		a.novel.DB.WithContext(a.ctx).Delete(&n) // 回滚孤儿 DB 记录
		return nil, fmt.Errorf("failed to init novel repo: %w", err)
	}

	if err := a.novel.DB.WithContext(a.ctx).Model(&n).Update("dir_path", n.DirPath).Error; err != nil {
		return nil, fmt.Errorf("failed to update novel path: %w", err)
	}

	// 为新小说创建 goink.md 空文件
	if err := git.WriteFile(n.ID, git.GoinkPath(), ""); err != nil {
		return nil, fmt.Errorf("failed to create goink.md: %w", err)
	}

	return &n, nil
}

// SetActiveNovel 记录当前活跃的小说 ID，下次启动自动恢复。
func (a *App) SetActiveNovel(input SetActiveNovelInput) error {
	a.settings.LastNovelID = input.NovelID
	return config.SaveSettings(a.db, a.settings)
}

// ── 创作偏好 ──────────────────────────────────────────────

// PreferenceResult 是 GetPreferences 的返回结构。
type PreferenceResult struct {
	Global []novel.PreferenceItem `json:"global"`
	Novel  []novel.PreferenceItem `json:"novel"`
}

// GetPreferences 返回全局偏好和当前小说的专属偏好。
func (a *App) GetPreferences(novelID int64) (*PreferenceResult, error) {
	global, err := a.novel.ListGlobalPreferences(a.ctx)
	if err != nil {
		return nil, err
	}
	novelPrefs, err := a.novel.ListNovelPreferences(a.ctx, novelID)
	if err != nil {
		return nil, err
	}
	return &PreferenceResult{Global: global, Novel: novelPrefs}, nil
}

// CreatePreferenceInput 是创建偏好的入参。
type CreatePreferenceInput struct {
	IsGlobal bool   `json:"is_global"`
	Category string `json:"category"`
	Content  string `json:"content"`
}

// CreatePreference 创建一条创作偏好。
func (a *App) CreatePreference(novelID int64, input CreatePreferenceInput) (*novel.PreferenceItem, error) {
	item := novel.PreferenceItem{
		NovelID:  novelID,
		IsGlobal: input.IsGlobal,
		Category: input.Category,
		Content:  input.Content,
	}
	if err := a.novel.DB.WithContext(a.ctx).Create(&item).Error; err != nil {
		return nil, fmt.Errorf("create preference: %w", err)
	}
	return &item, nil
}

// UpdatePreferenceInput 是更新偏好的入参。
type UpdatePreferenceInput struct {
	Category string `json:"category,omitempty"`
	Content  string `json:"content,omitempty"`
	IsGlobal *bool  `json:"is_global,omitempty"`
}

// UpdatePreference 更新一条创作偏好。
func (a *App) UpdatePreference(id int64, input UpdatePreferenceInput) (*novel.PreferenceItem, error) {
	var item novel.PreferenceItem
	if err := a.novel.DB.WithContext(a.ctx).First(&item, id).Error; err != nil {
		return nil, fmt.Errorf("update preference: %w", err)
	}
	if input.Category != "" {
		item.Category = input.Category
	}
	if input.Content != "" {
		item.Content = input.Content
	}
	if input.IsGlobal != nil {
		item.IsGlobal = *input.IsGlobal
	}
	if err := a.novel.DB.WithContext(a.ctx).Save(&item).Error; err != nil {
		return nil, fmt.Errorf("update preference: %w", err)
	}
	return &item, nil
}

// DeletePreference 删除一条创作偏好。
func (a *App) DeletePreference(id int64) error {
	if err := a.novel.DB.WithContext(a.ctx).Delete(&novel.PreferenceItem{}, id).Error; err != nil {
		return fmt.Errorf("delete preference: %w", err)
	}
	return nil
}
