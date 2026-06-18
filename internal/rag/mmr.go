package rag

import (
	"encoding/binary"
	"math"
)

// deserializeFloat32 将 sqlite-vec 的 embedding blob 还原为 []float32。
func deserializeFloat32(blob []byte) []float32 {
	vec := make([]float32, len(blob)/4)
	for i := range vec {
		vec[i] = math.Float32frombits(binary.LittleEndian.Uint32(blob[i*4 : (i+1)*4]))
	}
	return vec
}

// cosineSimilarity 计算两个 L2 归一化向量的余弦相似度，范围 [0, 1]。
func cosineSimilarity(a, b []float32) float64 {
	var dot, normA, normB float64
	for i := range a {
		dot += float64(a[i]) * float64(b[i])
		normA += float64(a[i]) * float64(a[i])
		normB += float64(b[i]) * float64(b[i])
	}
	if normA == 0 || normB == 0 {
		return 0
	}
	return math.Max(0, dot/(math.Sqrt(normA)*math.Sqrt(normB)))
}

// MMRRerank 对检索结果进行最大边际相关性重排序。
// lambda 控制相关性（λ）和多样性（1-λ）的权重，通常取 0.7。
// 使用向量余弦相似度衡量文本间的多样性，对中文友好。
// 返回最多 k 个结果。candidates 不会被修改。
func MMRRerank(query string, candidates []SearchResult, k int, lambda float64) []SearchResult {
	if len(candidates) <= k {
		return candidates
	}

	remaining := make([]SearchResult, len(candidates))
	copy(remaining, candidates)
	selected := make([]SearchResult, 0, k)

	for len(selected) < k && len(remaining) > 0 {
		bestIdx := 0
		bestScore := -1.0

		for i, c := range remaining {
			diversity := 0.0
			for _, s := range selected {
				if len(c.Embedding) > 0 && len(s.Embedding) > 0 {
					sim := cosineSimilarity(c.Embedding, s.Embedding)
					if sim > diversity {
						diversity = sim
					}
				}
			}
			score := lambda*c.Relevance - (1-lambda)*diversity
			if score > bestScore {
				bestScore = score
				bestIdx = i
			}
		}

		selected = append(selected, remaining[bestIdx])
		remaining = append(remaining[:bestIdx], remaining[bestIdx+1:]...)
	}

	return selected
}
