package app

import (
	"fmt"

	"novel/internal/novel"
)

// ── 小说 ──────────────────────────────────────────────────

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
		return nil, fmt.Errorf("创建小说失败: %w", err)
	}
	return &n, nil
}
