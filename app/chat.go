package app

import (
	"context"
	"fmt"
	"strings"
	"time"

	wails "github.com/wailsapp/wails/v2/pkg/runtime"

	"novel/internal/agent"
	"novel/internal/agentcfg"
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

	// 5. 持久化用户消息
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

	// 6. 构建消息列表：system1 + system2 + 历史 + 用户消息
	messages := []map[string]any{
		{"role": "system", "content": agentcfg.System1(agentcfg.MainAgent)},
	}

	sys2, err := agentcfg.System2(a.db, input.NovelID)
	if err != nil {
		a.logger.Warn("System2 构建失败", "novel_id", input.NovelID, "err", err)
	}
	if sys2 != "" {
		messages = append(messages, map[string]any{"role": "system", "content": sys2})
	}

	historyMsgs, err := a.session.GetMessagesForAPI(ctx, sess.SessionID, sess.ActiveVersion)
	if err != nil {
		a.logger.Warn("获取历史消息失败", "session_id", sess.SessionID, "err", err)
	}
	for _, m := range historyMsgs {
		messages = append(messages, m.ToAPIFormat())
	}

	// 7. 运行 Agent 循环
	wails.EventsEmit(ctx, "chat:started", map[string]any{
		"session_id": sess.SessionID,
		"turn_id":    turnID,
	})

	result, runErr := a.agent.Run(ctx, agent.RunOptions{
		TurnID:           turnID,
		SessionID:        sess.SessionID,
		NovelID:          input.NovelID,
		Messages:         messages,
		AllowedTools:     agentcfg.Allowlist(agentcfg.MainAgent),
		ActiveVersion:    sess.ActiveVersion,
		Model:            model,
		ProviderName:     input.ProviderName,
		AgentType:        "main",
		MaxTurns:         50,
		MaxContextTokens: 800000,
	})

	// 8. 持久化最终回复（成功路径；错误/取消路径由 agent 自行持久化 partial）
	if runErr == nil && result.FinalText != "" {
		finalMsg := &session.Message{
			SessionID:       sess.SessionID,
			TurnID:          turnID,
			Role:            "assistant",
			Content:         result.FinalText,
			ThinkingContent: result.ThinkingContent,
			Version:         sess.ActiveVersion,
			ToAPI:           true,
			ToFrontend:      true,
			AgentType:       "main",
		}
		if err := a.session.DB.WithContext(ctx).Create(finalMsg).Error; err != nil {
			a.logger.Warn("持久化最终回复失败", "err", err)
		}
	}

	if runErr != nil {
		a.logger.Error("对话失败", "err", runErr)
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
