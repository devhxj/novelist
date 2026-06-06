package rollback

import (
	"context"

	"gorm.io/gorm"

	"novel/internal/git"
	"novel/internal/storage"
)

// RollbackBeforeTurn 回退到 targetTurn 开始之前的状态。
// 撤销 [targetTurn, lastTurn] 区间所有变更：DB 元数据逆向 + git revert 章节文件。
// 整个操作在单个 DB 事务内完成，git revert 失败则事务 abort，DB 同时回滚。
func RollbackBeforeTurn(ctx context.Context, db *gorm.DB, sessionID string, targetTurn int,
	repo *git.Repo, tcStore *Store) error {

	// 1. 查询当前最新 turn
	var lastTurn int
	if err := db.WithContext(ctx).
		Raw("SELECT last_turn_id FROM sessions WHERE session_id = ?", sessionID).
		Scan(&lastTurn).Error; err != nil {
		return err
	}
	if lastTurn < targetTurn {
		return nil
	}

	// 2. 查询待 revert 的 git commit hash（时间正序，Revert 内部逆序处理）
	commits, err := tcStore.ListForRollback(ctx, sessionID, targetTurn, lastTurn)
	if err != nil {
		return err
	}
	hashes := make([]string, len(commits))
	for i, c := range commits {
		hashes[i] = c.CommitHash
	}

	// 3. 事务：DB 回退 → git revert → 清理 turn_commits
	// git revert 失败时自动触发事务 abort，保证 DB 和文件都不变。
	// 使用 background context 避免 oplog callback 记录回退操作本身。
	return db.Transaction(func(tx *gorm.DB) error {
		if err := storage.RollbackInTx(context.Background(), tx, sessionID, targetTurn, lastTurn); err != nil {
			return err
		}
		if len(hashes) > 0 {
			if err := repo.Revert(hashes); err != nil {
				return err
			}
		}
		return cleanupTurnCommits(tx, sessionID, targetTurn, lastTurn)
	})
}
