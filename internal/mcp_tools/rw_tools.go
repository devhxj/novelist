package mcp_tools

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"regexp"
	"strings"

	wails "github.com/wailsapp/wails/v2/pkg/runtime"

	"novel/internal/chapter"
	"novel/internal/git"
	"novel/internal/rag"
	"novel/internal/text"
)

// ── edit ──────────────────────────────────────────────────

// EditArgs 是 edit 工具的参数。
type EditArgs struct {
	Path       string `json:"path" jsonschema:"required,description=要编辑的文件路径。章节文件格式为 chapters/001.md（三位数字），大纲为 outlines/001.md，故事状态为 goink.md" validate:"required"`
	ChangeType string `json:"change_type" jsonschema:"required,enum=full_replace,enum=search_replace,enum=line_range_replace,description=编辑方式。full_replace：全文替换；search_replace：查找并替换指定文本；line_range_replace：替换指定行范围" validate:"required,oneof=full_replace search_replace line_range_replace"`
	SearchText string `json:"search_text" jsonschema:"description=要查找的原文片段（search_replace 时必填）。请从文件中精确复制" validate:"omitempty"`
	NewContent string `json:"new_content" jsonschema:"description=新内容。full_replace 时为完整全文；search_replace 时为替换后的文本；line_range_replace 时为插入的新行" validate:"omitempty"`
	ReplaceAll bool   `json:"replace_all" jsonschema:"description=是否替换所有匹配项。默认 false（仅替换第一个匹配）" validate:"omitempty"`
	StartLine  int    `json:"start_line" jsonschema:"description=起始行号 1-based 含此行（line_range_replace 时必填）" validate:"omitempty,min=1"`
	EndLine    int    `json:"end_line" jsonschema:"description=结束行号 1-based 含此行（line_range_replace 时必填）" validate:"omitempty,min=1"`
	Reason     string `json:"reason" jsonschema:"description=修改原因，供人类审阅" validate:"omitempty"`
	Title      string `json:"title" jsonschema:"description=章节标题（创建大纲或新章节时必填）" validate:"omitempty"`
}

// EditTool 编辑文件（章节或故事状态），支持全文替换、查找替换、行范围替换。
// 修改在内存中完成后生成 git diff 提交审批，通过后写入文件。
type EditTool struct{}

func (t *EditTool) Name() string           { return "edit" }
func (t *EditTool) Description() string    { return editDescription }
func (t *EditTool) Category() ToolCategory { return CategoryWritingAssistant }

func (t *EditTool) JSONSchema() json.RawMessage { return SchemaOf(EditArgs{}) }
func (t *EditTool) ExposeToLLM() bool           { return true }
func (t *EditTool) NewArgs() any                { return &EditArgs{} }

func (t *EditTool) Execute(ctx context.Context, args any, tc ToolContext) (*ToolResult, error) {
	a := args.(*EditArgs)

	// 内置 skill 只读
	if strings.HasPrefix(a.Path, "builtin/skills/") {
		return &ToolResult{Success: false, Error: "内置 skill 为只读，不可编辑"}, nil
	}

	// 1. 校验路径格式
	if !validPath(a.Path) {
		return &ToolResult{Success: false, Error: "无效文件路径，支持 chapters/001.md ~ chapters/999999.md、outlines/001.md ~ outlines/999999.md、goink.md、skills/<name>.md、~/.goink/skills/<name>.md"}, nil
	}

	// 2. 读取当前文件
	var fileExists bool
	current, err := git.ReadFile(tc.NovelID, a.Path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			if a.ChangeType == "full_replace" {
				current = ""
			} else {
				return &ToolResult{Success: false, Error: "文件不存在: " + a.Path}, nil
			}
		} else if errors.Is(err, git.ErrPathEscape) {
			return &ToolResult{Success: false, Error: "路径非法: " + a.Path}, nil
		} else {
			return nil, fmt.Errorf("read file %s: %w", a.Path, err)
		}
	} else {
		fileExists = true
	}

	// 3. 根据 change_type 生成新内容
	proposed, err := applyChange(a, current)
	if err != nil {
		return &ToolResult{Success: false, Error: fmt.Sprintf("编辑操作失败: %s", err.Error())}, nil
	}

	if proposed == current {
		return &ToolResult{
			Success: true,
			Data:    map[string]any{"path": a.Path, "message": "内容未变化，跳过"},
		}, nil
	}

	// 4. 审批（阻塞等待用户确认）
	if tc.Approver != nil {
		payload := map[string]any{
			"original":    current,
			"modified":    proposed,
			"path":        a.Path,
			"change_type": a.ChangeType,
			"reason":      a.Reason,
		}
		if tc.EmitApproval != nil {
			tc.EmitApproval(tc.ToolID, "file_edit", payload)
		}
		approval, err := tc.Approver.RequestApproval(ctx, tc.ToolID, payload)
		if err != nil {
			if errors.Is(err, context.Canceled) {
				return &ToolResult{Success: false, Error: "操作被中断"}, nil
			}
			return nil, fmt.Errorf("approval: %w", err)
		}
		if !approval.Approved {
			info := "你的修改被用户拒绝"
			if approval.Feedback != "" {
				info += "。用户反馈：" + approval.Feedback
			}
			return &ToolResult{
				Success: false,
				Error:   "审批未通过",
				Data: map[string]any{
					"path":        a.Path,
					"change_type": a.ChangeType,
					"approved":    false,
				},
				Inject: []InjectMessage{{Role: "user", Content: info}},
			}, nil
		}
	}

	// 5. 自动创建 DB 记录（文件不存在且为章节/大纲路径时）
	if !fileExists && (isChapterPath(a.Path) || isOutlinePath(a.Path)) {
		chapNum := parseChapterNum(a.Path)
		if chapNum == 0 {
			chapNum = parseOutlineNum(a.Path)
		}
		if chapNum > 0 {
			title := a.Title
			if title == "" {
				title = fmt.Sprintf("第%d章", chapNum)
			}
			ch := chapter.Chapter{
				NovelID:       tc.NovelID,
				ChapterNumber: chapNum,
				Title:         title,
			}
			if err := tc.DB.WithContext(ctx).Where("novel_id = ? AND chapter_number = ?", tc.NovelID, chapNum).FirstOrCreate(&ch).Error; err != nil {
				return nil, fmt.Errorf("auto-create chapter record: %w", err)
			}
		}
	}

	// 6. 写入前重读对比，阻止并发冲突
	if fresh, err := git.ReadFile(tc.NovelID, a.Path); err == nil && fresh != current {
		return &ToolResult{Success: false, Error: "文件已被修改，请重新读取最新内容后重试"}, nil
	}

	// 7. 写入文件
	if err := git.WriteFile(tc.NovelID, a.Path, proposed); err != nil {
		if errors.Is(err, git.ErrPathEscape) {
			return &ToolResult{Success: false, Error: "路径非法: " + a.Path}, nil
		}
		return nil, fmt.Errorf("write file: %w", err)
	}

	wails.EventsEmit(ctx, "file:changed", map[string]any{
		"novel_id": tc.NovelID,
		"path":     a.Path,
	})

	// 异步刷新章节向量 + 更新字数
	if isChapterPath(a.Path) {
		chapNum := parseChapterNum(a.Path)
		rag.SubmitRefresh(tc.NovelID, chapNum, proposed)
		stats := text.ComputeStats(proposed)
		tc.DB.WithContext(ctx).
			Model(&chapter.Chapter{}).
			Where("novel_id = ? AND chapter_number = ?", tc.NovelID, chapNum).
			Update("word_count", stats.WordCount)

	}

	// 8. inject 维护提醒（章节全量替换且 >500 字时）
	var injects []InjectMessage
	if a.ChangeType == "full_replace" && isChapterPath(a.Path) && len([]rune(proposed)) > 500 {
		chapNum := parseChapterNum(a.Path)
		injects = []InjectMessage{{
			Role:    "user",
			Content: fmt.Sprintf("你刚刚完成了第%d章的全量替换。请执行以下维护操作：\n1. 检查并更新角色设定（性格变化、新能力、身份转变等）\n2. 更新故事时间线（伏笔回收、新伏笔记录、章节计划推进）\n3. 更新读者认知（新悬念、已回收悬念）\n4. 更新故事弧线节点进度\n完成后向用户汇报修改摘要。", chapNum),
		}}
	}

	return &ToolResult{
		Success: true,
		Data: map[string]any{
			"path":        a.Path,
			"change_type": a.ChangeType,
			"approved":    true,
		},
		Inject: injects,
	}, nil
}

// ── 编辑操作 ──────────────────────────────────────────────

func applyChange(a *EditArgs, current string) (string, error) {
	switch a.ChangeType {
	case "full_replace":
		return a.NewContent, nil

	case "search_replace":
		if a.SearchText == "" {
			return "", fmt.Errorf("search_replace 模式需要提供 search_text")
		}
		result, found := searchReplace(current, a.SearchText, a.NewContent, a.ReplaceAll)
		if !found {
			return "", fmt.Errorf("未找到匹配文本，请用精确文本重试")
		}
		return result, nil

	case "line_range_replace":
		if a.StartLine <= 0 || a.EndLine <= 0 {
			return "", fmt.Errorf("line_range_replace 模式需要提供 start_line 和 end_line")
		}
		if a.StartLine > a.EndLine {
			return "", fmt.Errorf("start_line 不能大于 end_line")
		}
		return lineRangeReplace(current, a.StartLine, a.EndLine, a.NewContent)

	default:
		return "", fmt.Errorf("未知的 change_type: %s", a.ChangeType)
	}
}

// searchReplace 在 content 中查找 searchText 并替换为 newContent。
// replaceAll=false 时仅替换第一个匹配。返回修改后的内容和是否找到匹配。
func searchReplace(content, searchText, newContent string, replaceAll bool) (string, bool) {
	searchText = strings.TrimRight(searchText, "\n")

	// 精确匹配
	if strings.Contains(content, searchText) {
		n := 1
		if replaceAll {
			n = -1
		}
		return strings.Replace(content, searchText, newContent, n), true
	}

	// TrimSpace 兜底：LLM 多复制了首尾空白时仍能匹配
	trimmedSearch := strings.TrimSpace(searchText)
	if trimmedSearch != searchText && strings.Contains(content, trimmedSearch) {
		n := 1
		if replaceAll {
			n = -1
		}
		return strings.Replace(content, trimmedSearch, newContent, n), true
	}

	return "", false
}

// lineRangeReplace 替换 [startLine, endLine] 区间（1-based，含两端）。
func lineRangeReplace(content string, startLine, endLine int, newContent string) (string, error) {
	lines := strings.Split(content, "\n")
	if startLine < 1 || endLine > len(lines) {
		return "", fmt.Errorf("行号超出范围: start=%d end=%d 总行数=%d", startLine, endLine, len(lines))
	}

	var result []string
	result = append(result, lines[:startLine-1]...)
	if newContent != "" {
		result = append(result, strings.Split(newContent, "\n")...)
	}
	result = append(result, lines[endLine:]...)
	return strings.Join(result, "\n"), nil
}

// ── 路径校验 ──────────────────────────────────────────────

var pathRe = regexp.MustCompile(`^(chapters/\d{1,6}\.md|goink\.md|outlines/\d{1,6}\.md|skills/[^/]+\.md|~/.goink/skills/[^/]+\.md)$`)

func validPath(p string) bool {
	return pathRe.MatchString(p)
}

func isChapterPath(p string) bool {
	return strings.HasPrefix(p, "chapters/")
}

func parseChapterNum(p string) int {
	var n int
	fmt.Sscanf(p, "chapters/%d.md", &n)
	return n
}

func isOutlinePath(p string) bool {
	return strings.HasPrefix(p, "outlines/")
}

func parseOutlineNum(p string) int {
	var n int
	fmt.Sscanf(p, "outlines/%d.md", &n)
	return n
}

// ── 工具描述 ──────────────────────────────────────────────

const editDescription = `编辑小说文件（章节正文或大纲或故事状态 goink.md 或技能文件）。支持三种编辑模式：

1. **full_replace** — 全文替换整个文件。new_content 为完整的替换后内容。
2. **search_replace** — 查找并替换指定文本。search_text 为要查找的原文片段（请从文件中精确复制），new_content 为替换后的文本。replace_all=false（默认）仅替换第一个匹配项，replace_all=true 替换所有匹配。
3. **line_range_replace** — 替换指定行范围。start_line 和 end_line 为 1-based 行号（含两端），new_content 为插入的新内容。

路径格式：
- chapters/001.md ~ chapters/999999.md（三位数字补齐的章节文件）
- outlines/001.md ~ outlines/999999.md（章节大纲文件）
- goink.md（故事状态文档）
- skills/<name>.md（小说级技能）
- ~/.goink/skills/<name>.md（用户级技能）
只用上述路径。
所有修改会先生成 git diff 提交用户审批，审批通过后才写入文件。被拒绝时返回用户反馈，可根据反馈修正后重试。`

// ── read ──────────────────────────────────────────────────

// ReadArgs 是 read 工具的参数。
type ReadArgs struct {
	Path         string `json:"path" jsonschema:"required,description=要读取的文件路径。章节文件格式为 chapters/001.md（三位数字），大纲为 outlines/001.md，故事状态为 goink.md" validate:"required"`
	IncludeLines *bool  `json:"include_lines" jsonschema:"default=true,description=是否包含行号前缀（如 123|）。默认 true，用于精确引用和行范围编辑。传 false 获取纯文本"`
	StartLine    int    `json:"start_line" jsonschema:"default=1,description=起始行号 1-based 含此行" validate:"omitempty,min=1"`
	EndLine      int    `json:"end_line" jsonschema:"default=2000,description=结束行号 1-based 含此行，超出自动截到文末；设为 0 读取全部" validate:"omitempty,min=0"`
}

// ReadTool 读取文件内容（章节正文或故事状态 goink.md）。
// 默认含行号前缀（123|），LLM 传 include_lines=false 获取纯文本。
// start_line/end_line 支持行范围读取，用于翻页和精确引用。
type ReadTool struct{}

func (t *ReadTool) Name() string           { return "read" }
func (t *ReadTool) Description() string    { return readDescription }
func (t *ReadTool) Category() ToolCategory { return CategoryNovelManagement }

func (t *ReadTool) JSONSchema() json.RawMessage { return SchemaOf(ReadArgs{}) }
func (t *ReadTool) ExposeToLLM() bool           { return true }
func (t *ReadTool) NewArgs() any                { return &ReadArgs{} }

func (t *ReadTool) Execute(ctx context.Context, args any, tc ToolContext) (*ToolResult, error) {
	a := args.(*ReadArgs)

	// builtin skill 走 store 内存
	if strings.HasPrefix(a.Path, "builtin/skills/") {
		return t.readBuiltinSkill(a, tc)
	}

	if !validPath(a.Path) {
		return &ToolResult{Success: false, Error: "无效文件路径，支持 chapters/001.md ~ chapters/999999.md、outlines/001.md ~ outlines/999999.md、goink.md、skills/<name>.md、~/.goink/skills/<name>.md、builtin/skills/<name>.md（只读）"}, nil
	}

	content, err := git.ReadFile(tc.NovelID, a.Path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return &ToolResult{Success: false, Error: "文件不存在: " + a.Path}, nil
		}
		if errors.Is(err, git.ErrPathEscape) {
			return &ToolResult{Success: false, Error: "路径非法: " + a.Path}, nil
		}
		return nil, fmt.Errorf("read file %s: %w", a.Path, err)
	}

	start := a.StartLine
	if start == 0 {
		start = 1
	}
	end := a.EndLine
	if end == 0 {
		end = 2000
	}

	lines := strings.Split(content, "\n")
	totalLines := len(lines)

	if start > totalLines {
		return &ToolResult{Success: false, Error: fmt.Sprintf("起始行 %d 超出文件总行数 %d", start, totalLines)}, nil
	}
	if end > totalLines {
		end = totalLines
	}

	selected := lines[start-1 : end]

	includeLines := a.IncludeLines == nil || *a.IncludeLines

	var output string
	if includeLines {
		var sb strings.Builder
		for i, line := range selected {
			fmt.Fprintf(&sb, "%d|%s\n", start+i, line)
		}
		output = strings.TrimRight(sb.String(), "\n")
	} else {
		output = strings.Join(selected, "\n")
	}

	display := a.Path
	if isChapterPath(a.Path) {
		display = fmt.Sprintf("第%d章", parseChapterNum(a.Path))
	} else if isOutlinePath(a.Path) {
		display = fmt.Sprintf("第%d章大纲", parseOutlineNum(a.Path))
	}

	data := map[string]any{
		"path":        a.Path,
		"display":     display,
		"content":     output,
		"total_lines": totalLines,
		"start_line":  start,
		"end_line":    end,
	}
	if end < totalLines {
		data["truncated"] = true
	}

	return &ToolResult{Success: true, Data: data}, nil
}

// readBuiltinSkill 从 store 内存读取内置 skill，全量返回。
func (t *ReadTool) readBuiltinSkill(a *ReadArgs, tc ToolContext) (*ToolResult, error) {
	if tc.SkillStore == nil {
		return &ToolResult{Success: false, Error: "skill store 未初始化"}, nil
	}

	name := strings.TrimSuffix(strings.TrimPrefix(a.Path, "builtin/skills/"), ".md")
	if name == "" {
		return &ToolResult{Success: false, Error: "无效的 skill 路径: " + a.Path}, nil
	}

	sk, ok := tc.SkillStore.Get(tc.NovelID, name)
	if !ok {
		return &ToolResult{Success: false, Error: fmt.Sprintf("内置 skill %q 不存在", name)}, nil
	}

	return &ToolResult{Success: true, Data: map[string]any{
		"path":    a.Path,
		"display": fmt.Sprintf("技能: %s", sk.Name),
		"content": sk.Content,
	}}, nil
}

// ── 工具描述 ──────────────────────────────────────────────

const readDescription = `读取小说文件或技能文件。

路径格式（与 edit 工具一致）：
- chapters/001.md ~ chapters/999999.md（章节文件）
- outlines/001.md ~ outlines/999999.md（章节大纲）
- goink.md（故事状态文档）
- skills/<name>.md（小说级技能）
- ~/.goink/skills/<name>.md（用户级技能）
- builtin/skills/<name>.md（内置技能，只读）
特性：
- 默认添加行号前缀（123|），方便后续 edit 工具进行 line_range_replace 和 search_replace
- start_line 和 end_line 支持行范围读取：默认读前 2000 行，可通过调整参数翻页或精确引用
- 返回 total_lines 表示全文行数，用于判断是否被截断
- include_lines=false 返回纯文本（不含行号）`

// ── 注册 ──────────────────────────────────────────────────

// RegisterRWTools 注册读写工具。
func RegisterRWTools(r *Registry) {
	r.Register(&ReadTool{})
	r.Register(&EditTool{})
}
