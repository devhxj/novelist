package rollback

import (
	"context"
	"fmt"
	"log/slog"

	"gorm.io/gorm"
)

// Store 管理 turn_commits 表的读写。
type Store struct {
	DB     *gorm.DB
	logger *slog.Logger
}

// NewStore 创建 Store 实例。
func NewStore(db *gorm.DB, logger *slog.Logger) *Store {
	return &Store{DB: db, logger: logger}
}

// ListForRollback 查询 [targetTurn, lastTurn] 区间内所有 git commit，
// 按 turn_id ASC, id ASC 返回（时间正序，传给 Revert 时外部会逆序）。
// 与 storage.RollbackTo / RollbackInTx 的语义和签名对齐。
func (s *Store) ListForRollback(ctx context.Context, sessionID string, targetTurn, lastTurn int) ([]TurnCommit, error) {
	var commits []TurnCommit
	if err := s.DB.WithContext(ctx).
		Where("session_id = ? AND turn_id >= ? AND turn_id <= ?", sessionID, targetTurn, lastTurn).
		Order("turn_id ASC, id ASC").
		Find(&commits).Error; err != nil {
		return nil, fmt.Errorf("rollback store: list: %w", err)
	}
	return commits, nil
}

// cleanupTurnCommits 在 RollbackTurn 事务内删除指定区间的 turn_commits 记录。
func cleanupTurnCommits(tx *gorm.DB, sessionID string, targetTurn, lastTurn int) error {
	return tx.Where("session_id = ? AND turn_id >= ? AND turn_id <= ?",
		sessionID, targetTurn, lastTurn).Delete(&TurnCommit{}).Error
}
