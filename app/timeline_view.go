package app

import (
	"novel/internal/timeline"
)

// GetChapterPlans 返回指定小说的章节计划（next/near/far 三个槽位）。
func (a *App) GetChapterPlans(novelID int64) ([]timeline.ChapterPlan, error) {
	plans, err := a.timeline.GetPlans(a.ctx, novelID)
	if err != nil {
		return nil, err
	}
	if plans == nil {
		return []timeline.ChapterPlan{}, nil
	}
	return plans, nil
}

// GetTimelineEntries 按章节窗口返回伏笔/用户指令。from/to 为 0 表示不限。
func (a *App) GetTimelineEntries(novelID int64, fromChapter int, toChapter int) ([]timeline.TimelineEntry, error) {
	entries, err := a.timeline.ListByChapterRange(a.ctx, novelID, fromChapter, toChapter)
	if err != nil {
		return nil, err
	}
	if entries == nil {
		return []timeline.TimelineEntry{}, nil
	}
	return entries, nil
}
