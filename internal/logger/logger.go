package logger

import (
	"io"
	"log/slog"
	"os"
	"path/filepath"

	"novel/internal/platform"
)

// New 创建结构化日志器。
//
//	format: "text"（开发环境）或 "json"（生产环境）
//	level:  slog.LevelDebug / Info / Warn / Error
//	out:    os.Stdout、os.Stderr 或文件
func New(level slog.Level, format string, out io.Writer) *slog.Logger {
	var handler slog.Handler

	opts := &slog.HandlerOptions{
		Level:     level,
		AddSource: true,
	}

	switch format {
	case "json":
		handler = slog.NewJSONHandler(out, opts)
	default:
		handler = slog.NewTextHandler(out, opts)
	}

	return slog.New(handler)
}

// Default 返回开发环境默认日志器：文本格式、Debug 级别，同时写到 stderr 和数据目录下的 goink.log。
func Default() *slog.Logger {
	logPath := filepath.Join(platform.DataDir(), "goink.log")
	if err := os.MkdirAll(filepath.Dir(logPath), 0700); err != nil {
		return New(slog.LevelDebug, "text", os.Stderr)
	}
	f, err := os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0644)
	if err != nil {
		return New(slog.LevelDebug, "text", os.Stderr)
	}
	return New(slog.LevelDebug, "text", io.MultiWriter(os.Stderr, f))
}
