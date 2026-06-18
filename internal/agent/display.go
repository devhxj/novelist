package agent

import (
	"fmt"
	"strings"

	"novel/internal/mcp_tools"
)

// toolDisplayNames 工具名 → 中文展示名称。
var toolDisplayNames = map[string]string{
	"get_chapter_list":                "查看章节目录",
	"get_chapter_content":             "读取章节正文",
	"edit_chapter":                    "编辑章节内容",
	"create_new_chapter":              "创建新章节",
	"get_creative_profile":            "查看创作规则",
	"update_creative_profile":         "设置创作规则",
	"search_story_memory":             "搜索故事记忆",
	"get_character_memory":            "查询角色记忆",
	"get_timeline":                    "查看故事时间线",
	"create_timeline_entry":           "记录追踪条目",
	"update_timeline_entry":           "更新追踪条目",
	"update_chapter_plan":             "更新章节计划",
	"get_locations":                   "查看地点信息",
	"create_location":                 "创建新地点",
	"update_location":                 "更新地点设定",
	"create_location_relation":        "创建地点关系",
	"update_location_relation":        "更新地点关系",
	"get_novel_info":                  "查看小说信息",
	"get_characters":                  "查看角色信息",
	"get_character_relations":         "查看人物关系",
	"create_character":                "创建新角色",
	"update_character":                "更新角色设定",
	"update_character_relationship":   "更新人物关系",
	"run_subagent":                    "调度AI子任务",
	"get_reader_perspective":          "查看读者视角",
	"create_reader_perspective_entry": "添加读者视角",
	"update_reader_perspective_entry": "更新读者视角",
	"get_story_arcs":                  "查看故事弧线",
	"create_story_arc":                "创建故事弧线",
	"update_story_arc":                "更新故事弧线",
	"create_arc_node":                 "创建弧线节点",
	"update_arc_node":                 "更新弧线节点",
	"get_preferences":                 "查看创作偏好",
	"create_preference":               "创建创作偏好",
	"update_preference":               "更新创作偏好",
	"lint_chapter":                    "章节文本检查",
	"delete_record":                   "删除记录",
	"edit":                            "编辑文件内容",
	"read":                            "读取文件内容",
	"web_search":                      "搜索网络信息",
}

// toolActivityKinds 工具名 → 前端展示类别。
var toolActivityKinds = map[string]string{
	"get_chapter_list":                "browse",
	"get_chapter_content":             "view",
	"edit_chapter":                    "write",
	"create_new_chapter":              "create",
	"get_creative_profile":            "memory",
	"update_creative_profile":         "memory",
	"search_story_memory":             "memory",
	"get_character_memory":            "memory",
	"get_timeline":                    "view",
	"create_timeline_entry":           "write",
	"update_timeline_entry":           "edit",
	"update_chapter_plan":             "edit",
	"get_locations":                   "view",
	"create_location":                 "create",
	"update_location":                 "edit",
	"create_location_relation":        "create",
	"update_location_relation":        "edit",
	"get_novel_info":                  "view",
	"get_characters":                  "view",
	"get_character_relations":         "view",
	"create_character":                "create",
	"update_character":                "edit",
	"update_character_relationship":   "edit",
	"run_subagent":                    "plan",
	"get_reader_perspective":          "view",
	"create_reader_perspective_entry": "write",
	"update_reader_perspective_entry": "edit",
	"get_story_arcs":                  "view",
	"create_story_arc":                "create",
	"update_story_arc":                "edit",
	"create_arc_node":                 "create",
	"update_arc_node":                 "edit",
	"get_preferences":                 "view",
	"create_preference":               "create",
	"update_preference":               "edit",
	"lint_chapter":                    "review",
	"delete_record":                   "delete",
	"edit":                            "write",
	"read":                            "view",
	"web_search":                      "browse",
}

// chapterTools 需要查章节标题的工具集。
var chapterTools = map[string]bool{
	"get_chapter_content": true,
	"edit_chapter":        true,
	"edit":                true,
	"read":                true,
}

// buildDisplay 根据 tool_name + args + phase 生成前端展示文本。
// executing 阶段加 "正在" 前缀，completed/failed/cancelled 去掉。
// chapter 工具通过 novelID + chapter_number 查 DB 获取章节标题。
func (a *Agent) buildDisplay(name string, args map[string]any, phase mcp_tools.DisplayPhase, novelID int64) *mcp_tools.DisplayInfo {
	baseText := toolDisplayNames[name]
	if baseText == "" {
		baseText = name
	}
	activityKind := toolActivityKinds[name]
	if activityKind == "" {
		activityKind = "general"
	}

	var metadata map[string]any

	// run_subagent：根据 agent_type 定制展示文本
	if name == "run_subagent" {
		if at, ok := args["agent_type"].(string); ok {
			switch at {
			case "memory":
				baseText = "探索故事记忆"
			case "review":
				baseText = "审核章节内容"
			}
		}
		metadata = map[string]any{"agent_type": args["agent_type"]}
	}

	// chapter 工具：查 DB 取章节标题
	if chapterTools[name] {
		if cn, ok := chapterNumber(args); ok {
			label := a.lookupChapterBrief(novelID, cn)
			switch name {
			case "get_chapter_content":
				baseText = "查看 " + label
			case "edit_chapter":
				baseText = "编辑 " + label
			case "edit":
				baseText = "编辑 " + label
			case "read":
				baseText = "查看 " + label
			}
		}

		// rw 工具的 goink.md 路径特殊处理
		if path, ok := args["path"].(string); ok && path == "goink.md" {
			switch name {
			case "edit":
				baseText = "编辑 故事状态"
			case "read":
				baseText = "查看 故事状态"
			}
		}

		// rw 工具的 outlines/ 路径特殊处理
		if path, ok := args["path"].(string); ok && strings.HasPrefix(path, "outlines/") {
			var n int
			fmt.Sscanf(path, "outlines/%d.md", &n)
			label := fmt.Sprintf("第%d章大纲", n)
			switch name {
			case "edit":
				baseText = "编辑 " + label
			case "read":
				baseText = "查看 " + label
			}
		}
	}

	// executing 阶段加 "正在" 前缀
	isActive := phase == mcp_tools.PhaseExecuting || phase == mcp_tools.PhaseSelected
	if isActive {
		baseText = "正在" + baseText
	}

	return &mcp_tools.DisplayInfo{
		DisplayText:  baseText,
		ActivityKind: activityKind,
		Metadata:     metadata,
	}
}

func chapterNumber(args map[string]any) (int, bool) {
	if args == nil {
		return 0, false
	}
	for _, key := range []string{"chapter_number", "chapter_id"} {
		if v, ok := args[key]; ok {
			switch n := v.(type) {
			case float64:
				return int(n), true
			case int:
				return n, true
			}
		}
	}
	// edit 工具使用 path 参数，如 "chapters/001.md"
	if path, ok := args["path"].(string); ok {
		var n int
		if _, err := fmt.Sscanf(path, "chapters/%d.md", &n); err == nil && n > 0 {
			return n, true
		}
	}
	return 0, false
}

type chapterTitleRow struct {
	Title string `gorm:"column:title"`
}

func (a *Agent) lookupChapterBrief(novelID int64, chapterNumber int) string {
	var row chapterTitleRow
	err := a.db.Table("chapters").
		Where("novel_id = ? AND chapter_number = ?", novelID, chapterNumber).
		Select("title").
		Scan(&row).Error
	if err != nil || row.Title == "" {
		return fmt.Sprintf("第%d章", chapterNumber)
	}
	return fmt.Sprintf("第%d章 %s", chapterNumber, row.Title)
}

func buildToolDisplay(toolOutputs []toolOutput) []map[string]any {
	toolDisplays := make([]map[string]any, 0, len(toolOutputs))
	for _, to := range toolOutputs {
		phase := "completed"
		if !to.result.Success {
			phase = "failed"
		}
		toolDisplays = append(toolDisplays, map[string]any{
			"tool_id":       to.id,
			"tool_name":     to.name,
			"display_text":  to.displayText,
			"activity_kind": to.activityKind,
			"phase":         phase,
		})
	}
	return toolDisplays
}
