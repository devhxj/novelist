package rollback

import "time"

// TurnCommit 记录每个 turn 的 git commit hash，用于回退时精确定位。
// 每个 turn 最多两条记录：commit_type="user"（用户手动修改）和 commit_type="ai"（AI 修改）。
type TurnCommit struct {
	ID         int64     `gorm:"column:id;primaryKey;autoIncrement"`
	SessionID  string    `gorm:"column:session_id;not null;uniqueIndex:uk_turn_commits"`
	TurnID     int       `gorm:"column:turn_id;not null;uniqueIndex:uk_turn_commits"`
	CommitType string    `gorm:"column:commit_type;not null;uniqueIndex:uk_turn_commits"` // "user" | "ai"
	CommitHash string    `gorm:"column:commit_hash;not null"`
	CreatedAt  time.Time `gorm:"column:created_at;autoCreateTime"`
}

func (TurnCommit) TableName() string { return "turn_commits" }
