package app

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"runtime"

	"gorm.io/gorm"

	"novel/internal/agent"
	"novel/internal/approval"
	"novel/internal/chapter"
	"novel/internal/character"
	"novel/internal/config"
	"novel/internal/llm"
	"novel/internal/location"
	"novel/internal/mcp_tools"
	"novel/internal/migrate"
	"novel/internal/novel"
	"novel/internal/rag"
	"novel/internal/reader"
	"novel/internal/session"
	"novel/internal/storage"
	"novel/internal/storyarc"
	"novel/internal/timeline"
)

// App 是 Wails 绑定的根对象。前端通过 window.go.main.App 调用其导出方法。
// 各领域方法按文件拆分（novel.go / chapter.go 等），均接收 *App。
type App struct {
	ctx    context.Context
	logger *slog.Logger

	cfg      *config.AppConfig
	settings *config.AppSettings
	db       *gorm.DB

	llmClient *llm.Client
	agent     *agent.Agent
	registry  *mcp_tools.Registry
	approvals *approval.Service

	novel     *novel.Store
	chapter   *chapter.Store
	character *character.Store
	session   *session.Store
	timeline  *timeline.Store
	storyarc  *storyarc.Store
	location  *location.Store
	reader    *reader.Store
}

// New 创建 App 实例。初始化在 OnStartup 中完成。
func New(logger *slog.Logger) *App {
	return &App{logger: logger}
}

// ── 生命周期 ──────────────────────────────────────────────

// OnStartup 在 Wails 窗口创建后调用，完成基础设施初始化。
func (a *App) OnStartup(ctx context.Context) {
	a.ctx = ctx

	cfg, err := config.Load()
	if err != nil {
		a.logger.Warn("应用未初始化，等待用户配置", "err", err)
		return
	}
	a.initWithConfig(cfg)
}

// OnShutdown 在 Wails 窗口关闭前调用，释放资源。
func (a *App) OnShutdown(_ context.Context) {
	if a.db != nil {
		a.logger.Info("应用关闭，释放资源")
		storage.Close(a.db)
	}
}

// IsInitialized 返回指针文件是否已加载成功。前端据此决定显示初始化界面还是主界面。
func (a *App) IsInitialized() bool {
	return a.cfg != nil
}

// Initialize 在用户选择数据目录后完成首次初始化。
func (a *App) Initialize(dataDir string) error {
	if err := config.Save(dataDir); err != nil {
		return fmt.Errorf("保存配置失败: %w", err)
	}

	cfg, err := config.Load()
	if err != nil {
		return fmt.Errorf("加载配置失败: %w", err)
	}

	a.initWithConfig(cfg)
	return nil
}

// initWithConfig 在配置加载成功后初始化所有运行时模块。
func (a *App) initWithConfig(cfg *config.AppConfig) {
	a.cfg = cfg
	config.Set(cfg)

	// 1. 异步加载 ONNX 模型（不阻塞 GUI，尽早调用）
	rag.InitEmbedder(cfg.ModelsDir(), a.logger)

	// 2. 打开全局数据库
	db, err := storage.Open(cfg.GlobalDBPath(), a.logger)
	if err != nil {
		a.logger.Error("打开数据库失败", "err", err)
		return
	}
	a.db = db

	// 3. 自动建表
	if err := migrate.Run(db, a.logger); err != nil {
		a.logger.Error("数据库迁移失败", "err", err)
		return
	}

	// 4. 加载运行时配置
	settings, err := config.LoadSettings(db)
	if err != nil {
		a.logger.Error("加载设置失败", "err", err)
		return
	}
	a.settings = settings

	// 5. 创建所有领域 store
	a.novel = novel.NewStore(db, a.logger)
	a.chapter = chapter.NewStore(db, a.logger)
	a.character = character.NewStore(db, a.logger)
	a.session = session.NewStore(db, a.logger)
	a.timeline = timeline.NewStore(db, a.logger)
	a.storyarc = storyarc.NewStore(db, a.logger)
	a.location = location.NewStore(db, a.logger)
	a.reader = reader.NewStore(db, a.logger)

	// 6. 初始化 MCP 工具注册表
	a.registry = mcp_tools.NewRegistry(a.logger)
	mcp_tools.RegisterAllTools(a.registry)

	// 7. 初始化 LLM 客户端
	userConfig, err := llm.LoadUserConfig(config.LLMConfigPath())
	if err != nil {
		a.logger.Warn("加载 LLM 配置失败，使用空配置", "err", err)
		userConfig = &llm.UserLLMConfig{}
	}
	providers := llm.Merge(llm.Builtin, userConfig)
	a.llmClient = llm.NewClient(providers, a.logger)

	// 8. 创建 Agent 实例（全局复用）
	a.agent = agent.New(a.llmClient, a.registry, a.db, a.approvals, a.logger)

	// 9. 初始化审批服务
	a.approvals = approval.NewService(a.logger)

	a.logger.Info("应用初始化完成", "data_dir", cfg.DataDir)
}

// GetPlatform 返回平台信息，供前端决定默认路径等行为。
func (a *App) GetPlatform() map[string]any {
	info := map[string]any{
		"os":          runtime.GOOS,
		"defaultPath": defaultDataDir(),
	}
	return info
}

// defaultDataDir 根据平台返回默认数据目录。
// Windows: 检测 D/E 盘是否存在，否则回退到用户目录。
// 其他平台: ~/.goink。
func defaultDataDir() string {
	if runtime.GOOS == "windows" {
		for _, drive := range []string{"D:", "E:", "C:"} {
			if _, err := os.Stat(drive + "\\"); err == nil {
				return filepath.Join(drive, "Goink")
			}
		}
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".goink")
}

// GetAppConfig 返回当前运行时配置信息（供前端诊断）。
func (a *App) GetAppConfig() map[string]any {
	if a.cfg == nil {
		return map[string]any{"initialized": false}
	}
	return map[string]any{
		"initialized": true,
		"data_dir":    a.cfg.DataDir,
	}
}
