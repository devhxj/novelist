package config

import (
	"os"
	"testing"
)

func TestExpandTilde_WithHome(t *testing.T) {
	home := os.Getenv("HOME")
	if home == "" {
		t.Skip("HOME not set")
	}
	result := expandTilde("~/data")
	if result != home+"/data" {
		t.Errorf("expected %s, got %s", home+"/data", result)
	}
}

func TestExpandTilde_NoTilde(t *testing.T) {
	result := expandTilde("/absolute/path")
	if result != "/absolute/path" {
		t.Errorf("expected /absolute/path, got %s", result)
	}
}

func TestExpandTilde_Empty(t *testing.T) {
	home := os.Getenv("HOME")
	if home == "" {
		t.Skip("HOME not set")
	}
	result := expandTilde("")
	// 空字符串等价于 "~"，直接返回 home 目录
	if result != home {
		t.Errorf("expected %s, got %s", home, result)
	}
}

func TestExpandTilde_OnlyTilde(t *testing.T) {
	home := os.Getenv("HOME")
	if home == "" {
		t.Skip("HOME not set")
	}
	result := expandTilde("~")
	if result != home {
		t.Errorf("expected %s, got %s", home, result)
	}
}
