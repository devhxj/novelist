package app

import (
	"novel/internal/git"
)

// SaveContentInput 是保存文件内容的入参。
type SaveContentInput struct {
	NovelID int64  `json:"novel_id"`
	Path    string `json:"path"`
	Content string `json:"content"`
}

// GetContent 返回小说仓库中指定路径的文件内容。
func (a *App) GetContent(novelID int64, path string) (string, error) {
	content, err := git.ReadFile(novelID, path)
	if err != nil {
		return "", nil
	}
	return content, nil
}

// SaveContent 保存小说仓库中指定路径的文件内容。
func (a *App) SaveContent(input SaveContentInput) error {
	return git.WriteFile(input.NovelID, input.Path, input.Content)
}
