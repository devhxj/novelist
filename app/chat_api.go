package app

import (
	"fmt"

	"novel/internal/config"
	"novel/internal/llm"
	"novel/internal/session"
	"novel/internal/storage"
)

// SessionMeta 是前端会话列表的轻量视图。
type SessionMeta struct {
	SessionID string `json:"session_id"`
	Title     string `json:"title"`
	Model     string `json:"model"`
	UpdatedAt string `json:"updated_at"` // ISO 8601
}

// GetModels 返回所有可用模型列表，由后端决定能力和推理程度。
func (a *App) GetModels() []llm.AvailableModel {
	if a.llmClient == nil {
		return nil
	}
	return llm.Models(a.llmClient.Providers())
}

// GetSessionsInput 是 GetSessions 的入参。
type GetSessionsInput struct {
	NovelID int64  `json:"novel_id"`
	Page    int    `json:"page"`
	Size    int    `json:"size"`
	Search  string `json:"search"`
}

// GetSessions 分页查询当前小说的对话历史。search 非空时搜索消息内容。
func (a *App) GetSessions(input GetSessionsInput) (*storage.PageResult[SessionMeta], error) {
	if a.session == nil {
		return nil, nil
	}
	result, err := a.session.ListSessions(a.ctx, input.NovelID, session.ListSessionsOptions{
		PageParams: storage.PageParams{Page: input.Page, Size: input.Size},
		Search:     input.Search,
	})
	if err != nil {
		a.logger.Error("failed to list sessions", "novel_id", input.NovelID, "search", input.Search, "err", err)
		return nil, fmt.Errorf("app: list sessions: %w", err)
	}

	metas := make([]SessionMeta, 0, len(result.Items))
	for _, s := range result.Items {
		metas = append(metas, SessionMeta{
			SessionID: s.SessionID,
			Title:     s.Title,
			Model:     s.Model,
			UpdatedAt: s.UpdatedAt.Format("2006-01-02T15:04:05"),
		})
	}
	return storage.NewPageResult(metas, result.Total, input.Page, input.Size), nil
}

// GetSessionMessages 加载指定 session 的全部前端可见消息。
func (a *App) GetSessionMessages(sessionID string) ([]session.Message, error) {
	if a.session == nil {
		return nil, nil
	}
	msgs, err := a.session.GetMessagesForFrontend(a.ctx, sessionID)
	if err != nil {
		a.logger.Error("failed to get messages", "session_id", sessionID, "err", err)
		return nil, fmt.Errorf("app: get messages: %w", err)
	}
	if msgs == nil {
		return []session.Message{}, nil
	}
	return msgs, nil
}

// GetLLMConfig 返回合并内置模板和用户配置的完整视图。
func (a *App) GetLLMConfig() (*llm.LLMConfigView, error) {
	user, err := llm.LoadUserConfig(config.LLMConfigPath())
	if err != nil {
		a.logger.Error("failed to load LLM config", "err", err)
		return nil, fmt.Errorf("app: load llm config: %w", err)
	}
	return llm.BuildConfigView(user), nil
}

// SaveLLMConfig 保存用户 LLM 配置并热重载 Client。
func (a *App) SaveLLMConfig(input llm.LLMConfigView) error {
	cfg := input.ToUserConfig()
	if err := llm.SaveUserConfig(config.LLMConfigPath(), cfg); err != nil {
		a.logger.Error("failed to save LLM config", "err", err)
		return fmt.Errorf("app: save llm config: %w", err)
	}

	if a.llmClient != nil {
		providers := llm.Merge(llm.Builtin, cfg)
		a.llmClient.Reload(providers)
	}

	a.logger.Info("LLM config saved and hot-reloaded")
	return nil
}
