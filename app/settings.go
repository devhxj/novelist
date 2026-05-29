package app

import (
	"novel/internal/config"
)

// SaveSettingsInput 是保存设置的入参。
type SaveSettingsInput struct {
	APIKey       string `json:"api_key"`
	DefaultModel string `json:"default_model,omitempty"`
}

// ── 设置 ──────────────────────────────────────────────────

// GetSettings 返回运行时配置。
func (a *App) GetSettings() (*config.AppSettings, error) {
	return a.settings, nil
}

// SaveSettings 保存运行时配置。
func (a *App) SaveSettings(input SaveSettingsInput) error {
	a.settings.APIKey = input.APIKey
	a.settings.DefaultModel = input.DefaultModel
	return config.SaveSettings(a.db, a.settings)
}
