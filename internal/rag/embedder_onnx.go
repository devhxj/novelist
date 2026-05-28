//go:build cgo

package rag

import (
	"context"
	"fmt"
	"log/slog"
	"path/filepath"
	"sync"

	ort "github.com/yalue/onnxruntime_go"
)

var (
	instance  *OnnxEmbedder
	tokenizer *Tokenizer
	initOnce  sync.Once
	ready     = make(chan struct{})
	initErr   error
)

// OnnxEmbedder 使用 ONNX Runtime 加载 text2vec-base-chinese 模型生成 embedding。
// 全局仅一个实例，通过 InitEmbedder 异步初始化。
// Run 不是线程安全的，Embed/EmbedBatch 内部加锁。
type OnnxEmbedder struct {
	session   *ort.DynamicAdvancedSession
	tokenizer *Tokenizer
	mu        sync.Mutex
	log       *slog.Logger
}

// InitEmbedder 加载 tokenizer（同步）后异步加载 ONNX 模型，不阻塞调用方。
// main 启动时尽早调用，模型在后台加载，GUI 可先行渲染。
// GetTokenizer() 调用后立即可用，无需等待模型就绪。
func InitEmbedder(modelsDir string, log *slog.Logger) {
	initOnce.Do(func() {
		t, err := NewTokenizer(filepath.Join(modelsDir, "vocab.txt"))
		if err != nil {
			initErr = err
			close(ready)
			return
		}
		tokenizer = t

		go func() {
			defer close(ready)
			e, err := newOnnxEmbedder(modelsDir, t, log)
			if err != nil {
				initErr = err
				return
			}
			instance = e
		}()
	})
}

// GetEmbedder 返回全局 embedder 实例。若尚未加载完成则阻塞等待。
func GetEmbedder() (*OnnxEmbedder, error) {
	<-ready
	if initErr != nil {
		return nil, initErr
	}
	return instance, nil
}

// GetTokenizer 返回全局 BERT tokenizer 实例。InitEmbedder 调用后立即可用。
func GetTokenizer() *Tokenizer {
	return tokenizer
}

// newOnnxEmbedder 同步初始化 ONNX Runtime 并加载模型。仅由 InitEmbedder 调用。
func newOnnxEmbedder(modelsDir string, t *Tokenizer, log *slog.Logger) (*OnnxEmbedder, error) {
	modelPath := filepath.Join(modelsDir, "model.onnx")

	if err := ort.InitializeEnvironment(); err != nil {
		return nil, fmt.Errorf("rag: init onnx environment: %w", err)
	}

	session, err := ort.NewDynamicAdvancedSession(modelPath,
		[]string{"input_ids", "attention_mask", "token_type_ids"},
		[]string{"last_hidden_state"}, nil)
	if err != nil {
		return nil, fmt.Errorf("rag: load model: %w", err)
	}

	log.Info("ONNX embedder 已初始化", "model", modelPath)
	return &OnnxEmbedder{session: session, tokenizer: t, log: log}, nil
}

func (e *OnnxEmbedder) Dim() int { return 768 }

func (e *OnnxEmbedder) Embed(ctx context.Context, text string) ([]float32, error) {
	ids := e.tokenizer.Tokenize(text)
	if len(ids) > 512 {
		ids = ids[:512]
	}
	if len(ids) == 0 {
		return make([]float32, 768), nil
	}

	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	default:
	}

	seqLen := int64(len(ids))
	inputIDs := make([]int64, seqLen)
	attentionMask := make([]int64, seqLen)
	tokenTypeIDs := make([]int64, seqLen)
	for i, id := range ids {
		inputIDs[i] = int64(id)
		attentionMask[i] = 1
	}

	inputTensor, err := ort.NewTensor(ort.NewShape(1, seqLen), inputIDs)
	if err != nil {
		return nil, fmt.Errorf("rag: create input tensor: %w", err)
	}
	defer inputTensor.Destroy()

	maskTensor, err := ort.NewTensor(ort.NewShape(1, seqLen), attentionMask)
	if err != nil {
		return nil, fmt.Errorf("rag: create mask tensor: %w", err)
	}
	defer maskTensor.Destroy()

	typeIDsTensor, err := ort.NewTensor(ort.NewShape(1, seqLen), tokenTypeIDs)
	if err != nil {
		return nil, fmt.Errorf("rag: create type_ids tensor: %w", err)
	}
	defer typeIDsTensor.Destroy()

	outputTensor, err := ort.NewEmptyTensor[float32](ort.NewShape(1, seqLen, 768))
	if err != nil {
		return nil, fmt.Errorf("rag: create output tensor: %w", err)
	}
	defer outputTensor.Destroy()

	e.mu.Lock()
	defer e.mu.Unlock()
	err = e.session.Run(
		[]ort.Value{inputTensor, maskTensor, typeIDsTensor},
		[]ort.Value{outputTensor},
	)

	if err != nil {
		return nil, fmt.Errorf("rag: onnx run: %w", err)
	}

	hidden := outputTensor.GetData()
	return meanPool(hidden, int(seqLen), 768, attentionMask), nil
}

func (e *OnnxEmbedder) EmbedBatch(ctx context.Context, texts []string) ([][]float32, error) {
	results := make([][]float32, len(texts))
	for i, text := range texts {
		emb, err := e.Embed(ctx, text)
		if err != nil {
			return nil, fmt.Errorf("rag: embed batch [%d]: %w", i, err)
		}
		results[i] = emb
	}
	return results, nil
}

func (e *OnnxEmbedder) Close() error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.session != nil {
		e.session.Destroy()
		e.session = nil
	}
	return nil
}

// ── Pooling ──────────────────────────────────────────────

// meanPool 对 hidden states 做 attention-masked mean pooling，输出 [dim]float32。
func meanPool(hidden []float32, seqLen, dim int, mask []int64) []float32 {
	pooled := make([]float32, dim)
	var totalWeight float32
	for i := 0; i < seqLen; i++ {
		w := float32(mask[i])
		if w == 0 {
			continue
		}
		for j := 0; j < dim; j++ {
			pooled[j] += hidden[i*dim+j] * w
		}
		totalWeight += w
	}
	if totalWeight > 0 {
		for j := 0; j < dim; j++ {
			pooled[j] /= totalWeight
		}
	}
	return pooled
}
