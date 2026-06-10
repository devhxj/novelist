package app

import (
	"novel/internal/storage"
	"novel/internal/storyarc"
)

// GetStoryArcs 返回指定小说的全部叙事弧线。弧线通常 3-5 条，全量无分页。
func (a *App) GetStoryArcs(novelID int64) ([]storyarc.StoryArc, error) {
	result, err := a.storyarc.ListByNovel(a.ctx, novelID, storyarc.ListByNovelOptions{
		PageParams: storage.PageParams{Size: 100},
	})
	if err != nil {
		return nil, err
	}
	if result.Items == nil {
		return []storyarc.StoryArc{}, nil
	}
	return result.Items, nil
}

// GetArcNodes 按章节窗口获取弧线节点。fromChapter/toChapter 为 0 表示不限。
func (a *App) GetArcNodes(novelID int64, fromChapter int, toChapter int) ([]storyarc.ArcNode, error) {
	nodes, err := a.storyarc.ListNodesByChapterRange(a.ctx, novelID, fromChapter, toChapter)
	if err != nil {
		return nil, err
	}
	if nodes == nil {
		return []storyarc.ArcNode{}, nil
	}
	return nodes, nil
}
