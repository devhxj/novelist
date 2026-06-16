package skill

import (
	"embed"
	"log/slog"
)

//go:embed builtin/*.md
var BuiltinFS embed.FS

// LoadBuiltinSkills 从嵌入式文件系统解析内置 skill。
func LoadBuiltinSkills(logger *slog.Logger) ([]Skill, error) {
	return scanFS(logger, BuiltinFS, "builtin")
}
