package migrate

import (
	"fmt"
	"log/slog"

	"gorm.io/gorm"

	"novel/internal/character"
	"novel/internal/chapter"
	"novel/internal/config"
	"novel/internal/location"
	"novel/internal/novel"
	"novel/internal/reader"
	"novel/internal/session"
	"novel/internal/storage"
	"novel/internal/storyarc"
	"novel/internal/timeline"
	"novel/internal/rollback"
)

// Run 自动创建/更新全部数据表，幂等安全。
func Run(db *gorm.DB, log *slog.Logger) error {
	models := []any{
		&config.AppSettings{},
		&novel.Novel{},
		&novel.PreferenceItem{},
		&chapter.Chapter{},
		&character.Character{},
		&character.CharacterRelation{},
		&timeline.ChapterPlan{},
		&timeline.TimelineEntry{},
		&storyarc.StoryArc{},
		&storyarc.ArcNode{},
		&location.Location{},
		&location.LocationRelation{},
		&reader.ReaderPerspective{},
		&session.Session{},
		&session.Message{},
		&storage.OperationLogRecord{},
		&rollback.TurnCommit{},
	}

	for _, m := range models {
		if err := db.AutoMigrate(m); err != nil {
			return fmt.Errorf("migrate: %T: %w", m, err)
		}
	}

	log.Info("数据库迁移完成", "tables", len(models))
	return nil
}
