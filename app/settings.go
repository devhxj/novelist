package app

import (
	"novel/internal/config"
)

// SaveSettingsInput 是保存设置的入参。
type SaveSettingsInput struct {
	// 后续加 LLM 配置字段（provider、模型选择、APIKey 等）
}

// ── 设置 ──────────────────────────────────────────────────

// GetSettings 返回运行时配置。
func (a *App) GetSettings() (*config.AppSettings, error) {
	return a.settings, nil
}

// SaveSettings 保存运行时配置。
func (a *App) SaveSettings(input SaveSettingsInput) error {
	return config.SaveSettings(a.db, a.settings)
}

// SetSelectedModel 保存选中的模型 key 和推理程度，持久化到 DB。
func (a *App) SetSelectedModel(key, effort string) error {
	a.settings.SelectedModelKey = key
	a.settings.ReasoningEffort = effort
	return config.SaveSettings(a.db, a.settings)
}

// SetReasoningEffort 单独保存推理程度。
func (a *App) SetReasoningEffort(effort string) error {
	a.settings.ReasoningEffort = effort
	return config.SaveSettings(a.db, a.settings)
}

// SetChatPanelWidth 保存聊天面板宽度。
func (a *App) SetChatPanelWidth(width int) error {
	a.settings.ChatPanelWidth = width
	return config.SaveSettings(a.db, a.settings)
}

// SetLastSession 保存上次活跃的会话 ID。
func (a *App) SetLastSession(sessionID string) error {
	a.settings.LastSessionID = sessionID
	return config.SaveSettings(a.db, a.settings)
}

// SetEditorTabs 保存编辑器标签页元数据（per-novel JSON）。
func (a *App) SetEditorTabs(data string) error {
	a.settings.EditorTabs = data
	return config.SaveSettings(a.db, a.settings)
}
