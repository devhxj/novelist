package skill

import "embed"

//go:embed builtin/*.md
var BuiltinFS embed.FS

// LoadBuiltinSkills 从嵌入式文件系统解析内置 skill。
func LoadBuiltinSkills() ([]Skill, error) {
	return scanFS(BuiltinFS, "builtin")
}
