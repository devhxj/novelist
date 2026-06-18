package approval

import (
	"context"
	"fmt"
	"log/slog"
	"sync"
)

// Approval 是用户对一次审批请求的响应。
type Approval struct {
	Approved bool
	Feedback string
}

// Approver 是工具端看到的审批能力接口。调用方只需知道能发起审批请求，
// 不需要知道模式切换、前端信令等实现细节。
type Approver interface {
	RequestApproval(ctx context.Context, toolID string, payload any) (Approval, error)
}

// Service 管理审批流程，实现 Approver 接口。
// 零超时，goroutine 睡在 channel 上等待前端响应。
// mode 为 "auto" 时自动批准所有请求，无需前端交互。
type Service struct {
	mu     sync.Mutex
	pend   map[string]chan Approval
	mode   string // "manual" | "auto"
	logger *slog.Logger
}

// NewService 创建审批服务，默认手动模式。initialMode 为空时默认 "manual"。
func NewService(logger *slog.Logger, initialMode string) *Service {
	if initialMode == "" {
		initialMode = "manual"
	}
	return &Service{
		pend:   make(map[string]chan Approval),
		mode:   initialMode,
		logger: logger,
	}
}

// SetMode 切换审批模式。"auto" 自动批准，"manual" 等待前端。
func (s *Service) SetMode(mode string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.mode = mode
	s.logger.Info("审批模式切换", "mode", mode)
}

// RequestApproval 工具端调用，阻塞等待用户审批或 ctx 取消。
// mode 为 "auto" 时直接返回批准。
func (s *Service) RequestApproval(ctx context.Context, toolID string, payload any) (Approval, error) {
	s.mu.Lock()
	if s.mode == "auto" {
		s.mu.Unlock()
		return Approval{Approved: true, Feedback: "auto"}, nil
	}

	ch := make(chan Approval, 1)
	s.pend[toolID] = ch
	s.mu.Unlock()

	// defer 确保无论怎么退出都清理
	defer func() {
		s.mu.Lock()
		delete(s.pend, toolID)
		s.mu.Unlock()
	}()

	select {
	case approval := <-ch:
		return approval, nil
	case <-ctx.Done():
		return Approval{}, ctx.Err()
	}
}

// Complete 前端调用，向等待中的工具发送审批结果。
func (s *Service) Complete(toolID string, approved bool, feedback string) error {
	s.mu.Lock()
	ch, ok := s.pend[toolID]
	delete(s.pend, toolID)
	s.mu.Unlock()

	if !ok {
		return fmt.Errorf("审批请求不存在: %s", toolID)
	}
	ch <- Approval{Approved: approved, Feedback: feedback}
	return nil
}
