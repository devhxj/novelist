package migrate

import (
	"fmt"
	"log/slog"

	"gorm.io/gorm"

	"novel/internal/chapter"
	"novel/internal/character"
	"novel/internal/config"
	"novel/internal/location"
	"novel/internal/novel"
	"novel/internal/reader"
	"novel/internal/rollback"
	"novel/internal/session"
	"novel/internal/storage"
	"novel/internal/storyarc"
	"novel/internal/timeline"
)

// Run 自动创建/更新全部数据表，幂等安全。
func Run(db *gorm.DB, log *slog.Logger) error {
	// 移除旧 novels 表的 dir_path 列（该字段从未被读取过）。幂等：列不存在时报错忽略。
	if err := db.Exec("ALTER TABLE novels DROP COLUMN dir_path").Error; err != nil {
		log.Warn("迁移：删除 novels.dir_path 列失败（如列已不存在则无害）", "err", err)
	}

	models := []any{
		&config.AppSettings{},
		&novel.Novel{},
		&novel.PreferenceItem{},
		&chapter.Chapter{},
		&character.Character{},
		&character.CharacterRelation{},
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
