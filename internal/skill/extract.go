package skill

import (
	"context"
	"fmt"
	"strings"

	"novel/internal/llm"
)

// Extract 分析样本文字的写作风格，生成仿写 skill。
// llmClient 用于独立 LLM 调用（不走 agent 循环），支持 thinking。
func Extract(ctx context.Context, llmClient *llm.Client, sample, providerName, modelID, reasoningEffort string) (*Skill, error) {
	opts := &llm.CallOptions{}
	if reasoningEffort != "" {
		opts.ReasoningEffort = &reasoningEffort
	}

	events := llmClient.ChatStream(ctx, providerName, buildExtractMessages(sample), nil, modelID, opts)
	var fullText strings.Builder
	for evt := range events {
		if evt.Type == llm.EventError {
			return nil, fmt.Errorf("LLM 调用失败: %w", evt.Error)
		}
		if evt.Type == llm.EventContent {
			fullText.WriteString(evt.Data)
		}
	}

	raw := fullText.String()
	if raw == "" {
		return nil, fmt.Errorf("LLM 返回为空")
	}

	sk, err := ParseBytes([]byte(raw), "ai")
	if err != nil {
		return nil, fmt.Errorf("解析 skill 格式失败: %w", err)
	}

	return sk, nil
}

func buildExtractMessages(sample string) []map[string]any {
	return []map[string]any{
		{"role": "system", "content": extractSystemPrompt},
		{"role": "user", "content": fmt.Sprintf("请分析以下文本的写作风格：\n\n```\n%s\n```", sample)},
	}
}

const extractSystemPrompt = `你是一位专业的写作风格分析师。请分析用户提供的文本，从以下六个维度拆解其写作风格：

1. **句式特征**：句子长度分布、长短句搭配模式、句式变化程度
2. **用词习惯**：词汇量级、口语/书面语倾向、高频词类型、成语俗语使用
3. **修辞手法**：常用修辞（比喻、拟人、排比、反复、对比等）及其使用频率
4. **节奏控制**：段落组织方式、标点使用偏好、断句节奏
5. **叙事视角与距离**：人称选择、叙事者与内容的距离感
6. **氛围与语调**：情绪基调、幽默/严肃/温暖/冷峻、语言温度

请根据分析结果为这个风格起一个贴切的中文名称，并严格按以下 Markdown 格式输出（YAML frontmatter 必须包含在开头的 --- 和结尾的 --- 之间）：

---
name: {风格名称}
description: {一句话简要描述该风格，描述何时使用}
category: 风格仿写
mode: auto
author: ai
version: 1
---

# {风格名称}

## 风格概述
简要概括该风格的整体特点。

## 句式特征
详细分析句式特点、长短句搭配等。

## 用词习惯
详细分析用词偏好、词汇选择倾向等。

## 修辞手法
详细分析使用的修辞手法及其特点。

## 节奏控制
详细分析段落组织、断句节奏等。

## 叙事视角与距离
详细分析叙事者位置、与内容的距离感。

## 氛围与语调
详细分析情绪基调、语言温度等。

## 仿写要点
提炼 3-5 条可操作的仿写指导原则。

注意：你提取的是写作风格的模式和规律，而非原文的具体内容。分析时请归纳句式结构、用词偏好、修辞模式等抽象特征，不要直接照搬原文的句子和表达。`
