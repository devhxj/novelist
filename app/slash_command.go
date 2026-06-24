package app

// ListSlashCommandsInput 是 ListSlashCommands 的入参。
type ListSlashCommandsInput struct {
	NovelID int64 `json:"novel_id"`
}

// SlashCommand 是斜杠菜单的统一条目，由 skill 和 quick command 合并而成。
type SlashCommand struct {
	Name        string `json:"name"`
	Description string `json:"description"`
	Type        string `json:"type"` // "skill" | "command"
}

type quickCommandDef struct {
	Description string
	Prompt      string
}

var quickCommands = map[string]quickCommandDef{
	"review": {
		"调度审核编辑 agent，根据用户指令执行审阅任务",
		"用户要求你调用 review agent。请根据用户指令执行审阅任务。",
	},
	"memory": {
		"调度记忆检索 agent，根据用户指令检索相关信息",
		"用户要求你调用 memory agent。请根据用户指令检索相关信息。",
	},
	"collect": {
		"全面收集当前章节相关的角色、情节、伏笔信息，整理成创作参考",
		"用户要求你全面收集信息，整理成创作参考。请根据用户指令执行。",
	},
	"next": {
		"基于当前进度，开始创作下一章",
		"用户要求你基于当前进度开始创作下一章。请根据用户指令执行。",
	},
}

// ListSlashCommands 返回 skill 和 quick command 合并后的斜杠菜单条目。
func (a *App) ListSlashCommands(input ListSlashCommandsInput) []SlashCommand {
	var result []SlashCommand

	// 收集 command 名称，同名时不重复加入 skill
	seen := make(map[string]bool)

	for name, def := range quickCommands {
		seen[name] = true
		result = append(result, SlashCommand{
			Name:        name,
			Description: def.Description,
			Type:        "command",
		})
	}

	if a.skill != nil {
		for _, s := range a.skill.ListMeta(input.NovelID) {
			if seen[s.Name] {
				continue
			}
			seen[s.Name] = true
			result = append(result, SlashCommand{
				Name:        s.Name,
				Description: s.Description,
				Type:        "skill",
			})
		}
	}

	return result
}

// resolveQuickCommand 返回 quick command 的 system-reminder 注入内容。
func resolveQuickCommand(name string) (string, bool) {
	def, ok := quickCommands[name]
	if !ok {
		return "", false
	}
	return "<system-reminder>\n" + def.Prompt + "\n</system-reminder>", true
}

// resolveSlashCommand 统一解析 skill 和 quick command，返回注入内容（空串表示未命中）。
func (a *App) resolveSlashCommand(novelID int64, name string) (inject string, logName string) {
	// 优先查 skill
	if a.skill != nil {
		if sk, ok := a.skill.Get(novelID, name); ok {
			content := "用户启用了技能「" + sk.Name + "」。请根据该技能的定义和用户需求进行工作。\n\n---\n" + sk.RawContent + "\n---"
			return "<system-reminder>\n" + content + "\n</system-reminder>", sk.Name
		}
	}
	// fallback 查 quick command
	if inject, ok := resolveQuickCommand(name); ok {
		return inject, name
	}
	return "", ""
}
