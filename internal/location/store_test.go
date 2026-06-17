package location

import (
	"context"
	"log/slog"
	"os"
	"testing"

	"gorm.io/driver/sqlite"
	"gorm.io/gorm"

	"novel/internal/storage"
)

func openLocDB(t *testing.T) *gorm.DB {
	t.Helper()
	db, err := gorm.Open(sqlite.Open(":memory:"), &gorm.Config{})
	if err != nil {
		t.Fatalf("open db: %v", err)
	}
	if err := db.AutoMigrate(&Location{}, &LocationRelation{}); err != nil {
		t.Fatalf("migrate: %v", err)
	}
	return db
}

func testLocLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelError}))
}

func TestLocListAllByNovel(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	db.Create(&Location{NovelID: 1, Name: "森林"})
	db.Create(&Location{NovelID: 1, Name: "城堡"})
	db.Create(&Location{NovelID: 2, Name: "沙漠"})

	locs, _ := s.ListAllByNovel(ctx, 1)
	if len(locs) != 2 {
		t.Errorf("expected 2, got %d", len(locs))
	}
}

func TestLocListByNovel_Filter(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	db.Create(&Location{NovelID: 1, Name: "迷雾森林", LocationType: "森林"})
	db.Create(&Location{NovelID: 1, Name: "黑铁城堡", LocationType: "城堡"})

	result, _ := s.ListByNovel(ctx, 1, ListByNovelOptions{LocationType: "森林"})
	if result.Total != 1 {
		t.Errorf("filter by type: expected 1, got %d", result.Total)
	}
}

func TestLocListByNovel_Search(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	db.Create(&Location{NovelID: 1, Name: "迷雾森林"})
	db.Create(&Location{NovelID: 1, Name: "黑铁城堡"})

	result, _ := s.ListByNovel(ctx, 1, ListByNovelOptions{Search: "迷雾"})
	if result.Total != 1 {
		t.Errorf("search: expected 1, got %d", result.Total)
	}
}

func TestLocGetChildren(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	parent := Location{NovelID: 1, Name: "王宫"}
	db.Create(&parent)
	db.Create(&Location{NovelID: 1, Name: "大殿", ParentLocationID: &parent.ID})
	db.Create(&Location{NovelID: 1, Name: "密室", ParentLocationID: &parent.ID})

	children, _ := s.GetChildren(ctx, parent.ID)
	if len(children) != 2 {
		t.Errorf("expected 2 children, got %d", len(children))
	}
}

func TestLocGetByIDs(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	l1 := Location{NovelID: 1, Name: "A"}
	l2 := Location{NovelID: 1, Name: "B"}
	db.Create(&l1)
	db.Create(&l2)

	locs, _ := s.GetByIDs(ctx, []int64{l1.ID, l2.ID})
	if len(locs) != 2 {
		t.Errorf("expected 2, got %d", len(locs))
	}
}

func TestLocRelationsByLocation(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	db.Create(&LocationRelation{NovelID: 1, LocationA: 1, LocationB: 2, RelationType: "相邻"})
	db.Create(&LocationRelation{NovelID: 1, LocationA: 1, LocationB: 3, RelationType: "山路"})

	rels, _ := s.ListRelationsByLocation(ctx, 1)
	if len(rels) != 2 {
		t.Errorf("expected 2 relations for location 1, got %d", len(rels))
	}
}

func TestLocRelationsInvolving(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	db.Create(&LocationRelation{NovelID: 1, LocationA: 1, LocationB: 2, RelationType: "相邻"})
	db.Create(&LocationRelation{NovelID: 1, LocationA: 3, LocationB: 4, RelationType: "山路"})

	rels, _ := s.ListRelationsInvolving(ctx, []int64{1, 2})
	if len(rels) != 1 {
		t.Errorf("expected 1 relation involving {1,2}, got %d", len(rels))
	}
}

func TestLocUpsertRelation(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	rel := &LocationRelation{NovelID: 1, LocationA: 1, LocationB: 2, RelationType: "相邻"}
	if err := s.UpsertRelation(ctx, rel); err != nil {
		t.Fatalf("first upsert: %v", err)
	}

	// 第二次 upsert 同对，应该更新
	rel2 := &LocationRelation{NovelID: 1, LocationA: 1, LocationB: 2, RelationType: "骑马半天"}
	if err := s.UpsertRelation(ctx, rel2); err != nil {
		t.Fatalf("second upsert: %v", err)
	}

	var count int64
	db.Model(&LocationRelation{}).Count(&count)
	if count != 1 {
		t.Errorf("upsert should not create duplicate, got %d rows", count)
	}
}

func TestLocListByNovel_Pagination(t *testing.T) {
	db := openLocDB(t)
	s := NewStore(db, testLocLogger())
	ctx := context.Background()

	for _, name := range []string{"a", "b", "c", "d"} {
		db.Create(&Location{NovelID: 1, Name: name})
	}

	result, _ := s.ListByNovel(ctx, 1, ListByNovelOptions{
		PageParams: storage.PageParams{Page: 1, Size: 2},
	})
	if len(result.Items) != 2 {
		t.Errorf("expected 2, got %d", len(result.Items))
	}
}
