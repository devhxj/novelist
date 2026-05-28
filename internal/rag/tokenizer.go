package rag

import (
	"bufio"
	"fmt"
	"os"
	"unicode"
	"unicode/utf8"
)

// Tokenizer 是 BERT WordPiece tokenizer，仅依赖词表文件（~107KB），
// 不依赖 ONNX Runtime，可在模型加载前使用。
type Tokenizer struct {
	vocab map[string]int
}

// NewTokenizer 从 vocab.txt 加载词表。若缺失 [UNK] 则报错。
func NewTokenizer(vocabPath string) (*Tokenizer, error) {
	vocab, err := loadVocab(vocabPath)
	if err != nil {
		return nil, fmt.Errorf("rag: load vocab: %w", err)
	}
	if _, ok := vocab["[UNK]"]; !ok {
		return nil, fmt.Errorf("rag: vocab missing [UNK] token")
	}
	return &Tokenizer{vocab: vocab}, nil
}

// TokenCount 返回文本的 token 数量，供分块使用。
func (t *Tokenizer) TokenCount(text string) int {
	return len(t.Tokenize(text))
}

// Tokenize 将文本转换为 token ID 序列。不含特殊 token，不做长度截断。
func (t *Tokenizer) Tokenize(text string) []int {
	segs := segment(text)
	unkID := t.vocab["[UNK]"]
	var ids []int

	for _, seg := range segs {
		r, _ := utf8.DecodeRuneInString(seg)
		if r != utf8.RuneError && len(seg) == utf8.RuneLen(r) && (isCJK(r) || unicode.IsPunct(r)) {
			if id, ok := t.vocab[seg]; ok {
				ids = append(ids, id)
			} else {
				ids = append(ids, unkID)
			}
			continue
		}
		ids = append(ids, wordPiece(t.vocab, seg)...)
	}
	return ids
}

// ── 内部辅助 ──────────────────────────────────────────────

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

func isCJK(r rune) bool {
	return (r >= 0x4E00 && r <= 0x9FFF) ||
		(r >= 0x3400 && r <= 0x4DBF) ||
		(r >= 0x20000 && r <= 0x2A6DF)
}

// segment 将文本按 CJK 字符、标点、连续字母/数字切分为基本单元。
// 空白字符被丢弃（BERT tokenizer 约定）。
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
// ## 前缀表示子词续接。无法匹配时返回 [UNK]。
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
