package rag

import (
	"math"
	"testing"
)

func TestDeserializeFloat32_Roundtrip(t *testing.T) {
	original := []float32{0.1, -0.5, 1.0, 0.0, 0.333}
	blob := make([]byte, len(original)*4)
	for i, v := range original {
		bits := math.Float32bits(v)
		blob[i*4] = byte(bits)
		blob[i*4+1] = byte(bits >> 8)
		blob[i*4+2] = byte(bits >> 16)
		blob[i*4+3] = byte(bits >> 24)
	}

	result := deserializeFloat32(blob)
	if len(result) != len(original) {
		t.Fatalf("len mismatch: got %d, want %d", len(result), len(original))
	}
	for i := range original {
		if math.Abs(float64(result[i]-original[i])) > 1e-7 {
			t.Errorf("mismatch at %d: got %f, want %f", i, result[i], original[i])
		}
	}
}

func TestDeserializeFloat32_Empty(t *testing.T) {
	result := deserializeFloat32([]byte{})
	if len(result) != 0 {
		t.Errorf("expected empty, got %d", len(result))
	}
}

func TestCosineSimilarity_Identical(t *testing.T) {
	v := []float32{0.5, 0.5, 0.5, 0.5}
	sim := cosineSimilarity(v, v)
	if math.Abs(sim-1.0) > 1e-6 {
		t.Errorf("identical vectors should have similarity 1.0, got %f", sim)
	}
}

func TestCosineSimilarity_Orthogonal(t *testing.T) {
	a := []float32{1.0, 0.0}
	b := []float32{0.0, 1.0}
	sim := cosineSimilarity(a, b)
	if math.Abs(sim-0.0) > 1e-6 {
		t.Errorf("orthogonal vectors should have similarity 0.0, got %f", sim)
	}
}

func TestCosineSimilarity_Opposite(t *testing.T) {
	a := []float32{1.0, 0.0}
	b := []float32{-1.0, 0.0}
	sim := cosineSimilarity(a, b)
	// L2 归一化后余弦相似度为 -1，但我们 clamp 到 0
	if sim < 0 {
		t.Errorf("should clamp negative to 0, got %f", sim)
	}
}

func TestCosineSimilarity_ZeroVector(t *testing.T) {
	a := []float32{0.0, 0.0}
	b := []float32{1.0, 0.0}
	sim := cosineSimilarity(a, b)
	if sim != 0 {
		t.Errorf("zero vector should return 0, got %f", sim)
	}
}

func TestMMRRerank_FewerThanK(t *testing.T) {
	candidates := []SearchResult{
		{ChunkID: "1", Relevance: 0.9},
	}
	result := MMRRerank("测试", candidates, 5, 0.7)
	if len(result) != 1 {
		t.Errorf("should return all when fewer than k, got %d", len(result))
	}
}

func TestMMRRerank_Empty(t *testing.T) {
	result := MMRRerank("测试", nil, 3, 0.7)
	if len(result) != 0 {
		t.Errorf("expected empty, got %d", len(result))
	}
}

func TestMMRRerank_Diversity(t *testing.T) {
	// 构造两个几乎相同嵌入和一个不同嵌入的结果。
	// MMR 应优先选高相关性+多样化的组合。
	candidates := []SearchResult{
		{
			ChunkID:   "a",
			Content:   "主角走进了房间",
			Relevance: 0.95,
			Embedding: []float32{1.0, 0.0, 0.0},
		},
		{
			ChunkID:   "b",
			Content:   "主角走进了房屋",
			Relevance: 0.90,
			Embedding: []float32{0.99, 0.01, 0.0}, // 与 a 高度相似
		},
		{
			ChunkID:   "c",
			Content:   "敌人埋伏在远处",
			Relevance: 0.80,
			Embedding: []float32{0.0, 1.0, 0.0}, // 与 a 正交
		},
	}

	result := MMRRerank("查询", candidates, 2, 0.7)

	if result[0].ChunkID != "a" {
		t.Errorf("第一个应选最高相关性的 a，got %s", result[0].ChunkID)
	}
	// 在 a 已选的情况下，b 与 a 高度相似应被惩罚，c 应被选中
	if result[1].ChunkID != "c" {
		t.Errorf("第二个应选多样化的 c 而非相似的 b，got %s", result[1].ChunkID)
	}
}

func TestMMRRerank_LambdaHigh(t *testing.T) {
	// λ=0.99 时几乎只考虑相关性
	candidates := []SearchResult{
		{ChunkID: "a", Relevance: 0.95, Embedding: []float32{1.0, 0.0}},
		{ChunkID: "b", Relevance: 0.94, Embedding: []float32{1.0, 0.0}}, // 与 a 完全相同
		{ChunkID: "c", Relevance: 0.50, Embedding: []float32{0.0, 1.0}},
	}

	result := MMRRerank("查询", candidates, 2, 0.99)
	// 相关性主导，b 即使与 a 完全相同也会因为高相关性被选中
	if result[0].ChunkID != "a" {
		t.Errorf("first should be a, got %s", result[0].ChunkID)
	}
	if result[1].ChunkID != "b" {
		t.Errorf("second should be b (relevance dominates at λ=0.99), got %s", result[1].ChunkID)
	}
}

func TestMMRRerank_LambdaLow(t *testing.T) {
	// λ=0.01 时几乎只考虑多样性
	candidates := []SearchResult{
		{ChunkID: "a", Relevance: 0.95, Embedding: []float32{1.0, 0.0}},
		{ChunkID: "b", Relevance: 0.90, Embedding: []float32{1.0, 0.0}}, // 与 a 完全相同
		{ChunkID: "c", Relevance: 0.30, Embedding: []float32{0.0, 1.0}}, // 与 a 正交
	}

	result := MMRRerank("查询", candidates, 2, 0.01)
	// 多样性主导，b 与 a 完全相同会被严重惩罚
	if result[0].ChunkID != "a" {
		t.Errorf("first should be a, got %s", result[0].ChunkID)
	}
	if result[1].ChunkID != "c" {
		t.Errorf("second should be c (diversity dominates at λ=0.01), got %s", result[1].ChunkID)
	}
}

func TestMMRRerank_NoEmbedding(t *testing.T) {
	// 没有 Embedding 时退化：多样性恒为 0，只按相关性排序
	candidates := []SearchResult{
		{ChunkID: "x", Relevance: 0.3},
		{ChunkID: "y", Relevance: 0.9},
		{ChunkID: "z", Relevance: 0.6},
	}

	result := MMRRerank("查询", candidates, 2, 0.7)
	if len(result) != 2 {
		t.Fatalf("expected 2, got %d", len(result))
	}
	if result[0].ChunkID != "y" {
		t.Errorf("expected y (highest relevance), got %s", result[0].ChunkID)
	}
	if result[1].ChunkID != "z" {
		t.Errorf("expected z (second highest), got %s", result[1].ChunkID)
	}
}
