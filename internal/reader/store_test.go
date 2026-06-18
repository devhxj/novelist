package reader

import (
	"context"
	"log/slog"
	"os"
	"testing"

	"gorm.io/driver/sqlite"
	"gorm.io/gorm"

	"novel/internal/storage"
)

func openRdDB(t *testing.T) *gorm.DB {
	t.Helper()
	db, err := gorm.Open(sqlite.Open(":memory:"), &gorm.Config{})
	if err != nil {
		t.Fatalf("open db: %v", err)
	}
	if err := db.AutoMigrate(&ReaderPerspective{}); err != nil {
		t.Fatalf("migrate: %v", err)
	}
	return db
}

func testRdLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelError}))
}

func TestRdListByNovel_FilterType(t *testing.T) {
	db := openRdDB(t)
	s := NewStore(db, testRdLogger())
	ctx := context.Background()

	db.Create(&ReaderPerspective{NovelID: 1, Type: "known", PlantedChapter: 1})
	db.Create(&ReaderPerspective{NovelID: 1, Type: "suspense", PlantedChapter: 2})

	result, _ := s.ListByNovel(ctx, 1, ListByNovelOptions{Type: "suspense"})
	if result.Total != 1 {
		t.Errorf("filter by type: expected 1, got %d", result.Total)
	}
}

func TestRdListActive(t *testing.T) {
	db := openRdDB(t)
	s := NewStore(db, testRdLogger())
	ctx := context.Background()

	db.Create(&ReaderPerspective{NovelID: 1, Type: "known", PlantedChapter: 1, RevealedChapter: 0})
	db.Create(&ReaderPerspective{NovelID: 1, Type: "suspense", PlantedChapter: 2, RevealedChapter: 5}) // 已揭示
	db.Create(&ReaderPerspective{NovelID: 1, Type: "misconception", PlantedChapter: 3, RevealedChapter: 0})

	active, _ := s.ListActive(ctx, 1)
	if len(active) != 2 {
		t.Errorf("expected 2 active (revealed_chapter=0), got %d", len(active))
	}
}

func TestRdListByNovel_Pagination(t *testing.T) {
	db := openRdDB(t)
	s := NewStore(db, testRdLogger())
	ctx := context.Background()

	for i := 1; i <= 4; i++ {
		db.Create(&ReaderPerspective{NovelID: 1, Type: "known", PlantedChapter: i})
	}

	result, _ := s.ListByNovel(ctx, 1, ListByNovelOptions{
		PageParams: storage.PageParams{Page: 1, Size: 2},
	})
	if len(result.Items) != 2 {
		t.Errorf("expected 2 items, got %d", len(result.Items))
	}
	if result.Total != 4 {
		t.Errorf("total should be 4, got %d", result.Total)
	}
}
