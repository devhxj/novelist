package agentcfg

import (
	"strings"

	"novel/internal/skill"
)

// BuildSkillCatalog 将 skill 元数据格式化为 LLM 可用的技能目录。
// 按 novel > user > builtin 分组输出，每组只列出 name 和 description。
func BuildSkillCatalog(meta []skill.SkillMeta) string {
	if len(meta) == 0 {
		return ""
	}

	novel := filterBySource(meta, "novel")
	user := filterBySource(meta, "user")
	builtin := filterBySource(meta, "builtin")

	var b strings.Builder
	b.WriteString("<available_skills>\n")

	if len(novel) > 0 {
		b.WriteString("## 小说专属技能\n")
		for _, s := range novel {
			b.WriteString("- ")
			b.WriteString(s.Name)
			if s.Description != "" {
				b.WriteString(": ")
				b.WriteString(s.Description)
			}
			b.WriteString("\n")
		}
		b.WriteString("\n")
	}
	if len(user) > 0 {
		b.WriteString("## 用户技能\n")
		for _, s := range user {
			b.WriteString("- ")
			b.WriteString(s.Name)
			if s.Description != "" {
				b.WriteString(": ")
				b.WriteString(s.Description)
			}
			b.WriteString("\n")
		}
		b.WriteString("\n")
	}
	if len(builtin) > 0 {
		b.WriteString("## 内置技能（只读）\n")
		for _, s := range builtin {
			b.WriteString("- ")
			b.WriteString(s.Name)
			if s.Description != "" {
				b.WriteString(": ")
				b.WriteString(s.Description)
			}
			b.WriteString("\n")
		}
		b.WriteString("\n")
	}

	b.WriteString("---\n")
	b.WriteString("使用 read 工具加载技能完整内容：\n")
	b.WriteString("- skills/<name>.md（小说技能）\n")
	b.WriteString("- ~/.goink/skills/<name>.md（用户技能）\n")
	b.WriteString("- /builtin/skills/<name>.md（内置技能，只读）\n")
	b.WriteString("使用 edit 工具创建或修改技能。内置技能不可编辑。\n")
	b.WriteString("</available_skills>")

	return b.String()
}

func filterBySource(meta []skill.SkillMeta, source string) []skill.SkillMeta {
	var result []skill.SkillMeta
	for _, s := range meta {
		if s.Source == source {
			result = append(result, s)
		}
	}
	return result
}
