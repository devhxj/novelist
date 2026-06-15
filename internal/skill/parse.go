package skill

import (
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"

	"gopkg.in/yaml.v3"
)

// ParseFile 读取并解析一个 skill markdown 文件。
func ParseFile(path string) (*Skill, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("skill: 读取文件失败 %s: %w", path, err)
	}
	sk, err := ParseBytes(data, "user")
	if err != nil {
		return nil, fmt.Errorf("skill: 解析 %s 失败: %w", path, err)
	}
	return sk, nil
}

// ParseFS 从 fs.FS 中读取并解析一个 skill markdown 文件。
func ParseFS(fsys fs.FS, name string) (*Skill, error) {
	data, err := fs.ReadFile(fsys, name)
	if err != nil {
		return nil, fmt.Errorf("skill: 读取 %s 失败: %w", name, err)
	}
	sk, err := ParseBytes(data, "builtin")
	if err != nil {
		return nil, fmt.Errorf("skill: 解析 %s 失败: %w", name, err)
	}
	return sk, nil
}

// ParseBytes 将一段 markdown 字节解析为 Skill。
// 可用于校验原始内容格式是否合法。
func ParseBytes(data []byte, defaultAuthor string) (*Skill, error) {
	fm, body, err := splitFrontmatter(strings.TrimSpace(string(data)))
	if err != nil {
		return nil, err
	}

	sk, err := parseFrontmatter(fm)
	if err != nil {
		return nil, fmt.Errorf("YAML frontmatter 解析失败: %w", err)
	}

	if sk.Name == "" {
		return nil, fmt.Errorf("缺少 name 字段")
	}
	if sk.Mode == "" {
		sk.Mode = "on_demand"
	}
	if sk.Author == "" {
		sk.Author = defaultAuthor
	}
	sk.Content = body
	sk.RawContent = string(data)
	return sk, nil
}

// splitFrontmatter 从 markdown 原文中分离 frontmatter 和正文。
// frontmatter 由开头的 --- 开始，下一个 --- 结束。
func splitFrontmatter(raw string) (frontmatter, body string, err error) {
	if !strings.HasPrefix(raw, "---") {
		return "", raw, nil
	}

	raw = raw[3:]
	frontmatter, body, found := strings.Cut(raw, "\n---")
	if !found {
		return "", "", fmt.Errorf("未闭合的 frontmatter：缺少结束标记 ---")
	}
	body = strings.TrimSpace(body)
	return frontmatter, body, nil
}

// parseFrontmatter 将 YAML frontmatter 字符串解析为 Skill（不含 Content）。
func parseFrontmatter(raw string) (*Skill, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return &Skill{}, nil
	}

	var sk Skill
	if err := yaml.Unmarshal([]byte(raw), &sk); err != nil {
		return nil, fmt.Errorf("YAML 解析失败: %w", err)
	}
	return &sk, nil
}

// scanFS 扫描 fs.FS 指定目录下的所有 .md 文件并解析为 Skill 切片。
func scanFS(fsys fs.FS, dir string) ([]Skill, error) {
	entries, err := fs.ReadDir(fsys, dir)
	if err != nil {
		return nil, fmt.Errorf("skill: 读取目录 %s 失败: %w", dir, err)
	}

	var skills []Skill
	for _, entry := range entries {
		if entry.IsDir() || filepath.Ext(entry.Name()) != ".md" {
			continue
		}
		sk, err := ParseFS(fsys, dir+"/"+entry.Name())
		if err != nil {
			continue
		}
		skills = append(skills, *sk)
	}
	return skills, nil
}

// scanDir 扫描目录下所有 .md 文件并解析为 Skill 切片。
// YAML name 与文件名不一致时以 YAML name 为准重命名文件。
// 目录不存在时返回空切片（不报错）。
func scanDir(dir string) ([]Skill, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, fmt.Errorf("读取目录 %s 失败: %w", dir, err)
	}

	var skills []Skill
	for _, entry := range entries {
		if entry.IsDir() || filepath.Ext(entry.Name()) != ".md" {
			continue
		}
		sk, err := ParseFile(filepath.Join(dir, entry.Name()))
		if err != nil {
			continue
		}
		fileBase := strings.TrimSuffix(entry.Name(), ".md")
		if sk.Name != "" && sk.Name != fileBase {
			oldPath := filepath.Join(dir, entry.Name())
			newPath := filepath.Join(dir, sk.Name+".md")
			if err := os.Rename(oldPath, newPath); err != nil {
				return nil, fmt.Errorf("重命名 skill 文件失败 %s -> %s: %w", oldPath, newPath, err)
			}
		}
		skills = append(skills, *sk)
	}
	return skills, nil
}
