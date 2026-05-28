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

const (
	bertMaxLen   = 512 // BERT 模型最大输入 token 数（含特殊 token）
	maxBatchSize = 64  // 单次 ONNX Run 最大样本数，防 OOM
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
type OnnxEmbedder struct {
	session   *ort.DynamicAdvancedSession
	tokenizer *Tokenizer
	clsID     int
	sepID     int
	mu        sync.Mutex
	log       *slog.Logger
}

// InitEmbedder 加载 tokenizer（同步）后异步加载 ONNX 模型，不阻塞调用方。
// main 启动时尽早调用，模型在后台加载，GUI 可先行渲染。
// 调用后 GetTokenizer() 即可使用，无需等待模型就绪。
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

// GetTokenizer 返回全局 tokenizer 实例。InitEmbedder 调用后即可使用，
// 未调用 InitEmbedder 时返回 nil。
func GetTokenizer() *Tokenizer {
	return tokenizer
}

func (e *OnnxEmbedder) Dim() int { return 768 }

// newOnnxEmbedder 同步初始化 ONNX Runtime 并加载模型。仅由 InitEmbedder 调用。
func newOnnxEmbedder(modelsDir string, t *Tokenizer, log *slog.Logger) (*OnnxEmbedder, error) {
	modelPath := filepath.Join(modelsDir, "model.onnx")

	clsID, ok1 := t.vocab["[CLS]"]
	sepID, ok2 := t.vocab["[SEP]"]
	if !ok1 || !ok2 {
		return nil, fmt.Errorf("rag: vocab missing [CLS] or [SEP]")
	}

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
	return &OnnxEmbedder{
		session:   session,
		tokenizer: t,
		clsID:     clsID,
		sepID:     sepID,
		log:       log,
	}, nil
}

// ── 模型适配层 ────────────────────────────────────────────

// prepare 对原始 token 先截断再加 [CLS]/[SEP]，保证总长度 ≤ bertMaxLen。
// 换模型时替换此实现即可。
func (e *OnnxEmbedder) prepare(text string) []int {
	raw := e.tokenizer.Tokenize(text)
	limit := bertMaxLen - 2
	if len(raw) > limit {
		e.log.Warn("token 序列超过模型上限，已截断", "len", len(raw), "max", limit)
		raw = raw[:limit]
	}
	ids := make([]int, len(raw)+2)
	ids[0] = e.clsID
	copy(ids[1:], raw)
	ids[len(ids)-1] = e.sepID
	return ids
}

// ── Embedding ────────────────────────────────────────────

func (e *OnnxEmbedder) Embed(ctx context.Context, text string) ([]float32, error) {
	ids := e.prepare(text)

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
	if len(texts) == 0 {
		return nil, nil
	}
	if len(texts) <= maxBatchSize {
		return e.embedBatch(ctx, texts)
	}

	results := make([][]float32, 0, len(texts))
	for i := 0; i < len(texts); i += maxBatchSize {
		end := i + maxBatchSize
		if end > len(texts) {
			end = len(texts)
		}
		batch, err := e.embedBatch(ctx, texts[i:end])
		if err != nil {
			return nil, fmt.Errorf("rag: batch [%d:%d]: %w", i, end, err)
		}
		results = append(results, batch...)
	}
	return results, nil
}

// embedBatch 执行单次 batch ONNX 推理，调用方保证 len(texts) ≤ maxBatchSize。
func (e *OnnxEmbedder) embedBatch(ctx context.Context, texts []string) ([][]float32, error) {
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	default:
	}

	// 1. Tokenize all texts, find max length.
	tokenized := make([][]int, len(texts))
	maxLen := 0
	for i, text := range texts {
		tokenized[i] = e.prepare(text)
		if len(tokenized[i]) > maxLen {
			maxLen = len(tokenized[i])
		}
	}
	if maxLen == 0 {
		results := make([][]float32, len(texts))
		for i := range results {
			results[i] = make([]float32, 768)
		}
		return results, nil
	}

	batchSize := int64(len(texts))
	seqLen := int64(maxLen)

	// 2. Build padded tensors [N, maxLen].
	inputIDs := make([]int64, batchSize*seqLen)
	attentionMask := make([]int64, batchSize*seqLen)
	tokenTypeIDs := make([]int64, batchSize*seqLen)

	for i := int64(0); i < batchSize; i++ {
		ids := tokenized[i]
		base := i * seqLen
		for j, id := range ids {
			inputIDs[base+int64(j)] = int64(id)
			attentionMask[base+int64(j)] = 1
		}
	}

	inputTensor, err := ort.NewTensor(ort.NewShape(batchSize, seqLen), inputIDs)
	if err != nil {
		return nil, fmt.Errorf("rag: create batch input tensor: %w", err)
	}
	defer inputTensor.Destroy()

	maskTensor, err := ort.NewTensor(ort.NewShape(batchSize, seqLen), attentionMask)
	if err != nil {
		return nil, fmt.Errorf("rag: create batch mask tensor: %w", err)
	}
	defer maskTensor.Destroy()

	typeIDsTensor, err := ort.NewTensor(ort.NewShape(batchSize, seqLen), tokenTypeIDs)
	if err != nil {
		return nil, fmt.Errorf("rag: create batch type_ids tensor: %w", err)
	}
	defer typeIDsTensor.Destroy()

	outputTensor, err := ort.NewEmptyTensor[float32](ort.NewShape(batchSize, seqLen, 768))
	if err != nil {
		return nil, fmt.Errorf("rag: create batch output tensor: %w", err)
	}
	defer outputTensor.Destroy()

	// 3. Single ONNX Run.
	e.mu.Lock()
	defer e.mu.Unlock()
	err = e.session.Run(
		[]ort.Value{inputTensor, maskTensor, typeIDsTensor},
		[]ort.Value{outputTensor},
	)
	if err != nil {
		return nil, fmt.Errorf("rag: batch onnx run: %w", err)
	}

	// 4. Mean pool each sample with its own attention mask.
	hidden := outputTensor.GetData()
	results := make([][]float32, batchSize)
	sampleSize := int(seqLen) * 768
	for i := int64(0); i < batchSize; i++ {
		start := int(i) * sampleSize
		mask := attentionMask[i*seqLen : (i+1)*seqLen]
		results[i] = meanPool(hidden[start:start+sampleSize], int(seqLen), 768, mask)
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
