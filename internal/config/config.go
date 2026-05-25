package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
)

// ErrNotInitialized 表示指针文件不存在，应用尚未完成首次初始化。没初始化弹出来初始化界面，如果初始化了但是还是出错就谈配置错误恢复
var ErrNotInitialized = errors.New("指针文件不存在，应用未初始化")

// AppConfig 是启动指针文件 ~/.novel_agent/config.json 的内容。
// 仅记录用户选择的数据目录，其他运行时配置走 SQLite app_config 表。
type AppConfig struct {
	DataDir string `json:"data_dir"` // 用户选择的数据根目录
}

// DataDir 返回数据根目录（带尾部路径分隔符）。
func (c *AppConfig) DataDirPath() string { return c.DataDir }

// GlobalDBPath 返回全局数据库路径。
func (c *AppConfig) GlobalDBPath() string {
	return filepath.Join(c.DataDir, "novel-agent.db")
}

// NovelDirPath 返回指定小说的 Git 仓库根目录。
func (c *AppConfig) NovelDirPath(novelID int64) string {
	return filepath.Join(c.DataDir, "novels", fmt.Sprintf("%d", novelID))
}

// ModelsDir 返回 ONNX 模型目录路径。
func (c *AppConfig) ModelsDir() string {
	return filepath.Join(c.DataDir, "models")
}

// configDir 返回指针文件所在的目录 ~/.novel_agent。
func configDir() (string, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return "", fmt.Errorf("获取用户目录失败: %w", err)
	}
	return filepath.Join(home, ".novel_agent"), nil
}

// configPath 返回指针文件的完整路径。
func configPath() (string, error) {
	dir, err := configDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "config.json"), nil
}

// Load 读取启动指针文件，返回 AppConfig。
// 文件不存在时返回错误，调用方应引导用户完成初始化。
func Load() (*AppConfig, error) {
	path, err := configPath()
	if err != nil {
		return nil, err
	}

	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, fmt.Errorf("%w: %s", ErrNotInitialized, path)
		}
		return nil, fmt.Errorf("读取配置文件失败: %w", err)
	}

	cfg := &AppConfig{}
	if err := json.Unmarshal(data, cfg); err != nil {
		return nil, fmt.Errorf("解析配置文件失败: %w", err)
	}
	if cfg.DataDir == "" {
		return nil, fmt.Errorf("配置文件中 data_dir 为空")
	}

	// 确保数据目录存在（用户配置的路径，理应有权创建）
	if err := os.MkdirAll(cfg.DataDir, 0700); err != nil {
		return nil, fmt.Errorf("创建数据目录 %s 失败: %w", cfg.DataDir, err)
	}
	return cfg, nil
}

// Save 将数据目录路径写入指针文件。
// 如果 ~/.novel_agent/ 目录不存在则自动创建。
func Save(dataDir string) error {
	dir, err := configDir()
	if err != nil {
		return err
	}
	if err := os.MkdirAll(dir, 0700); err != nil {
		return fmt.Errorf("创建配置目录失败: %w", err)
	}

	path := filepath.Join(dir, "config.json")
	cfg := AppConfig{DataDir: dataDir}
	raw, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return fmt.Errorf("序列化配置失败: %w", err)
	}
	if err := os.WriteFile(path, raw, 0600); err != nil {
		return fmt.Errorf("写入配置文件失败: %w", err)
	}
	return nil
}
