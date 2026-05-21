package storyarc

import (
	"context"
	"fmt"
	"log/slog"

	"gorm.io/gorm"

	"novel/internal/storage"
)

// Store 管理 StoryArc 和 ArcNode 持久化。DB 导出供调用方做简单 CRUD。
//
// 设计要点：
//   - 弧线节点窗口策略跟 TimelineEntry 一致：以章节号为锚点，ListNodesAfter 覆盖未来（>=N），
//     ListNodesBefore 覆盖近期历史（<N 最近 N 条），ListPendingNodesBefore 兜底远古未完成的。
//     三个方法返回原始数据，MCP 工具负责格式化输出。
//   - ArcNode 没有 Upsert——LLM 有 id 就 Update，没 id 就新建（GetMaxSequence+1 自动编号）。
//   - paused 弧线的恢复不在此层判断——MCP 工具将弧线名+断点（下个 pending 节点的 title+target_chapter）
//     +reactivate_at 格式化后呈现给 LLM，LLM 自行判断是否满足恢复条件。
type Store struct {
	DB     *gorm.DB
	logger *slog.Logger
}

// NewStore 创建 storyarc 存储。
func NewStore(db *gorm.DB, logger *slog.Logger) *Store {
	return &Store{DB: db, logger: logger}
}

// ── StoryArc ─────────────────────────────────────────

// ListByNovelOptions 是 ListByNovel 的可选参数。
type ListByNovelOptions struct {
	PageParams storage.PageParams
	ArcType    string // 空字符串=不过滤，"main"/"sub"/"character"/"background"
	Status     string // 空字符串=不过滤，"active"/"paused"/"completed"/"abandoned"
}

// ListByNovel 分页列出某小说的叙事弧线，支持类型和状态过滤。前端管理页和 MCP full 模式用。
func (s *Store) ListByNovel(ctx context.Context, novelID int64, opts ListByNovelOptions) (*storage.PageResult[StoryArc], error) {
	pp := opts.PageParams
	pp.Normalize()

	q := s.DB.WithContext(ctx).Model(&StoryArc{}).Where("novel_id = ?", novelID)

	if opts.ArcType != "" {
		q = q.Where("arc_type = ?", opts.ArcType)
	}
	if opts.Status != "" {
		q = q.Where("status = ?", opts.Status)
	}

	var total int64
	if err := q.Count(&total).Error; err != nil {
		return nil, fmt.Errorf("storyarc store: count: %w", err)
	}

	var arcs []StoryArc
	offset := (pp.Page - 1) * pp.Size
	if err := q.Order("importance DESC, created_at ASC").Offset(offset).Limit(pp.Size).Find(&arcs).Error; err != nil {
		return nil, fmt.Errorf("storyarc store: list: %w", err)
	}

	s.logger.Debug("storyarc store: listed", "novel_id", novelID, "total", total, "page", pp.Page)
	return storage.NewPageResult(arcs, total, pp.Page, pp.Size), nil
}

// ListNonArchived 返回 active 和 paused 的弧线。completed/abandoned 由 MCP 工具按需单查，store 不限制。
func (s *Store) ListNonArchived(ctx context.Context, novelID int64) ([]StoryArc, error) {
	var arcs []StoryArc
	if err := s.DB.WithContext(ctx).
		Where("novel_id = ? AND status IN ?", novelID, []string{"active", "paused"}).
		Order("importance DESC, created_at ASC").
		Find(&arcs).Error; err != nil {
		return nil, fmt.Errorf("storyarc store: list non-archived: %w", err)
	}
	return arcs, nil
}

// ── ArcNode ──────────────────────────────────────────

// ListByArcs 批量取多条弧线的全部节点，按 (story_arc_id, sequence) 排序。
// MCP 工具展开弧线链和前端渲染用。返回的是全量节点，不做窗口切分。
func (s *Store) ListByArcs(ctx context.Context, arcIDs []int64) ([]ArcNode, error) {
	if len(arcIDs) == 0 {
		return nil, nil
	}
	var nodes []ArcNode
	if err := s.DB.WithContext(ctx).
		Where("story_arc_id IN ?", arcIDs).
		Order("story_arc_id, sequence ASC").
		Find(&nodes).Error; err != nil {
		return nil, fmt.Errorf("storyarc store: list by arcs: %w", err)
	}
	return nodes, nil
}

// ListNodesBefore 取 target_chapter < chapterID 的最近 limit 条节点，不论状态。
// 窗口含义：当前章节之前的近期节点，含 resolved/abandoned，提供"最近完成了什么、放弃了什么"的历史上下文。
// MCP 工具调用后按弧线分组，格式化到对应弧线的节点链中。
func (s *Store) ListNodesBefore(ctx context.Context, arcIDs []int64, chapterID int, limit int) ([]ArcNode, error) {
	if len(arcIDs) == 0 {
		return nil, nil
	}
	var nodes []ArcNode
	if err := s.DB.WithContext(ctx).
		Where("story_arc_id IN ? AND target_chapter < ?", arcIDs, chapterID).
		Order("target_chapter DESC").
		Limit(limit).
		Find(&nodes).Error; err != nil {
		return nil, fmt.Errorf("storyarc store: nodes before: %w", err)
	}
	return nodes, nil
}

// ListPendingNodesBefore 取 target_chapter < chapterID 且 pending 的全部节点，兜底截断 100。
// 窗口含义：远在过去但仍未完成的节点——异常状态，应在输出中标红提醒 LLM 处理。
func (s *Store) ListPendingNodesBefore(ctx context.Context, arcIDs []int64, chapterID int) ([]ArcNode, error) {
	if len(arcIDs) == 0 {
		return nil, nil
	}
	var nodes []ArcNode
	if err := s.DB.WithContext(ctx).
		Where("story_arc_id IN ? AND target_chapter < ? AND status = ?", arcIDs, chapterID, "pending").
		Order("target_chapter ASC").
		Limit(100).
		Find(&nodes).Error; err != nil {
		return nil, fmt.Errorf("storyarc store: pending nodes before: %w", err)
	}
	return nodes, nil
}

// ListNodesAfter 取 target_chapter >= chapterID 的全部节点，不论状态，兜底截断 100。
// 窗口含义：当前章节及之后的全部节点，提供"接下来该发生什么"的未来视图。
func (s *Store) ListNodesAfter(ctx context.Context, arcIDs []int64, chapterID int) ([]ArcNode, error) {
	if len(arcIDs) == 0 {
		return nil, nil
	}
	var nodes []ArcNode
	if err := s.DB.WithContext(ctx).
		Where("story_arc_id IN ? AND target_chapter >= ?", arcIDs, chapterID).
		Order("target_chapter ASC").
		Limit(100).
		Find(&nodes).Error; err != nil {
		return nil, fmt.Errorf("storyarc store: nodes after: %w", err)
	}
	return nodes, nil
}

// GetMaxSequence 返回某弧线的最大 sequence 值，新建节点时自动编号用。空弧线返回 0。
func (s *Store) GetMaxSequence(ctx context.Context, arcID int64) (int, error) {
	var maxSeq int
	if err := s.DB.WithContext(ctx).
		Model(&ArcNode{}).
		Where("story_arc_id = ?", arcID).
		Select("COALESCE(MAX(sequence), 0)").
		Scan(&maxSeq).Error; err != nil {
		return 0, fmt.Errorf("storyarc store: max sequence: %w", err)
	}
	return maxSeq, nil
}
