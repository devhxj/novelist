package agent

import (
	"context"
	"strconv"
	"time"

	wails "github.com/wailsapp/wails/v2/pkg/runtime"

	"novel/internal/llm"
)

// initRunningTokens 对初始消息列表逐条 token 计数，返回按 role 的分组统计。
func (a *Agent) initRunningTokens(messages []map[string]any) map[string]int {
	tokens := map[string]int{"system": 0, "user": 0, "assistant": 0, "tool": 0}
	for _, m := range messages {
		role, _ := m["role"].(string)
		if _, ok := tokens[role]; ok {
			n, err := llm.CountMessageTokens(m)
			if err != nil {
				a.logger.Warn("token count failed", "role", role, "err", err)
			}
			tokens[role] += n
		}
	}
	return tokens
}

// updateUsage 计算 usage_ratio + 分角色 detail → 持久化到 session + 推送前端。
func (a *Agent) updateUsage(ctx context.Context, apiUsage map[string]any, runningTokens map[string]int, opts RunOptions) {
	localTotal := runningTokens["system"] + runningTokens["user"] + runningTokens["assistant"] + runningTokens["tool"]
	apiTotal, _ := apiUsage["total_tokens"].(float64)

	detail := make(map[string]int)
	for role, tokens := range runningTokens {
		if localTotal > 0 && int(apiTotal) > 0 {
			detail[role] = int(float64(tokens) * apiTotal / float64(localTotal))
		} else {
			detail[role] = tokens
		}
	}

	usage := map[string]any{
		"prompt_tokens":     apiUsage["prompt_tokens"],
		"completion_tokens": apiUsage["completion_tokens"],
		"total_tokens":      apiUsage["total_tokens"],
		"context_window":    opts.Model.ContextWindow,
		"detail":            detail,
	}
	if opts.Model.ContextWindow > 0 {
		usage["usage_ratio"] = float64(int(apiTotal)) / float64(opts.Model.ContextWindow) * 100
	}

	wails.EventsEmit(ctx, "agent:"+strconv.Itoa(opts.TurnID), AgentEvent{
		TurnID:    opts.TurnID,
		Type:      EventUsage,
		Usage:     usage,
		Timestamp: time.Now(),
	})
}
