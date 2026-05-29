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

	n.DirPath = a.cfg.NovelDirPath(n.ID)
	if _, err := git.New(n.ID); err != nil {
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
