package skill

import (
	"testing"
)

func TestParseBytes_Minimal(t *testing.T) {
	input := `---
name: 测试
mode: always
author: me
---
这是一段正文内容。`

	sk, err := ParseBytes([]byte(input), "default")
	if err != nil {
		t.Fatalf("ParseBytes: %v", err)
	}
	if sk.Name != "测试" {
		t.Errorf("Name: expected 测试, got %s", sk.Name)
	}
	if sk.Mode != "always" {
		t.Errorf("Mode: expected always, got %s", sk.Mode)
	}
	if sk.Author != "me" {
		t.Errorf("Author: expected me, got %s", sk.Author)
	}
	if sk.Content != "这是一段正文内容。" {
		t.Errorf("Content mismatch: %s", sk.Content)
	}
}

func TestParseBytes_DefaultMode(t *testing.T) {
	input := `---
name: test
---
content`
	sk, err := ParseBytes([]byte(input), "builtin")
	if err != nil {
		t.Fatalf("ParseBytes: %v", err)
	}
	if sk.Mode != "auto" {
		t.Errorf("default Mode should be auto, got %s", sk.Mode)
	}
}

func TestParseBytes_DefaultAuthor(t *testing.T) {
	input := `---
name: test
---
content`
	sk, err := ParseBytes([]byte(input), "builtin")
	if err != nil {
		t.Fatalf("ParseBytes: %v", err)
	}
	if sk.Author != "builtin" {
		t.Errorf("default Author should be 'builtin', got %s", sk.Author)
	}
}

func TestParseBytes_MissingName(t *testing.T) {
	input := `---
mode: always
---
content`
	_, err := ParseBytes([]byte(input), "")
	if err == nil {
		t.Fatal("expected error for missing name")
	}
}

func TestParseBytes_NoFrontmatter(t *testing.T) {
	input := `没有 frontmatter 的纯文本内容。`
	_, err := ParseBytes([]byte(input), "user")
	if err == nil {
		t.Fatal("expected error: no frontmatter means no name, which is required")
	}
}

func TestParseBytes_InvalidYAML(t *testing.T) {
	input := `---
name: [unclosed
---
content`
	_, err := ParseBytes([]byte(input), "")
	if err == nil {
		t.Fatal("expected error for invalid YAML")
	}
}

func TestSplitFrontmatter_Valid(t *testing.T) {
	raw := "---\nname: test\n---\n正文开始"
	fm, body, err := splitFrontmatter(raw)
	if err != nil {
		t.Fatalf("splitFrontmatter: %v", err)
	}
	if fm != "\nname: test" {
		t.Errorf("frontmatter mismatch: %q", fm)
	}
	if body != "正文开始" {
		t.Errorf("body mismatch: %q", body)
	}
}

func TestSplitFrontmatter_NoFrontmatter(t *testing.T) {
	raw := "纯文本没有 frontmatter"
	fm, body, err := splitFrontmatter(raw)
	if err != nil {
		t.Fatalf("splitFrontmatter: %v", err)
	}
	if fm != "" {
		t.Errorf("expected empty frontmatter, got %q", fm)
	}
	if body != raw {
		t.Errorf("body mismatch: %q", body)
	}
}

func TestSplitFrontmatter_Unclosed(t *testing.T) {
	raw := "---\nname: test\nnever closed"
	_, _, err := splitFrontmatter(raw)
	if err == nil {
		t.Fatal("expected error for unclosed frontmatter")
	}
}

func TestParseFrontmatter_Empty(t *testing.T) {
	sk, err := parseFrontmatter("")
	if err != nil {
		t.Fatalf("parseFrontmatter: %v", err)
	}
	if sk.Name != "" {
		t.Errorf("empty frontmatter should yield empty Skill")
	}
}

func TestParseFrontmatter_WithFields(t *testing.T) {
	raw := "name: 高潮场景\nmode: auto\nauthor: 系统"
	sk, err := parseFrontmatter(raw)
	if err != nil {
		t.Fatalf("parseFrontmatter: %v", err)
	}
	if sk.Name != "高潮场景" {
		t.Errorf("Name: got %s", sk.Name)
	}
	if sk.Mode != "auto" {
		t.Errorf("Mode: got %s", sk.Mode)
	}
	if sk.Author != "系统" {
		t.Errorf("Author: got %s", sk.Author)
	}
}

func TestFindByName(t *testing.T) {
	skills := []Skill{
		{Name: "aaa"},
		{Name: "bbb"},
		{Name: "ccc"},
	}
	if sk := findByName(skills, "bbb"); sk == nil || sk.Name != "bbb" {
		t.Error("should find bbb")
	}
	if sk := findByName(skills, "xxx"); sk != nil {
		t.Error("should return nil for missing skill")
	}
}
