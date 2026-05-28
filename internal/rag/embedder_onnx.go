//go:build cgo

package rag

import (
	"bufio"
	"context"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"sync"
	"unicode"
	"unicode/utf8"

	ort "github.com/yalue/onnxruntime_go"
)

var (
	instance *OnnxEmbedder
	initOnce sync.Once
	ready    = make(chan struct{})
	initErr  error
)

// OnnxEmbedder 使用 ONNX Runtime 加载 text2vec-base-chinese 模型生成 embedding。
// 全局仅一个实例，通过 InitEmbedder 异步初始化。
// Run 不是线程安全的，Embed/EmbedBatch 内部加锁。
type OnnxEmbedder struct {
	session *ort.DynamicAdvancedSession
	vocab   map[string]int
	mu      sync.Mutex
	log     *slog.Logger
}

// InitEmbedder 启动异步加载 ONNX 模型，不阻塞调用方。多次调用安全。
// main 启动时尽早调用，模型在后台加载，GUI 可先行渲染。
func InitEmbedder(modelsDir string, log *slog.Logger) {
	initOnce.Do(func() {
		go func() {
			defer close(ready)
			e, err := newOnnxEmbedder(modelsDir, log)
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

// newOnnxEmbedder 同步初始化 ONNX Runtime 并加载模型。modelsDir 应包含 model.onnx 和 vocab.txt。
// 仅由 InitEmbedder 调用，不直接暴露。
func newOnnxEmbedder(modelsDir string, log *slog.Logger) (*OnnxEmbedder, error) {
	vocabPath := filepath.Join(modelsDir, "vocab.txt")
	modelPath := filepath.Join(modelsDir, "model.onnx")

	vocab, err := loadVocab(vocabPath)
	if err != nil {
		return nil, fmt.Errorf("rag: load vocab: %w", err)
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

	log.Info("ONNX embedder 已初始化", "model", modelPath, "vocab_size", len(vocab))
	return &OnnxEmbedder{session: session, vocab: vocab, log: log}, nil
}

func (e *OnnxEmbedder) Dim() int { return 768 }

// TokenCount 返回文本的 BERT token 数量，供分块逻辑使用。
func (e *OnnxEmbedder) TokenCount(text string) int {
	return len(e.tokenize(text))
}

func (e *OnnxEmbedder) Embed(ctx context.Context, text string) ([]float32, error) {
	ids := e.tokenize(text)
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

// ── Tokenizer ────────────────────────────────────────────

// loadVocab 从 vocab.txt 读取词表，每行一个 token，行号即 token ID。
func loadVocab(path string) (map[string]int, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	v := make(map[string]int)
	scanner := bufio.NewScanner(f)
	for id := 0; scanner.Scan(); id++ {
		v[scanner.Text()] = id
	}
	return v, scanner.Err()
}

func (e *OnnxEmbedder) tokenize(text string) []int {
	segs := segment(text)
	unkID := e.vocab["[UNK]"]
	var ids []int

	for _, seg := range segs {
		r, _ := utf8.DecodeRuneInString(seg)
		if r != utf8.RuneError && len(seg) == utf8.RuneLen(r) && (isCJK(r) || unicode.IsPunct(r)) {
			if id, ok := e.vocab[seg]; ok {
				ids = append(ids, id)
			} else {
				ids = append(ids, unkID)
			}
			continue
		}
		ids = append(ids, wordPiece(e.vocab, seg)...)
	}
	return ids
}

func isCJK(r rune) bool {
	return (r >= 0x4E00 && r <= 0x9FFF) ||
		(r >= 0x3400 && r <= 0x4DBF) ||
		(r >= 0x20000 && r <= 0x2A6DF)
}

// segment 将文本按 CJK 字符、标点、连续字母/数字切分为基本单元。
func segment(text string) []string {
	var segs []string
	var buf []rune

	flush := func() {
		if len(buf) > 0 {
			segs = append(segs, string(buf))
			buf = buf[:0]
		}
	}

	for _, r := range text {
		if unicode.IsSpace(r) {
			flush()
			continue
		}
		if isCJK(r) || unicode.IsPunct(r) {
			flush()
			segs = append(segs, string(r))
		} else {
			buf = append(buf, unicode.ToLower(r))
		}
	}
	flush()
	return segs
}

// wordPiece 用贪婪最长匹配算法拆分连续字母/数字串，返回 token ID。
// ## 前缀表示子词续接。
func wordPiece(vocab map[string]int, word string) []int {
	unkID := vocab["[UNK]"]
	runes := []rune(word)
	var ids []int
	start := 0
	isFirst := true

	for start < len(runes) {
		end := len(runes)
		found := false
		for end > start {
			sub := string(runes[start:end])
			lookup := sub
			if !isFirst {
				lookup = "##" + sub
			}
			if id, ok := vocab[lookup]; ok {
				ids = append(ids, id)
				start = end
				isFirst = false
				found = true
				break
			}
			end--
		}
		if !found {
			ids = append(ids, unkID)
			break
		}
	}
	return ids
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
