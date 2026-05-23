package mcp_tools

import (
	"context"
	"encoding/json"
	"log/slog"

	"novel/internal/chapter"
	"novel/internal/storage"
)

// ── get_chapter_list ─────────────────────────────────

// GetChapterListArgs 是 get_chapter_list 的参数。
type GetChapterListArgs struct {
	Page int `json:"page" jsonschema:"description=页码,default=1,minimum=1"    validate:"min=1,omitempty"`
	Size int `json:"size" jsonschema:"description=每页数量,default=20,minimum=1,maximum=100" validate:"min=1,max=100,omitempty"`
}

// GetChapterListTool 获取章节列表，按章节号降序。
type GetChapterListTool struct{}

func (t *GetChapterListTool) Name() string        { return "get_chapter_list" }
func (t *GetChapterListTool) Description() string { return "获取小说的章节列表，支持分页。按章节号降序排列（最新的在前）。返回每章的 id、章节号、标题、字数、摘要。" }
func (t *GetChapterListTool) Category() ToolCategory { return CategoryNovelManagement }

func (t *GetChapterListTool) JSONSchema() json.RawMessage {
	return SchemaOf(GetChapterListArgs{})
}

func (t *GetChapterListTool) ExposeToLLM() bool { return true }
func (t *GetChapterListTool) NewArgs() any     { return &GetChapterListArgs{} }

func (t *GetChapterListTool) Execute(ctx context.Context, args any, tc ToolContext) (*ToolResult, error) {
	a := args.(*GetChapterListArgs)

	if a.Page < 1 {
		a.Page = 1
	}
	if a.Size < 1 || a.Size > 100 {
		a.Size = 20
	}

	chStore := chapter.NewStore(tc.DB, slog.Default())
	result, err := chStore.ListByNovel(ctx, tc.NovelID, chapter.ListByNovelOptions{
		PageParams: storage.PageParams{Page: a.Page, Size: a.Size},
		Order:      "desc",
	})
	if err != nil {
		return &ToolResult{Success: false, Error: "查询章节列表失败"}, nil
	}

	items := make([]map[string]any, len(result.Items))
	for i, ch := range result.Items {
		items[i] = map[string]any{
			"id":             ch.ID,
			"chapter_number": ch.ChapterNumber,
			"title":          ch.Title,
			"word_count":     ch.WordCount,
			"summary":        ch.Summary,
			"created_at":     ch.CreatedAt,
			"updated_at":     ch.UpdatedAt,
		}
	}

	return &ToolResult{
		Success: true,
		Data: map[string]any{
			"items":       items,
			"total":       result.Total,
			"page":        result.Page,
			"size":        result.Size,
			"total_pages": result.TotalPages,
		},
	}, nil
}

// ── 注册 ──────────────────────────────────────────────

// RegisterNovelTools 注册小说管理类工具。
func RegisterNovelTools(r *Registry) {
	r.Register(&GetChapterListTool{})
}
