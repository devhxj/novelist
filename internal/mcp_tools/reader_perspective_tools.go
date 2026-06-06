package mcp_tools

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"strings"

	"gorm.io/gorm"

	"novel/internal/reader"
	"novel/internal/storage"
)

// ── get_reader_perspective ──────────────────────────────

// GetReaderPerspectiveArgs 无参——LLM 按需调用即可。
type GetReaderPerspectiveArgs struct{}

// GetReaderPerspectiveTool 返回读者当前认知状态的三段式摘要。
// known 兜底截断 100 条——完整认知上下文只需最近的关键事实。
type GetReaderPerspectiveTool struct{}

func (t *GetReaderPerspectiveTool) Name() string        { return "get_reader_perspective" }
func (t *GetReaderPerspectiveTool) Description() string {
	return "获取当前小说的读者认知状态：已知信息、活跃悬念、读者误知。" +
		"每条条目末尾的 `[entry_id:X]` 是该条目的唯一标识，更新或回收时填入 entry_id。" +
		"这不是 TODO 列表——尽量合并同类信息到已有条目，减少重复创建。"
}
func (t *GetReaderPerspectiveTool) Category() ToolCategory { return CategoryMemoryRetrieval }

func (t *GetReaderPerspectiveTool) JSONSchema() json.RawMessage {
	return SchemaOf(GetReaderPerspectiveArgs{})
}

func (t *GetReaderPerspectiveTool) ExposeToLLM() bool { return true }
func (t *GetReaderPerspectiveTool) NewArgs() any     { return &GetReaderPerspectiveArgs{} }

func (t *GetReaderPerspectiveTool) Execute(ctx context.Context, args any, tc ToolContext) (*ToolResult, error) {
	rs := reader.NewStore(tc.DB, slog.Default())

	// known：全量可能很大，兜底截断 100 条，按 planted_chapter DESC 取最近的
	knownResult, err := rs.ListByNovel(ctx, tc.NovelID, reader.ListByNovelOptions{
		PageParams: storage.PageParams{Page: 1, Size: 100},
		Type:       reader.TypeKnown,
	})
	if err != nil {
		return nil, fmt.Errorf("query known perspectives: %w", err)
	}

	// suspense + misconception：只取未回收的
	active, err := rs.ListActive(ctx, tc.NovelID)
	if err != nil {
		return nil, fmt.Errorf("query active perspectives: %w", err)
	}

	var suspenses []reader.ReaderPerspective
	var misconceptions []reader.ReaderPerspective
	for _, e := range active {
		switch e.Type {
		case reader.TypeSuspense:
			suspenses = append(suspenses, e)
		case reader.TypeMisconception:
			misconceptions = append(misconceptions, e)
		}
	}

	formatted := formatReaderPerspective(knownResult.Items, suspenses, misconceptions)

	return &ToolResult{
		Success: true,
		Data: map[string]any{
			"content": formatted,
			"counts": map[string]int{
				"known":         len(knownResult.Items),
				"suspense":      len(suspenses),
				"misconception": len(misconceptions),
			},
		},
	}, nil
}

// ── 格式化 ──────────────────────────────────────────────

func formatReaderPerspective(known, suspenses, misconceptions []reader.ReaderPerspective) string {
	var sections []string

	ref := func(e reader.ReaderPerspective) string {
		return fmt.Sprintf(" `[entry_id:%d]`", e.ID)
	}

	// 已知信息
	if len(known) > 0 {
		lines := []string{"### 已知信息"}
		for _, e := range known {
			lines = append(lines, fmt.Sprintf("- %s [第%d章起]%s", e.Content, e.PlantedChapter, ref(e)))
		}
		sections = append(sections, strings.Join(lines, "\n"))
	}

	// 活跃悬念
	if len(suspenses) > 0 {
		lines := []string{"### 活跃悬念"}
		for _, e := range suspenses {
			refText := fmt.Sprintf("（第%d章种下", e.PlantedChapter)
			if e.LastMentionedChapter > 0 {
				refText += fmt.Sprintf("，最近提及：第%d章", e.LastMentionedChapter)
			}
			refText += "）"
			lines = append(lines, fmt.Sprintf("- %s%s%s", e.Content, refText, ref(e)))
		}
		sections = append(sections, strings.Join(lines, "\n"))
	}

	// 读者误知
	if len(misconceptions) > 0 {
		lines := []string{"### 读者误知"}
		for _, e := range misconceptions {
			truth := ""
			if e.RelatedTruth != "" {
				truth = fmt.Sprintf(" → 实际：%s", e.RelatedTruth)
			}
			lines = append(lines, fmt.Sprintf("- %s%s%s", e.Content, truth, ref(e)))
		}
		sections = append(sections, strings.Join(lines, "\n"))
	}

	if len(sections) == 0 {
		return "暂无读者认知数据。"
	}
	return strings.Join(sections, "\n\n")
}

// ── create_reader_perspective_entry ──────────────────────

// CreateReaderPerspectiveEntryItem 是 create_reader_perspective_entry 的单条参数。
type CreateReaderPerspectiveEntryItem struct {
	Type                 string `json:"type" jsonschema:"required,description=条目类型,enum=known,enum=suspense,enum=misconception" validate:"required,oneof=known suspense misconception"`
	Content              string `json:"content" jsonschema:"required,description=内容描述"          validate:"required"`
	PlantedChapter       int    `json:"planted_chapter" jsonschema:"required,description=种下的章节号"    validate:"required,min=1"`
	RelatedTruth         string `json:"related_truth" jsonschema:"description=仅 misconception：真实情况是什么"`
	PlannedRevealChapter int    `json:"planned_reveal_chapter" jsonschema:"description=仅 suspense/misconception：计划在哪章揭露或回收"`
}

// CreateReaderPerspectiveEntryArgs 是 create_reader_perspective_entry 的参数。
type CreateReaderPerspectiveEntryArgs struct {
	Entries []CreateReaderPerspectiveEntryItem `json:"entries" jsonschema:"required,description=要创建的读者认知条目（1-10个）" validate:"required,min=1,max=10,dive"`
}

// CreateReaderPerspectiveEntryTool 创建一条读者认知条目。
type CreateReaderPerspectiveEntryTool struct{}

func (t *CreateReaderPerspectiveEntryTool) Name() string { return "create_reader_perspective_entry" }
func (t *CreateReaderPerspectiveEntryTool) Description() string {
	return "批量添加读者认知条目（1-10个）。所有条目在一次批量 INSERT 中写入，单语句保证原子性。三种类型：\n" +
		"- known：读者在某章之后知道了什么\n" +
		"- suspense：读者当前在等待解答的悬念\n" +
		"- misconception：读者以为的情况（用于未来反转）\n" +
		"每章写完后如有新揭露的信息或新种下的悬念，应主动添加。"
}
func (t *CreateReaderPerspectiveEntryTool) Category() ToolCategory { return CategoryWritingAssistant }

func (t *CreateReaderPerspectiveEntryTool) JSONSchema() json.RawMessage {
	return SchemaOf(CreateReaderPerspectiveEntryArgs{})
}

func (t *CreateReaderPerspectiveEntryTool) ExposeToLLM() bool { return true }
func (t *CreateReaderPerspectiveEntryTool) NewArgs() any     { return &CreateReaderPerspectiveEntryArgs{} }

func (t *CreateReaderPerspectiveEntryTool) Execute(ctx context.Context, args any, tc ToolContext) (*ToolResult, error) {
	a := args.(*CreateReaderPerspectiveEntryArgs)

	// 预校验：misconception 必须提供 related_truth
	for _, item := range a.Entries {
		if item.Type == reader.TypeMisconception && item.RelatedTruth == "" {
			return &ToolResult{Success: false, Error: "misconception 类型必须提供 related_truth（实际真相）"}, nil
		}
	}

	items := make([]reader.ReaderPerspective, len(a.Entries))
	for i, item := range a.Entries {
		entry := reader.ReaderPerspective{
			NovelID:        tc.NovelID,
			Type:           item.Type,
			Content:        item.Content,
			PlantedChapter: item.PlantedChapter,
			RelatedTruth:   item.RelatedTruth,
		}
		if item.Type == reader.TypeSuspense || item.Type == reader.TypeMisconception {
			entry.RevealedChapter = item.PlannedRevealChapter
		}
		items[i] = entry
	}

	if err := tc.DB.WithContext(ctx).Create(&items).Error; err != nil {
		return nil, fmt.Errorf("create reader perspective: %w", err)
	}

	ids := make([]int64, len(items))
	for i := range items {
		ids[i] = items[i].ID
	}

	return &ToolResult{
		Success: true,
		Data:    map[string]any{"ids": ids, "count": len(ids)},
	}, nil
}

// ── update_reader_perspective_entry ──────────────────────

// UpdateReaderPerspectiveEntryArgs 是 update_reader_perspective_entry 的参数。
type UpdateReaderPerspectiveEntryArgs struct {
	EntryID              int `json:"entry_id" jsonschema:"required,description=要更新的条目 ID" validate:"required,min=1"`
	LastMentionedChapter int `json:"last_mentioned_chapter" jsonschema:"description=最近提及的章节号"`
	Content              string `json:"content" jsonschema:"description=更新后的完整内容描述"`
	RevealedChapter      int `json:"revealed_chapter" jsonschema:"description=实际揭露或回收的章节号（设置后该条目不再出现在活跃列表中）"`
	PlantedChapter       int    `json:"planted_chapter" jsonschema:"description=在哪章种下的章节号"`
	RelatedTruth         string `json:"related_truth" jsonschema:"description=作者视角的真实情况（支持所有类型）"`
	Type                 string `json:"type" jsonschema:"description=条目类型：known（已知信息）、suspense（悬念）、misconception（读者误知）"`
}

// UpdateReaderPerspectiveEntryTool 更新读者认知条目。
type UpdateReaderPerspectiveEntryTool struct{}

func (t *UpdateReaderPerspectiveEntryTool) Name() string { return "update_reader_perspective_entry" }
func (t *UpdateReaderPerspectiveEntryTool) Description() string {
	return "更新一条读者认知条目。常见用途：\n" +
		"- 回收悬念：设置 revealed_chapter\n" +
		"- 更新提及频率：设置 last_mentioned_chapter\n" +
		"- 揭露误知：设置 revealed_chapter"
}
func (t *UpdateReaderPerspectiveEntryTool) Category() ToolCategory { return CategoryWritingAssistant }

func (t *UpdateReaderPerspectiveEntryTool) JSONSchema() json.RawMessage {
	return SchemaOf(UpdateReaderPerspectiveEntryArgs{})
}

func (t *UpdateReaderPerspectiveEntryTool) ExposeToLLM() bool { return true }
func (t *UpdateReaderPerspectiveEntryTool) NewArgs() any     { return &UpdateReaderPerspectiveEntryArgs{} }

func (t *UpdateReaderPerspectiveEntryTool) Execute(ctx context.Context, args any, tc ToolContext) (*ToolResult, error) {
	a := args.(*UpdateReaderPerspectiveEntryArgs)

	if a.LastMentionedChapter == 0 && a.RevealedChapter == 0 && a.PlantedChapter == 0 && a.Content == "" && a.Type == "" && a.RelatedTruth == "" {
		return &ToolResult{Success: false, Error: "至少需要提供一个要修改的字段"}, nil
	}

	var entry reader.ReaderPerspective
	if err := tc.DB.WithContext(ctx).
		Where("id = ? AND novel_id = ?", a.EntryID, tc.NovelID).
		First(&entry).Error; err != nil {
		if err == gorm.ErrRecordNotFound {
			return &ToolResult{Success: false, Error: fmt.Sprintf("条目 %d 不存在", a.EntryID)}, nil
		}
		return nil, fmt.Errorf("query perspective entry: %w", err)
	}

	json.Unmarshal(tc.RawArgs, &entry)

	if err := tc.DB.WithContext(ctx).Save(&entry).Error; err != nil {
		return nil, fmt.Errorf("save perspective entry: %w", err)
	}

	return &ToolResult{
		Success: true,
		Data:    map[string]any{"id": entry.ID, "revealed_chapter": entry.RevealedChapter},
	}, nil
}

// ── 注册 ──────────────────────────────────────────────

// RegisterReaderPerspectiveTools 注册读者认知类工具。
func RegisterReaderPerspectiveTools(r *Registry) {
	r.Register(&GetReaderPerspectiveTool{})
	r.Register(&CreateReaderPerspectiveEntryTool{})
	r.Register(&UpdateReaderPerspectiveEntryTool{})
}
