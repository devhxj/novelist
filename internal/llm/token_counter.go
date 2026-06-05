package llm

import (
	"embed"
	"encoding/base64"
	"encoding/json"
	"io"
	"strconv"
	"strings"
	"sync"

	"github.com/pkoukk/tiktoken-go"
)

//go:embed o200k_base.tiktoken
var bpeData embed.FS

func init() {
	tiktoken.SetBpeLoader(&embedLoader{})
}

// embedLoader 从 embed.FS 加载 BPE 文件，零网络依赖。
type embedLoader struct{}

func (l *embedLoader) LoadTiktokenBpe(tiktokenBpeFile string) (map[string]int, error) {
	f, err := bpeData.Open("o200k_base.tiktoken")
	if err != nil {
		return nil, err
	}
	defer f.Close()

	contents, err := io.ReadAll(f)
	if err != nil {
		return nil, err
	}

	bpeRanks := make(map[string]int)
	for _, line := range strings.Split(string(contents), "\n") {
		line = strings.TrimRight(line, "\r")
		if line == "" {
			continue
		}
		parts := strings.Split(line, " ")
		token, err := base64.StdEncoding.DecodeString(parts[0])
		if err != nil {
			return nil, err
		}
		rank, err := strconv.Atoi(parts[1])
		if err != nil {
			return nil, err
		}
		bpeRanks[string(token)] = rank
	}
	return bpeRanks, nil
}

var (
	tokenEncOnce sync.Once
	tokenEnc     *tiktoken.Tiktoken
	tokenEncErr  error
)

// getTokenEnc 返回 o200k_base 编码器，全局复用。
func getTokenEnc() (*tiktoken.Tiktoken, error) {
	tokenEncOnce.Do(func() {
		tokenEnc, tokenEncErr = tiktoken.GetEncoding("o200k_base")
	})
	return tokenEnc, tokenEncErr
}

// CountTokens 计算一段文本的 token 数。
func CountTokens(text string) (int, error) {
	enc, err := getTokenEnc()
	if err != nil {
		return 0, err
	}
	if text == "" {
		return 0, nil
	}
	return len(enc.Encode(text, nil, nil)), nil
}

// CountMessageTokens 计算一条 OpenAI 格式消息的 token 数。
// 与 Python _count_msg_tokens 一致：只算 content + tool_calls + tool_call_id + reasoning_content。
func CountMessageTokens(msg map[string]any) (int, error) {
	enc, err := getTokenEnc()
	if err != nil {
		return 0, err
	}

	n := 0
	if content, _ := msg["content"].(string); content != "" {
		n += len(enc.Encode(content, nil, nil))
	}
	if toolCalls, ok := msg["tool_calls"]; ok && toolCalls != nil {
		if b, err := json.Marshal(toolCalls); err == nil {
			n += len(enc.Encode(string(b), nil, nil))
		}
	}
	if id, _ := msg["tool_call_id"].(string); id != "" {
		n += len(enc.Encode(id, nil, nil))
	}
	if reasoning, _ := msg["reasoning_content"].(string); reasoning != "" {
		n += len(enc.Encode(reasoning, nil, nil))
	}
	return n, nil
}

// CountMessagesTokens 计算消息列表的总 token 数，并按 role 分组计数。
func CountMessagesTokens(messages []map[string]any) (total int, byRole map[string]int, err error) {
	byRole = map[string]int{}
	for _, msg := range messages {
		n, err := CountMessageTokens(msg)
		if err != nil {
			return 0, nil, err
		}
		total += n
		role, _ := msg["role"].(string)
		byRole[role] += n
	}
	return total, byRole, nil
}
