package session

import (
	"context"
	"fmt"
	"log/slog"

	"gorm.io/gorm"
)

// Store 管理 Session/Message 持久化。DB 导出供调用方做简单 CRUD（Create/First/Append）。
type Store struct {
	DB     *gorm.DB
	logger *slog.Logger
}

// NewStore 创建 session 存储。
func NewStore(db *gorm.DB, logger *slog.Logger) *Store {
	return &Store{DB: db, logger: logger}
}

// ========== Session 查询 ==========

// ListSessions 按小说列出会话，updated_at 倒序，分页。
func (s *Store) ListSessions(ctx context.Context, novelID int64, page, size int) ([]Session, int64, error) {
	if page < 1 {
		page = 1
	}
	if size < 1 || size > 100 {
		size = 20
	}

	var sessions []Session
	var total int64

	if err := s.DB.WithContext(ctx).
		Model(&Session{}).
		Where("novel_id = ?", novelID).
		Count(&total).Error; err != nil {
		return nil, 0, fmt.Errorf("session store: count sessions: %w", err)
	}

	offset := (page - 1) * size
	if err := s.DB.WithContext(ctx).
		Where("novel_id = ?", novelID).
		Order("updated_at DESC").
		Offset(offset).
		Limit(size).
		Find(&sessions).Error; err != nil {
		return nil, 0, fmt.Errorf("session store: list sessions: %w", err)
	}

	s.logger.Debug("session store: listed sessions", "novel_id", novelID, "total", total, "page", page)
	return sessions, total, nil
}

// ========== Session 更新 ==========

// UpdateSessionMeta 增量更新标题、模型、推理深度。空字符串不更新。
func (s *Store) UpdateSessionMeta(ctx context.Context, sessionID, title, model, reasoningEffort string) error {
	if title == "" && model == "" && reasoningEffort == "" {
		return nil
	}

	var sess Session
	if err := s.DB.WithContext(ctx).
		Where("session_id = ?", sessionID).
		First(&sess).Error; err != nil {
		return fmt.Errorf("session store: update meta: %w", err)
	}

	if title != "" {
		sess.Title = title
	}
	if model != "" {
		sess.Model = model
	}
	if reasoningEffort != "" {
		sess.ReasoningEffort = reasoningEffort
	}

	if err := s.DB.WithContext(ctx).Save(&sess).Error; err != nil {
		return fmt.Errorf("session store: update meta: %w", err)
	}
	return nil
}

// UpdateSessionUsage 更新最近一次 LLM 的 token 用量。
func (s *Store) UpdateSessionUsage(ctx context.Context, sessionID, usageJSON string) error {
	var sess Session
	if err := s.DB.WithContext(ctx).
		Where("session_id = ?", sessionID).
		First(&sess).Error; err != nil {
		return fmt.Errorf("session store: update usage: %w", err)
	}

	sess.Usage = usageJSON

	if err := s.DB.WithContext(ctx).Save(&sess).Error; err != nil {
		return fmt.Errorf("session store: update usage: %w", err)
	}
	return nil
}

// BumpActiveVersion 递增 active_version 并返回新值。
func (s *Store) BumpActiveVersion(ctx context.Context, sessionID string) (int, error) {
	var sess Session
	if err := s.DB.WithContext(ctx).
		Where("session_id = ?", sessionID).
		First(&sess).Error; err != nil {
		return 0, fmt.Errorf("session store: bump version: %w", err)
	}

	sess.ActiveVersion++

	if err := s.DB.WithContext(ctx).Save(&sess).Error; err != nil {
		return 0, fmt.Errorf("session store: bump version: %w", err)
	}

	newV := sess.ActiveVersion
	s.logger.Debug("session store: bumped version", "session_id", sessionID, "new_version", newV)
	return newV, nil
}

// ========== Message 查询 ==========

// NextTurn 原子递增 last_turn_id 并返回新值。
// agent loop 在每个 turn 开始时调用，一步完成递增 + 持久化。
func (s *Store) NextTurn(ctx context.Context, sessionID string) (int, error) {
	var turnID int
	if err := s.DB.WithContext(ctx).
		Raw("UPDATE sessions SET last_turn_id = last_turn_id + 1 WHERE session_id = ? RETURNING last_turn_id", sessionID).
		Scan(&turnID).Error; err != nil {
		return 0, fmt.Errorf("session store: next turn: %w", err)
	}

	s.logger.Debug("session store: next turn", "session_id", sessionID, "turn_id", turnID)
	return turnID, nil
}

// GetMessagesForAPI 返回 LLM context 所需的消息。
func (s *Store) GetMessagesForAPI(ctx context.Context, sessionID string, version int) ([]Message, error) {
	var msgs []Message
	if err := s.DB.WithContext(ctx).
		Where("session_id = ? AND to_api = ? AND version = ?", sessionID, true, version).
		Order("created_at ASC").
		Find(&msgs).Error; err != nil {
		return nil, fmt.Errorf("session store: get api messages: %w", err)
	}
	return msgs, nil
}

// GetMessagesForFrontend 返回前端展示所需的消息。
func (s *Store) GetMessagesForFrontend(ctx context.Context, sessionID string) ([]Message, error) {
	var msgs []Message
	if err := s.DB.WithContext(ctx).
		Where("session_id = ? AND to_frontend = ?", sessionID, true).
		Order("created_at ASC").
		Find(&msgs).Error; err != nil {
		return nil, fmt.Errorf("session store: get frontend messages: %w", err)
	}
	return msgs, nil
}

// GetAllMessages 返回全部消息，审计/回退用。
func (s *Store) GetAllMessages(ctx context.Context, sessionID string) ([]Message, error) {
	var msgs []Message
	if err := s.DB.WithContext(ctx).
		Where("session_id = ?", sessionID).
		Order("created_at ASC").
		Find(&msgs).Error; err != nil {
		return nil, fmt.Errorf("session store: get all messages: %w", err)
	}
	return msgs, nil
}
