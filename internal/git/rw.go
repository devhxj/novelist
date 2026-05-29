package git

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"novel/internal/config"
)

// ── 文件路径 ──────────────────────────────────────────────

func ChapterPath(num int) string {
	return fmt.Sprintf("chapters/%03d.md", num)
}

func GoinkPath() string {
	return "goink.md"
}

// ── 文件读写 ──────────────────────────────────────────────
// path 为相对于小说仓库根目录的路径，如 "chapters/001.md"、"goink.md"。

func ReadFile(novelID int64, path string) (string, error) {
	dir, err := novelDir(novelID)
	if err != nil {
		return "", err
	}
	fullPath, err := safePath(dir, path)
	if err != nil {
		return "", err
	}
	data, err := os.ReadFile(fullPath)
	if err != nil {
		if os.IsNotExist(err) {
			return "", fmt.Errorf("%w: %s", os.ErrNotExist, path)
		}
		return "", fmt.Errorf("git: read %s: %w", path, err)
	}
	return string(data), nil
}

func WriteFile(novelID int64, path, content string) error {
	dir, err := novelDir(novelID)
	if err != nil {
		return err
	}
	fullPath, err := safePath(dir, path)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(fullPath), 0755); err != nil {
		return fmt.Errorf("git: mkdir for %s: %w", path, err)
	}
	if err := os.WriteFile(fullPath, []byte(content), 0644); err != nil {
		return fmt.Errorf("git: write %s: %w", path, err)
	}
	return nil
}

var errPathEscape = errors.New("git: path escapes novel directory")

func novelDir(novelID int64) (string, error) {
	cfg := config.Get()
	if cfg == nil {
		return "", fmt.Errorf("git: config not initialized")
	}
	return cfg.NovelDirPath(novelID), nil
}

// safePath 对给定的上级目录和相对路径求最终路径，如果路径跳出上级目录则返回 error。
func safePath(base, rel string) (string, error) {
	absBase, err := filepath.Abs(base)
	if err != nil {
		return "", fmt.Errorf("git: resolve base: %w", err)
	}
	full := filepath.Clean(filepath.Join(absBase, rel))
	if !strings.HasPrefix(full, absBase+string(filepath.Separator)) && full != absBase {
		return "", fmt.Errorf("%w: %s", errPathEscape, rel)
	}
	return full, nil
}
