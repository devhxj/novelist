package app

import (
	"context"
	"fmt"
	"strings"
	"time"

	wails "github.com/wailsapp/wails/v2/pkg/runtime"

	"novel/internal/agent"
	"novel/internal/agentcfg"
	"novel/internal/git"
	"novel/internal/rollback"
	"novel/internal/session"
)

// ChatInput 是一次对话请求的入参。
type ChatInput struct {
	SessionID       string `json:"session_id"` // 空=新建会话
	NovelID         int64  `json:"novel_id"`
	Message         string `json:"message"`
	ProviderName    string `json:"provider_name"`    // "deepseek"
	ModelID         string `json:"model_id"`         // "deepseek-v4-pro"
	ReasoningEffort string `json:"reasoning_effort"` // "high" | "max" | ""
}

// ChatResult 是一次对话请求的返回值。
type ChatResult struct {
	SessionID string `json:"session_id"`
	TurnID    int    `json:"turn_id"`
	FinalText string `json:"final_text"`
}

// Chat 是对话入口。Wails 绑定，前端调用后同步执行，期间通过 EventsEmit 推流。
func (a *App) Chat(input ChatInput) (*ChatResult, error) {
	ctx, cancel := context.WithCancel(a.ctx)
	defer cancel()

	// 1. 加载或创建 Session
	sess, isNew, err := a.loadOrCreateSession(ctx, input)
	if err != nil {
		return nil, fmt.Errorf("session 初始化失败: %w", err)
	}

	a.agent.RegisterCancel(sess.SessionID, cancel)
	defer a.agent.UnregisterCancel(sess.SessionID)

	// 2. 新会话自动生成标题（异步，与 agent LLM 调用并发）
	if isNew && sess.Title == "" {
		go a.generateTitle(sess.SessionID, input.Message, input.ProviderName, input.ModelID)
	}

	// 3. 查找模型元信息
	model, ok := a.llmClient.ProviderModel(input.ProviderName, input.ModelID)
	if !ok {
		return nil, fmt.Errorf("模型未找到: %s/%s", input.ProviderName, input.ModelID)
	}

	// 4. 获取下一个 turn ID
	turnID, err := a.session.NextTurn(ctx, sess.SessionID)
	if err != nil {
		return nil, fmt.Errorf("获取 turn ID 失败: %w", err)
	}

	// 5. 打开 git 仓库，提交用户在对话间隙的手动编辑
	repo, repoErr := git.New(input.NovelID)
	if repoErr != nil {
		a.logger.Warn("auto-commit: 打开 git 仓库失败，跳过本轮自动提交", "err", repoErr)
	} else {
		a.commitUserChanges(repo, turnID, sess.SessionID)
		defer a.commitAIChanges(repo, turnID, sess.SessionID, model.Name)
	}

	// 6. 新 session 写入 System1 + System2
	if isNew {
		if err := a.writeSystemMessages(ctx, sess.SessionID, input.NovelID, turnID); err != nil {
			return nil, fmt.Errorf("写入系统消息失败: %w", err)
		}
	}

	// 6. 持久化用户消息
	userMsg := &session.Message{
		SessionID:  sess.SessionID,
		TurnID:     turnID,
		Role:       "user",
		Content:    input.Message,
		Version:    sess.ActiveVersion,
		ToAPI:      true,
		ToFrontend: true,
		AgentType:  "main",
	}
	if err := a.session.DB.WithContext(ctx).Create(userMsg).Error; err != nil {
		return nil, fmt.Errorf("持久化用户消息失败: %w", err)
	}

	// 7. 构建消息列表：全部来自 DB（含 System1/System2/历史/用户消息）
	messages, err := a.loadAPIMessages(ctx, sess.SessionID, sess.ActiveVersion)
	if err != nil {
		return nil, fmt.Errorf("加载 API 消息失败: %w", err)
	}

	// 8. 运行 Agent 循环
	wails.EventsEmit(ctx, "chat:started", map[string]any{
		"session_id": sess.SessionID,
		"turn_id":    turnID,
	})

	result, runErr := a.agent.Run(ctx, agent.RunOptions{
		TurnID:        turnID,
		SessionID:     sess.SessionID,
		NovelID:       input.NovelID,
		Messages:      messages,
		AllowedTools:  agentcfg.Allowlist(agentcfg.MainAgent),
		ActiveVersion: sess.ActiveVersion,
		Model:         model,
		ProviderName:  input.ProviderName,
		AgentType:     "main",
		MaxTurns:      50,
	})

	// 9. 最终回复已由 agent.Run() 内部 appendMsg 持久化，此处不重复存储
	if runErr != nil {
		a.logger.Error("对话失败", "err", runErr)
		// 写入中断标记，前端 rebuildTurns 识别后将 turn 状态设为 interrupted
		a.session.DB.WithContext(ctx).Create(&session.Message{
			SessionID:  sess.SessionID,
			TurnID:     turnID,
			Role:       "system",
			EventType:  "interrupt",
			Content:    agent.FriendlyError(runErr),
			ToFrontend: true,
			ToAPI:      false,
			AgentType:  "main",
		})
		return &ChatResult{
			SessionID: sess.SessionID,
			TurnID:    turnID,
			FinalText: result.FinalText,
		}, fmt.Errorf("%s", agent.FriendlyError(runErr))
	}

	return &ChatResult{
		SessionID: sess.SessionID,
		TurnID:    turnID,
		FinalText: result.FinalText,
	}, nil
}

// loadOrCreateSession 加载已有 session 或创建新 session。
func (a *App) loadOrCreateSession(ctx context.Context, input ChatInput) (*session.Session, bool, error) {
	if input.SessionID != "" {
		var sess session.Session
		err := a.session.DB.WithContext(ctx).
			Where("session_id = ?", input.SessionID).
			First(&sess).Error
		if err == nil {
			return &sess, false, nil
		}
	}

	// 创建新 session
	sess := &session.Session{
		SessionID:       fmt.Sprintf("sess_%d_%x", input.NovelID, time.Now().UnixNano()),
		NovelID:         input.NovelID,
		Model:           input.ModelID,
		ReasoningEffort: input.ReasoningEffort,
	}
	if err := a.session.DB.WithContext(ctx).Create(sess).Error; err != nil {
		return nil, false, err
	}

	wails.EventsEmit(ctx, "chat:session_created", sess)
	return sess, true, nil
}

// writeSystemMessages 为新 session 写入 System1 和 System2 到 messages 表。
func (a *App) writeSystemMessages(ctx context.Context, sessionID string, novelID int64, turnID int) error {
	db := a.session.DB.WithContext(ctx)

	sys1 := &session.Message{
		SessionID:  sessionID,
		TurnID:     turnID,
		Role:       "system",
		Content:    agentcfg.System1(agentcfg.MainAgent),
		Version:    1,
		ToAPI:      true,
		ToFrontend: false,
		AgentType:  "main",
	}
	if err := db.Create(sys1).Error; err != nil {
		return fmt.Errorf("写入 System1 失败: %w", err)
	}

	sys2Content, err := agentcfg.System2(a.db, novelID)
	if err != nil {
		a.logger.Warn("System2 构建失败，写入空消息", "novel_id", novelID, "err", err)
		sys2Content = ""
	}
	if sys2Content != "" {
		sys2 := &session.Message{
			SessionID:  sessionID,
			TurnID:     turnID,
			Role:       "system",
			Content:    sys2Content,
			Version:    1,
			ToAPI:      true,
			ToFrontend: false,
			AgentType:  "main",
		}
		if err := db.Create(sys2).Error; err != nil {
			return fmt.Errorf("写入 System2 失败: %w", err)
		}
	}

	return nil
}

// loadAPIMessages 加载指定 version 的所有 to_api 消息，转为 map 格式。
func (a *App) loadAPIMessages(ctx context.Context, sessionID string, version int) ([]map[string]any, error) {
	msgs, err := a.session.GetMessagesForAPI(ctx, sessionID, version)
	if err != nil {
		return nil, err
	}
	result := make([]map[string]any, 0, len(msgs))
	for _, m := range msgs {
		result = append(result, m.ToAPIFormat())
	}
	return result, nil
}

// generateTitle 用 LLM 为非流式调用生成对话标题（≤10 字）。
func (a *App) generateTitle(sessionID, userMessage, providerName, modelID string) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	messages := []map[string]any{
		{
			"role":    "system",
			"content": "基于用户消息，生成一个不超过10个字的对话标题。只需输出标题文本，不要添加引号、标点或者额外解释。",
		},
		{"role": "user", "content": userMessage},
	}

	title, err := a.llmClient.GenerateText(ctx, providerName, messages, modelID)
	if err != nil {
		a.logger.Warn("自动生成标题失败", "err", err)
		return
	}

	title = strings.TrimSpace(title)
	if len([]rune(title)) > 30 {
		title = string([]rune(title)[:30])
	}
	if title == "" {
		return
	}

	if err := a.session.UpdateSessionMeta(a.ctx, sessionID, title, "", ""); err != nil {
		a.logger.Warn("更新标题失败", "err", err)
		return
	}

	wails.EventsEmit(a.ctx, "chat:title_updated", map[string]any{
		"session_id": sessionID,
		"title":      title,
	})
}

// ApproveTool 前端调用，响应审批请求。
func (a *App) ApproveTool(toolID string, approved bool, feedback string) error {
	return a.approvals.Complete(toolID, approved, feedback)
}

// SetApprovalMode 前端调用，切换审批模式。"auto" 自动批准，"manual" 等待用户操作。
func (a *App) SetApprovalMode(mode string) {
	a.approvals.SetMode(mode)
}

// CancelChat 前端调用，取消一个正在进行的对话。
func (a *App) CancelChat(sessionID string) {
	a.agent.Cancel(sessionID)
}

// CompressInput 是手动压缩请求的入参。
type CompressInput struct {
	SessionID    string `json:"session_id"`
	ProviderName string `json:"provider_name"`
	ModelID      string `json:"model_id"`
}

// CompressResult 是手动压缩请求的返回值。
type CompressResult struct {
	TurnID int `json:"turn_id"`
}

// CompressContext 手动触发上下文压缩。仅在无进行中 turn 时允许。
func (a *App) CompressContext(input CompressInput) (*CompressResult, error) {
	if a.agent.IsRunning(input.SessionID) {
		return nil, fmt.Errorf("对话进行中，无法手动压缩上下文，请等待当前对话完成")
	}

	// 1. 加载 Session
	var sess session.Session
	if err := a.session.DB.Where("session_id = ?", input.SessionID).First(&sess).Error; err != nil {
		return nil, fmt.Errorf("session 不存在: %w", err)
	}

	// 2. 查找模型
	model, ok := a.llmClient.ProviderModel(input.ProviderName, input.ModelID)
	if !ok {
		return nil, fmt.Errorf("模型未找到: %s/%s", input.ProviderName, input.ModelID)
	}

	// 3. 获取下一个 turn ID（手动压缩独立成一个 turn）
	turnID, err := a.session.NextTurn(context.Background(), sess.SessionID)
	if err != nil {
		return nil, fmt.Errorf("获取 turn ID 失败: %w", err)
	}

	// 4. 构建消息列表：全部来自 DB
	messages, err := a.loadAPIMessages(context.Background(), sess.SessionID, sess.ActiveVersion)
	if err != nil {
		return nil, fmt.Errorf("加载 API 消息失败: %w", err)
	}

	// 5. 创建上下文（含 cancel，支持打断）
	ctx, cancel := context.WithCancel(a.ctx)
	defer cancel()
	a.agent.RegisterCancel(sess.SessionID, cancel)
	defer a.agent.UnregisterCancel(sess.SessionID)

	// 6. 初始化 runningTokens
	runningTokens := a.agent.InitRunningTokens(messages)

	// 7. 执行压缩
	opts := agent.RunOptions{
		TurnID:        turnID,
		SessionID:     sess.SessionID,
		NovelID:       sess.NovelID,
		Messages:      messages,
		ActiveVersion: sess.ActiveVersion,
		Model:         model,
		ProviderName:  input.ProviderName,
		AgentType:     "main",
		MaxTurns:      50,
	}

	if err := a.agent.Compress(ctx, &opts, runningTokens); err != nil {
		return nil, err
	}
	return &CompressResult{TurnID: turnID}, nil
}

// commitUserChanges 在 turn 开始时提交用户在对话间隙对章节文件的手动修改。
// git 操作失败只记日志，不阻塞对话流程。
func (a *App) commitUserChanges(repo *git.Repo, turnID int, sessionID string) {
	has, err := repo.HasUncommitted()
	if err != nil {
		a.logger.Warn("auto-commit: 检查未提交变更失败", "err", err)
		return
	}
	if !has {
		return
	}

	msg := fmt.Sprintf("turn %d: user manual changes\n\nSession: %s", turnID, sessionID)
	a.doCommit(repo, turnID, sessionID, "user", msg)
}

// commitAIChanges 在 turn 结束时提交 AI 对章节文件的所有修改。
// 通过 defer 调用，确保正常结束、用户打断、异常退出时都会执行。
func (a *App) commitAIChanges(repo *git.Repo, turnID int, sessionID string, modelName string) {
	has, err := repo.HasUncommitted()
	if err != nil {
		a.logger.Warn("auto-commit: 检查未提交变更失败", "err", err)
		return
	}
	if !has {
		return
	}

	msg := fmt.Sprintf("turn %d: AI changes\n\nSession: %s\n\nCo-authored-by: Goink (%s)", turnID, sessionID, modelName)
	a.doCommit(repo, turnID, sessionID, "ai", msg)
}

// doCommit 执行 git add + commit，并将 hash 写入 turn_commits 表。
func (a *App) doCommit(repo *git.Repo, turnID int, sessionID, commitType, msg string) {
	if err := repo.StageAll(); err != nil {
		a.logger.Warn("auto-commit: git add 失败", "err", err)
		return
	}

	hash, err := repo.Commit(msg)
	if err != nil {
		a.logger.Warn("auto-commit: git commit 失败", "err", err)
		return
	}

	tc := &rollback.TurnCommit{
		SessionID:  sessionID,
		TurnID:     turnID,
		CommitType: commitType,
		CommitHash: hash,
	}
	if err := a.db.Create(tc).Error; err != nil {
		a.logger.Warn("auto-commit: 写入 turn_commits 失败", "err", err)
		return
	}

	a.logger.Info("auto-commit: 提交成功", "turn", turnID, "type", commitType, "hash", hash[:7])
}
