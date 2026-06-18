package storyarc

import (
	"context"
	"log/slog"
	"os"
	"testing"

	"gorm.io/driver/sqlite"
	"gorm.io/gorm"
)

func openArcDB(t *testing.T) *gorm.DB {
	t.Helper()
	db, err := gorm.Open(sqlite.Open(":memory:"), &gorm.Config{})
	if err != nil {
		t.Fatalf("open db: %v", err)
	}
	if err := db.AutoMigrate(&StoryArc{}, &ArcNode{}); err != nil {
		t.Fatalf("migrate: %v", err)
	}
	return db
}

func testArcLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelError}))
}

func TestArcListByNovel(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	db.Create(&StoryArc{NovelID: 1, Name: "主线", ArcType: "main", Status: "active", Importance: 5})
	db.Create(&StoryArc{NovelID: 1, Name: "支线", ArcType: "sub", Status: "paused", Importance: 3})

	result, _ := s.ListByNovel(ctx, 1, ListByNovelOptions{})
	if result.Total != 2 {
		t.Errorf("expected 2, got %d", result.Total)
	}
}

func TestArcListByNovel_Filter(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	db.Create(&StoryArc{NovelID: 1, Name: "主线", ArcType: "main", Status: "active", Importance: 5})
	db.Create(&StoryArc{NovelID: 1, Name: "支线", ArcType: "sub", Status: "completed", Importance: 2})

	result, _ := s.ListByNovel(ctx, 1, ListByNovelOptions{Status: "active"})
	if result.Total != 1 {
		t.Errorf("filter active: expected 1, got %d", result.Total)
	}
}

func TestArcListNonArchived(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	db.Create(&StoryArc{NovelID: 1, Name: "A", Status: "active", Importance: 1})
	db.Create(&StoryArc{NovelID: 1, Name: "B", Status: "paused", Importance: 1})
	db.Create(&StoryArc{NovelID: 1, Name: "C", Status: "completed", Importance: 1})
	db.Create(&StoryArc{NovelID: 1, Name: "D", Status: "abandoned", Importance: 1})

	arcs, _ := s.ListNonArchived(ctx, 1)
	if len(arcs) != 2 {
		t.Errorf("expected 2 non-archived (active+paused), got %d", len(arcs))
	}
}

func TestArcListByArcs(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	arc := StoryArc{NovelID: 1, Name: "主线", Status: "active", Importance: 5}
	db.Create(&arc)
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "节点1", TargetChapter: 5, Status: "pending"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "节点2", TargetChapter: 10, Status: "pending"})

	nodes, _ := s.ListByArcs(ctx, []int64{arc.ID})
	if len(nodes) != 2 {
		t.Errorf("expected 2 nodes, got %d", len(nodes))
	}
}

func TestArcNodesByChapterRange(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	db.Create(&ArcNode{NovelID: 1, StoryArcID: 1, Title: "早", TargetChapter: 5, Status: "pending"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: 1, Title: "中", TargetChapter: 10, Status: "pending"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: 1, Title: "晚", TargetChapter: 15, Status: "pending"})

	result, _ := s.ListNodesByChapterRange(ctx, 1, 8, 12)
	if len(result) != 1 {
		t.Errorf("range [8,12]: expected 1, got %d", len(result))
	}
}

func TestArcNodesBeforeByArc(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	arc := StoryArc{NovelID: 1, Name: "主线", Status: "active", Importance: 5}
	db.Create(&arc)
	for i := 1; i <= 10; i++ {
		db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "n", TargetChapter: i, Status: "pending"})
	}

	result, _ := s.ListNodesBeforeByArc(ctx, []int64{arc.ID}, 6, 3)
	nodes := result[arc.ID]
	if len(nodes) != 3 {
		t.Errorf("expected 3 before ch6, got %d", len(nodes))
	}
}

func TestArcPendingNodesBeforeByArc(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	arc := StoryArc{NovelID: 1, Name: "主线", Status: "active", Importance: 5}
	db.Create(&arc)
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "pending", TargetChapter: 3, Status: "pending"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "done", TargetChapter: 5, Status: "completed"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "future", TargetChapter: 10, Status: "pending"})

	result, _ := s.ListPendingNodesBeforeByArc(ctx, []int64{arc.ID}, 8)
	if len(result[arc.ID]) != 1 {
		t.Errorf("expected 1 pending before ch8, got %d", len(result[arc.ID]))
	}
}

func TestArcNodesAfterByArc(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	arc := StoryArc{NovelID: 1, Name: "主线", Status: "active", Importance: 5}
	db.Create(&arc)
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "past", TargetChapter: 5, Status: "pending"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "now", TargetChapter: 10, Status: "pending"})

	result, _ := s.ListNodesAfterByArc(ctx, []int64{arc.ID}, 10)
	if len(result[arc.ID]) != 1 {
		t.Errorf("expected 1 at ch>=10, got %d", len(result[arc.ID]))
	}
}

func TestArcGetBreakpoint(t *testing.T) {
	db := openArcDB(t)
	s := NewStore(db, testArcLogger())
	ctx := context.Background()

	arc := StoryArc{NovelID: 1, Name: "暂停弧", Status: "paused", Importance: 3}
	db.Create(&arc)
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "完成1", TargetChapter: 3, Status: "completed"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "完成2", TargetChapter: 5, Status: "completed"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "断点", TargetChapter: 8, Status: "pending"})
	db.Create(&ArcNode{NovelID: 1, StoryArcID: arc.ID, Title: "下一个", TargetChapter: 12, Status: "pending"})

	before, pending, err := s.GetBreakpoint(ctx, arc.ID)
	if err != nil {
		t.Fatalf("GetBreakpoint: %v", err)
	}
	if len(before) != 2 {
		t.Errorf("expected 2 completed before, got %d", len(before))
	}
	if len(pending) != 2 {
		t.Errorf("expected 2 pending (breakpoint + next), got %d", len(pending))
	}
}
